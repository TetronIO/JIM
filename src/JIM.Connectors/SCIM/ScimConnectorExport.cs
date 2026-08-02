// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Scim.Messages;
using JIM.Scim.Schema;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Applies Pending Exports to a service provider: creates with POST, updates with PATCH, deletes with
/// DELETE.
/// <para>
/// One result is returned per Pending Export, in the order they arrived, because that is how JIM pairs
/// an outcome with the change that produced it. A failure is a per-object result rather than an
/// exception, so one rejected object does not abandon the rest of the batch.
/// </para>
/// </summary>
internal sealed class ScimConnectorExport
{
    private readonly ScimHttpClient _client;
    private readonly ScimDiscoveryResult _discovery;
    private readonly ILogger _logger;

    public ScimConnectorExport(ScimHttpClient client, ScimDiscoveryResult discovery, ILogger logger)
    {
        _client = client;
        _discovery = discovery;
        _logger = logger;
    }

    public async Task<List<ConnectedSystemExportResult>> ExecuteAsync(IList<PendingExport> pendingExports, CancellationToken cancellationToken)
    {
        var results = new List<ConnectedSystemExportResult>(pendingExports.Count);
        var created = 0;
        var updated = 0;
        var deleted = 0;
        var failed = 0;

        foreach (var pendingExport in pendingExports)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await ExportOneAsync(pendingExport, cancellationToken);
            results.Add(result);

            if (!result.Success)
                failed++;
            else if (pendingExport.ChangeType == PendingExportChangeType.Create)
                created++;
            else if (pendingExport.ChangeType == PendingExportChangeType.Delete)
                deleted++;
            else
                updated++;
        }

        // Every batch operation reports its totals, so a run's effect is legible without reading each item.
        _logger.Information("SCIM export: {Created} created, {Updated} updated, {Deleted} deleted, {Failed} failed, out of {Total} Pending Export(s).",
            created, updated, deleted, failed, pendingExports.Count);

        return results;
    }

    private async Task<ConnectedSystemExportResult> ExportOneAsync(PendingExport pendingExport, CancellationToken cancellationToken)
    {
        var objectTypeName = ResolveObjectTypeName(pendingExport);
        if (objectTypeName == null)
            return ConnectedSystemExportResult.Failed("The Pending Export does not say which Connected System Object Type it applies to, so JIM cannot tell the service provider where to send it.");

        var target = ResolveTarget(objectTypeName);
        if (target == null)
            return ConnectedSystemExportResult.Failed($"The service provider does not publish a resource type named '{objectTypeName}', so there is nowhere to send this change. Re-import the schema to pick up what it does publish.");

        try
        {
            return pendingExport.ChangeType switch
            {
                PendingExportChangeType.Create => await CreateAsync(pendingExport, target, cancellationToken),
                PendingExportChangeType.Delete => await DeleteAsync(pendingExport, target, cancellationToken),
                _ => await UpdateAsync(pendingExport, target, cancellationToken)
            };
        }
        catch (ScimRequestException ex)
        {
            _logger.Warning(ex, "SCIM export: the service provider rejected a {ChangeType} of a {ObjectType}.", pendingExport.ChangeType, LogSanitiser.Sanitise(objectTypeName));
            return ConnectedSystemExportResult.Failed(ex.Message, ClassifyError(ex));
        }
    }

    #region operations
    private async Task<ConnectedSystemExportResult> CreateAsync(PendingExport pendingExport, ScimExportTarget target, CancellationToken cancellationToken)
    {
        var writes = pendingExport.AttributeValueChanges
            .Where(c => c.ChangeType != PendingExportAttributeChangeType.Remove)
            .GroupBy(c => c.Attribute.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ScimAttributeWrite(g.Key, g.Select(ValueOf).ToList()))
            .ToList();

        var built = ScimResourceWriter.BuildResource(writes, target.Attributes, target.SchemaUrn);
        if (built.UnknownAttributes.Count > 0)
            return UnknownAttributesFailure(built.UnknownAttributes);

        var response = await _client.PostAsync<JsonElement>(ScimQueryBuilder.NormaliseEndpoint(target.Endpoint), built.Resource, cancellationToken);

        // The provider assigns the id, and JIM has to record it: without it the new object cannot be
        // updated or deleted later, and the confirming import would create a second Connected System
        // Object for the same resource.
        var externalId = ReadId(response);
        if (externalId == null)
            return ConnectedSystemExportResult.Failed("The service provider accepted the create but returned no id, so JIM has nothing to identify the new resource by.");

        return ConnectedSystemExportResult.Succeeded(externalId);
    }

    private async Task<ConnectedSystemExportResult> UpdateAsync(PendingExport pendingExport, ScimExportTarget target, CancellationToken cancellationToken)
    {
        var resourceId = ResolveResourceId(pendingExport);
        if (resourceId == null)
            return MissingResourceIdFailure();

        var changes = pendingExport.AttributeValueChanges
            .Select(c => new ScimAttributeChange(c.Attribute.Name, PatchOperation(c), ValueOf(c)))
            .ToList();

        var path = $"{ScimQueryBuilder.NormaliseEndpoint(target.Endpoint)}/{Uri.EscapeDataString(resourceId)}";

        return _discovery.Capabilities.SupportsPatch
            ? await PatchAsync(pendingExport, target, path, resourceId, changes, cancellationToken)
            : await ReplaceAsync(target, path, resourceId, changes, cancellationToken);
    }

    /// <summary>
    /// Sends only what changed, which is what PATCH exists for: a whole-resource PUT would assert every
    /// attribute, including ones JIM does not manage.
    /// </summary>
    private async Task<ConnectedSystemExportResult> PatchAsync(
        PendingExport pendingExport,
        ScimExportTarget target,
        string path,
        string resourceId,
        List<ScimAttributeChange> changes,
        CancellationToken cancellationToken)
    {
        var built = ScimPatchBuilder.Build(changes, target.Attributes);
        if (built.UnknownAttributes.Count > 0)
            return UnknownAttributesFailure(built.UnknownAttributes);

        if (built.Operations.Count == 0)
            return ConnectedSystemExportResult.Succeeded();

        var patch = new ScimPatchRequest { Operations = built.Operations };
        await _client.PatchAsync<JsonElement>(path, patch, cancellationToken, EntityTagFor(pendingExport));

        return ConnectedSystemExportResult.Succeeded(resourceId);
    }

    /// <summary>
    /// The fallback for a provider without PATCH: read the resource, lay JIM's changes onto it, and
    /// write the whole thing back.
    /// <para>
    /// Read-modify-write rather than a PUT built from JIM's changes alone, because a PUT asserts the
    /// entire resource: building one from the changes would clear every attribute the provider holds
    /// that JIM does not manage. The entity tag from the read is sent as <c>If-Match</c>, so the window
    /// between the read and the write cannot silently swallow someone else's change.
    /// </para>
    /// </summary>
    private async Task<ConnectedSystemExportResult> ReplaceAsync(
        ScimExportTarget target,
        string path,
        string resourceId,
        List<ScimAttributeChange> changes,
        CancellationToken cancellationToken)
    {
        var current = await _client.GetWithMetadataAsync<JsonNode>(path, cancellationToken);
        if (current.Body is not JsonObject resource)
            return ConnectedSystemExportResult.Failed("The service provider did not return the resource to update, so JIM cannot apply the change without risking clearing attributes it does not manage.");

        var applied = ScimResourceWriter.ApplyChanges(resource, changes, target.Attributes);
        if (applied.UnknownAttributes.Count > 0)
            return UnknownAttributesFailure(applied.UnknownAttributes);

        await _client.PutAsync<JsonElement>(path, applied.Resource, cancellationToken, current.ETag ?? EntityTagOf(resource));

        return ConnectedSystemExportResult.Succeeded(resourceId);
    }

    /// <summary>
    /// The entity tag JIM last saw for the object, taken from its imported <c>meta.version</c>. Sent
    /// only where the provider says it maintains entity tags: one that does not would either ignore
    /// <c>If-Match</c> or, worse, reject every write.
    /// </summary>
    private string? EntityTagFor(PendingExport pendingExport)
    {
        if (!_discovery.Capabilities.SupportsETag)
            return null;

        return pendingExport.ConnectedSystemObject?.AttributeValues
            .FirstOrDefault(v => string.Equals(v.Attribute?.Name, ScimCommonAttributes.MetaVersion, StringComparison.OrdinalIgnoreCase))
            ?.StringValue;
    }

    /// <summary>
    /// The entity tag from a freshly read resource's own metadata, for a provider that publishes
    /// <c>meta.version</c> but sends no <c>ETag</c> header.
    /// </summary>
    private string? EntityTagOf(JsonObject resource)
    {
        return _discovery.Capabilities.SupportsETag && resource["meta"] is JsonObject meta
            ? meta["version"]?.GetValue<string>()
            : null;
    }

    private async Task<ConnectedSystemExportResult> DeleteAsync(PendingExport pendingExport, ScimExportTarget target, CancellationToken cancellationToken)
    {
        var resourceId = ResolveResourceId(pendingExport);
        if (resourceId == null)
            return MissingResourceIdFailure();

        try
        {
            await _client.DeleteAsync($"{ScimQueryBuilder.NormaliseEndpoint(target.Endpoint)}/{Uri.EscapeDataString(resourceId)}", cancellationToken);
        }
        catch (ScimRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The intended end state is that the resource is gone, and it is. Failing here would leave a
            // Pending Export retrying for ever against a provider that has already done what was asked.
            _logger.Debug("SCIM export: the resource to delete was already absent from the service provider, which is the intended outcome.");
        }

        return ConnectedSystemExportResult.Succeeded();
    }
    #endregion

    #region translation
    /// <summary>
    /// Maps JIM's attribute change type onto the SCIM operation that expresses it. Add and Update differ
    /// on a multi-valued attribute (append versus replace the lot) and are the same on a single-valued
    /// one, which is why the distinction is kept rather than collapsed.
    /// </summary>
    private static string PatchOperation(PendingExportAttributeValueChange change)
    {
        return change.ChangeType switch
        {
            PendingExportAttributeChangeType.Add => ScimPatchOperations.Add,
            PendingExportAttributeChangeType.Remove => ScimPatchOperations.Remove,
            _ => ScimPatchOperations.Replace
        };
    }

    /// <summary>
    /// Reads the one value a change carries, whichever typed column holds it.
    /// </summary>
    private static object? ValueOf(PendingExportAttributeValueChange change)
    {
        if (change.StringValue != null) return change.StringValue;
        if (change.IntValue.HasValue) return change.IntValue.Value;
        if (change.LongValue.HasValue) return change.LongValue.Value;
        if (change.DecimalValue.HasValue) return change.DecimalValue.Value;
        if (change.BoolValue.HasValue) return change.BoolValue.Value;
        if (change.DateTimeValue.HasValue) return change.DateTimeValue.Value;
        if (change.GuidValue.HasValue) return change.GuidValue.Value;
        if (change.ByteValue != null) return change.ByteValue;

        // A reference JIM never resolved to a Connected System Object still carries the provider's own
        // identifier for the target, which is exactly what a SCIM reference needs.
        return change.UnresolvedReferenceValue;
    }

    /// <summary>
    /// The provider's id for the resource being changed, which is the Connected System Object's
    /// External ID.
    /// </summary>
    private static string? ResolveResourceId(PendingExport pendingExport)
    {
        var externalId = pendingExport.ConnectedSystemObject?.ExternalIdAttributeValue?.ToStringNoName();
        return string.IsNullOrWhiteSpace(externalId) ? null : externalId;
    }

    /// <summary>
    /// Which resource type the change is for. A create has no Connected System Object yet, so the type
    /// comes from the attributes being written instead.
    /// </summary>
    private static string? ResolveObjectTypeName(PendingExport pendingExport)
    {
        var fromObject = pendingExport.ConnectedSystemObject?.Type?.Name;
        if (!string.IsNullOrWhiteSpace(fromObject))
            return fromObject;

        return pendingExport.AttributeValueChanges
            .Select(c => c.Attribute.ConnectedSystemObjectType?.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private ScimExportTarget? ResolveTarget(string objectTypeName)
    {
        var resourceType = _discovery.ResourceTypes
            .FirstOrDefault(r => string.Equals(r.Name, objectTypeName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.Endpoint));

        if (resourceType == null)
            return null;

        return new ScimExportTarget(
            resourceType.Endpoint!,
            resourceType.Schema ?? objectTypeName,
            _discovery.FlattenedAttributes.TryGetValue(objectTypeName, out var attributes) ? attributes : []);
    }

    private static string? ReadId(JsonElement resource)
    {
        if (resource.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in resource.EnumerateObject().Where(p => string.Equals(p.Name, ScimCommonAttributes.Id, StringComparison.OrdinalIgnoreCase)))
            return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;

        return null;
    }

    /// <summary>
    /// Classifies a provider rejection so JIM can react to it. A missing reference is the one worth
    /// telling apart: RFC 7644 makes the client responsible for creating dependencies first, so this
    /// says the referenced object has not been exported yet rather than that the data is wrong.
    /// </summary>
    private static ConnectedSystemExportErrorType ClassifyError(ScimRequestException exception)
    {
        // A 412 means the resource moved on between JIM reading it and writing it back. Retrying blindly
        // would just race again; the next import reconciles what actually changed.
        if (exception.StatusCode == HttpStatusCode.PreconditionFailed)
            return ConnectedSystemExportErrorType.ConcurrencyConflict;

        return exception.StatusCode == HttpStatusCode.BadRequest
               && string.Equals(exception.ScimType, ScimErrorTypes.InvalidValue, StringComparison.OrdinalIgnoreCase)
            ? ConnectedSystemExportErrorType.MissingDependency
            : ConnectedSystemExportErrorType.General;
    }

    private static ConnectedSystemExportResult UnknownAttributesFailure(List<string> unknownAttributes)
    {
        return ConnectedSystemExportResult.Failed(
            $"The service provider's schema has no writable attribute named {string.Join(", ", unknownAttributes.Select(a => $"'{LogSanitiser.Sanitise(a)}'"))}. " +
            "The change was not sent, because exporting the rest would record it as applied when it was not. Re-import the schema and check the Attribute Flows targeting it.");
    }

    private static ConnectedSystemExportResult MissingResourceIdFailure()
    {
        return ConnectedSystemExportResult.Failed(
            "The Connected System Object has no External ID, so JIM does not know which resource on the service provider to change. A Full Import will re-establish it.");
    }
    #endregion

    /// <summary>
    /// Where a resource type's changes are sent, and the schema they are shaped by.
    /// </summary>
    private sealed record ScimExportTarget(string Endpoint, string SchemaUrn, List<ScimFlattenedAttribute> Attributes);
}
