using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AprimoExport.Auth;
using AprimoExport.Configuration;

namespace AprimoExport.Http;

/// <summary>
/// HTTP access to the Aprimo DAM API: token injection and renewal, client-side rate
/// limiting, and retry with backoff. Every call passes through the rate limiter, so
/// retries cannot push the emitted rate above the configured ceiling.
/// </summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly OAuthTokenProvider _tokens;
    private readonly RateLimiter _limiter;
    private readonly ExportConfig _config;
    private readonly Action<string> _log;
    private readonly Random _jitter = new();

    private long _requestCount;
    private long _retryCount;
    private long _bytesReceived;

    public ApiClient(
        HttpClient http,
        OAuthTokenProvider tokens,
        RateLimiter limiter,
        ExportConfig config,
        Action<string> log)
    {
        _http = http;
        _tokens = tokens;
        _limiter = limiter;
        _config = config;
        _log = log;
    }

    public long RequestCount => Interlocked.Read(ref _requestCount);
    public long RetryCount => Interlocked.Read(ref _retryCount);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>
    /// Sends a request and parses the JSON response. The caller owns the returned
    /// <see cref="JsonDocument"/> and must dispose it.
    /// </summary>
    /// <param name="requestFactory">
    /// Builds a fresh <see cref="HttpRequestMessage"/> per attempt — a message cannot be resent.
    /// </param>
    public async Task<JsonDocument> SendJsonAsync(
        Func<HttpRequestMessage> requestFactory,
        string description,
        CancellationToken cancellationToken)
    {
        var retry = _config.Source.Retry;
        var backoff = retry.InitialBackoffSeconds;
        var authRetried = false;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var slot = await _limiter.AcquireAsync(cancellationToken).ConfigureAwait(false);

            HttpResponseMessage? response = null;
            try
            {
                using var request = requestFactory();
                await ApplyStandardHeadersAsync(request, cancellationToken).ConfigureAwait(false);

                Interlocked.Increment(ref _requestCount);

                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                // A 401 on a token we believed valid: renew once, then retry immediately.
                if (response.StatusCode == HttpStatusCode.Unauthorized && !authRetried)
                {
                    authRetried = true;
                    _log($"{description}: 401 Unauthorized — renewing access token and retrying.");
                    await _tokens.InvalidateAsync(cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _retryCount);
                    response.Dispose();
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    var payload = await response.Content
                        .ReadAsByteArrayAsync(cancellationToken)
                        .ConfigureAwait(false);

                    Interlocked.Add(ref _bytesReceived, payload.Length);

                    try
                    {
                        return JsonDocument.Parse(payload);
                    }
                    catch (JsonException ex)
                    {
                        throw new ApiException(
                            $"{description}: response was not valid JSON ({ex.Message}). " +
                            $"First 500 bytes: {Preview(payload, 500)}",
                            response.StatusCode);
                    }
                }

                // Throttling or transient server error → back off and retry.
                if (IsTransient(response.StatusCode))
                {
                    if (attempt >= retry.MaxAttempts)
                    {
                        var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
                        throw new ApiException(
                            $"{description}: giving up after {attempt} attempts. " +
                            $"Last status {(int)response.StatusCode} {response.StatusCode}. Body: {body}",
                            response.StatusCode);
                    }

                    var wait = ResolveDelay(response, backoff, retry);

                    _log($"{description}: {(int)response.StatusCode} {response.StatusCode} — " +
                         $"attempt {attempt}/{retry.MaxAttempts}, waiting {wait.TotalSeconds:F1}s.");

                    Interlocked.Increment(ref _retryCount);

                    // 429/503 means we are going too fast: hold every caller back, not just this one.
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                        await _limiter.PenalizeAsync(wait, cancellationToken).ConfigureAwait(false);

                    response.Dispose();
                    response = null;

                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    backoff = Math.Min(backoff * retry.BackoffMultiplier, retry.MaxBackoffSeconds);
                    continue;
                }

                // Non-retryable: surface it with the server's explanation.
                {
                    var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
                    throw new ApiException(
                        $"{description}: {(int)response.StatusCode} {response.StatusCode}. " +
                        $"{ExplainStatus(response.StatusCode)} Body: {body}",
                        response.StatusCode);
                }
            }
            catch (HttpRequestException ex) when (attempt < retry.MaxAttempts)
            {
                Interlocked.Increment(ref _retryCount);
                var wait = WithJitter(backoff);
                _log($"{description}: network error ({ex.Message}) — " +
                     $"attempt {attempt}/{retry.MaxAttempts}, waiting {wait.TotalSeconds:F1}s.");
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                backoff = Math.Min(backoff * retry.BackoffMultiplier, retry.MaxBackoffSeconds);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested &&
                                                   attempt < retry.MaxAttempts)
            {
                // Timeout rather than user cancellation.
                Interlocked.Increment(ref _retryCount);
                var wait = WithJitter(backoff);
                _log($"{description}: request timed out after " +
                     $"{_config.Source.RequestTimeoutSeconds}s ({ex.Message}) — " +
                     $"attempt {attempt}/{retry.MaxAttempts}, waiting {wait.TotalSeconds:F1}s.");
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                backoff = Math.Min(backoff * retry.BackoffMultiplier, retry.MaxBackoffSeconds);
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    private async Task ApplyStandardHeadersAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Mandatory per the spec.
        request.Headers.TryAddWithoutValidation("API-VERSION", _config.Aprimo.ApiVersion);

        // Flat JSON, not HAL.
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(_config.Aprimo.Languages))
            request.Headers.TryAddWithoutValidation("languages", _config.Aprimo.Languages);
    }

    private TimeSpan ResolveDelay(HttpResponseMessage response, double backoffSeconds, RetryConfig retry)
    {
        if (retry.RespectRetryAfter && response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
                return Cap(delta, retry);

            if (retryAfter.Date is { } date)
            {
                var until = date - DateTimeOffset.UtcNow;
                if (until > TimeSpan.Zero) return Cap(until, retry);
            }
        }

        return WithJitter(backoffSeconds);
    }

    private static TimeSpan Cap(TimeSpan value, RetryConfig retry) =>
        value > TimeSpan.FromSeconds(retry.MaxBackoffSeconds)
            ? TimeSpan.FromSeconds(retry.MaxBackoffSeconds)
            : value;

    /// <summary>Full jitter, so parallel workers do not resynchronise after a failure.</summary>
    private TimeSpan WithJitter(double seconds)
    {
        double factor;
        lock (_jitter) factor = 0.5 + _jitter.NextDouble() * 0.5;
        return TimeSpan.FromSeconds(seconds * factor);
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests            // 429
                or HttpStatusCode.RequestTimeout            // 408
                or HttpStatusCode.InternalServerError       // 500
                or HttpStatusCode.BadGateway                // 502
                or HttpStatusCode.ServiceUnavailable        // 503
                or HttpStatusCode.GatewayTimeout;           // 504

    private static string ExplainStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized =>
            "Token renewal did not help. Confirm the registration grants scope 'api' and is enabled.",
        HttpStatusCode.Forbidden =>
            "Authenticated but not authorised. The registration's user may lack read permission on these records.",
        HttpStatusCode.NotFound =>
            "Endpoint or resource not found. Check Aprimo.ApiBaseUrl ends with /api/core and the tenant is correct.",
        HttpStatusCode.BadRequest =>
            "The API rejected the request — most often a malformed search expression, sort field, or page size > 1000.",
        _ => string.Empty
    };

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? "(empty)"
                 : body.Length <= 1000 ? body
                 : body.Substring(0, 1000) + "…";
        }
        catch (Exception ex)
        {
            return $"(could not read body: {ex.Message})";
        }
    }

    private static string Preview(byte[] payload, int max)
    {
        var length = Math.Min(payload.Length, max);
        return length == 0 ? "(empty)" : Encoding.UTF8.GetString(payload, 0, length);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class ApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public ApiException(string message, HttpStatusCode? statusCode = null) : base(message)
        => StatusCode = statusCode;
}
