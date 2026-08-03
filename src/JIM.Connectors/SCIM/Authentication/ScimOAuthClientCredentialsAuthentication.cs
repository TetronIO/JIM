// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JIM.Connectors.SCIM.Authentication;

/// <summary>
/// OAuth 2.0 Client Credentials grant (RFC 6749 section 4.4), the usual machine-to-machine method for
/// SCIM provisioning. Tokens are cached and refreshed shortly before they lapse.
/// </summary>
public class ScimOAuthClientCredentialsAuthentication : IScimAuthenticationStrategy
{
    /// <summary>
    /// How far ahead of stated expiry a token is refreshed. Without a margin, a token acquired with one
    /// second remaining would reach the provider already expired.
    /// </summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    private readonly HttpClient _tokenClient;
    private readonly Uri _tokenEndpoint;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string? _scope;
    private readonly Func<DateTimeOffset> _utcNow;

    // One acquisition at a time: parallel export means many callers arrive together, and without this
    // they would each fire an identical token request at the authorisation server.
    private readonly SemaphoreSlim _acquisitionLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset? _cachedTokenExpiresAt;

    /// <param name="tokenClient">Client used solely for token acquisition; carries the connector's TLS configuration.</param>
    /// <param name="tokenEndpoint">The authorisation server's token endpoint.</param>
    /// <param name="utcNow">Clock seam, so expiry behaviour is testable without waiting.</param>
    public ScimOAuthClientCredentialsAuthentication(
        HttpClient tokenClient,
        Uri tokenEndpoint,
        string clientId,
        string clientSecret,
        string? scope = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(tokenClient);
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("A client ID is required.", nameof(clientId));
        ArgumentNullException.ThrowIfNull(clientSecret);

        _tokenClient = tokenClient;
        _tokenEndpoint = tokenEndpoint;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _scope = string.IsNullOrWhiteSpace(scope) ? null : scope;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
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
        // client_secret_post (RFC 6749 section 2.3.1): credentials in the form body. This is the most
        // widely accepted form across SCIM providers; if a provider mandates HTTP Basic client
        // authentication instead, that becomes an additional setting rather than a change here.
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", _clientId),
            new("client_secret", _clientSecret)
        };

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
            throw new ScimAuthenticationException(
                $"Could not reach the OAuth 2.0 token endpoint at {_tokenEndpoint.GetLeftPart(UriPartial.Path)}.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The body is deliberately not included: authorisation servers echo request parameters
                // in error responses, and this text reaches Activity errors and logs.
                throw new ScimAuthenticationException(
                    $"The OAuth 2.0 token endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                    "Check the Client ID, Client Secret, OAuth Scope and Token Endpoint URL settings.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            OAuthTokenResponse? token;
            try
            {
                token = JsonSerializer.Deserialize<OAuthTokenResponse>(body);
            }
            catch (JsonException ex)
            {
                throw new ScimAuthenticationException("The OAuth 2.0 token endpoint returned a response that is not valid JSON.", ex);
            }

            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                throw new ScimAuthenticationException("The OAuth 2.0 token endpoint response did not contain an access_token.");

            return token;
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
