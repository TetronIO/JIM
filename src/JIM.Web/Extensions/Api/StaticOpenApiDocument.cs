// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JIM.Web.Extensions.Api;

/// <summary>
/// The OpenAPI document a released image serves, written when the image was built (#2544). No identity provider
/// existed then, so its OAuth endpoints are placeholders; this replaces them with the identity provider's real ones as
/// the document is served, so the API reference's Try It can sign in. The endpoints are read when the document is
/// asked for, never at start-up, where an identity provider that is down or not yet trusted would stop JIM starting
/// (#2363). When they cannot be read, the document is served as written.
/// </summary>
public sealed class StaticOpenApiDocument
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Served as JSON, never embedded in HTML, so characters such as '<', '&' and accented letters are kept as
        // written rather than escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly Func<CancellationToken, Task<(string AuthorizationEndpoint, string TokenEndpoint)?>> _readEndpoints;
    private string? _written;
    private Served? _served;

    /// <param name="path">Where the built document is.</param>
    /// <param name="readEndpoints">Reads the identity provider's endpoints, or null when it cannot.</param>
    public StaticOpenApiDocument(string path, Func<CancellationToken, Task<(string AuthorizationEndpoint, string TokenEndpoint)?>> readEndpoints)
    {
        _path = path;
        _readEndpoints = readEndpoints;
    }

    /// <summary>
    /// The document to serve: the built document with the identity provider's endpoints, or as written when they
    /// cannot be read.
    /// </summary>
    public async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        var written = _written ??= await File.ReadAllTextAsync(_path, cancellationToken);
        var endpoints = await _readEndpoints(cancellationToken);
        if (endpoints is not { } real)
            return written;

        // The endpoints rarely change, and the document is large, so the last one served is kept for as long as they hold.
        if (_served is { } served && served.Endpoints == real)
            return served.Document;

        var document = WithOAuthEndpoints(written, real.AuthorizationEndpoint, real.TokenEndpoint);
        _served = new Served(real, document);
        return document;
    }

    private static string WithOAuthEndpoints(string written, string authorizationEndpoint, string tokenEndpoint)
    {
        var root = JsonNode.Parse(written);
        if (root?["components"]?["securitySchemes"]?[OpenApiSecurityConfiguration.OAuth2SchemeName]?["flows"]?["authorizationCode"] is not JsonObject flow)
            return written;

        flow["authorizationUrl"] = authorizationEndpoint;
        flow["tokenUrl"] = tokenEndpoint;
        return root.ToJsonString(WriteOptions);
    }

    private sealed record Served((string AuthorizationEndpoint, string TokenEndpoint) Endpoints, string Document);
}
