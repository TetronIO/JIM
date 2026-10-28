// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JIM.Web.Extensions.Api;

/// <summary>
/// The OpenAPI document a released image serves, written when the image was built (#2544). No identity provider
/// existed then, so its OAuth endpoints are placeholders and its API scope is the build's; this gives the document the
/// identity provider's real endpoints, and the scope Try It must ask for (#2594), as it is served, so the API
/// reference's Try It can sign in. The endpoints are read when the document is asked for, never at start-up, where an
/// identity provider that is down or not yet trusted would stop JIM starting (#2363). When they cannot be read, the
/// built endpoints are served.
/// </summary>
public sealed class StaticOpenApiDocument
{
    private const string OpenIdScope = "openid";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Served as JSON, never embedded in HTML, so characters such as '<', '&' and accented letters are kept as
        // written rather than escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly Func<CancellationToken, Task<(string AuthorizationEndpoint, string TokenEndpoint)?>> _readEndpoints;
    private readonly string? _apiScope;
    private string? _written;
    private Served? _served;

    /// <param name="path">Where the built document is.</param>
    /// <param name="readEndpoints">Reads the identity provider's endpoints, or null when it cannot.</param>
    /// <param name="apiScope">The scope Try It asks for JIM's API with (<c>PublicClientSignIn.ApiScope</c>), or null to
    /// serve the built one.</param>
    public StaticOpenApiDocument(string path, Func<CancellationToken, Task<(string AuthorizationEndpoint, string TokenEndpoint)?>> readEndpoints,
        string? apiScope = null)
    {
        _path = path;
        _readEndpoints = readEndpoints;
        _apiScope = apiScope;
    }

    /// <summary>
    /// The document to serve: the built document with the identity provider's endpoints and Try It's API scope, each
    /// left as built when it cannot be read or is not configured.
    /// </summary>
    public async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        var written = _written ??= await File.ReadAllTextAsync(_path, cancellationToken);
        var endpoints = await _readEndpoints(cancellationToken);

        // The endpoints rarely change, and the document is large, so the last one served is kept for as long as they hold.
        if (_served is { } served && served.Endpoints == endpoints)
            return served.Document;

        var document = WithSignIn(written, endpoints, _apiScope);
        _served = new Served(endpoints, document);
        return document;
    }

    private static string WithSignIn(string written, (string AuthorizationEndpoint, string TokenEndpoint)? endpoints, string? apiScope)
    {
        var root = JsonNode.Parse(written);
        if (root?["components"]?["securitySchemes"]?[OpenApiSecurityConfiguration.OAuth2SchemeName]?["flows"]?["authorizationCode"] is not JsonObject flow)
            return written;

        var changed = false;
        if (endpoints is { } real)
        {
            flow["authorizationUrl"] = real.AuthorizationEndpoint;
            flow["tokenUrl"] = real.TokenEndpoint;
            changed = true;
        }

        if (!string.IsNullOrEmpty(apiScope) && !OffersOnly(flow["scopes"], apiScope))
        {
            flow["scopes"] = new JsonObject { [OpenIdScope] = "OpenID Connect", [apiScope] = "Access JIM API" };
            foreach (var requirement in (root["security"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (requirement.ContainsKey(OpenApiSecurityConfiguration.OAuth2SchemeName))
                    requirement[OpenApiSecurityConfiguration.OAuth2SchemeName] = new JsonArray(OpenIdScope, apiScope);
            }
            changed = true;
        }

        return changed ? root.ToJsonString(WriteOptions) : written;
    }

    /// <summary>Whether the built flow already offers exactly openid and the API scope.</summary>
    private static bool OffersOnly(JsonNode? scopes, string apiScope) =>
        scopes is JsonObject offered
        && offered.Count == 2
        && offered.ContainsKey(OpenIdScope)
        && offered.ContainsKey(apiScope);

    private sealed record Served((string AuthorizationEndpoint, string TokenEndpoint)? Endpoints, string Document);
}
