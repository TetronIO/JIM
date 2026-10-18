// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Interfaces;
using JIM.Scim.Discovery;
using JIM.Scim.Schema;
using JIM.Scim.Serialisation;
using JIM.Scim.Service;

namespace JIM.Connectors.SCIM.ServiceProvider;

/// <summary>
/// A store's definitions as the Service Provider Connector uses them: compiled, for putting each resource's references
/// back and projecting it exactly as the SCIM endpoint would return it, and flattened, for reading it into Connected
/// System Object attributes by the same convention the SCIM client Connector uses.
/// </summary>
internal sealed class ScimServiceProviderDefinitions
{
    private const string MetaLocation = "meta.location";

    private ScimServiceProviderDefinitions(ScimServiceDefinitions compiled, Dictionary<string, List<ScimFlattenedAttribute>> attributes)
    {
        Compiled = compiled;
        Attributes = attributes;
    }

    public ScimServiceDefinitions Compiled { get; }

    /// <summary>
    /// The attributes each resource type is read through, keyed by its name. Write-only attributes are left out, since a
    /// client writes them and nothing ever reads them back, and so is <c>meta.location</c>: the store holds no base URL to
    /// form one from, and an attribute that never holds a value would only mislead.
    /// </summary>
    public IReadOnlyDictionary<string, List<ScimFlattenedAttribute>> Attributes { get; }

    public static async Task<ScimServiceProviderDefinitions> LoadAsync(IScimResourceStore store, CancellationToken cancellationToken)
    {
        var resourceTypeRows = await store.GetResourceTypesAsync(cancellationToken);
        var schemaRows = await store.GetSchemasAsync(cancellationToken);

        // Compiling first is what validates the rows: a resource type naming a schema the store does not hold fails here,
        // where the composer below would fall back to the RFC's definitions and serve something the store never declared.
        var compiled = ScimServiceDefinitions.Compile(resourceTypeRows, schemaRows);
        var composition = ScimResourceTypeComposer.Compose(
            resourceTypeRows.Select(r => Deserialise<ScimResourceType>(r.Definition)),
            schemaRows.Select(s => Deserialise<ScimSchema>(s.Definition)));

        var attributes = composition.FlattenedAttributes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Where(a => !a.IsWriteOnly && a.Name != MetaLocation).ToList(),
            StringComparer.OrdinalIgnoreCase);

        return new ScimServiceProviderDefinitions(compiled, attributes);
    }

    private static T Deserialise<T>(string definition) where T : class =>
        JsonSerializer.Deserialize<T>(definition, ScimJson.Options)
        ?? throw new InvalidOperationException("A SCIM definition in this Connected System's store is empty.");
}
