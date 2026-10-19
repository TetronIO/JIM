// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Data.Repositories;
using JIM.Models.Scim;
using JIM.Scim;
using JIM.Scim.Messages;
using JIM.Scim.Resources;
using JIM.Scim.Schema;
using JIM.Scim.Service;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// The SCIM 2.0 Service Provider (#124): the single place an identity provider's pushes change a Connected System's
/// SCIM store. Every rule comes from the Connected System's stored definitions (D18), so User, Group and any custom
/// resource type are served by the same code.
/// </summary>
/// <remarks>
/// <para>
/// Each write does its parsing, intake and validation that needs no database first, then takes the store's write lock
/// (<see cref="IScimStoreRepository.BeginWriteAsync"/>) for the reads and writes that must agree: the resource as it now
/// stands, the <c>If-Match</c> check, the reference checks (D9), uniqueness, and a journal entry for every resource the
/// write changed (D17). A write that changes nothing writes nothing, so an identity provider re-sending what JIM already
/// holds raises no version and gives the next import nothing to do.
/// </para>
/// <para>
/// A PATCH that only adds or removes members takes a direct path (<see cref="ScimReferencePatch"/>): it reads only the
/// members the request names, so one member added to a Group of 100,000 reads none of the others.
/// </para>
/// </remarks>
public class ScimServer
{
    private const string NoSuchResource = "The resource does not exist.";

    /// <summary>
    /// The most ignored attribute names one journal entry keeps.
    /// </summary>
    public const int MaxIgnoredAttributesJournalled = 50;

    /// <summary>
    /// The page size of a list request that gives no <c>count</c>.
    /// </summary>
    public const int DefaultPageSize = 100;

    /// <summary>
    /// The most resources one page of a list returns, whatever <c>count</c> asks for; what discovery publishes as
    /// <c>filter.maxResults</c>.
    /// </summary>
    public const int MaxPageSize = 1000;

    private readonly ILogger _logger = Log.ForContext<ScimServer>();

    private JimApplication Application { get; }

    internal ScimServer(JimApplication application)
    {
        Application = application;
    }

    private IScimStoreRepository Store => Application.Repository.ScimStore;

    /// <summary>
    /// Gives a Connected System's store the core User, Group and Enterprise User definitions if it has none. Returns
    /// whether it added them.
    /// </summary>
    public async Task<bool> EnsureCoreDefinitionsAsync(int connectedSystemId)
    {
        if ((await Store.GetResourceTypesAsync(connectedSystemId)).Count > 0)
            return false;

        var (schemas, resourceTypes) = ScimCoreDefinitions.Rows();
        await Store.AddDefinitionsAsync(connectedSystemId, schemas, resourceTypes);
        return true;
    }

    /// <summary>
    /// The resource types a Connected System's store serves, compiled from its definitions.
    /// </summary>
    public async Task<ScimServiceDefinitions> GetDefinitionsAsync(int connectedSystemId) =>
        ScimServiceDefinitions.Compile(await Store.GetResourceTypesAsync(connectedSystemId), await Store.GetSchemasAsync(connectedSystemId));

    #region import
    /// <summary>
    /// A Connected System's stored resource type definitions, as the Service Provider Connector composes its schema from.
    /// </summary>
    public Task<IReadOnlyList<ScimStoreResourceType>> GetResourceTypeRowsAsync(int connectedSystemId, CancellationToken cancellationToken = default) =>
        Store.GetResourceTypesAsync(connectedSystemId, cancellationToken);

    /// <summary>
    /// A Connected System's stored schema definitions.
    /// </summary>
    public Task<IReadOnlyList<ScimStoreSchema>> GetSchemaRowsAsync(int connectedSystemId, CancellationToken cancellationToken = default) =>
        Store.GetSchemasAsync(connectedSystemId, cancellationToken);

    /// <summary>
    /// A Connected System's store counters: its committed change sequence (an import's horizon) and its purge floor.
    /// </summary>
    public Task<ScimStoreState?> GetStoreStateAsync(int connectedSystemId, CancellationToken cancellationToken = default) =>
        Store.GetStateAsync(connectedSystemId, cancellationToken);

    /// <summary>
    /// One page of what an import reads from a Connected System's store.
    /// </summary>
    public Task<ScimStoreImportPage> ReadForImportAsync(int connectedSystemId, ScimStoreImportQuery query, CancellationToken cancellationToken = default) =>
        Store.ReadForImportAsync(connectedSystemId, query, cancellationToken);

    /// <summary>
    /// Removes tombstones an import has read and that are older than the cut-off, raising the store's purge floor.
    /// </summary>
    public Task<int> PurgeTombstonesAsync(int connectedSystemId, long importedUpTo, DateTime deletedBefore, CancellationToken cancellationToken = default) =>
        Store.PurgeTombstonesAsync(connectedSystemId, importedUpTo, deletedBefore, cancellationToken);
    #endregion

    #region create
    /// <summary>
    /// POST: creates a resource at version 1 and returns it with <c>201</c>.
    /// </summary>
    public async Task<ScimServiceResult> CreateAsync(ScimRequestContext context, string endpoint, JsonObject body, ScimAttributeSelection? selection = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);

        var definitions = await GetDefinitionsAsync(context.ConnectedSystemId);
        var type = definitions.FindByEndpoint(endpoint);
        if (type == null)
            return NoSuchEndpoint();

        var intake = ScimResourceIntake.Read(body, type.Schema);
        LogCoercions(context, type, intake.Coercions);
        if (intake.Error != null)
            return ScimServiceResult.Failed(intake.Error);

        var split = ScimReferenceEntries.Split(intake.Resource!, type);
        if (split.Error != null)
            return ScimServiceResult.Failed(split.Error);

        var id = Guid.NewGuid();
        await using var session = await Store.BeginWriteAsync(context.ConnectedSystemId);
        if (session == null)
            return NoSuchEndpoint();

        var targets = await CheckReferencesAsync(session, definitions, type,
            split.References.ToDictionary(r => r.Key, r => (IReadOnlyCollection<Guid>)r.Value));
        if (targets.Error != null)
            return ScimServiceResult.Failed(targets.Error);

        var resource = new ScimStoreResource
        {
            Id = id,
            ResourceTypeId = type.Id,
            Created = session.Timestamp,
            Payload = split.Payload.ToJsonString()
        };
        var written = await session.InsertResourceAsync(resource, type.UniqueValues(split.Payload));
        if (written.Outcome == ScimStoreWriteOutcome.UniquenessConflict)
            return NotUnique(written.ConflictingAttribute!);

        foreach (var (attribute, ids) in split.References.Where(r => r.Value.Count > 0))
            await session.AddReferencesAsync(id, attribute, ids.ToList());

        var held = Held(split.References, targets.TypeNames);
        var composed = ScimReferenceEntries.Compose(split.Payload, type, held);
        await session.AppendJournalEntriesAsync([Journal(context, id, type.Name, ScimStoreOperation.Created,
            ScimResourceDiff.ChangedAttributes([], composed, type.Schema), intake.IgnoredAttributes)]);
        await session.CommitAsync();

        resource.LastModified = session.Timestamp;
        resource.Version = written.Version;
        return ScimServiceResult.Created(Respond(context, definitions, type, resource, split.Payload, held, selection), written.Version,
            Location(context, type, id));
    }
    #endregion

    #region get
    /// <summary>
    /// GET of one resource.
    /// </summary>
    public async Task<ScimServiceResult> GetAsync(ScimRequestContext context, string endpoint, string id, ScimAttributeSelection? selection = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var definitions = await GetDefinitionsAsync(context.ConnectedSystemId);
        var type = definitions.FindByEndpoint(endpoint);
        if (type == null)
            return NoSuchEndpoint();
        if (!Guid.TryParse(id, out var resourceId))
            return ScimServiceResult.NotFound(NoSuchResource);

        var resource = await Store.GetLiveResourceAsync(context.ConnectedSystemId, type.Id, resourceId);
        if (resource == null)
            return ScimServiceResult.NotFound(NoSuchResource);

        var held = ReferencesWanted(type, selection)
            ? Held(await Store.GetReferencesAsync(resourceId), definitions)
            : new Dictionary<string, IReadOnlyList<ScimHeldReference>>();
        return ScimServiceResult.Ok(Respond(context, definitions, type, resource, Payload(resource), held, selection), resource.Version);
    }
    #endregion

    #region list
    /// <summary>
    /// GET of a resource type's endpoint (and, in Phase 4, <c>.search</c>): one page of the live resources the filter
    /// selects, in id order. Index paging is the default; every page that has a successor carries a <c>nextCursor</c>,
    /// whichever paging the request used, so a client can switch to cursors, which a deletion mid-walk cannot make skip
    /// a resource.
    /// </summary>
    public async Task<ScimServiceResult> ListAsync(ScimRequestContext context, string endpoint, ScimListRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var definitions = await GetDefinitionsAsync(context.ConnectedSystemId);
        var type = definitions.FindByEndpoint(endpoint);
        if (type == null)
            return NoSuchEndpoint();

        ScimStoreFilter? filter = null;
        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            var translation = ScimStoreFilterTranslator.Translate(request.Filter, type);
            if (translation.Error != null)
                return ScimServiceResult.Failed(translation.Error);
            filter = translation.Filter;
        }

        Guid? after = null;
        var useCursor = request.Cursor != null;
        if (!string.IsNullOrEmpty(request.Cursor))
        {
            if (!ScimCursor.TryDecode(request.Cursor, out var lastId))
            {
                return ScimServiceResult.Failed(ScimError.ForStatus(400,
                    "The cursor is not one this service provider issued; send an empty cursor to start again.", ScimErrorTypes.InvalidCursor));
            }

            after = lastId;
        }

        var count = Math.Clamp(request.Count ?? DefaultPageSize, 0, MaxPageSize);
        var startIndex = Math.Max(request.StartIndex ?? 1, 1);
        var page = await Store.ListLiveResourcesAsync(context.ConnectedSystemId, new ScimStoreQuery
        {
            ResourceTypeId = type.Id,
            Filter = filter,
            Count = count,
            StartIndex = useCursor ? 1 : startIndex,
            After = after
        });

        var references = page.Resources.Count > 0 && ReferencesWanted(type, request.Selection)
            ? await Store.GetReferencesAsync(page.Resources.Select(r => r.Id).ToList())
            : new Dictionary<Guid, IReadOnlyList<ScimStoreReferenceTarget>>();

        var resources = new JsonArray();
        foreach (var resource in page.Resources)
        {
            var held = references.TryGetValue(resource.Id, out var targets)
                ? Held(targets, definitions)
                : new Dictionary<string, IReadOnlyList<ScimHeldReference>>();
            resources.Add(Respond(context, definitions, type, resource, Payload(resource), held, request.Selection));
        }

        var body = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimUrns.ListResponse),
            ["totalResults"] = page.TotalResults,
            ["itemsPerPage"] = page.Resources.Count
        };
        if (!useCursor)
            body["startIndex"] = startIndex;
        if (page.HasMore)
            body["nextCursor"] = ScimCursor.Encode(page.Resources[^1].Id);
        body["Resources"] = resources;
        return ScimServiceResult.List(body);
    }
    #endregion

    #region replace
    /// <summary>
    /// PUT: replaces everything a client may set on a resource.
    /// </summary>
    public async Task<ScimServiceResult> ReplaceAsync(ScimRequestContext context, string endpoint, string id, JsonObject body, string? ifMatch,
        ScimAttributeSelection? selection = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);

        var definitions = await GetDefinitionsAsync(context.ConnectedSystemId);
        var type = definitions.FindByEndpoint(endpoint);
        if (type == null)
            return NoSuchEndpoint();
        if (!Guid.TryParse(id, out var resourceId))
            return ScimServiceResult.NotFound(NoSuchResource);

        var intake = ScimResourceIntake.Read(body, type.Schema);
        LogCoercions(context, type, intake.Coercions);
        if (intake.Error != null)
            return ScimServiceResult.Failed(intake.Error);

        var split = ScimReferenceEntries.Split(intake.Resource!, type);
        if (split.Error != null)
            return ScimServiceResult.Failed(split.Error);

        await using var session = await Store.BeginWriteAsync(context.ConnectedSystemId);
        if (session == null)
            return NoSuchEndpoint();

        return await WriteWholeAsync(session, context, definitions, type, resourceId, ifMatch, ScimStoreOperation.Replaced, selection,
            _ => new ScimPatchResult { Resource = intake.Resource, IgnoredAttributes = intake.IgnoredAttributes }, split);
    }
    #endregion

    #region patch
    /// <summary>
    /// PATCH: applies the operations atomically (RFC 7644 section 3.5.2) and returns the resource with <c>200</c>.
    /// </summary>
    public async Task<ScimServiceResult> PatchAsync(ScimRequestContext context, string endpoint, string id, IReadOnlyList<ScimPatchOperation> operations,
        string? ifMatch, ScimAttributeSelection? selection = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);

        var definitions = await GetDefinitionsAsync(context.ConnectedSystemId);
        var type = definitions.FindByEndpoint(endpoint);
        if (type == null)
            return NoSuchEndpoint();
        if (!Guid.TryParse(id, out var resourceId))
            return ScimServiceResult.NotFound(NoSuchResource);

        var plan = ScimReferencePatch.TryPlan(operations, type);

        await using var session = await Store.BeginWriteAsync(context.ConnectedSystemId);
        if (session == null)
            return NoSuchEndpoint();

        if (plan != null)
            return await PatchReferencesAsync(session, context, definitions, type, resourceId, plan, ifMatch, selection);

        return await WriteWholeAsync(session, context, definitions, type, resourceId, ifMatch, ScimStoreOperation.Patched, selection,
            current =>
            {
                var applied = ScimPatchApplier.Apply(current, operations, type.Schema);
                LogCoercions(context, type, applied.Coercions);
                if (applied.Error != null)
                    return applied;

                // An operation can remove a required attribute, which only the patched resource shows.
                var missing = ScimResourceIntake.CheckRequired(applied.Resource!, type.Schema);
                return missing == null ? applied : new ScimPatchResult { Error = missing };
            }, split: null);
    }

    /// <summary>
    /// The direct path: membership changes as row inserts and deletes, reading only the references the request names.
    /// </summary>
    private async Task<ScimServiceResult> PatchReferencesAsync(IScimStoreWriteSession session, ScimRequestContext context,
        ScimServiceDefinitions definitions, ScimServiceResourceType type, Guid id, ScimReferencePatchPlan plan, string? ifMatch,
        ScimAttributeSelection? selection)
    {
        var current = await session.GetLiveResourceAsync(type.Id, id);
        if (current == null)
            return ScimServiceResult.NotFound(NoSuchResource);
        if (ifMatch != null && !ScimETag.IfMatchSatisfied(ifMatch, current.Version))
            return PreconditionFailed();

        var mentioned = plan.MentionedIds.Values.SelectMany(ids => ids).Distinct().ToList();
        var heldMentioned = Held((await session.GetReferencesAsync(id, mentioned))
            .Where(r => plan.MentionedIds.ContainsKey(r.Attribute)).ToList(), definitions);

        var outcome = plan.Resolve(heldMentioned);
        LogCoercions(context, type, outcome.Coercions);
        if (outcome.Error != null)
            return ScimServiceResult.Failed(outcome.Error);

        long version = current.Version;
        if (outcome.Changes.Count > 0)
        {
            var targets = await CheckReferencesAsync(session, definitions, type,
                outcome.Changes.ToDictionary(c => c.Attribute, c => (IReadOnlyCollection<Guid>)c.Add.ToList()));
            if (targets.Error != null)
                return ScimServiceResult.Failed(targets.Error);

            foreach (var change in outcome.Changes)
            {
                if (change.ReplaceAll)
                {
                    version = await session.ReplaceReferencesAsync(id, change.Attribute, change.Add);
                    continue;
                }

                version = await session.RemoveReferencesAsync(id, change.Attribute, change.Remove);
                version = await session.AddReferencesAsync(id, change.Attribute, change.Add);
            }

            await session.AppendJournalEntriesAsync([Journal(context, id, type.Name, ScimStoreOperation.Patched,
                outcome.Changes.Select(c => c.Attribute).Distinct().Order(StringComparer.Ordinal), outcome.IgnoredAttributes)]);
            await session.CommitAsync();
            current.Version = version;
            current.LastModified = session.Timestamp;
        }

        await session.DisposeAsync();

        // Read once the write lock is released: a Group's whole membership is never read while other writes wait.
        var held = ReferencesWanted(type, selection)
            ? Held(await Store.GetReferencesAsync(id), definitions)
            : new Dictionary<string, IReadOnlyList<ScimHeldReference>>();
        return ScimServiceResult.Ok(Respond(context, definitions, type, current, Payload(current), held, selection), version);
    }
    #endregion

    #region delete
    /// <summary>
    /// DELETE: tombstones the resource, freeing its unique values and removing every reference to it, and returns
    /// <c>204</c>.
    /// </summary>
    public async Task<ScimServiceResult> DeleteAsync(ScimRequestContext context, string endpoint, string id, string? ifMatch)
    {
        ArgumentNullException.ThrowIfNull(context);

        var definitions = await GetDefinitionsAsync(context.ConnectedSystemId);
        var type = definitions.FindByEndpoint(endpoint);
        if (type == null)
            return NoSuchEndpoint();
        if (!Guid.TryParse(id, out var resourceId))
            return ScimServiceResult.NotFound(NoSuchResource);

        await using var session = await Store.BeginWriteAsync(context.ConnectedSystemId);
        if (session == null)
            return NoSuchEndpoint();

        var current = await session.GetLiveResourceAsync(type.Id, resourceId);
        if (current == null)
            return ScimServiceResult.NotFound(NoSuchResource);
        if (ifMatch != null && !ScimETag.IfMatchSatisfied(ifMatch, current.Version))
            return PreconditionFailed();

        var referrers = await session.TombstoneResourceAsync(type.Id, resourceId)
                        ?? throw new InvalidOperationException($"SCIM resource {resourceId} was live under the store's write lock and then was not.");

        var entries = new List<ScimStoreJournalEntry> { Journal(context, resourceId, type.Name, ScimStoreOperation.Deleted, []) };
        entries.AddRange(referrers
            .GroupBy(r => (r.SourceId, r.SourceResourceTypeId))
            .Select(g => Journal(context, g.Key.SourceId, TypeName(definitions, g.Key.SourceResourceTypeId), ScimStoreOperation.ReferenceRemovedByDeletion,
                g.Select(r => r.Attribute).Distinct().Order(StringComparer.Ordinal))));
        await session.AppendJournalEntriesAsync(entries);
        await session.CommitAsync();
        return ScimServiceResult.NoContent();
    }
    #endregion

    #region shared
    /// <summary>
    /// A write that produces the whole resource (PUT, and a PATCH outside the direct path): reads the resource as it
    /// stands, has <paramref name="produce"/> make the new one from it (or refuse the request), and writes the payload
    /// and whichever reference attributes changed.
    /// </summary>
    private async Task<ScimServiceResult> WriteWholeAsync(IScimStoreWriteSession session, ScimRequestContext context,
        ScimServiceDefinitions definitions, ScimServiceResourceType type, Guid id, string? ifMatch, ScimStoreOperation operation,
        ScimAttributeSelection? selection, Func<JsonObject, ScimPatchResult> produce, ScimReferenceSplit? split)
    {
        var current = await session.GetLiveResourceAsync(type.Id, id);
        if (current == null)
            return ScimServiceResult.NotFound(NoSuchResource);
        if (ifMatch != null && !ScimETag.IfMatchSatisfied(ifMatch, current.Version))
            return PreconditionFailed();

        var currentPayload = Payload(current);
        var currentHeld = Held(await session.GetReferencesAsync(id), definitions);
        var before = ScimReferenceEntries.Compose(currentPayload, type, currentHeld);

        var produced = produce(before);
        if (produced.Error != null)
            return ScimServiceResult.Failed(produced.Error);

        split ??= ScimReferenceEntries.Split(produced.Resource!, type);
        if (split.Error != null)
            return ScimServiceResult.Failed(split.Error);

        var currentIds = type.ReferenceAttributes.ToDictionary(a => a.Name,
            a => currentHeld.TryGetValue(a.Name, out var held) ? held.Select(r => r.TargetId).ToHashSet() : [], StringComparer.OrdinalIgnoreCase);
        var changedReferences = split.References
            .Where(r => !currentIds[r.Key].SetEquals(r.Value))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);

        // Only references being added need checking: one already held is to a live resource of an allowed type.
        var targets = await CheckReferencesAsync(session, definitions, type,
            changedReferences.ToDictionary(r => r.Key, r => (IReadOnlyCollection<Guid>)r.Value.Where(t => !currentIds[r.Key].Contains(t)).ToList()));
        if (targets.Error != null)
            return ScimServiceResult.Failed(targets.Error);

        var typeNames = currentHeld.Values.SelectMany(h => h).ToDictionary(r => r.TargetId, r => r.TargetTypeName);
        foreach (var (target, name) in targets.TypeNames)
            typeNames[target] = name;
        var held = Held(split.References, typeNames);

        // References are sets, so they are compared as sets: members re-sent in another order are not a change.
        var changed = ScimResourceDiff.ChangedAttributes(currentPayload, split.Payload, type.Schema)
            .Union(changedReferences.Keys)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (changed.Count == 0)
            return ScimServiceResult.Ok(Respond(context, definitions, type, current, currentPayload, currentHeld, selection), current.Version);

        var payloadChanged = !JsonNode.DeepEquals(currentPayload, split.Payload);
        var version = current.Version;
        if (payloadChanged)
        {
            current.Payload = split.Payload.ToJsonString();
            var written = await session.UpdateResourceAsync(current, type.UniqueValues(split.Payload));
            if (written.Outcome == ScimStoreWriteOutcome.UniquenessConflict)
                return NotUnique(written.ConflictingAttribute!);
            version = written.Version;
        }

        foreach (var (attribute, ids) in changedReferences)
            version = await session.ReplaceReferencesAsync(id, attribute, ids.ToList());

        await session.AppendJournalEntriesAsync([Journal(context, id, type.Name, operation, changed, produced.IgnoredAttributes)]);
        await session.CommitAsync();

        current.Version = version;
        current.LastModified = session.Timestamp;
        return ScimServiceResult.Ok(Respond(context, definitions, type, current, split.Payload, held, selection), version);
    }

    /// <summary>
    /// Checks every reference a write adds is to a live resource of a type the attribute may refer to (D9), and
    /// returns each one's type name.
    /// </summary>
    private static async Task<(ScimError? Error, Dictionary<Guid, string> TypeNames)> CheckReferencesAsync(IScimStoreWriteSession session,
        ScimServiceDefinitions definitions, ScimServiceResourceType type, IReadOnlyDictionary<string, IReadOnlyCollection<Guid>> references)
    {
        var typeNames = new Dictionary<Guid, string>();
        var wanted = references.Values.SelectMany(ids => ids).Distinct().ToList();
        if (wanted.Count == 0)
            return (null, typeNames);

        var live = await session.GetLiveResourceTypeIdsAsync(wanted);
        foreach (var (name, ids) in references)
        {
            var attribute = type.FindReferenceAttribute(name)!;
            foreach (var target in ids)
            {
                if (!live.TryGetValue(target, out var targetTypeId))
                    return (ScimError.ForStatus(400, $"The attribute '{attribute.AttributeName}' refers to a resource that does not exist.", ScimErrorTypes.InvalidValue), typeNames);

                var targetType = TypeName(definitions, targetTypeId);
                if (!attribute.TargetTypeNames.Contains(targetType, StringComparer.OrdinalIgnoreCase))
                    return (ScimError.ForStatus(400, $"The attribute '{attribute.AttributeName}' cannot refer to a {targetType}.", ScimErrorTypes.InvalidValue), typeNames);

                typeNames[target] = targetType;
            }
        }

        return (null, typeNames);
    }

    /// <summary>
    /// The response body: the resource with its references (each with its <c>$ref</c>), <c>id</c> and <c>meta</c>,
    /// projected for the request.
    /// </summary>
    private static JsonObject Respond(ScimRequestContext context, ScimServiceDefinitions definitions, ScimServiceResourceType type,
        ScimStoreResource resource, JsonObject payload, IReadOnlyDictionary<string, IReadOnlyList<ScimHeldReference>> held,
        ScimAttributeSelection? selection)
    {
        var composed = ScimReferenceEntries.Compose(payload, type, held,
            reference => Location(context, definitions.FindByName(reference.TargetTypeName)!, reference.TargetId));
        var metadata = new ScimResourceMetadata
        {
            Id = ScimReferenceEntries.Format(resource.Id),
            ResourceType = type.Name,
            Created = new DateTimeOffset(DateTime.SpecifyKind(resource.Created, DateTimeKind.Utc)),
            LastModified = new DateTimeOffset(DateTime.SpecifyKind(resource.LastModified, DateTimeKind.Utc)),
            Version = resource.Version,
            Location = Location(context, type, resource.Id)
        };
        return ScimResourceProjection.Project(composed, metadata, type.Schema, selection?.Attributes, selection?.ExcludedAttributes);
    }

    /// <summary>
    /// Whether a response may need a resource's references. Only an exact exclusion, or an <c>attributes</c> list that
    /// names none of them, saves reading them; anything else reads them and leaves the choice to the projection.
    /// </summary>
    private static bool ReferencesWanted(ScimServiceResourceType type, ScimAttributeSelection? selection)
    {
        bool Names(string item, ScimReferenceAttribute attribute) =>
            string.Equals(item, attribute.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item, $"{attribute.SchemaUrn ?? type.Schema.BaseSchemaUrn}:{attribute.AttributeName}", StringComparison.OrdinalIgnoreCase) ||
            (attribute.SchemaUrn != null && string.Equals(item, attribute.SchemaUrn, StringComparison.OrdinalIgnoreCase));

        bool NamesWithin(string item, ScimReferenceAttribute attribute) =>
            Names(item, attribute) ||
            item.StartsWith($"{attribute.Name}.", StringComparison.OrdinalIgnoreCase) ||
            item.StartsWith($"{attribute.SchemaUrn ?? type.Schema.BaseSchemaUrn}:{attribute.AttributeName}.", StringComparison.OrdinalIgnoreCase);

        if (selection?.Attributes is { Count: > 0 } attributes)
            return type.ReferenceAttributes.Any(a => attributes.Any(item => NamesWithin(item, a)));

        if (selection?.ExcludedAttributes is { Count: > 0 } excluded)
            return type.ReferenceAttributes.Any(a => !excluded.Any(item => Names(item, a)));

        return type.ReferenceAttributes.Count > 0;
    }

    private static Dictionary<string, IReadOnlyList<ScimHeldReference>> Held(IReadOnlyList<ScimStoreReferenceTarget> references,
        ScimServiceDefinitions definitions) =>
        references.GroupBy(r => r.Attribute, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key,
                g => (IReadOnlyList<ScimHeldReference>)g.Select(r => new ScimHeldReference(r.TargetId, TypeName(definitions, r.TargetResourceTypeId))).ToList(),
                StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, IReadOnlyList<ScimHeldReference>> Held(IReadOnlyDictionary<string, IReadOnlyList<Guid>> references,
        IReadOnlyDictionary<Guid, string> typeNames) =>
        references.ToDictionary(r => r.Key,
            r => (IReadOnlyList<ScimHeldReference>)r.Value.Select(target => new ScimHeldReference(target, typeNames[target])).ToList(),
            StringComparer.OrdinalIgnoreCase);

    private static string TypeName(ScimServiceDefinitions definitions, int resourceTypeId) =>
        definitions.FindById(resourceTypeId)?.Name
        ?? throw new InvalidOperationException($"The SCIM store holds a resource of type {resourceTypeId}, which its definitions do not name.");

    private static JsonObject Payload(ScimStoreResource resource) =>
        JsonNode.Parse(resource.Payload ?? throw new InvalidOperationException($"Live SCIM resource {resource.Id} has no payload.")) as JsonObject
        ?? throw new InvalidOperationException($"SCIM resource {resource.Id}'s payload is not a JSON object.");

    private static string Location(ScimRequestContext context, ScimServiceResourceType type, Guid id) =>
        $"{context.BaseUrl.TrimEnd('/')}/{type.Endpoint.Trim('/')}/{ScimReferenceEntries.Format(id)}";

    /// <param name="ignoredAttributes">Attributes the request carried that the definitions do not declare (D17). The names
    /// come from the client, so at most <see cref="MaxIgnoredAttributesJournalled"/> are kept, each cut to the store's
    /// attribute name length.</param>
    private static ScimStoreJournalEntry Journal(ScimRequestContext context, Guid resourceId, string typeName, ScimStoreOperation operation,
        IEnumerable<string> changedAttributes, IReadOnlyList<string>? ignoredAttributes = null) => new()
    {
        ResourceId = resourceId,
        ResourceTypeName = typeName,
        Operation = operation,
        ChangedAttributes = changedAttributes.ToList(),
        IgnoredAttributes = (ignoredAttributes ?? [])
            .Take(MaxIgnoredAttributesJournalled)
            .Select(name => name.Length > ScimStoreLimits.AttributeMaxLength ? name[..ScimStoreLimits.AttributeMaxLength] : name)
            .ToList(),
        ApiKeyId = context.ApiKeyId,
        ApiKeyName = context.ApiKeyName,
        ClientAddress = context.ClientAddress
    };

    private static ScimServiceResult NoSuchEndpoint() => ScimServiceResult.NotFound("The endpoint does not exist.");

    private static ScimServiceResult NotUnique(string attribute) =>
        ScimServiceResult.Failed(ScimError.ForStatus(409, $"The attribute '{attribute}' must be unique.", ScimErrorTypes.Uniqueness));

    private static ScimServiceResult PreconditionFailed() =>
        ScimServiceResult.Failed(ScimError.ForStatus(412, "The resource has changed since the version given in If-Match."));

    /// <summary>
    /// The leniency a request needed, logged without values (D6); attribute names come from the client, so they are
    /// sanitised. Debug, not Information: an identity provider pushing at its usual rate repeats the same leniency on
    /// most requests.
    /// </summary>
    private void LogCoercions(ScimRequestContext context, ScimServiceResourceType type, IReadOnlyList<string> coercions)
    {
        foreach (var coercion in coercions)
        {
            _logger.Debug("SCIM request to Connected System {ConnectedSystemId} ({ResourceType}): {Coercion}",
                context.ConnectedSystemId, type.Name, LogSanitiser.Sanitise(coercion));
        }
    }
    #endregion
}
