// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace JIM.Web.Authentication;

/// <summary>
/// The issuers the identity provider's discovery document names for the tokens it issues (#2440).
/// <para>
/// Besides <c>issuer</c>, a discovery document may publish <c>access_token_issuer</c>: AD FS signs people in as
/// <c>https://{host}/adfs</c> but issues access tokens under its federation service identifier,
/// <c>http://{host}/adfs/services/trust</c>, and says so there. JIM accepts a bearer token from either, so AD FS needs no
/// <c>JIM_SSO_VALID_ISSUERS</c>. Any issuer the administrator configures is accepted as well.
/// </para>
/// </summary>
public static class DiscoveredIssuers
{
    /// <summary>The discovery document member naming the issuer of access tokens.</summary>
    public const string AccessTokenIssuerMember = "access_token_issuer";

    /// <summary>
    /// The issuers a discovery document names: <c>issuer</c>, and <c>access_token_issuer</c> when it publishes one.
    /// </summary>
    public static IEnumerable<string> From(BaseConfiguration? configuration)
    {
        if (!string.IsNullOrEmpty(configuration?.Issuer))
            yield return configuration.Issuer;

        if (configuration is OpenIdConnectConfiguration openIdConfiguration
            && openIdConfiguration.AdditionalData.TryGetValue(AccessTokenIssuerMember, out var value))
        {
            var accessTokenIssuer = value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null
            };
            if (!string.IsNullOrEmpty(accessTokenIssuer))
                yield return accessTokenIssuer;
        }
    }

    /// <summary>
    /// Accepts a token whose issuer is one the parameters name or one the discovery document names, and refuses any other.
    /// Matches as IdentityModel's own issuer check does: ordinally.
    /// </summary>
    public static string ValidateIssuer(string issuer, SecurityToken securityToken, TokenValidationParameters validationParameters,
        BaseConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(validationParameters);

        if (!validationParameters.ValidateIssuer)
            return issuer;

        if (!string.IsNullOrEmpty(issuer)
            && (string.Equals(validationParameters.ValidIssuer, issuer, StringComparison.Ordinal)
                || (validationParameters.ValidIssuers?.Contains(issuer, StringComparer.Ordinal) ?? false)
                || From(configuration).Contains(issuer, StringComparer.Ordinal)))
            return issuer;

        throw new SecurityTokenInvalidIssuerException($"The token's issuer '{issuer}' is not one JIM trusts.")
        {
            InvalidIssuer = issuer
        };
    }
}
