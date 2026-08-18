using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AprimoExport.Configuration;

namespace AprimoExport.Auth;

/// <summary>
/// OAuth 2.0 client-credentials token provider for Aprimo.
///
/// <para>Tokens roll automatically: a cached token is reused until it is within
/// <see cref="AprimoConfig.RefreshSkewSeconds"/> of expiry, then renewed. Concurrent
/// callers during a renewal are single-flighted through one lock, so a burst of
/// requests triggers exactly one token call. <see cref="InvalidateAsync"/> lets the
/// HTTP layer force a renewal when the API returns 401 despite a token that looked
/// valid (revocation, tenant-side rotation, clock skew).</para>
/// </summary>
public sealed class OAuthTokenProvider : IDisposable
{
    private readonly HttpClient _http;
    private readonly AprimoConfig _config;
    private readonly RetryConfig _retry;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Random _jitter = new();

    private string? _accessToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private int _tokenRequestCount;

    public OAuthTokenProvider(HttpClient http, AprimoConfig config, RetryConfig retry, Action<string> log)
    {
        _http = http;
        _config = config;
        _retry = retry;
        _log = log;
    }

    public int TokenRequestCount => _tokenRequestCount;
    public DateTimeOffset ExpiresAt => _expiresAt;

    /// <summary>Returns a valid bearer token, renewing it if needed.</summary>
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var cached = _accessToken;
        if (cached is not null && !IsExpiring(_expiresAt))
            return cached;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check: another caller may have renewed while we waited.
            if (_accessToken is not null && !IsExpiring(_expiresAt))
                return _accessToken;

            return await RequestTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Drops the cached token so the next <see cref="GetAccessTokenAsync"/> renews.
    /// Called after an unexpected 401.
    /// </summary>
    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _accessToken = null;
            _expiresAt = DateTimeOffset.MinValue;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool IsExpiring(DateTimeOffset expiresAt) =>
        DateTimeOffset.UtcNow >= expiresAt.AddSeconds(-_config.RefreshSkewSeconds);

    /// <summary>
    /// Requests a token, retrying transient failures. Renewal happens mid-export on a
    /// long run, so a momentary network blip or a 503 here must not abort the whole job.
    /// Credential rejections are permanent and fail immediately.
    /// </summary>
    private async Task<string> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var backoff = _retry.InitialBackoffSeconds;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await SendTokenRequestAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (AuthenticationFailedException ex) when (ex.IsTransient && attempt < _retry.MaxAttempts)
            {
                var wait = ex.RetryAfter ?? WithJitter(backoff);
                _log($"Token request: {ex.Message.Split('\n')[0]} — " +
                     $"attempt {attempt}/{_retry.MaxAttempts}, waiting {wait.TotalSeconds:F1}s.");
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                backoff = Math.Min(backoff * _retry.BackoffMultiplier, _retry.MaxBackoffSeconds);
            }
            catch (HttpRequestException ex) when (IsUnresolvableHost(ex))
            {
                // A hostname that does not resolve is almost always a wrong tenant, not a
                // blip. Retrying five times only delays the message that actually helps.
                throw new AuthenticationFailedException(DescribeNetworkFailure(ex, attempt), ex);
            }
            catch (HttpRequestException ex) when (attempt < _retry.MaxAttempts)
            {
                var wait = WithJitter(backoff);
                _log($"Token request: network error ({ex.Message}) — " +
                     $"attempt {attempt}/{_retry.MaxAttempts}, waiting {wait.TotalSeconds:F1}s.");
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                backoff = Math.Min(backoff * _retry.BackoffMultiplier, _retry.MaxBackoffSeconds);
            }
            catch (HttpRequestException ex)
            {
                throw new AuthenticationFailedException(DescribeNetworkFailure(ex, attempt), ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested &&
                                                  attempt < _retry.MaxAttempts)
            {
                var wait = WithJitter(backoff);
                _log($"Token request timed out — attempt {attempt}/{_retry.MaxAttempts}, " +
                     $"waiting {wait.TotalSeconds:F1}s. ({ex.Message})");
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                backoff = Math.Min(backoff * _retry.BackoffMultiplier, _retry.MaxBackoffSeconds);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AuthenticationFailedException(
                    $"Token request to {_config.ResolvedTokenUrl} timed out after " +
                    $"{_retry.MaxAttempts} attempts. Check network and proxy access to that host.", ex);
            }
        }
    }

    private static bool IsUnresolvableHost(HttpRequestException ex) =>
        ex.InnerException is System.Net.Sockets.SocketException
        {
            SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound
                          or System.Net.Sockets.SocketError.NoData
        };

    private string DescribeNetworkFailure(HttpRequestException ex, int attempts) =>
        $"Could not reach the token endpoint {_config.ResolvedTokenUrl} after " +
        $"{attempts} attempt(s): {ex.Message}{Environment.NewLine}" +
        $"  Check that Aprimo.Tenant ('{_config.Tenant}') is correct — the token host is " +
        "{tenant}.aprimo.com with no '.dam' segment, unlike the API host." + Environment.NewLine +
        "  Also confirm outbound HTTPS and any proxy configuration.";

    /// <summary>Full jitter, so retries do not resynchronise.</summary>
    private TimeSpan WithJitter(double seconds)
    {
        double factor;
        lock (_jitter) factor = 0.5 + _jitter.NextDouble() * 0.5;
        return TimeSpan.FromSeconds(seconds * factor);
    }

    private async Task<string> SendTokenRequestAsync(CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        };

        if (!string.IsNullOrWhiteSpace(_config.Scope))
            form["scope"] = _config.Scope;

        foreach (var kvp in _config.AdditionalTokenParameters)
            form[kvp.Key] = kvp.Value;

        using var request = new HttpRequestMessage(HttpMethod.Post, _config.ResolvedTokenUrl);

        if (_config.ClientAuthStyle == ClientAuthStyle.Basic)
        {
            // RFC 6749 §2.3.1: credentials are form-urlencoded before base64.
            var raw = $"{Uri.EscapeDataString(_config.ClientId)}:{Uri.EscapeDataString(_config.ClientSecret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }
        else
        {
            form["client_id"] = _config.ClientId;
            form["client_secret"] = _config.ClientSecret;
        }

        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        Interlocked.Increment(ref _tokenRequestCount);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Throttling and server-side faults are worth retrying; a rejected
            // credential or scope never is.
            var transient = response.StatusCode is HttpStatusCode.TooManyRequests
                                                or HttpStatusCode.RequestTimeout
                                                or HttpStatusCode.InternalServerError
                                                or HttpStatusCode.BadGateway
                                                or HttpStatusCode.ServiceUnavailable
                                                or HttpStatusCode.GatewayTimeout;

            TimeSpan? retryAfter = null;
            if (_retry.RespectRetryAfter && response.Headers.RetryAfter is { } header)
            {
                if (header.Delta is { } delta && delta > TimeSpan.Zero) retryAfter = delta;
                else if (header.Date is { } date && date > DateTimeOffset.UtcNow) retryAfter = date - DateTimeOffset.UtcNow;

                if (retryAfter > TimeSpan.FromSeconds(_retry.MaxBackoffSeconds))
                    retryAfter = TimeSpan.FromSeconds(_retry.MaxBackoffSeconds);
            }

            throw new AuthenticationFailedException(
                BuildTokenErrorMessage(response.StatusCode, body), transient, retryAfter);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("access_token", out var tokenElement) ||
            tokenElement.ValueKind != JsonValueKind.String)
            throw new AuthenticationFailedException(
                "Token endpoint returned 200 but no 'access_token' field. Body: " + Truncate(body, 500));

        var token = tokenElement.GetString()!;

        // Default to a conservative 5 minutes when expires_in is absent.
        var lifetime = root.TryGetProperty("expires_in", out var expiresIn) &&
                       expiresIn.TryGetInt32(out var seconds)
            ? seconds
            : 300;

        _accessToken = token;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);

        _log($"Access token acquired (#{_tokenRequestCount}); expires in {lifetime}s " +
             $"at {_expiresAt:HH:mm:ss}Z, renewing {_config.RefreshSkewSeconds}s early.");

        return token;
    }

    private string BuildTokenErrorMessage(HttpStatusCode status, string body)
    {
        var detail = Truncate(body, 500);
        var error = TryReadError(body);

        var hint = (status, error) switch
        {
            (HttpStatusCode.BadRequest, "invalid_client") or (HttpStatusCode.Unauthorized, "invalid_client") =>
                "The identity server rejected this client ID / secret pair. Likely causes, in order:" +
                Environment.NewLine +
                "  1. The registration's OAuth Flow Type is not 'Client Credential'. An Authorization Code" +
                Environment.NewLine +
                "     with PKCE registration is a public client with no secret, so presenting one is" +
                Environment.NewLine +
                "     rejected as invalid_client. Check Administration > Integration > Registrations." +
                Environment.NewLine +
                "  2. Registration changes take up to 15 minutes to propagate. If you just saved it, wait." +
                Environment.NewLine +
                $"  3. The secret was truncated or padded when copied (you supplied {_config.ClientSecret.Length} " +
                $"characters; client ID '{_config.ClientId}' is {_config.ClientId.Length})." +
                Environment.NewLine +
                $"  4. This client may expect credentials in the form body rather than a Basic header." +
                Environment.NewLine +
                $"     You used ClientAuthStyle={_config.ClientAuthStyle}; retry with " +
                $"--client-auth-style {(_config.ClientAuthStyle == ClientAuthStyle.Basic ? "Body" : "Basic")}",
            (_, "invalid_scope") =>
                $"Scope '{_config.Scope}' was rejected. The Aprimo DAM API scope is 'api'.",
            (_, "unauthorized_client") =>
                "The registration exists but is not permitted to use the client_credentials grant. " +
                "Enable that grant type on the registration.",
            (HttpStatusCode.NotFound, _) =>
                $"Token endpoint not found. Expected https://{{tenant}}.aprimo.com/login/connect/token — " +
                "note the token host has no '.dam' segment, unlike the API host.",
            _ => "Verify Aprimo.TokenUrl, ClientId, ClientSecret and Scope."
        };

        return $"OAuth token request failed: {(int)status} {status}. {hint}{Environment.NewLine}Response: {detail}";
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? "(empty)"
        : value.Length <= max ? value
        : value.Substring(0, max) + "…";

    public void Dispose() => _lock.Dispose();
}

public sealed class AuthenticationFailedException : Exception
{
    /// <summary>True when retrying could plausibly succeed (throttling, server fault).</summary>
    public bool IsTransient { get; }

    /// <summary>Server-requested delay from a <c>Retry-After</c> header, when present.</summary>
    public TimeSpan? RetryAfter { get; }

    public AuthenticationFailedException(string message, bool isTransient = false, TimeSpan? retryAfter = null)
        : base(message)
    {
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    public AuthenticationFailedException(string message, Exception innerException)
        : base(message, innerException) { }
}
