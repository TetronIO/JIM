// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace JIM.Web.Extensions.Api;

/// <summary>
/// Declares how the published OpenAPI document says a caller authenticates: SSO through OAuth2, or an API key. Both
/// are bearer tokens in the <c>Authorization</c> header. Lives outside <c>Program.cs</c> so the serialised result can
/// be unit tested.
/// </summary>
public static class OpenApiSecurityConfiguration
{
    /// <summary>
    /// The OpenAPI name of the SSO (OAuth2 authorisation code) scheme.
    /// </summary>
    public const string OAuth2SchemeName = "OAuth2";

    /// <summary>
    /// The OpenAPI name of the API key scheme.
    /// </summary>
    public const string ApiKeySchemeName = "ApiKey";

    /// <summary>
    /// Adds both security schemes to the document, and a document-wide requirement that either one is used.
    /// </summary>
    /// <param name="document">The document being generated.</param>
    /// <param name="authorizationUrl">The identity provider's authorisation endpoint.</param>
    /// <param name="tokenUrl">The identity provider's token endpoint.</param>
    /// <param name="apiScope">The scope an access token must carry to call the JIM API.</param>
    public static void Apply(OpenApiDocument document, Uri authorizationUrl, Uri tokenUrl, string apiScope)
    {
        ArgumentNullException.ThrowIfNull(document);

        // OAuth2 security scheme for SSO authentication
        var oauth2Scheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = authorizationUrl,
                    TokenUrl = tokenUrl,
                    Scopes = new Dictionary<string, string>
                    {
                        { "openid", "OpenID Connect" },
                        { apiScope, "Access JIM API" }
                    }
                }
            }
        };

        // API keys are bearer tokens too (RFC 6750), sent as "Authorization: Bearer jim_ak_...". Declared as an HTTP
        // bearer scheme so OpenAPI tooling sends them the same way.
        var apiKeyScheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "API key for non-interactive authentication, sent as a bearer token: " +
                          "Authorization: Bearer jim_ak_<random>"
        };

        var components = document.Components ??= new OpenApiComponents();
        var schemes = components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        schemes.Add(OAuth2SchemeName, oauth2Scheme);
        schemes.Add(ApiKeySchemeName, apiKeyScheme);

        // Either method authenticates a request. Separate requirement objects are alternatives (OR); schemes listed
        // inside one requirement object would all be required together (AND). Each reference is bound to the document
        // that holds its scheme: an unbound reference cannot resolve, and the serialiser silently drops it, leaving
        // an empty requirement that OpenAPI reads as "no authentication needed".
        var security = document.Security ??= [];
        security.Add(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecuritySchemeReference(OAuth2SchemeName, document),
                new List<string> { "openid", apiScope }
            }
        });
        security.Add(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecuritySchemeReference(ApiKeySchemeName, document),
                new List<string>()
            }
        });
    }

    /// <summary>
    /// Publishes an anonymous endpoint (<c>[AllowAnonymous]</c>: the health probes, version, and the auth
    /// configuration a client reads before it can obtain a token) as needing no authentication. An empty
    /// operation-level <c>security</c> overrides the document-wide requirement; without it, the endpoint inherits
    /// that requirement and tooling that honours the document demands a credential the endpoint never asks for.
    /// Every other operation inherits the document's requirement and is left untouched.
    /// </summary>
    /// <param name="operation">The operation being generated.</param>
    /// <param name="endpointMetadata">The endpoint's metadata, as the authorisation middleware sees it.</param>
    public static void ApplyToOperation(OpenApiOperation operation, IList<object> endpointMetadata)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(endpointMetadata);

        if (endpointMetadata.OfType<IAllowAnonymous>().Any())
            operation.Security = [];
    }
}
