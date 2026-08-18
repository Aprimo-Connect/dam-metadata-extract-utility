using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AprimoExport.Configuration;

namespace AprimoExport.Auth;

/// <summary>
/// Stores OAuth client credentials encrypted with Windows DPAPI, so they can be entered
/// once and reused without a plaintext secret anywhere on disk.
///
/// <para>Protection is <see cref="DataProtectionScope.CurrentUser"/>: the ciphertext can
/// only be decrypted by the same Windows account on the same machine. Copying the file
/// elsewhere, or another user reading it, yields nothing usable. An application-specific
/// entropy value is mixed in so the blob is not interchangeable with other DPAPI data.</para>
///
/// <para>The file lives under <c>%LOCALAPPDATA%</c>, deliberately outside the project
/// tree so it cannot be committed.</para>
///
/// <para>Entries are keyed by tenant, so credentials for several tenants can coexist.</para>
/// </summary>
public static class CredentialStore
{
    /// <summary>Mixed into the DPAPI blob so it is specific to this application.</summary>
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("AprimoExport.CredentialStore.v1");

    private static readonly JsonSerializerOptions StoreJson = new() { WriteIndented = false };

    /// <summary>DPAPI is a Windows facility; there is no cross-platform equivalent here.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AprimoExport",
        "credentials.dat");

    private sealed class Entry
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string SavedAtUtc { get; set; } = "";
    }

    /// <summary>
    /// Fills any missing credential on <paramref name="config"/> from the store.
    /// Deliberately only fills gaps: a value from the config file, environment or command
    /// line always wins, so the saved credential never silently overrides an explicit one.
    /// </summary>
    public static bool TryFill(AprimoConfig config, Action<string> log, string? storePath = null)
    {
        // A placeholder is not a real value, so the store may replace it. Without this, a
        // stray APRIMO_CLIENT_ID=your-client-id would outrank the saved credential on
        // every run — the store would see a value present and decline to fill.
        var idIsPlaceholder = !string.IsNullOrWhiteSpace(config.ClientId) &&
                              PlaceholderDetection.LooksLikePlaceholder(config.ClientId);
        var secretIsPlaceholder = !string.IsNullOrWhiteSpace(config.ClientSecret) &&
                                  PlaceholderDetection.LooksLikePlaceholder(config.ClientSecret);

        var needsId = string.IsNullOrWhiteSpace(config.ClientId) || idIsPlaceholder;
        var needsSecret = string.IsNullOrWhiteSpace(config.ClientSecret) || secretIsPlaceholder;
        if (!needsId && !needsSecret) return false;

        // Note: nothing is announced until an entry is actually found. Saying we would
        // prefer a saved credential before knowing one exists is just misleading.
        var entries = TryRead(storePath, log);
        if (entries is null) return false;

        var tenant = TenantKey(config);
        if (!entries.TryGetValue(tenant, out var entry))
        {
            log($"No saved credentials for tenant '{tenant}'. " +
                $"Saved tenants: {(entries.Count == 0 ? "(none)" : string.Join(", ", entries.Keys))}. " +
                "Run --save-credentials to add them.");
            return false;
        }

        // Captured before the overwrite, so the message can name what was replaced.
        var replacedId = config.ClientId;

        var filled = new List<string>();
        if (needsId && !string.IsNullOrWhiteSpace(entry.ClientId))
        {
            config.ClientId = entry.ClientId;
            filled.Add("ClientId");
        }
        if (needsSecret && !string.IsNullOrWhiteSpace(entry.ClientSecret))
        {
            config.ClientSecret = entry.ClientSecret;
            filled.Add("ClientSecret");
        }

        if (filled.Count == 0) return false;

        var saved = string.IsNullOrEmpty(entry.SavedAtUtc) ? "unknown date" : entry.SavedAtUtc;
        log($"Applied from encrypted store: {string.Join(", ", filled)} " +
            $"(tenant '{tenant}', saved {saved}).");

        if (idIsPlaceholder)
            log($"  The saved client ID replaced the placeholder '{replacedId}'. " +
                "Clear APRIMO_CLIENT_ID to remove the confusion.");

        return true;
    }

    /// <summary>Adds or replaces the entry for a tenant.</summary>
    public static void Save(
        string tenant,
        string clientId,
        string clientSecret,
        Action<string> log,
        string? storePath = null)
    {
        RequireSupport();

        var path = storePath ?? DefaultStorePath;
        var entries = TryRead(storePath, _ => { }) ?? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        var key = string.IsNullOrWhiteSpace(tenant) ? "(default)" : tenant.Trim();
        var replacing = entries.ContainsKey(key);

        entries[key] = new Entry
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            SavedAtUtc = DateTimeOffset.UtcNow.ToString("u")
        };

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(entries, StoreJson);
        var ciphertext = Protect(plaintext);

        // Write-then-replace so an interrupted save cannot corrupt an existing store.
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ciphertext);
        File.Move(temp, path, overwrite: true);

        // The plaintext only ever existed in this process's memory; clear our copy.
        Array.Clear(plaintext);

        log($"{(replacing ? "Replaced" : "Saved")} credentials for tenant '{key}' in {path}");
        log($"  Encrypted with Windows DPAPI (current user, this machine). {ciphertext.Length} bytes.");
    }

    /// <summary>Removes one tenant's entry, or the whole store when <paramref name="tenant"/> is null.</summary>
    public static bool Clear(string? tenant, Action<string> log, string? storePath = null)
    {
        var path = storePath ?? DefaultStorePath;

        if (!File.Exists(path))
        {
            log($"Nothing to clear: no credential store at {path}");
            return false;
        }

        if (tenant is null)
        {
            File.Delete(path);
            log($"Deleted the credential store at {path}");
            return true;
        }

        var entries = TryRead(storePath, log);
        if (entries is null) return false;

        if (!entries.Remove(tenant))
        {
            log($"No saved credentials for tenant '{tenant}'; nothing removed.");
            return false;
        }

        if (entries.Count == 0)
        {
            File.Delete(path);
            log($"Removed the last entry; deleted the store at {path}");
            return true;
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(entries, StoreJson);
        File.WriteAllBytes(path, Protect(plaintext));
        Array.Clear(plaintext);

        log($"Removed credentials for tenant '{tenant}'. Remaining: {string.Join(", ", entries.Keys)}");
        return true;
    }

    /// <summary>Tenants with saved credentials. Never returns secrets.</summary>
    public static IReadOnlyList<string> ListTenants(Action<string> log, string? storePath = null)
    {
        var entries = TryRead(storePath, log);
        return entries is null ? Array.Empty<string>() : entries.Keys.ToList();
    }

    public static bool Exists(string? storePath = null) => File.Exists(storePath ?? DefaultStorePath);

    private static Dictionary<string, Entry>? TryRead(string? storePath, Action<string> log)
    {
        var path = storePath ?? DefaultStorePath;
        if (!File.Exists(path)) return null;

        if (!IsSupported)
        {
            log("A credential store exists but DPAPI is only available on Windows; ignoring it.");
            return null;
        }

        byte[] plaintext;
        try
        {
            plaintext = Unprotect(File.ReadAllBytes(path));
        }
        catch (CryptographicException ex)
        {
            log($"The credential store at {path} could not be decrypted ({ex.Message}).");
            log("  DPAPI ties the file to one Windows account on one machine, so this usually means it was");
            log("  copied from elsewhere, or your profile changed. Run --clear-credentials then --save-credentials.");
            return null;
        }
        catch (IOException ex)
        {
            log($"The credential store at {path} could not be read ({ex.Message}).");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(plaintext, StoreJson)
                   is { } parsed
                ? new Dictionary<string, Entry>(parsed, StringComparer.OrdinalIgnoreCase)
                : null;
        }
        catch (JsonException ex)
        {
            log($"The credential store at {path} decrypted but its contents are not valid ({ex.Message}). " +
                "Run --clear-credentials to reset it.");
            return null;
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    private static string TenantKey(AprimoConfig config) =>
        string.IsNullOrWhiteSpace(config.Tenant) ? "(default)" : config.Tenant.Trim();

    private static void RequireSupport()
    {
        if (!IsSupported) throw PlatformNotSupported();
    }

    private static PlatformNotSupportedException PlatformNotSupported() => new(
        "The credential store uses Windows DPAPI, which is not available on this platform. " +
        "Use the APRIMO_CLIENT_ID / APRIMO_CLIENT_SECRET environment variables instead.");

    // The OperatingSystem.IsWindows() checks are inline rather than delegated to
    // RequireSupport() because that is the form the platform-compatibility analyzer
    // (CA1416) recognises as a guard.

    private static byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows()) throw PlatformNotSupported();
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    private static byte[] Unprotect(byte[] ciphertext)
    {
        if (!OperatingSystem.IsWindows()) throw PlatformNotSupported();
        return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
    }
}
