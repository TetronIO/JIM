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
/// <para>
/// Every change is composed into a <see cref="ScimExportOperation"/> first and dispatched second, so
/// the payload a provider receives is identical whether it arrives on its own or inside a bulk request.
/// </para>
/// </summary>
internal sealed class ScimConnectorExport
{
    private readonly ScimHttpClient _client;
    private readonly ScimDiscoveryResult _discovery;
    private readonly ILogger _logger;
    private readonly ScimBulkExporter? _bulkExporter;

    /// <summary>
    /// How long a request already under way may run on once the export run is cancelled, before JIM stops waiting for it
    /// (#2083). Long enough for an ordinary request, and a bound on one held up by a provider's retries and throttling
    /// waits, which could otherwise hold a cancellation up for many minutes.
    /// </summary>
    internal static readonly TimeSpan DefaultInFlightGracePeriod = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _inFlightGracePeriod;

    /// <param name="bulkEndpointState">
    /// What this run has learned about the provider's bulk endpoint, or null where the administrator has
    /// not opted into bulk operations.
    /// </param>
    /// <param name="inFlightGracePeriod">
    /// How long a request already under way may run on once the run is cancelled; <see cref="DefaultInFlightGracePeriod"/>
    /// when null.
    /// </param>
    public ScimConnectorExport(ScimHttpClient client, ScimDiscoveryResult discovery, ILogger logger, ScimBulkEndpointState? bulkEndpointState = null,
        TimeSpan? inFlightGracePeriod = null)
    {
        _client = client;
        _discovery = discovery;
        _logger = logger;
        _inFlightGracePeriod = inFlightGracePeriod ?? DefaultInFlightGracePeriod;
        _bulkExporter = bulkEndpointState == null ? null : new ScimBulkExporter(client, discovery.Capabilities, bulkEndpointState, logger);
    }

    /// <summary>
    /// Applies a batch of Pending Exports, returning one result per Pending Export in the same order.
    /// <para>
    /// A cancelled run stops between requests, never inside one (#2083). Preparation only reads, so a cancellation
    /// during it throws before anything is written. Once requests are being sent, the one under way finishes, so a
    /// resource the provider has created comes back with its id, and every change not yet sent is reported
    /// <see cref="ConnectedSystemExportResult.NotAttempted"/>. Throwing instead would lose the id of every resource
    /// already created, and JIM would post them again. A request still under way once the grace period after the
    /// cancellation has run out is abandoned and reported as a failed attempt whose outcome is unknown.
    /// </para>
    /// </summary>
    public async Task<List<ConnectedSystemExportResult>> ExecuteAsync(IList<PendingExport> pendingExports, CancellationToken cancellationToken)
    {
        var prepared = new List<ScimPreparedExport>(pendingExports.Count);
        foreach (var pendingExport in pendingExports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            prepared.Add(await PrepareAsync(pendingExport, cancellationToken));
        }

        using var inFlight = new InFlightCancellation(cancellationToken, _inFlightGracePeriod);
        var results = await DispatchAsync(prepared, cancellationToken, inFlight);
        LogSummary(pendingExports, results);

        return results;
    }

    #region dispatch
    /// <summary>
    /// Sends whatever preparation did not already settle, either a batch at a time or one request each.
    /// </summary>
    private async Task<List<ConnectedSystemExportResult>> DispatchAsync(List<ScimPreparedExport> prepared, CancellationToken cancellationToken, InFlightCancellation inFlight)
    {
        var outstanding = prepared
            .Select((item, index) => (item, index))
            .Where(entry => entry.item.Operation != null)
            .Select(entry => new ScimBulkExportOperation(entry.index, entry.item.Operation!))
            .ToList();

        var sent = _bulkExporter is { IsUsable: true }
            ? await _bulkExporter.ExecuteAsync(outstanding, SendAsync, cancellationToken, inFlight)
            : await SendEachAsync(outstanding, cancellationToken, inFlight);

        var results = new List<ConnectedSystemExportResult>(prepared.Count);
        for (var index = 0; index < prepared.Count; index++)
        {
            results.Add(prepared[index].Settled
                        ?? sent.GetValueOrDefault(index)
                        // Unreachable: every operation is either settled or dispatched. Reported as a
                        // failure regardless, because a result JIM cannot account for must never
                        // default to success, which is how a caller reads a missing one.
                        ?? ConnectedSystemExportResult.Failed("JIM did not receive an outcome for this change from the service provider."));
        }

        return results;
    }

    private async Task<Dictionary<int, ConnectedSystemExportResult>> SendEachAsync(List<ScimBulkExportOperation> operations, CancellationToken cancellationToken,
        InFlightCancellation inFlight)
    {
        var results = new Dictionary<int, ConnectedSystemExportResult>(operations.Count);

        foreach (var operation in operations)
        {
            // A request started is not handed the run's token, so it finishes and its outcome is recorded (#2083).
            results[operation.Index] = cancellationToken.IsCancellationRequested
                ? ConnectedSystemExportResult.NotAttempted()
                : await SendWithinGracePeriodAsync(operation.Operation, SendAsync, inFlight, _logger);
        }

        return results;
    }

    /// <summary>
    /// Sends one operation with the in-flight cancellation (#2083), reporting it abandoned if the grace period after the run
    /// was cancelled runs out while it is under way.
    /// </summary>
    internal static async Task<ConnectedSystemExportResult> SendWithinGracePeriodAsync(
        ScimExportOperation operation,
        Func<ScimExportOperation, CancellationToken, Task<ConnectedSystemExportResult>> send,
        InFlightCancellation inFlight,
        ILogger logger)
    {
        try
        {
            return await send(operation, inFlight.Token);
        }
        catch (OperationCanceledException) when (inFlight.IsCancellationRequested)
        {
            logger.Warning("SCIM export: a {Method} of {Path} was still under way {GracePeriod} after the run was cancelled, so JIM stopped waiting for it",
                operation.Method.Method, LogSanitiser.Sanitise(operation.Path), inFlight.GracePeriod);
            return Abandoned(inFlight);
        }
    }

    /// <summary>
    /// The result for a change whose request was still under way when the grace period after the run's cancellation ran out
    /// (#2083). Whether the provider applied it is unknown, so it is a failed attempt, never a success.
    /// </summary>
    internal static ConnectedSystemExportResult Abandoned(InFlightCancellation inFlight) =>
        ConnectedSystemExportResult.Failed(
            $"The export run was cancelled, and the request for this change was still under way {inFlight.GracePeriod.TotalSeconds:0} seconds later, " +
            "so JIM stopped waiting for it. Whether the service provider applied it is not known; the next import will show what it holds.");

    /// <summary>
    /// Sends one operation as its own request.
    /// </summary>
    private async Task<ConnectedSystemExportResult> SendAsync(ScimExportOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            if (operation.Method == HttpMethod.Post)
                return await CreateAsync(operation, cancellationToken);

            if (operation.Method == HttpMethod.Delete)
                return await DeleteAsync(operation, cancellationToken);

            if (operation.Method == HttpMethod.Patch)
                await _client.PatchAsync<JsonElement>(operation.Path, operation.Body!, cancellationToken, operation.EntityTag);
            else
                await _client.PutAsync<JsonElement>(operation.Path, operation.Body!, cancellationToken, operation.EntityTag);

            return ConnectedSystemExportResult.Succeeded(operation.ResourceId);
        }
        catch (ScimRequestException ex)
        {
            _logger.Warning(ex, "SCIM export: the service provider rejected a {Method} of {Path}.",
                operation.Method.Method, LogSanitiser.Sanitise(operation.Path));

            return ScimExportErrorClassifier.Failure(ex.Message, (int?)ex.StatusCode, ex.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else (the transport, or an authentication refresh, failing in a way the provider did not answer)
            // fails this change alone (#2197). Escaping the batch would lose the result of every request already made, so a
            // resource already created would be posted again. Cancellation still propagates, to be handled as the run's.
            _logger.Error(ex, "SCIM export: a {Method} of {Path} failed before the service provider answered it.",
                operation.Method.Method, LogSanitiser.Sanitise(operation.Path));

            return ConnectedSystemExportResult.Failed(
                $"The request for this change failed before the service provider answered it: {ex.Message} Whether the service provider applied it is not known; the next import will show what it holds.");
        }
    }

    private async Task<ConnectedSystemExportResult> CreateAsync(ScimExportOperation operation, CancellationToken cancellationToken)
    {
        var response = await _client.PostAsync<JsonElement>(operation.Path, operation.Body!, cancellationToken);

        // The provider assigns the id, and JIM has to record it: without it the new object cannot be
        // updated or deleted later, and the confirming import would create a second Connected System
        // Object for the same resource.
        var externalId = ReadId(response);

        return externalId == null
            ? ConnectedSystemExportResult.Failed("The service provider accepted the create but returned no id, so JIM has nothing to identify the new resource by.")
            : ConnectedSystemExportResult.Succeeded(externalId);
    }

    private async Task<ConnectedSystemExportResult> DeleteAsync(ScimExportOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteAsync(operation.Path, cancellationToken);
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

    #region preparation
    /// <summary>
    /// Turns one Pending Export into the request that applies it, or into the outcome that settles it
    /// without one.
    /// </summary>
    private async Task<ScimPreparedExport> PrepareAsync(PendingExport pendingExport, CancellationToken cancellationToken)
    {
        var objectTypeName = ResolveObjectTypeName(pendingExport);
        if (objectTypeName == null)
            return ScimPreparedExport.From(ConnectedSystemExportResult.Failed("The Pending Export does not say which Connected System Object Type it applies to, so JIM cannot tell the service provider where to send it."));

        var target = ResolveTarget(objectTypeName);
        if (target == null)
            return ScimPreparedExport.From(ConnectedSystemExportResult.Failed($"The service provider does not publish a resource type named '{LogSanitiser.Sanitise(objectTypeName)}', so there is nowhere to send this change. Re-import the schema to pick up what it does publish."));

        try
        {
            return pendingExport.ChangeType switch
            {
                PendingExportChangeType.Create => PrepareCreate(pendingExport, target),
                PendingExportChangeType.Delete => PrepareDelete(pendingExport, target),
                _ => await PrepareUpdateAsync(pendingExport, target, cancellationToken)
            };
        }
        catch (ScimRequestException ex)
        {
            // Only the read that precedes a whole-resource replace can fail here; nothing has been
            // written, so this is a rejection of the read rather than an unknown outcome.
            _logger.Warning(ex, "SCIM export: the service provider would not return a {ObjectType} JIM needed to read before writing it back.", LogSanitiser.Sanitise(objectTypeName));
            return ScimPreparedExport.From(ScimExportErrorClassifier.Failure(ex.Message, (int?)ex.StatusCode, ex.Error));
        }
    }

    private static ScimPreparedExport PrepareCreate(PendingExport pendingExport, ScimExportTarget target)
    {
        var writes = pendingExport.AttributeValueChanges
            .Where(c => c.ChangeType != PendingExportAttributeChangeType.Remove)
            .GroupBy(c => c.Attribute.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ScimAttributeWrite(g.Key, g.Select(ValueOf).ToList()))
            .ToList();

        var built = ScimResourceWriter.BuildResource(writes, target.Attributes, target.SchemaUrn);
        if (built.UnknownAttributes.Count > 0)
            return ScimPreparedExport.From(UnknownAttributesFailure(built.UnknownAttributes));

        return ScimPreparedExport.From(new ScimExportOperation(
            HttpMethod.Post, ScimQueryBuilder.NormaliseEndpoint(target.Endpoint), built.Resource, EntityTag: null, ResourceId: null));
    }

    private static ScimPreparedExport PrepareDelete(PendingExport pendingExport, ScimExportTarget target)
    {
        var resourceId = ResolveResourceId(pendingExport);
        if (resourceId == null)
            return ScimPreparedExport.From(MissingResourceIdFailure());

        return ScimPreparedExport.From(new ScimExportOperation(
            HttpMethod.Delete, ResourcePath(target, resourceId), Body: null, EntityTag: null, resourceId));
    }

    /// <summary>
    /// Prefers PATCH, which is what it exists for: a whole-resource PUT would assert every attribute,
    /// including ones JIM does not manage.
    /// </summary>
    private async Task<ScimPreparedExport> PrepareUpdateAsync(PendingExport pendingExport, ScimExportTarget target, CancellationToken cancellationToken)
    {
        var resourceId = ResolveResourceId(pendingExport);
        if (resourceId == null)
            return ScimPreparedExport.From(MissingResourceIdFailure());

        var changes = pendingExport.AttributeValueChanges
            .Select(c => new ScimAttributeChange(c.Attribute.Name, PatchOperation(c), ValueOf(c)))
            .ToList();

        var path = ResourcePath(target, resourceId);

        if (_discovery.Capabilities.SupportsPatch)
        {
            var built = ScimPatchBuilder.Build(changes, target.Attributes);
            if (built.UnknownAttributes.Count > 0)
                return ScimPreparedExport.From(UnknownAttributesFailure(built.UnknownAttributes));

            if (built.Operations.Count == 0)
                return ScimPreparedExport.From(ConnectedSystemExportResult.Succeeded());

            return ScimPreparedExport.From(new ScimExportOperation(
                HttpMethod.Patch, path, new ScimPatchRequest { Operations = built.Operations }, EntityTagFor(pendingExport), resourceId));
        }

        return await PrepareReplaceAsync(target, path, resourceId, changes, cancellationToken);
    }

    /// <summary>
    /// The fallback for a provider without PATCH: read the resource, lay JIM's changes onto it, and
    /// write the whole thing back.
    /// <para>
    /// Read-modify-write rather than a PUT built from JIM's changes alone, because a PUT asserts the
    /// entire resource: building one from the changes would clear every attribute the provider holds
    /// that JIM does not manage. The entity tag from the read guards the write, so the window between
    /// the read and the write cannot silently swallow someone else's change.
    /// </para>
    /// </summary>
    private async Task<ScimPreparedExport> PrepareReplaceAsync(
        ScimExportTarget target,
        string path,
        string resourceId,
        List<ScimAttributeChange> changes,
        CancellationToken cancellationToken)
    {
        var current = await _client.GetWithMetadataAsync<JsonNode>(path, cancellationToken);
        if (current.Body is not JsonObject resource)
            return ScimPreparedExport.From(ConnectedSystemExportResult.Failed("The service provider did not return the resource to update, so JIM cannot apply the change without risking clearing attributes it does not manage."));

        var applied = ScimResourceWriter.ApplyChanges(resource, changes, target.Attributes);
        if (applied.UnknownAttributes.Count > 0)
            return ScimPreparedExport.From(UnknownAttributesFailure(applied.UnknownAttributes));

        return ScimPreparedExport.From(new ScimExportOperation(
            HttpMethod.Put, path, applied.Resource, current.ETag ?? EntityTagOf(resource), resourceId));
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
    #endregion

    #region translation
    private static string ResourcePath(ScimExportTarget target, string resourceId)
    {
        return $"{ScimQueryBuilder.NormaliseEndpoint(target.Endpoint)}/{Uri.EscapeDataString(resourceId)}";
    }

    /// <summary>
    /// Maps JIM's attribute change type onto the SCIM operation that expresses it. Add and Update differ
    /// on a multi-valued attribute (append versus replace the lot) and are the same on a single-valued
    /// one, which is why the distinction is kept rather than collapsed.
    /// </summary>
    /// <remarks>
    /// A change carrying no value is a clear: an all-null Update, which is how export staging tells a
    /// single-valued attribute it holds nothing (#2182), or a RemoveAll. RFC 7644 section 3.5.2.3 requires
    /// a replace to carry a value, and a null one is left out of the request altogether, so a clear is sent
    /// as a remove naming the attribute alone, which section 3.5.2.2 reads as removing every value it holds.
    /// </remarks>
    private static string PatchOperation(PendingExportAttributeValueChange change)
    {
        if (ValueOf(change) == null)
            return ScimPatchOperations.Remove;

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
    /// Every batch operation reports its totals, so a run's effect is legible without reading each item.
    /// </summary>
    private void LogSummary(IList<PendingExport> pendingExports, List<ConnectedSystemExportResult> results)
    {
        var created = 0;
        var updated = 0;
        var deleted = 0;
        var failed = 0;
        var notAttempted = 0;

        for (var index = 0; index < pendingExports.Count; index++)
        {
            if (results[index].IsNotAttempted)
                notAttempted++;
            else if (!results[index].Success)
                failed++;
            else if (pendingExports[index].ChangeType == PendingExportChangeType.Create)
                created++;
            else if (pendingExports[index].ChangeType == PendingExportChangeType.Delete)
                deleted++;
            else
                updated++;
        }

        _logger.Information("SCIM export: {Created} created, {Updated} updated, {Deleted} deleted, {Failed} failed, {NotAttempted} not attempted because the run was cancelled, " +
            "out of {Total} Pending Export(s).", created, updated, deleted, failed, notAttempted, pendingExports.Count);
    }

    /// <summary>
    /// Where a resource type's changes are sent, and the schema they are shaped by.
    /// </summary>
    private sealed record ScimExportTarget(string Endpoint, string SchemaUrn, List<ScimFlattenedAttribute> Attributes);
}
