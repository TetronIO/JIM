// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text.Json;
using JIM.Models.Staging;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Messages;
using JIM.Scim.Schema;
using JIM.Scim.Serialisation;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Reads a service provider's three discovery documents and turns them into a Connector Schema.
/// <para>
/// Each document is optional in practice, whatever RFC 7644 requires, and each has a fallback: the core
/// resource types when <c>/ResourceTypes</c> is absent, the RFC's own schema definitions when
/// <c>/Schemas</c> is, and the protocol floors when <c>/ServiceProviderConfig</c> is. Every fallback is
/// reported as a warning. Refusing to configure a provider over a missing optional document would rule
/// out providers that work perfectly well.
/// </para>
/// <para>
/// A missing document is not the same as a broken provider: only 404 and 501 are read as "not
/// published". Anything else propagates, because absorbing a 500 or a 403 would persist an empty schema
/// over a good one and silently unmap every Attribute Flow that pointed at it.
/// </para>
/// </summary>
public class ScimConnectorSchema
{
    private readonly ScimHttpClient _client;
    private readonly ILogger _logger;

    public ScimConnectorSchema(ScimHttpClient client, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Runs discovery against the service provider.
    /// </summary>
    /// <exception cref="ScimRequestException">The provider failed a discovery request for a reason other than not publishing the endpoint.</exception>
    public async Task<ScimDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var config = await GetOptionalAsync<ScimServiceProviderConfig>(ScimEndpoints.ServiceProviderConfig, cancellationToken);
        if (config == null)
            warnings.Add($"The service provider does not publish {ScimEndpoints.ServiceProviderConfig}, so its optional capabilities could not be determined.");

        var capabilities = ScimProviderCapabilities.From(config);
        warnings.AddRange(capabilities.Warnings);

        var resourceTypes = await GetListAsync<ScimResourceType>(ScimEndpoints.ResourceTypes, cancellationToken);
        if (resourceTypes.Count == 0)
        {
            warnings.Add($"The service provider does not publish {ScimEndpoints.ResourceTypes}, so JIM assumed the standard User and Group resource types.");
            resourceTypes = ScimCoreSchemas.ResourceTypes();
        }

        var schemas = await GetListAsync<ScimSchema>(ScimEndpoints.Schemas, cancellationToken);
        if (schemas.Count == 0)
            warnings.Add($"The service provider does not publish {ScimEndpoints.Schemas}, so JIM used the core schema definitions from RFC 7643. Any vendor extension attributes will be missing.");

        var schemasByUrn = BuildSchemaIndex(schemas);
        var schema = new ConnectorSchema();
        var flattenedAttributes = new Dictionary<string, List<ScimFlattenedAttribute>>(StringComparer.OrdinalIgnoreCase);

        foreach (var resourceType in resourceTypes.Where(r => !string.IsNullOrWhiteSpace(r.Name)))
        {
            var flattened = BuildFlattenedAttributes(resourceType, schemasByUrn, warnings);
            flattenedAttributes[resourceType.Name!] = flattened;
            schema.ObjectTypes.Add(BuildObjectType(resourceType, flattened));
        }

        _logger.Debug("SCIM discovery found {ObjectTypeCount} object type(s) and raised {WarningCount} warning(s).",
            schema.ObjectTypes.Count, warnings.Count);

        return new ScimDiscoveryResult
        {
            Schema = schema,
            Capabilities = capabilities,
            ResourceTypes = resourceTypes,
            FlattenedAttributes = flattenedAttributes,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Probes the discovery endpoints to prove the base URL, the transport and the credential all work.
    /// <para>
    /// Any one endpoint answering is enough. All three reporting "not published", by contrast, is the
    /// signature of a base URL that does not point at a SCIM service provider, which is the typo worth
    /// catching while an administrator is still on the settings page.
    /// </para>
    /// </summary>
    /// <returns>True when the provider answered at least one discovery endpoint.</returns>
    /// <exception cref="ScimRequestException">The provider could not be reached, or refused the request.</exception>
    public async Task<bool> TestConnectivityAsync(CancellationToken cancellationToken)
    {
        string[] endpoints = [ScimEndpoints.ServiceProviderConfig, ScimEndpoints.ResourceTypes, ScimEndpoints.Schemas];

        foreach (var endpoint in endpoints)
        {
            try
            {
                using var response = await _client.SendAsync(HttpMethod.Get, endpoint, requestBody: null, cancellationToken);
                return true;
            }
            catch (ScimRequestException ex) when (IsEndpointNotPublished(ex))
            {
                _logger.Debug("Connectivity test: {Endpoint} is not published (HTTP {StatusCode}).", endpoint, (int?)ex.StatusCode);
            }
        }

        return false;
    }

    /// <summary>
    /// Flattens everything a resource type is composed of: its base schema, its extensions and the
    /// common attributes every SCIM resource carries. Names repeat at most once.
    /// </summary>
    private static List<ScimFlattenedAttribute> BuildFlattenedAttributes(
        ScimResourceType resourceType,
        Dictionary<string, ScimSchema> schemasByUrn,
        List<string> warnings)
    {
        var flattened = ScimCommonAttributes.For(resourceType.Schema ?? string.Empty);

        var baseSchema = ResolveSchema(resourceType.Schema, schemasByUrn);
        if (baseSchema == null)
        {
            // Keep the object type: the provider advertises it, and hiding it would make a provider gap
            // look like a JIM one. The common attributes still make it importable.
            warnings.Add($"The service provider advertises the {LogSanitiser.Sanitise(resourceType.Name)} resource type but published no schema for it, " +
                         "so only the standard SCIM attributes are available. Attributes specific to this resource type cannot be synchronised.");
        }
        else
        {
            flattened.AddRange(ScimAttributeMapper.FlattenSchema(baseSchema));
        }

        AddExtensionAttributes(resourceType, schemasByUrn, flattened, warnings);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return flattened.Where(a => seen.Add(a.Name)).ToList();
    }

    /// <summary>
    /// Builds one Connected System Object Type from a resource type's flattened attributes.
    /// </summary>
    private static ConnectorSchemaObjectType BuildObjectType(ScimResourceType resourceType, List<ScimFlattenedAttribute> flattened)
    {
        return new ConnectorSchemaObjectType(resourceType.Name!)
        {
            // The provider assigns id and never lets a client change it, which is exactly what an
            // external identifier has to be.
            RecommendedExternalIdAttribute = ScimCommonAttributes.For(resourceType.Schema ?? string.Empty)
                .Single(a => a.Name == ScimCommonAttributes.Id)
                .ToConnectorSchemaAttribute(),
            Attributes = flattened.Select(a => a.ToConnectorSchemaAttribute()).ToList()
        };
    }

    private static void AddExtensionAttributes(
        ScimResourceType resourceType,
        Dictionary<string, ScimSchema> schemasByUrn,
        List<ScimFlattenedAttribute> flattened,
        List<string> warnings)
    {
        var usedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var extension in resourceType.SchemaExtensions.Where(e => !string.IsNullOrWhiteSpace(e.Schema)))
        {
            var extensionSchema = ResolveSchema(extension.Schema, schemasByUrn);
            if (extensionSchema == null)
            {
                warnings.Add($"The {LogSanitiser.Sanitise(resourceType.Name)} resource type declares the schema extension " +
                             $"{LogSanitiser.Sanitise(extension.Schema)}, but the provider published no definition for it, so its attributes are unavailable.");
                continue;
            }

            // Two vendors naming their extension the same must not collapse into one set of attributes,
            // so the loser of a prefix collision is addressed by its URN instead.
            var prefix = ScimAttributeMapper.DeriveNamePrefix(extensionSchema);
            if (!usedPrefixes.Add(prefix))
                prefix = extension.Schema!;

            flattened.AddRange(ScimAttributeMapper.FlattenSchema(extensionSchema, prefix));
        }
    }

    /// <summary>
    /// Finds a schema in the provider's published set, falling back to the RFC's own definition when the
    /// provider did not publish one for a schema JIM knows.
    /// </summary>
    private static ScimSchema? ResolveSchema(string? urn, Dictionary<string, ScimSchema> schemasByUrn)
    {
        if (string.IsNullOrWhiteSpace(urn))
            return null;

        return schemasByUrn.TryGetValue(urn, out var schema) ? schema : ScimCoreSchemas.ByUrn(urn);
    }

    private static Dictionary<string, ScimSchema> BuildSchemaIndex(List<ScimSchema> schemas)
    {
        var index = new Dictionary<string, ScimSchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in schemas.Where(s => !string.IsNullOrWhiteSpace(s.Id)))
            index.TryAdd(schema.Id!, schema);

        return index;
    }

    /// <summary>
    /// Reads a discovery document, returning null when the provider does not publish that endpoint.
    /// </summary>
    private async Task<T?> GetOptionalAsync<T>(string endpoint, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await _client.GetAsync<T>(endpoint, cancellationToken);
        }
        catch (ScimRequestException ex) when (IsEndpointNotPublished(ex))
        {
            _logger.Information("The SCIM service provider does not publish {Endpoint} (HTTP {StatusCode}).", endpoint, (int?)ex.StatusCode);
            return null;
        }
    }

    /// <summary>
    /// Reads a discovery collection, tolerating both the ListResponse envelope RFC 7644 requires and the
    /// bare JSON array some providers return instead. An absent endpoint yields an empty list.
    /// </summary>
    private async Task<List<T>> GetListAsync<T>(string endpoint, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            using var response = await _client.SendAsync(HttpMethod.Get, endpoint, requestBody: null, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (ScimRequestException ex) when (IsEndpointNotPublished(ex))
        {
            _logger.Information("The SCIM service provider does not publish {Endpoint} (HTTP {StatusCode}).", endpoint, (int?)ex.StatusCode);
            return [];
        }

        if (string.IsNullOrWhiteSpace(body))
            return [];

        try
        {
            // The envelope is the specified shape, so it is tried first; a bare array fails to bind to it
            // and is then read directly.
            var listResponse = JsonSerializer.Deserialize<ScimListResponse<T>>(body, ScimJson.Options);
            return listResponse?.Resources ?? [];
        }
        catch (JsonException)
        {
            try
            {
                return JsonSerializer.Deserialize<List<T>>(body, ScimJson.Options) ?? [];
            }
            catch (JsonException ex)
            {
                throw new ScimRequestException(
                    $"The SCIM service provider returned a {endpoint} document that is neither a ListResponse nor an array of resources.",
                    HttpStatusCode.OK, ex);
            }
        }
    }

    /// <summary>
    /// Whether a failure means "this provider does not publish that endpoint" rather than "this provider
    /// is broken or is refusing us". Only the two status codes that carry that meaning qualify.
    /// </summary>
    private static bool IsEndpointNotPublished(ScimRequestException exception)
    {
        return exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented;
    }
}
