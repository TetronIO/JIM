using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using JIM.Application;
using JIM.Models.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Serilog;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// Authentication handler for API key authentication.
/// Validates API keys provided in the X-API-Key header.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    /// <summary>
    /// The name of the authentication scheme.
    /// </summary>
    public const string SchemeName = "ApiKey";

    /// <summary>
    /// The header name for the API key.
    /// </summary>
    public const string ApiKeyHeaderName = "X-API-Key";

    /// <summary>
    /// The prefix for API keys (e.g., jim_ak_).
    /// </summary>
    public const string ApiKeyPrefix = "jim_ak_";

    private readonly IServiceProvider _serviceProvider;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IServiceProvider serviceProvider)
        : base(options, logger, encoder)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Log.Debug("ApiKeyAuthenticationHandler: Checking for X-API-Key header on path {Path}", Request.Path);

        // Check if the X-API-Key header is present
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeader))
        {
            Log.Debug("ApiKeyAuthenticationHandler: No X-API-Key header found");
            // No API key header - let other authentication schemes handle it
            return AuthenticateResult.NoResult();
        }

        var providedKey = apiKeyHeader.ToString();

        // Validate the key format
        if (string.IsNullOrWhiteSpace(providedKey) || !providedKey.StartsWith(ApiKeyPrefix))
        {
            Log.Warning("ApiKeyAuthenticationHandler: Invalid API key format provided");
            return AuthenticateResult.Fail("Invalid API key format");
        }

        try
        {
            // Create a scope to get a fresh DbContext for this authentication operation
            // This prevents DbContext threading issues when the controller also uses the same context
            using var scope = _serviceProvider.CreateScope();
            var jim = scope.ServiceProvider.GetRequiredService<JimApplication>();

            // Hash the provided key to look it up
            var keyHash = HashApiKey(providedKey);
            var apiKey = await jim.Repository.ApiKeys.GetByHashAsync(keyHash);

            if (apiKey == null)
            {
                Log.Warning("ApiKeyAuthenticationHandler: API key not found (prefix: {KeyPrefix})",
                    providedKey.Length >= 12 ? providedKey[..12] : providedKey);
                return AuthenticateResult.Fail("Invalid API key");
            }

            Log.Debug("ApiKeyAuthenticationHandler: Found key '{Name}', IsEnabled={IsEnabled}, Roles={RoleCount}",
                apiKey.Name, apiKey.IsEnabled, apiKey.Roles.Count);

            // Check if the key is enabled
            if (!apiKey.IsEnabled)
            {
                Log.Warning("ApiKeyAuthenticationHandler: API key is disabled (prefix: {KeyPrefix})", apiKey.KeyPrefix);
                return AuthenticateResult.Fail("API key is disabled");
            }

            // Check if the key has expired
            if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt.Value < DateTime.UtcNow)
            {
                Log.Warning("ApiKeyAuthenticationHandler: API key has expired (prefix: {KeyPrefix})", apiKey.KeyPrefix);
                return AuthenticateResult.Fail("API key has expired");
            }

            // Record usage (fire and forget - don't block authentication)
            var ipAddress = Context.Connection.RemoteIpAddress?.ToString();
            var apiKeyId = apiKey.Id;
            _ = Task.Run(async () =>
            {
                try
                {
                    // Create a new scope for the background task since the original scope will be disposed
                    using var usageScope = _serviceProvider.CreateScope();
                    var usageJim = usageScope.ServiceProvider.GetRequiredService<JimApplication>();
                    await usageJim.Repository.ApiKeys.RecordUsageAsync(apiKeyId, ipAddress);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "ApiKeyAuthenticationHandler: Failed to record API key usage");
                }
            });

            // Build claims for the authenticated API key
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, apiKey.Id.ToString()),
                new(ClaimTypes.Name, apiKey.Name),
                new("auth_method", "api_key"),
                new("key_prefix", apiKey.KeyPrefix)
            };

            // Add role claims from the API key's assigned roles
            foreach (var role in apiKey.Roles)
            {
                claims.Add(new Claim(Constants.BuiltInRoles.RoleClaimType, role.Name));
            }

            // Add the virtual "User" role for basic access (consistent with SSO auth)
            claims.Add(new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User));

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            var rolesList = string.Join(", ", apiKey.Roles.Select(r => r.Name));
            Log.Information("ApiKeyAuthenticationHandler: Successfully authenticated API key '{KeyName}' (prefix: {KeyPrefix}) with roles: {Roles}",
                apiKey.Name, apiKey.KeyPrefix, rolesList);

            return AuthenticateResult.Success(ticket);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ApiKeyAuthenticationHandler: Error during authentication");
            return AuthenticateResult.Fail("Authentication error");
        }
    }

    /// <summary>
    /// Generates a new API key with the standard prefix.
    /// </summary>
    /// <returns>A tuple containing the full key and its prefix.</returns>
    public static (string FullKey, string KeyPrefix) GenerateApiKey()
    {
        // Generate 32 random bytes and convert to hex (64 chars)
        var randomBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        var randomPart = Convert.ToHexString(randomBytes).ToLowerInvariant();

        var fullKey = $"{ApiKeyPrefix}{randomPart}";
        var keyPrefix = fullKey[..12]; // jim_ak_ + first 4 chars of random part

        return (fullKey, keyPrefix);
    }

    /// <summary>
    /// Hashes an API key using SHA256.
    /// </summary>
    public static string HashApiKey(string apiKey)
    {
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
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
