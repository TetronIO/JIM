// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JIM.Connectors.SCIM.Authentication;
using JIM.Scim.Messages;
using JIM.Scim.Serialisation;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Sends SCIM 2.0 requests to a service provider, applying authentication, retry and throttling.
/// <para>
/// No external SCIM SDK is involved: this is <see cref="HttpClient"/> plus <c>System.Text.Json</c>, which
/// keeps the connector's supply-chain surface and air-gap posture unchanged.
/// </para>
/// </summary>
public class ScimHttpClient : IDisposable
{
    /// <summary>
    /// The SCIM media type (RFC 7644 section 3.1). Some providers reject plain <c>application/json</c>.
    /// </summary>
    private const string ScimMediaType = "application/scim+json";

    /// <summary>
    /// Pause proactively once the provider says this many calls remain in the window, leaving headroom
    /// for concurrent export workers sharing the same allowance.
    /// </summary>
    private const int ThrottleRemainingThreshold = 1;

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUrl;
    private readonly IScimAuthenticationStrategy _authentication;
    private readonly ScimRetryPolicy _retryPolicy;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _utcNow;

    // Set when a provider reports its allowance is nearly spent, and consumed by the next request.
    private TimeSpan? _pendingThrottlePause;

    /// <param name="httpClient">Configured with the connector's timeout, TLS policy and certificate validation.</param>
    /// <param name="baseUrl">The service provider's base URL, which may carry a path prefix.</param>
    /// <param name="delay">Wait seam, so retry and throttle behaviour is testable without real delays.</param>
    /// <param name="utcNow">Clock seam, used to resolve an HTTP-date <c>Retry-After</c>.</param>
    public ScimHttpClient(
        HttpClient httpClient,
        Uri baseUrl,
        IScimAuthenticationStrategy authentication,
        ScimRetryPolicy retryPolicy,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _baseUrl = EnsureTrailingSlash(baseUrl);
        _authentication = authentication;
        _retryPolicy = retryPolicy;
        _logger = logger;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<T?> GetAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        return (await GetWithMetadataAsync<T>(relativePath, cancellationToken)).Body;
    }

    /// <summary>
    /// Sends a GET and returns the body alongside the response metadata the connector needs, which
    /// today means the provider's clock for delta import to watermark against.
    /// </summary>
    public async Task<ScimResponse<T>> GetWithMetadataAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, relativePath, requestBody: null, cancellationToken);
        var body = await DeserialiseAsync<T>(response, cancellationToken);
        return new ScimResponse<T>(body, response.Headers.Date, response.Headers.ETag?.ToString());
    }

    public async Task<T?> PostAsync<T>(string relativePath, object body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, relativePath, body, cancellationToken);
        return await DeserialiseAsync<T>(response, cancellationToken);
    }

    /// <param name="ifMatch">
    /// The entity tag the resource carried when JIM last read it. Sent as <c>If-Match</c> so the
    /// provider refuses the write if the resource has moved on since, rather than letting JIM overwrite
    /// a change it never saw.
    /// </param>
    public async Task<T?> PutAsync<T>(string relativePath, object body, CancellationToken cancellationToken, string? ifMatch = null)
    {
        using var response = await SendAsync(HttpMethod.Put, relativePath, body, cancellationToken, ifMatch);
        return await DeserialiseAsync<T>(response, cancellationToken);
    }

    /// <inheritdoc cref="PutAsync{T}"/>
    public async Task<T?> PatchAsync<T>(string relativePath, object body, CancellationToken cancellationToken, string? ifMatch = null)
    {
        using var response = await SendAsync(HttpMethod.Patch, relativePath, body, cancellationToken, ifMatch);
        return await DeserialiseAsync<T>(response, cancellationToken);
    }

    public async Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, relativePath, requestBody: null, cancellationToken);
    }

    /// <summary>
    /// Sends a request, retrying transient failures per the policy. The returned response is successful;
    /// anything else has already been turned into a <see cref="ScimRequestException"/>.
    /// </summary>
    /// <exception cref="ScimRequestException">The request failed and is not retryable, or retries are spent.</exception>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        object? requestBody,
        CancellationToken cancellationToken,
        string? ifMatch = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var requestUri = new Uri(_baseUrl, relativePath);
        var attempt = 0;
        var credentialAlreadyRefreshed = false;

        while (true)
        {
            attempt++;
            await ApplyPendingThrottlePauseAsync(cancellationToken);

            // A fresh HttpRequestMessage per attempt: a sent message cannot be reused, and re-applying
            // authentication is what lets a refreshed credential take effect on the retry.
            using var request = BuildRequest(method, requestUri, requestBody, ifMatch);
            await _authentication.ApplyAsync(request, cancellationToken);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException)
            {
                // Cancellation is not retryable, so the policy declines it and the original exception
                // propagates, keeping an aborting run responsive.
                cancellationToken.ThrowIfCancellationRequested();

                var exceptionDecision = _retryPolicy.EvaluateException(ex, attempt);
                if (!exceptionDecision.ShouldRetry)
                    throw new ScimRequestException(
                        $"The SCIM request to {LogSanitiser.Sanitise(requestUri.AbsolutePath)} failed: {exceptionDecision.Reason}",
                        HttpStatusCode.ServiceUnavailable, ex);

                _logger.Warning(ex, "SCIM request attempt {Attempt} failed. {Reason} Waiting {DelayMs}ms",
                    attempt, exceptionDecision.Reason, exceptionDecision.Delay.TotalMilliseconds);
                await _delay(exceptionDecision.Delay, cancellationToken);
                continue;
            }

            RecordThrottleHints(response);

            if (response.IsSuccessStatusCode)
                return response;

            // A 401 can mean a token was revoked ahead of its advertised expiry, so discard the cached
            // credential and try once more. Only once: a wrong secret must not loop.
            if (response.StatusCode == HttpStatusCode.Unauthorized && !credentialAlreadyRefreshed)
            {
                credentialAlreadyRefreshed = true;
                response.Dispose();
                _logger.Warning("The SCIM endpoint rejected the credential (401). Discarding it and retrying once.");
                _authentication.InvalidateCachedCredentials();
                continue;
            }

            var decision = _retryPolicy.EvaluateResponse(response, attempt, _utcNow());
            if (!decision.ShouldRetry)
            {
                var error = await TryReadScimErrorAsync(response, cancellationToken);
                var statusCode = response.StatusCode;
                response.Dispose();
                throw BuildRequestException(method, requestUri, statusCode, error, decision.Reason);
            }

            _logger.Warning("SCIM request attempt {Attempt} returned HTTP {StatusCode}. {Reason} Waiting {DelayMs}ms",
                attempt, (int)response.StatusCode, decision.Reason, decision.Delay.TotalMilliseconds);
            response.Dispose();
            await _delay(decision.Delay, cancellationToken);
        }
    }

    /// <summary>
    /// Base URLs routinely carry a path prefix (for example <c>/scim/v2</c>). Without a trailing slash,
    /// <see cref="Uri"/> composition replaces the last path segment instead of appending to it.
    /// </summary>
    private static Uri EnsureTrailingSlash(Uri baseUrl)
    {
        return baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, Uri requestUri, object? requestBody, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ScimMediaType));

        // Weak tags are what SCIM providers issue (RFC 7644 section 3.14), and TryParseAdd tolerates a
        // malformed one rather than failing the write over a header JIM did not author.
        if (!string.IsNullOrWhiteSpace(ifMatch))
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        if (requestBody != null)
        {
            var json = JsonSerializer.Serialize(requestBody, ScimJson.Options);
            request.Content = new StringContent(json, Encoding.UTF8, ScimMediaType);
        }

        return request;
    }

    private async Task ApplyPendingThrottlePauseAsync(CancellationToken cancellationToken)
    {
        var pause = _pendingThrottlePause;
        if (!pause.HasValue)
            return;

        _pendingThrottlePause = null;
        _logger.Debug("Pausing {DelayMs}ms before the next SCIM request; the provider reports its rate-limit allowance is nearly spent.",
            pause.Value.TotalMilliseconds);
        await _delay(pause.Value, cancellationToken);
    }

    private void RecordThrottleHints(HttpResponseMessage response)
    {
        var hints = ScimThrottleHints.Read(response, _utcNow());
        if (hints.HasHints)
            _pendingThrottlePause = hints.GetPauseBeforeNextRequest(ThrottleRemainingThreshold);
    }

    private static async Task<T?> DeserialiseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            return default;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(body, ScimJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ScimRequestException(
                "The SCIM endpoint returned a successful response whose body could not be parsed as JSON.",
                response.StatusCode, ex);
        }
    }

    /// <summary>
    /// Parses a SCIM error body, returning null when the response is not one. Providers behind gateways
    /// return HTML or plain text on failure, and that must not mask the status code.
    /// </summary>
    private static async Task<ScimError?> TryReadScimErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            var error = JsonSerializer.Deserialize<ScimError>(body, ScimJson.Options);

            // An empty JSON object deserialises without error but carries nothing worth reporting.
            return error != null && (error.Detail != null || error.ScimType != null || error.Status != null) ? error : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static ScimRequestException BuildRequestException(
        HttpMethod method,
        Uri requestUri,
        HttpStatusCode statusCode,
        ScimError? error,
        string reason)
    {
        var message = new StringBuilder()
            .Append($"The SCIM {method} request to {LogSanitiser.Sanitise(requestUri.AbsolutePath)} ")
            .Append($"failed with HTTP {(int)statusCode}. {reason}");

        // The provider's scimType is a protocol keyword and safe to include; detail is provider-authored
        // free text, so it is sanitised before it reaches an Activity error or a log.
        if (error?.ScimType != null)
            message.Append($" SCIM error type: {LogSanitiser.Sanitise(error.ScimType)}.");
        if (error?.Detail != null)
            message.Append($" Provider detail: {LogSanitiser.Sanitise(error.Detail)}");

        return new ScimRequestException(message.ToString(), statusCode, error);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
