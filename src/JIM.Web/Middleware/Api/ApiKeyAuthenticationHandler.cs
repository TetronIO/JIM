// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using JIM.Application;
using JIM.Application.Utilities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Web.Authentication;
using JIM.Web.Extensions;
using JIM.Web.Scim;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Serilog;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// Authentication handler for API key authentication.
/// Validates API keys presented as bearer tokens: <c>Authorization: Bearer jim_ak_...</c>.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    /// <summary>
    /// The name of the authentication scheme.
    /// </summary>
    public const string SchemeName = "ApiKey";

    // IServiceScopeFactory, not IServiceProvider: authentication handlers are resolved from the request scope, so
    // an injected IServiceProvider is the request's own provider and is disposed the moment the response completes.
    // The fire-and-forget usage recording below outlives the request, and CreateScope() on the disposed request
    // provider throws ObjectDisposedException, silently dropping the write. IServiceScopeFactory is a root
    // singleton; scopes created from it survive the request. Failed authentications are recorded through
    // IFailedAuthenticationRecorder, which has the same property.
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFailedAuthenticationRecorder _failedAuthenticationRecorder;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IServiceScopeFactory serviceScopeFactory,
        IFailedAuthenticationRecorder failedAuthenticationRecorder)
        : base(options, logger, encoder)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _failedAuthenticationRecorder = failedAuthenticationRecorder;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // An API key arrives as a bearer token (RFC 6750), exactly as an SSO access token does; the key prefix is what
        // tells the two apart. Anything else (no Authorization header, another scheme, a JWT) is not this handler's to
        // judge, so it returns NoResult and leaves the request to the other schemes.
        if (!TryReadApiKey(Request, out var providedKey))
        {
            Log.Debug("ApiKeyAuthenticationHandler: No API key bearer token on path {Path}", Request.Path);
            return AuthenticateResult.NoResult();
        }

        // ForwardedHeaders middleware (when configured) rewrites RemoteIpAddress to the real client address even
        // behind a reverse proxy, so this is proxy-truthful for free; see docs/administration/security-headers.md.
        var clientIp = Context.Connection.RemoteIpAddress.ToNormalisedString();

        try
        {
            // Create a scope to get a fresh DbContext for this authentication operation
            // This prevents DbContext threading issues when the controller also uses the same context
            using var scope = _serviceScopeFactory.CreateScope();
            var jim = scope.ServiceProvider.GetRequiredService<JimApplication>();

            // Hash the provided key to look it up
            var keyHash = ApiKeyMaterial.Hash(providedKey);
            var apiKey = await jim.Security.GetApiKeyByHashAsync(keyHash);

            if (apiKey == null)
            {
                var unknownKeyPrefix = providedKey.Length >= 12 ? providedKey[..12] : providedKey;
                Log.Warning("ApiKeyAuthenticationHandler: API key not found (prefix: {KeyPrefix})", unknownKeyPrefix);
                RecordFailedAuthenticationEvent("API key not found", unknownKeyPrefix, clientIp);
                return AuthenticateResult.Fail("Invalid API key");
            }

            Log.Debug("ApiKeyAuthenticationHandler: Found key '{Name}', IsEnabled={IsEnabled}, Roles={RoleCount}",
                apiKey.Name, apiKey.IsEnabled, apiKey.Roles.Count);

            // Check if the key is enabled
            if (!apiKey.IsEnabled)
            {
                Log.Warning("ApiKeyAuthenticationHandler: API key is disabled (prefix: {KeyPrefix})", apiKey.KeyPrefix);
                RecordFailedAuthenticationEvent("API key is disabled", apiKey.KeyPrefix, clientIp);
                return AuthenticateResult.Fail("API key is disabled");
            }

            // Check if the key has expired
            if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt.Value < DateTime.UtcNow)
            {
                Log.Warning("ApiKeyAuthenticationHandler: API key has expired (prefix: {KeyPrefix})", apiKey.KeyPrefix);
                RecordFailedAuthenticationEvent("API key has expired", apiKey.KeyPrefix, clientIp);
                return AuthenticateResult.Fail("API key has expired");
            }

            // A scoped key is a credential only on the endpoint it was minted for; anywhere else it opens nothing, so it
            // fails here rather than relying on every policy downstream to refuse it. Which Connected System it may
            // reach is the SCIM endpoint's own check.
            if (apiKey.Scope != ApiKeyScope.None && !ScimRoutes.IsScimRequest(Request))
            {
                Log.Warning("ApiKeyAuthenticationHandler: Scoped API key used outside the SCIM endpoint (prefix: {KeyPrefix})", apiKey.KeyPrefix);
                RecordFailedAuthenticationEvent("API key is scoped to the SCIM endpoint", apiKey.KeyPrefix, clientIp);
                return AuthenticateResult.Fail("API key is scoped to the SCIM endpoint");
            }

            // Record usage (fire and forget - don't block authentication)
            var ipAddress = clientIp;
            var apiKeyId = apiKey.Id;
            _ = Task.Run(async () =>
            {
                try
                {
                    // Create a new scope for the background task since the original scope will be disposed
                    using var usageScope = _serviceScopeFactory.CreateScope();
                    var usageJim = usageScope.ServiceProvider.GetRequiredService<JimApplication>();
                    await usageJim.Security.RecordApiKeyUsageAsync(apiKeyId, ipAddress);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "ApiKeyAuthenticationHandler: Failed to record API key usage");
                }
            });

            // Build claims for the authenticated API key
            var claims = BuildApiKeyClaims(apiKey);

            var identity = new ClaimsIdentity(claims, SchemeName, nameType: ClaimTypes.Name, roleType: Constants.BuiltInRoles.RoleClaimType);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            var rolesList = string.Join(", ", apiKey.Roles.Select(r => r.Name));
            Log.Debug("ApiKeyAuthenticationHandler: Successfully authenticated API key '{KeyName}' (prefix: {KeyPrefix}) with roles: {Roles}",
                apiKey.Name, apiKey.KeyPrefix, rolesList);

            return AuthenticateResult.Success(ticket);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ApiKeyAuthenticationHandler: Error during authentication");
            RecordFailedAuthenticationEvent("Authentication error", apiKeyPrefix: null, clientIp);
            return AuthenticateResult.Fail("Authentication error");
        }
    }

    /// <summary>
    /// Reads an API key from the request: a bearer token carrying the key prefix, or on <c>/scim</c> only, the bare
    /// key with no scheme, because some SCIM clients send the configured header value verbatim (D3).
    /// </summary>
    private static bool TryReadApiKey(HttpRequest request, out string key)
    {
        if (BearerToken.TryRead(request, out key) && BearerToken.IsApiKey(key))
            return true;

        var values = request.Headers.Authorization;
        key = values.Count == 1 ? values[0]?.Trim() ?? string.Empty : string.Empty;
        if (ScimRoutes.IsScimRequest(request) && BearerToken.IsApiKey(key) && !key.Contains(' ', StringComparison.Ordinal))
            return true;

        key = string.Empty;
        return false;
    }

    /// <summary>
    /// Records a failed API key authentication in the security audit log, off the request path: authentication must
    /// never be slowed or destabilised by audit capture.
    /// </summary>
    private void RecordFailedAuthenticationEvent(string reason, string? apiKeyPrefix, string? clientIp) =>
        _ = _failedAuthenticationRecorder.RecordAsync(FailedAuthenticationEventNames.ApiKey, reason, apiKeyPrefix, clientIp);

    /// <summary>
    /// Builds the claim set for an authenticated API key principal. Extracted as an internal static so the claim
    /// composition (identity claims, role claims, the virtual "User" role, the infrastructure-key marker, and a scoped
    /// key's scope in place of all roles) can
    /// be unit tested without spinning up the ASP.NET Core authentication pipeline.
    /// </summary>
    /// <param name="apiKey">The validated API key entity, with its <see cref="ApiKey.Roles"/> loaded.</param>
    public static List<Claim> BuildApiKeyClaims(ApiKey apiKey)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, apiKey.Id.ToString()),
            new(ClaimTypes.Name, apiKey.Name),
            new("auth_method", "api_key"),
            new("key_prefix", apiKey.KeyPrefix)
        };

        // A scoped key carries its permission and target instead of roles, not even the virtual User role, so no role
        // check anywhere in JIM can admit it, whatever roles the stored key might wrongly hold.
        if (ApiKeyScopeNames.For(apiKey.Scope) is { } scopeName)
        {
            claims.Add(new Claim(Constants.BuiltInClaims.KeyScope, scopeName));
            if (apiKey.ScopeConnectedSystemId is { } connectedSystemId)
                claims.Add(new Claim(Constants.BuiltInClaims.KeyScopeConnectedSystemId,
                    connectedSystemId.ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer32));
            return claims;
        }

        // Role claims from the API key's assigned roles.
        claims.AddRange(apiKey.Roles.Select(role => new Claim(Constants.BuiltInRoles.RoleClaimType, role.Name)));

        // The virtual "User" role for basic access (consistent with SSO auth).
        claims.Add(new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User));

        // Infrastructure keys are trusted backend automation; mark them so the rate limiter can exempt them
        // (see RateLimitPartitionResolver). Only attached for infrastructure keys, so an ordinary key is never exempt.
        if (apiKey.IsInfrastructureKey)
            claims.Add(new Claim(Constants.BuiltInClaims.IsInfrastructureKey, "true"));

        return claims;
    }
}

/// <summary>
/// Options for API key authentication.
/// </summary>
public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Extension methods for registering API key authentication.
/// </summary>
public static class ApiKeyAuthenticationExtensions
{
    /// <summary>
    /// Adds API key authentication to the authentication builder.
    /// </summary>
    public static AuthenticationBuilder AddApiKeyAuthentication(this AuthenticationBuilder builder)
    {
        return builder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationHandler.SchemeName, null);
    }
}
