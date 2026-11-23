// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using JIM.Models.Localisation;
using Serilog;

namespace JIM.Connectors.Http;

/// <summary>
/// OAuth 2.0 Client Credentials grant (RFC 6749 section 4.4), the usual machine-to-machine method for
/// provisioning APIs. Tokens are cached and refreshed shortly before they lapse.
/// </summary>
public class OAuthClientCredentialsAuthentication : IHttpAuthenticationStrategy
{
    /// <summary>
    /// How far ahead of stated expiry a token is refreshed. Without a margin, a token acquired with one
    /// second remaining would reach the provider already expired.
    /// </summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    private readonly HttpClient _tokenClient;
    private readonly Uri _tokenEndpoint;
    private readonly string _clientId;
    private readonly IOAuthClientCredential _clientCredential;
    private readonly string? _scope;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<HttpStatusCode, string?, string>? _describeFailure;
    private readonly Func<HttpStatusCode, string?, JimMessage>? _describeFailureMessage;
    private readonly OAuthStillBeingAppliedRetry? _stillBeingApplied;
    private readonly ILogger? _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // One acquisition at a time: parallel export means many callers arrive together, and without this
    // they would each fire an identical token request at the authorisation server.
    private readonly SemaphoreSlim _acquisitionLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset? _cachedTokenExpiresAt;

    /// <summary>Authenticates the client with a client secret sent in the request body (client_secret_post).</summary>
    /// <param name="tokenClient">Client used solely for token acquisition; carries the connector's TLS configuration.</param>
    /// <param name="tokenEndpoint">The authorisation server's token endpoint.</param>
    /// <param name="utcNow">Clock seam, so expiry behaviour is testable without waiting.</param>
    public OAuthClientCredentialsAuthentication(
        HttpClient tokenClient,
        Uri tokenEndpoint,
        string clientId,
        string clientSecret,
        string? scope = null,
        Func<DateTimeOffset>? utcNow = null)
        : this(tokenClient, tokenEndpoint, clientId, new OAuthClientSecretCredential(clientSecret ?? throw new ArgumentNullException(nameof(clientSecret))), scope, utcNow)
    {
    }

    /// <summary>Authenticates the client with any <see cref="IOAuthClientCredential"/>, such as a signed assertion.</summary>
    /// <param name="tokenClient">Client used solely for token acquisition; carries the connector's TLS configuration.</param>
    /// <param name="tokenEndpoint">The authorisation server's token endpoint.</param>
    /// <param name="utcNow">Clock seam, so expiry behaviour is testable without waiting.</param>
    /// <param name="describeFailure">
    /// Turns a refused token request (its status and body) into the message an administrator reads, for an authorisation
    /// server whose error responses say more than the status does. It must not echo the body: authorisation servers
    /// repeat request parameters in error responses, and the message reaches Activity errors and logs. When null, a
    /// generic message naming the status is used.
    /// </param>
    /// <param name="stillBeingApplied">
    /// Which refusals mean the authorisation server has not yet applied a newly added credential, and how long to wait
    /// before asking again; a refusal still of that kind after the last wait is reported like any other. When null, no
    /// refusal is retried.
    /// </param>
    /// <param name="delay">Waiting seam, so the retry is testable without waiting.</param>
    /// <param name="logger">Where each wait for a credential still being applied is logged; nothing is logged when null.</param>
    public OAuthClientCredentialsAuthentication(
        HttpClient tokenClient,
        Uri tokenEndpoint,
        string clientId,
        IOAuthClientCredential clientCredential,
        string? scope = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<HttpStatusCode, string?, string>? describeFailure = null,
        OAuthStillBeingAppliedRetry? stillBeingApplied = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? logger = null,
        Func<HttpStatusCode, string?, JimMessage>? describeFailureMessage = null)
    {
        ArgumentNullException.ThrowIfNull(tokenClient);
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        // The credential travels in the request body, so this class refuses a cleartext endpoint whatever
        // constructs it. The URI is not echoed: it may carry credentials of its own.
        if (!HttpTransportSecurity.IsPermitted(tokenEndpoint))
            throw new ArgumentException("The token endpoint must use HTTPS, or HTTP to a loopback address only.", nameof(tokenEndpoint));
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("A client ID is required.", nameof(clientId));
        ArgumentNullException.ThrowIfNull(clientCredential);

        _tokenClient = tokenClient;
        _tokenEndpoint = tokenEndpoint;
        _clientId = clientId;
        _clientCredential = clientCredential;
        _scope = string.IsNullOrWhiteSpace(scope) ? null : scope;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _describeFailure = describeFailure;
        _describeFailureMessage = describeFailureMessage;
        _stillBeingApplied = stillBeingApplied;
        _delay = delay ?? Task.Delay;
        _logger = logger;
    }

    public async Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void InvalidateCachedCredentials()
    {
        // Assigning under the lock is unnecessary: a caller mid-acquisition will overwrite these anyway,
        // and any caller that already read the old token is committed to its in-flight request.
        _cachedToken = null;
        _cachedTokenExpiresAt = null;
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetCachedToken(out var cached))
            return cached;

        await _acquisitionLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the lock: while queuing, another caller may have acquired a token, in
            // which case this caller must not fire a second identical request.
            if (TryGetCachedToken(out cached))
                return cached;

            var token = await RequestTokenAsync(cancellationToken);
            _cachedToken = token.AccessToken;

            // A response without expires_in leaves the lifetime unknown. Treating it as long-lived risks
            // silent 401s mid-run, so the token is used once and re-acquired next time.
            _cachedTokenExpiresAt = token.ExpiresInSeconds.HasValue
                ? _utcNow().AddSeconds(token.ExpiresInSeconds.Value)
                : null;

            return token.AccessToken;
        }
        finally
        {
            _acquisitionLock.Release();
        }
    }

    private bool TryGetCachedToken(out string token)
    {
        var cached = _cachedToken;
        var expiresAt = _cachedTokenExpiresAt;

        if (cached != null && expiresAt.HasValue && _utcNow() + ExpiryMargin < expiresAt.Value)
        {
            token = cached;
            return true;
        }

        token = string.Empty;
        return false;
    }

    private async Task<OAuthTokenResponse> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var delays = _stillBeingApplied?.Delays ?? [];
        for (var attempt = 0; ; attempt++)
        {
            var (token, refusal) = await TryRequestTokenAsync(cancellationToken);
            if (token != null)
                return token;

            // A fresh request, with a freshly built credential, after each wait: the server that refused may be the only
            // one yet to apply the credential.
            if (attempt < delays.Count && _stillBeingApplied!.IsStillBeingApplied(refusal!.Value.Status, refusal.Value.Body))
            {
                // The described refusal, never the body: authorisation servers echo request parameters in theirs.
                _logger?.Warning("The token endpoint refused the credential, which it may still be applying; asking again in {DelaySeconds}s (wait {Wait} of {Waits}). {Refusal}",
                    delays[attempt].TotalSeconds, attempt + 1, delays.Count, DescribeRefusal(refusal.Value.Status, refusal.Value.ReasonPhrase, refusal.Value.Body).Message);
                await _delay(delays[attempt], cancellationToken);
                continue;
            }

            throw DescribeRefusal(refusal!.Value.Status, refusal.Value.ReasonPhrase, refusal.Value.Body);
        }
    }

    private HttpAuthenticationException DescribeRefusal(HttpStatusCode status, string? reasonPhrase, string? body)
    {
        if (_describeFailureMessage != null)
            return new HttpAuthenticationException(_describeFailureMessage(status, body));

        if (_describeFailure != null)
            return new HttpAuthenticationException(_describeFailure(status, body));

        // The body is deliberately not included: authorisation servers echo request parameters
        // in error responses, and this text reaches Activity errors and logs.
        return new HttpAuthenticationException(
            $"The OAuth 2.0 token endpoint returned HTTP {(int)status} ({reasonPhrase}). " +
            "Check the Client ID, Client Secret, OAuth Scope and Token Endpoint URL settings.");
    }

    /// <returns>The token, or the refusal's status, reason and body (read only where something will look at it).</returns>
    private async Task<(OAuthTokenResponse? Token, (HttpStatusCode Status, string? ReasonPhrase, string? Body)? Refusal)> TryRequestTokenAsync(
        CancellationToken cancellationToken)
    {
        // Client authentication travels in the form body. If a provider mandates HTTP Basic client
        // authentication instead, that becomes another IOAuthClientCredential rather than a change here.
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", _clientId)
        };
        form.AddRange(await _clientCredential.GetTokenRequestParametersAsync(_tokenEndpoint, _utcNow(), cancellationToken));

        if (_scope != null)
            form.Add(new KeyValuePair<string, string>("scope", _scope));

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint) { Content = new FormUrlEncodedContent(form) };

        HttpResponseMessage response;
        try
        {
            response = await _tokenClient.SendAsync(tokenRequest, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpAuthenticationException(
                $"Could not reach the OAuth 2.0 token endpoint at {_tokenEndpoint.GetLeftPart(UriPartial.Path)}.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = _describeFailure != null || _describeFailureMessage != null || _stillBeingApplied != null
                    ? await response.Content.ReadAsStringAsync(cancellationToken)
                    : null;
                return (null, (response.StatusCode, response.ReasonPhrase, errorBody));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            OAuthTokenResponse? token;
            try
            {
                token = JsonSerializer.Deserialize<OAuthTokenResponse>(body);
            }
            catch (JsonException ex)
            {
                throw new HttpAuthenticationException("The OAuth 2.0 token endpoint returned a response that is not valid JSON.", ex);
            }

            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                throw new HttpAuthenticationException("The OAuth 2.0 token endpoint response did not contain an access_token.");

            return (token, null);
        }
    }

    /// <summary>
    /// The subset of RFC 6749 section 5.1 that the connector needs. Property names are snake_case per
    /// the specification, so they are mapped explicitly.
    /// </summary>
    private sealed class OAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int? ExpiresInSeconds { get; set; }
    }
}
