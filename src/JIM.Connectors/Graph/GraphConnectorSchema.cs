// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.Graph;

/// <summary>
/// Reads the Microsoft Graph Connector's schema: the curated user and group attributes (<see cref="GraphSchemaCatalogue"/>)
/// and the tenant's directory extensions, discovered with one request.
/// </summary>
internal sealed class GraphConnectorSchema
{
    private const string ExtensionDiscoveryPath = "directoryObjects/getAvailableExtensionProperties";

    private readonly GraphHttpClient _client;
    private readonly ILogger _logger;

    public GraphConnectorSchema(GraphHttpClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<ConnectorSchema> GetSchemaAsync(CancellationToken cancellationToken)
    {
        var schema = new ConnectorSchema();
        var extensions = await DiscoverExtensionsAsync(schema.Warnings, cancellationToken);

        schema.ObjectTypes.Add(BuildObjectType(GraphSchemaCatalogue.UserObjectType, GraphSchemaCatalogue.UserAttributes, extensions, "User"));
        schema.ObjectTypes.Add(BuildObjectType(GraphSchemaCatalogue.GroupObjectType, GraphSchemaCatalogue.GroupAttributes, extensions, "Group"));
        return schema;
    }

    private static ConnectorSchemaObjectType BuildObjectType(
        string name,
        IReadOnlyList<GraphSchemaAttribute> catalogue,
        IReadOnlyList<(GraphExtensionProperty Property, AttributeDataType Type)> extensions,
        string extensionTarget)
    {
        var objectType = new ConnectorSchemaObjectType(name);
        objectType.Attributes.AddRange(catalogue.Select(a => a.ToConnectorSchemaAttribute()));
        objectType.Attributes.AddRange(extensions
            .Where(e => e.Property.TargetObjects.Contains(extensionTarget, StringComparer.OrdinalIgnoreCase))
            .Select(e => ToSchemaAttribute(e.Property, e.Type)));
        objectType.RecommendedExternalIdAttribute = objectType.Attributes.Single(a => a.Name == GraphSchemaCatalogue.Id);
        return objectType;
    }

    private static ConnectorSchemaAttribute ToSchemaAttribute(GraphExtensionProperty property, AttributeDataType type)
    {
        var source = property.AppDisplayName != null ? $" registered by {property.AppDisplayName}" : "";
        var synchronised = property.IsSyncedFromOnPremises == true ? ", synchronised from on-premises Active Directory" : "";
        return new ConnectorSchemaAttribute(property.Name!, type, property.IsMultiValued ? AttributePlurality.MultiValued : AttributePlurality.SingleValued)
        {
            Description = $"A directory extension{source}{synchronised}."
        };
    }

    /// <summary>
    /// The tenant's directory extensions, with the JIM type each maps to. Discovery needs Directory.Read.All, which the
    /// least-privilege permissions leave out; without it the built-in schema stands and a warning says what to grant.
    /// Any other failure is not a schema without extensions, so it propagates.
    /// </summary>
    private async Task<IReadOnlyList<(GraphExtensionProperty Property, AttributeDataType Type)>> DiscoverExtensionsAsync(List<string> warnings, CancellationToken cancellationToken)
    {
        GraphCollectionPage<GraphExtensionProperty>? page;
        try
        {
            // An empty body asks for every extension, synchronised from on-premises or not. The action takes no paging.
            page = await _client.SendJsonAsync<GraphCollectionPage<GraphExtensionProperty>>(HttpMethod.Post, ExtensionDiscoveryPath, new { }, null, cancellationToken);
        }
        catch (GraphRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.Warning(ex, "Microsoft Graph refused directory extension discovery.");
            warnings.Add($"The tenant's directory extensions were not read, because discovering them needs the {GraphPermissions.DirectoryRead} " +
                         "application permission. Every built-in attribute is present; grant the permission with admin consent and refresh the schema to add the extensions.");
            return [];
        }

        var extensions = new List<(GraphExtensionProperty, AttributeDataType)>();
        foreach (var property in (page?.Value ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Name) && p.DeletedDateTime == null))
        {
            var type = MapDataType(property.DataType);
            if (type == null)
            {
                warnings.Add($"The directory extension {LogSanitiser.Sanitise(property.Name)} has the data type '{LogSanitiser.Sanitise(property.DataType)}', which JIM does not recognise, so it was left out.");
                continue;
            }

            extensions.Add((property, type.Value));
        }

        _logger.Debug("Microsoft Graph schema: {ExtensionCount} directory extensions discovered.", extensions.Count);
        return extensions;
    }

    private static AttributeDataType? MapDataType(string? dataType) => dataType?.ToLowerInvariant() switch
    {
        "binary" => AttributeDataType.Binary,
        "boolean" => AttributeDataType.Boolean,
        "datetime" => AttributeDataType.DateTime,
        "integer" => AttributeDataType.Number,
        "largeinteger" => AttributeDataType.LongNumber,
        "string" => AttributeDataType.Text,
        _ => null
    };
}
