// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Serilog;

namespace JIM.Web.Authentication;

/// <summary>
/// Lets <c>/api</c> accept the first token signed by a key the identity provider introduced without publishing it first
/// (#2442). On its own the configuration manager only starts a key set refresh when it meets such a token, in the
/// background, and refuses the token; this fetches the key set, waits for it, and validates the token against it.
/// <para>
/// It fetches at most once per the scheme's <see cref="JwtBearerOptions.RefreshInterval"/>, and requests arriving
/// meanwhile use that fetch, so a stream of tokens naming made-up keys cannot make JIM fetch the key set on every
/// request. IdentityModel's own switch for a blocking refresh is not used: it makes every request wait while the key
/// set is fetched, including when the identity provider cannot be reached, which #2363 relies on not happening.
/// </para>
/// </summary>
public sealed class BearerSigningKeyRecovery
{
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OpenIdConnectConfiguration? _fetched;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    /// <param name="time">The clock the fetch interval is measured on.</param>
    public BearerSigningKeyRecovery(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Validates a token whose signing key JIM did not hold against the identity provider's current key set.
    /// </summary>
    /// <returns>The successful validation, or null when the token is still refused or the key set could not be fetched.</returns>
    public async Task<TokenValidationResult?> TryValidateAsync(string token, JwtBearerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(options.MetadataAddress) || options.Backchannel == null)
            return null;

        var configuration = await GetRecentConfigurationAsync(options, cancellationToken);
        if (configuration == null)
            return null;

        // As JwtBearerHandler builds its parameters from the configuration, with the configuration manager taken out so
        // nothing falls back to the key set JIM held before.
        var parameters = options.TokenValidationParameters.Clone();
        parameters.ConfigurationManager = null;
        parameters.IssuerSigningKeys = configuration.SigningKeys;
        if (string.IsNullOrEmpty(parameters.ValidAudience) && !string.IsNullOrEmpty(options.Audience))
            parameters.ValidAudience = options.Audience;
        parameters.ValidIssuers = (parameters.ValidIssuers ?? []).Concat(DiscoveredIssuers.From(configuration)).ToArray();


        // Each token handler in turn, as JwtBearerHandler tries them.
        foreach (var handler in options.TokenHandlers)
        {
            var result = await handler.ValidateTokenAsync(token, parameters);
            if (result.IsValid)
                return result;
        }

        return null;
    }

    /// <summary>
    /// The identity provider's configuration, fetched now unless it was fetched less than the refresh interval ago.
    /// </summary>
    private async Task<OpenIdConnectConfiguration?> GetRecentConfigurationAsync(JwtBearerOptions options, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (now - _fetchedAt < options.RefreshInterval)
                return _fetched;

            _fetchedAt = now;
            try
            {
                var retriever = new HttpDocumentRetriever(options.Backchannel) { RequireHttps = options.RequireHttpsMetadata };
                _fetched = await OpenIdConnectConfigurationRetriever.GetAsync(options.MetadataAddress, retriever, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The token is refused as it would have been; the identity provider trust check reports why it cannot
                // be reached.
                Log.Warning(ex, "Could not fetch the identity provider's signing keys for a token signed by a key JIM has not seen");
                _fetched = null;
            }

            return _fetched;
        }
        finally
        {
            _gate.Release();
        }
    }
}
