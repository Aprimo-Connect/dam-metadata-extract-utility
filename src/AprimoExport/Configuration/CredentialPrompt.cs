using System.Text;

namespace AprimoExport.Configuration;

/// <summary>
/// Interactive entry of OAuth credentials, for when you would rather not put them in
/// a config file or environment variable.
///
/// <para>Prompts only when stdin is an actual console. Under redirected input — CI, a
/// scheduled task, a pipe — it stays silent and lets validation report the missing
/// value, so an unattended run fails fast instead of hanging on a prompt nobody sees.</para>
///
/// <para>Entered values live in memory for the process only; nothing is written to disk.</para>
/// </summary>
public static class CredentialPrompt
{
    /// <summary>
    /// Fills in any missing client ID / secret by asking. Returns false when values are
    /// still missing afterwards (non-interactive, or the user cancelled).
    /// </summary>
    /// <param name="force">
    /// Prompt even when values are already configured — lets you override a stale
    /// config file or environment variable for one run.
    /// </param>
    public static bool EnsureCredentials(AprimoConfig config, bool force, Action<string> log)
    {
        // A placeholder counts as absent. Otherwise a stray APRIMO_CLIENT_ID=your-client-id
        // is treated as "supplied", the prompt skips it, and the real secret gets paired
        // with a bogus ID — which is exactly how this bites people.
        var idIsPlaceholder = !string.IsNullOrWhiteSpace(config.ClientId) &&
                              PlaceholderDetection.LooksLikePlaceholder(config.ClientId);
        var secretIsPlaceholder = !string.IsNullOrWhiteSpace(config.ClientSecret) &&
                                  PlaceholderDetection.LooksLikePlaceholder(config.ClientSecret);

        if (idIsPlaceholder)
            log($"The configured client ID is the placeholder '{config.ClientId}', so it will be asked for. " +
                "Clear APRIMO_CLIENT_ID to stop it overriding your real value on future runs.");
        if (secretIsPlaceholder)
            log("The configured client secret is a documentation placeholder, so it will be asked for.");

        var needsId = force || string.IsNullOrWhiteSpace(config.ClientId) || idIsPlaceholder;
        var needsSecret = force || string.IsNullOrWhiteSpace(config.ClientSecret) || secretIsPlaceholder;

        if (!needsId && !needsSecret) return true;

        if (Console.IsInputRedirected)
        {
            log("Credentials are missing and stdin is not a console, so there is nothing to prompt. " +
                "Set APRIMO_CLIENT_ID / APRIMO_CLIENT_SECRET, or pass --client-id / --client-secret.");
            return false;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"OAuth client credentials for tenant '{config.Tenant}'");
        Console.Error.WriteLine("(Administration > Integration > Registrations. Nothing entered here is saved.)");

        try
        {
            if (needsId)
            {
                var id = ReadVisible("  Client ID     : ");
                if (string.IsNullOrWhiteSpace(id))
                {
                    log("No client ID entered.");
                    return false;
                }
                config.ClientId = id.Trim();
            }

            if (needsSecret)
            {
                var secret = ReadMasked("  Client secret : ");
                if (string.IsNullOrWhiteSpace(secret))
                {
                    log("No client secret entered.");
                    return false;
                }

                // Pasting often drags in a trailing space or tab, which fails as an
                // opaque invalid_client. Trim it, but say so rather than silently
                // changing what was entered.
                var trimmed = secret.Trim();
                if (trimmed.Length != secret.Length)
                    log($"Note: removed {secret.Length - trimmed.Length} whitespace character(s) " +
                        "from the secret (likely from pasting).");

                config.ClientSecret = trimmed;
                log($"Credentials captured: client ID {config.ClientId.Length} chars, " +
                    $"secret {trimmed.Length} chars.");
            }
        }
        catch (InvalidOperationException ex)
        {
            // No console buffer attached (e.g. some IDE/agent hosts).
            log($"Could not read from the console ({ex.Message}). " +
                "Use APRIMO_CLIENT_ID / APRIMO_CLIENT_SECRET instead.");
            return false;
        }

        Console.Error.WriteLine();
        return true;
    }

    private static string ReadVisible(string prompt)
    {
        Console.Error.Write(prompt);
        return Console.ReadLine() ?? "";
    }

    /// <summary>
    /// Reads a secret without echoing it. Asterisks go to stderr so stdout stays clean.
    /// Backspace edits; Escape cancels.
    /// </summary>
    private static string ReadMasked(string prompt)
    {
        Console.Error.Write(prompt);

        var builder = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.Error.WriteLine();
                    return builder.ToString();

                case ConsoleKey.Escape:
                    Console.Error.WriteLine(" (cancelled)");
                    return "";

                case ConsoleKey.Backspace:
                    if (builder.Length > 0)
                    {
                        builder.Length--;
                        Console.Error.Write("\b \b");
                    }
                    continue;
            }

            // Ignore control characters; accept everything else, including pasted input.
            if (char.IsControl(key.KeyChar)) continue;

            builder.Append(key.KeyChar);
            Console.Error.Write('*');
        }
    }
}
