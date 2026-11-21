// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JIM.Connectors.Http;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.Graph;

/// <summary>
/// Sends Microsoft Graph requests for one Connected System: authenticated, paced, retried, and marked low priority so
/// that under pressure Graph throttles JIM before the tenant's interactive applications.
/// </summary>
/// <remarks>
/// No Graph SDK is involved: this is <see cref="HttpClient"/> plus <c>System.Text.Json</c>, as for the SCIM Connector.
/// One instance serves a whole run and is disposed with it.
/// </remarks>
internal sealed class GraphHttpClient : IDisposable
{
    private const string JsonMediaType = "application/json";

    /// <summary>The longest Graph error message repeated in an exception, so an unexpected body cannot flood an Activity.</summary>
    private const int MaximumErrorMessageLength = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly GraphCloud _cloud;
    private readonly IHttpAuthenticationStrategy _authentication;
    private readonly HttpRetryPolicy _retryPolicy;
    private readonly GraphRequestPacer _pacer;
    private readonly ILogger _logger;
    private readonly IDisposable? _ownedCredential;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _utcNow;
    private int _throttledResponseCount;

    /// <param name="httpClient">Configured with the Connector's timeout, TLS policy and certificate validation; owned and disposed by this client.</param>
    /// <param name="ownedCredential">Disposed with this client: the certificate a client assertion signs with, where one is used.</param>
    /// <param name="delay">Wait seam, so retry behaviour is testable without real delays.</param>
    /// <param name="utcNow">Clock seam, used to resolve an HTTP-date <c>Retry-After</c>.</param>
    public GraphHttpClient(
        HttpClient httpClient,
        GraphCloud cloud,
        IHttpAuthenticationStrategy authentication,
        HttpRetryPolicy retryPolicy,
        GraphRequestPacer pacer,
        ILogger logger,
        IDisposable? ownedCredential = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(pacer);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _cloud = cloud;
        _authentication = authentication;
        _retryPolicy = retryPolicy;
        _pacer = pacer;
        _logger = logger;
        _ownedCredential = ownedCredential;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>How many 429 responses this client has received, so a run can report sustained throttling.</summary>
    public int ThrottledResponseCount => Volatile.Read(ref _throttledResponseCount);

    /// <summary>
    /// Sends a request, retrying transient failures and throttling per the policy. The returned response is successful;
    /// anything else has already become a <see cref="GraphRequestException"/>.
    /// </summary>
    /// <param name="pathOrUrl">
    /// A path relative to the cloud's v1.0 endpoint (<c>users?$select=id</c>), or an absolute URL Graph returned, such
    /// as an <c>@odata.nextLink</c>. An absolute URL must be on this cloud's Graph host over HTTPS.
    /// </param>
    /// <exception cref="GraphRequestException">The request failed and is not retryable, or retries are spent.</exception>
    /// <exception cref="HttpAuthenticationException">No access token could be acquired.</exception>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string pathOrUrl, object? body, GraphRequestOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrUrl);
        options ??= GraphRequestOptions.Default;

        var requestUri = ResolveRequestUri(pathOrUrl);
        var writes = options.Writes ?? (method == HttpMethod.Get ? 0 : 1);
        var attempt = 0;
        var credentialAlreadyRefreshed = false;

        while (true)
        {
            attempt++;
            await _pacer.WaitForWriteSlotsAsync(writes, cancellationToken);

            // A fresh message per attempt: a sent message cannot be reused, and re-applying authentication is what
            // lets a refreshed token take effect on the retry.
            using var request = BuildRequest(method, requestUri, body, options);
            await _authentication.ApplyAsync(request, cancellationToken);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException)
            {
                // Cancellation is not retryable, so the policy declines it and the original exception propagates.
                cancellationToken.ThrowIfCancellationRequested();

                var exceptionDecision = _retryPolicy.EvaluateException(ex, attempt);
                if (!exceptionDecision.ShouldRetry)
                    throw new GraphRequestException(
                        $"The Microsoft Graph request to {LogSanitiser.Sanitise(requestUri.AbsolutePath)} failed: {exceptionDecision.Reason}",
                        HttpStatusCode.ServiceUnavailable, innerException: ex);

                _logger.Warning(ex, "Microsoft Graph request attempt {Attempt} failed. {Reason} Waiting {DelayMs}ms",
                    attempt, exceptionDecision.Reason, exceptionDecision.Delay.TotalMilliseconds);
                await _delay(exceptionDecision.Delay, cancellationToken);
                continue;
            }

            _pacer.ObserveThrottleLimitPercentage(ReadThrottleLimitPercentage(response));

            if (response.IsSuccessStatusCode)
                return response;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                Interlocked.Increment(ref _throttledResponseCount);

            // A 401 can mean a token was revoked ahead of its advertised expiry, so discard it and try once more.
            // Only once: a refused credential must not loop.
            if (response.StatusCode == HttpStatusCode.Unauthorized && !credentialAlreadyRefreshed)
            {
                credentialAlreadyRefreshed = true;
                response.Dispose();
                _logger.Warning("Microsoft Graph rejected the access token (401). Acquiring a new one and retrying once.");
                _authentication.InvalidateCachedCredentials();
                continue;
            }

            var decision = _retryPolicy.EvaluateResponse(response, attempt, _utcNow());
            if (!decision.ShouldRetry)
            {
                var exception = await BuildRequestExceptionAsync(method, requestUri, response, decision.Reason, cancellationToken);
                response.Dispose();
                throw exception;
            }

            _logger.Warning("Microsoft Graph request attempt {Attempt} returned HTTP {StatusCode}. {Reason} Waiting {DelayMs}ms",
                attempt, (int)response.StatusCode, decision.Reason, decision.Delay.TotalMilliseconds);
            response.Dispose();
            await _delay(decision.Delay, cancellationToken);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _ownedCredential?.Dispose();
    }

    /// <summary>
    /// The access token travels with every request, so an absolute URL is only followed when it is on this cloud's Graph
    /// host over HTTPS. Graph's own paging links always are; anything else is refused before a byte is sent.
    /// </summary>
    private Uri ResolveRequestUri(string pathOrUrl)
    {
        if (!Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute))
            return new Uri(_cloud.ApiBaseUrl, pathOrUrl.TrimStart('/'));

        if (absolute.Scheme == Uri.UriSchemeHttps && string.Equals(absolute.Host, _cloud.GraphHost.Host, StringComparison.OrdinalIgnoreCase) && absolute.IsDefaultPort)
            return absolute;

        throw new GraphRequestException(
            $"Refused to follow a link to {LogSanitiser.Sanitise(absolute.GetLeftPart(UriPartial.Authority))}, which is not Microsoft Graph for the {_cloud.Name} cloud.",
            HttpStatusCode.BadRequest);
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, Uri requestUri, object? body, GraphRequestOptions options)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));

        // Under pressure Graph throttles low-priority requests first. JIM's work is background work, so it yields to
        // the tenant's interactive applications. The header changes no limit.
        request.Headers.Add("x-ms-throttle-priority", "Low");

        // random-id: a correlation id for support, not a row id; Graph echoes it in its logs.
        request.Headers.Add("client-request-id", Guid.NewGuid().ToString());

        if (options.EventualConsistency)
            request.Headers.Add("ConsistencyLevel", "eventual");

        if (body != null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, JsonMediaType);

        return request;
    }

    private static double? ReadThrottleLimitPercentage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-ms-throttle-limit-percentage", out var values))
            return null;

        return double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage)
            ? percentage
            : null;
    }

    private async Task<GraphRequestException> BuildRequestExceptionAsync(
        HttpMethod method,
        Uri requestUri,
        HttpResponseMessage response,
        string reason,
        CancellationToken cancellationToken)
    {
        var (code, message) = await TryReadGraphErrorAsync(response, cancellationToken);
        var requestId = response.Headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() : null;
        var path = LogSanitiser.Sanitise(requestUri.AbsolutePath);

        var text = code != null
            ? $"Microsoft Graph refused {method} {path} with HTTP {(int)response.StatusCode} ({code}): {message}"
            : $"Microsoft Graph refused {method} {path} with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {reason}";

        return new GraphRequestException(text, response.StatusCode, code, requestId);
    }

    /// <summary>
    /// Reads Graph's error body (<c>{"error":{"code","message"}}</c>), returning nulls when the response is not one:
    /// gateways and proxies in front of Graph answer with HTML or plain text, and that must not mask the status.
    /// </summary>
    private static async Task<(string? Code, string? Message)> TryReadGraphErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return (null, null);

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("code", out var codeElement)
                || codeElement.ValueKind != JsonValueKind.String)
                return (null, null);

            var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;
            if (message is { Length: > MaximumErrorMessageLength })
                message = message[..MaximumErrorMessageLength] + "...";

            return (codeElement.GetString(), message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
