// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.Graph;

/// <summary>
/// Reads Microsoft Entra ID users and groups, a page at a time, for a Full Import or a Delta Import. Held for the length
/// of the run, so what is learned once (the token's permissions, which objects are cloud-managed, a Delta Import's
/// changes) is learned once.
/// </summary>
/// <remarks>
/// <para>
/// A page of groups is completed before it is returned: each group's members (and owners) are read in full through
/// <c>$batch</c>, so a group is never staged with part of its membership, which JIM would read as removals.
/// </para>
/// <para>
/// A Delta Import reads each Object Type's delta round to learn which objects changed or were removed, then reads each
/// changed object again for its current state. JIM stages a changed object whole, reading an attribute it does not
/// carry as one with no values, and a round carries only what changed of a relationship (<c>members@delta</c> lists the
/// members added and removed), so its own payload cannot be staged. Reading again also keeps one path for staging an
/// object, whichever run reads it. The whole round is read before any object is, so each object is read once however
/// many times the round reports it, and as it stands after the round's link was issued.
/// </para>
/// </remarks>
internal sealed class GraphConnectorImport
{
    /// <summary>Graph's largest page for users and groups.</summary>
    public const int MaximumPageSize = 999;

    /// <summary>
    /// How long to wait before reading again an object a delta round reported as changed but Graph cannot yet find.
    /// Replication delay typically clears within seconds.
    /// </summary>
    internal static readonly TimeSpan[] NotFoundRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private const string UserType = "#microsoft.graph.user";
    private const string GroupType = "#microsoft.graph.group";

    private const string ReadEverythingInstead =
        "Every object was read instead, and an object JIM holds that Microsoft Entra ID no longer has is marked as deleted, within this Run Profile's deletion limits.";

    private readonly GraphHttpClient _client;
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _deltaLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _cloudManaged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _cloudManagedHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<GraphChange>> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private GraphImportPlan? _plan;

    public GraphConnectorImport(GraphHttpClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<ConnectedSystemImportResult> ImportPageAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        string? persistedConnectorData,
        GraphImportPosition position,
        IConnectorProgress progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var firstPage = _plan == null;
        _plan ??= await PlanAsync(connectedSystem, runProfile, persistedConnectorData, progress, cancellationToken);

        var result = _plan.ReadsEverything
            ? await ReadEverythingPageAsync(_plan, runProfile, position, progress, cancellationToken)
            : await ReadChangesPageAsync(_plan, runProfile, position, progress, cancellationToken);

        // Every page says whether the run reads every object, so JIM detects deletions by absence where it does (#2720).
        result.CompleteRead = _plan.ReadsEverything;

        // JIM keeps the first warning a run produces, so everything the run has to say is said on its first page.
        if (firstPage && _plan.Warnings.Count > 0)
        {
            result.WarningMessage = string.Join(" ", _plan.Warnings);
            result.WarningErrorType = _plan.FellBack ? ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport : null;
        }

        return result;
    }

    /// <summary>
    /// Settles, before any object is returned, how the run reads: JIM holds a run to the answer its first page gives
    /// about reading every object. A Delta Import whose changes cannot be read for any Object Type reads every object of
    /// every type.
    /// </summary>
    private async Task<GraphImportPlan> PlanAsync(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile, string? persistedConnectorData,
        IConnectorProgress progress, CancellationToken cancellationToken)
    {
        await progress.EnterPhaseAsync(GraphConnectorPhases.Prepare, "Preparing to read from Microsoft Entra ID...");
        var roles = GraphAccessToken.ReadRoles(await _client.GetAccessTokenAsync(cancellationToken));
        var targets = GraphImportTarget.For(connectedSystem, GraphPermissions.CanReadHiddenMembership(roles));

        // Which synchronised objects have had their Source of Authority switched is read only when tracking is on: the
        // only permission that reads it also allows changing it. Off, the set is empty and no warning is given.
        var warnings = new List<string>();
        var tracksSourceOfAuthority = GraphConnector.TracksSourceOfAuthority(connectedSystem.SettingValues);
        foreach (var target in targets)
        {
            if (!tracksSourceOfAuthority)
            {
                _cloudManaged[target.Name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _cloudManagedHashes[target.Name] = null;
            }
            else if (await LoadCloudManagedAsync(target, cancellationToken) is { } warning)
            {
                warnings.Add(warning);
            }
        }

        if (runProfile.RunType != ConnectedSystemRunType.DeltaImport)
            return new GraphImportPlan(targets, ReadsEverything: true, FellBack: false, warnings);

        var fallback = await ReadAllChangesAsync(targets, GraphImportState.Read(persistedConnectorData, _logger), progress, cancellationToken);
        if (fallback == null)
            return new GraphImportPlan(targets, ReadsEverything: false, FellBack: false, warnings);

        _logger.Warning("Microsoft Graph Delta Import: {Reason} Reading every object instead.", fallback);
        _deltaLinks.Clear();
        _changes.Clear();
        warnings.Insert(0, $"{fallback} {ReadEverythingInstead}");
        return new GraphImportPlan(targets, ReadsEverything: true, FellBack: true, warnings);
    }

    #region Reading every object

    private async Task<ConnectedSystemImportResult> ReadEverythingPageAsync(GraphImportPlan plan, ConnectedSystemRunProfile runProfile,
        GraphImportPosition position, IConnectorProgress progress, CancellationToken cancellationToken)
    {
        var result = new ConnectedSystemImportResult();
        if (position.TargetIndex >= plan.Targets.Count)
            return Finish(result, plan);

        var target = plan.Targets[position.TargetIndex];
        if (position.NextLink == null)
        {
            await progress.EnterPhaseAsync(GraphConnectorPhases.Prepare, $"Preparing to read {target.Collection}...");
            _deltaLinks[target.Name] = await TakeDeltaBaselineAsync(target, cancellationToken);
        }

        await progress.EnterPhaseAsync(GraphConnectorPhases.Fetch, $"Fetching {target.Collection}...");
        var url = position.NextLink ?? FirstPageUrl(target, runProfile.PageSize);
        var page = await _client.SendJsonAsync<GraphCollectionPage<JsonElement>>(HttpMethod.Get, url, null, null, cancellationToken)
                   ?? new GraphCollectionPage<JsonElement>();

        // A Full Import asserts the object exists; JIM decides whether that is a create or an update.
        var staged = page.Value.Select(o => Stage(o, target, ObjectChangeType.Added)).ToList();
        if (target.Members || target.Owners)
            await ReadRelationshipsAsync(staged, target, cancellationToken);

        result.ImportObjects.AddRange(staged.Select(s => s.ImportObject));
        await progress.ReportObjectsReadAsync(result.ImportObjects.Count);

        if (page.NextLink != null)
            result.PaginationTokens.Add(new GraphImportPosition(position.TargetIndex, page.NextLink).ToToken());
        else if (position.TargetIndex + 1 < plan.Targets.Count)
            result.PaginationTokens.Add(new GraphImportPosition(position.TargetIndex + 1, null).ToToken());
        else
            Finish(result, plan);

        _logger.Debug("Microsoft Graph import: staged {Count} {ObjectType} object(s).", result.ImportObjects.Count, target.Name);
        return result;
    }

    private static string FirstPageUrl(GraphImportTarget target, int pageSize)
    {
        var top = Math.Clamp(pageSize, 1, MaximumPageSize);
        var url = $"{target.Collection}?$select={string.Join(',', target.Select)}&$top={top.ToString(CultureInfo.InvariantCulture)}";
        return target.Manager ? $"{url}&$expand=manager($select=id)" : url;
    }

    /// <summary>
    /// The delta link the next Delta Import starts from, taken before this Object Type is read so a change made while
    /// it is being read is read again next time rather than missed. <c>$deltatoken=latest</c> returns the link without
    /// returning the tenant's objects.
    /// </summary>
    private async Task<string> TakeDeltaBaselineAsync(GraphImportTarget target, CancellationToken cancellationToken)
    {
        var url = $"{target.Collection}/delta?$select={string.Join(',', target.DeltaSelect)}&$deltatoken=latest";
        var page = await _client.SendJsonAsync<GraphCollectionPage<JsonElement>>(HttpMethod.Get, url, null, null, cancellationToken);
        return page?.DeltaLink
               ?? throw new GraphRequestException($"Microsoft Graph did not return a delta link for {target.Collection}, so a later Delta Import would have nothing to start from.", HttpStatusCode.BadGateway);
    }

    #endregion

    #region Reading changes

    /// <summary>
    /// Reads every Object Type's delta round, recording which objects changed and which were removed, and the links the
    /// next Delta Import starts from.
    /// </summary>
    /// <returns>Why the changes cannot be read, so the run must read every object instead; null when they were read.</returns>
    private async Task<string?> ReadAllChangesAsync(List<GraphImportTarget> targets, GraphImportState? state, IConnectorProgress progress, CancellationToken cancellationToken)
    {
        // Everything that can be checked without reading a round is checked first, so a run that must read everything
        // does not read a round it then discards.
        foreach (var target in targets)
        {
            if (state == null || !state.DeltaLinks.ContainsKey(target.Name))
                return $"Delta Import was requested, but there is no delta link for {target.Collection} to read their changes from: no Full Import has read them since they were selected.";

            if (!state.Signatures.TryGetValue(target.Name, out var signature) || signature != target.Signature)
                return $"The selected attributes of {target.Collection}, or the Connector's permission to read hidden group membership, have changed since the last Full Import, " +
                       "and Microsoft Graph reports changes only to what was read then.";

            if (!state.CloudManagedHashes.TryGetValue(target.Name, out var hash) || hash != _cloudManagedHashes.GetValueOrDefault(target.Name))
                return $"The Source of Authority of some {target.Collection} has been switched between Active Directory and Entra ID since the last import, or tracking it was turned on or off, " +
                       "which Microsoft Graph's changes do not report, so every one is read to bring its sourceOfAuthority up to date.";
        }

        foreach (var target in targets)
        {
            if (await ReadChangesAsync(target, state!.DeltaLinks[target.Name], progress, cancellationToken) is { } refused)
                return refused;
        }

        return null;
    }

    /// <summary>
    /// Reads one Object Type's delta round to its end. Each object's last appearance in the round decides whether it
    /// was removed: one removed and restored within the round is read as it now stands.
    /// </summary>
    /// <returns>Why the round cannot be read; null when it was.</returns>
    private async Task<string?> ReadChangesAsync(GraphImportTarget target, string deltaLink, IConnectorProgress progress, CancellationToken cancellationToken)
    {
        await progress.EnterPhaseAsync(GraphConnectorPhases.Prepare, $"Reading changes to {target.Collection}...");
        var removedById = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var url = deltaLink;
        var pageNumber = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++pageNumber > 1)
                    await progress.ReportAsync($"Reading changes to {target.Collection} (page {pageNumber:N0})...");

                var page = await _client.SendJsonAsync<GraphCollectionPage<JsonElement>>(HttpMethod.Get, url, null, null, cancellationToken)
                           ?? throw new GraphRequestException($"Microsoft Graph returned nothing for a page of changes to {target.Collection}.", HttpStatusCode.BadGateway);

                foreach (var (entry, id) in page.Value.Select(e => (Entry: e, Id: ReadId(e))).Where(e => e.Id != null))
                {
                    // Removed then re-added, so the dictionary's order is each object's last appearance.
                    removedById.Remove(id!);
                    removedById[id!] = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("@removed", out _);
                }

                if (page.NextLink != null)
                {
                    url = page.NextLink;
                    continue;
                }

                _deltaLinks[target.Name] = page.DeltaLink
                    ?? throw new GraphRequestException($"Microsoft Graph ended the changes to {target.Collection} without a delta link, so the next Delta Import would have nothing to start from.", HttpStatusCode.BadGateway);
                break;
            }
        }
        catch (GraphRequestException ex) when (IsDeltaLinkRefused(ex))
        {
            _logger.Warning(ex, "Microsoft Graph refused the delta link for {Collection}.", target.Collection);
            return $"Microsoft Graph no longer honours the delta link for {target.Collection} ({ex.ErrorCode ?? $"HTTP {(int)ex.StatusCode}"}). " +
                   "A link expires after seven days without use, and Graph can also ask for every object to be read again after maintenance on the tenant; " +
                   "run a Delta Import at least weekly to keep the link alive.";
        }

        _changes[target.Name] = removedById.Select(c => new GraphChange(c.Key, c.Value)).ToList();
        _logger.Information("Microsoft Graph Delta Import: {Changed} {Collection} changed and {Removed} removed.",
            removedById.Count(c => !c.Value), target.Collection, removedById.Count(c => c.Value));
        return null;
    }

    /// <summary>
    /// Whether Graph refused a delta link because it can no longer read changes from it: <c>410 Gone</c> after maintenance
    /// on the tenant, or one of the codes it uses for a link that has expired.
    /// </summary>
    private static bool IsDeltaLinkRefused(GraphRequestException ex) =>
        ex.StatusCode == HttpStatusCode.Gone
        || ex.ErrorCode is { } code && (code.Equals("syncStateNotFound", StringComparison.OrdinalIgnoreCase)
                                        || code.Equals("syncStateInvalid", StringComparison.OrdinalIgnoreCase)
                                        || code.Equals("resyncRequired", StringComparison.OrdinalIgnoreCase));

    private async Task<ConnectedSystemImportResult> ReadChangesPageAsync(GraphImportPlan plan, ConnectedSystemRunProfile runProfile,
        GraphImportPosition position, IConnectorProgress progress, CancellationToken cancellationToken)
    {
        var result = new ConnectedSystemImportResult();
        var (targetIndex, staged) = NextChange(plan, position.TargetIndex, position.ChangesStaged);
        if (targetIndex >= plan.Targets.Count)
            return Finish(result, plan);

        var target = plan.Targets[targetIndex];
        var changes = _changes[target.Name];
        var page = changes.Skip(staged).Take(Math.Clamp(runProfile.PageSize, 1, MaximumPageSize)).ToList();
        await progress.EnterPhaseAsync(GraphConnectorPhases.Fetch, $"Fetching changed {target.Collection}...");

        result.ImportObjects.AddRange(page.Where(c => c.Removed).Select(c => Deletion(target, c.Id)));
        var read = await ReadCurrentStateAsync(target, page.Where(c => !c.Removed).Select(c => c.Id).ToList(), cancellationToken);
        if (target.Members || target.Owners)
            await ReadRelationshipsAsync(read, target, cancellationToken);
        result.ImportObjects.AddRange(read.Select(s => s.ImportObject));
        await progress.ReportObjectsReadAsync(result.ImportObjects.Count);

        var next = NextChange(plan, targetIndex, staged + page.Count);
        if (next.TargetIndex < plan.Targets.Count)
            result.PaginationTokens.Add(new GraphImportPosition(next.TargetIndex, null, next.ChangesStaged).ToToken());
        else
            Finish(result, plan);

        _logger.Debug("Microsoft Graph Delta Import: staged {Count} changed {ObjectType} object(s).", result.ImportObjects.Count, target.Name);
        return result;
    }

    /// <summary>Where the next change to stage is, passing over Object Types with none left; past the last type when none remain.</summary>
    private (int TargetIndex, int ChangesStaged) NextChange(GraphImportPlan plan, int targetIndex, int staged)
    {
        while (targetIndex < plan.Targets.Count && staged >= _changes[plan.Targets[targetIndex].Name].Count)
            (targetIndex, staged) = (targetIndex + 1, 0);

        return (targetIndex, staged);
    }

    /// <summary>
    /// Reads changed objects again for their current state, twenty a <c>$batch</c>, as a Full Import reads them. An object
    /// Graph cannot find yet (replication delay) is read again after a pause; one it still cannot find fails the run.
    /// Staged as deleted it could delete an object that exists, and left out its change would be lost when the link
    /// advanced. Failing keeps the old link, and the next run either reads it or is told it was removed.
    /// </summary>
    private async Task<List<StagedObject>> ReadCurrentStateAsync(GraphImportTarget target, List<string> ids, CancellationToken cancellationToken)
    {
        var staged = new List<StagedObject>(ids.Count);
        var outstanding = ids;
        for (var attempt = 0; outstanding.Count > 0; attempt++)
        {
            var notFound = new List<string>();
            foreach (var chunk in outstanding.Chunk(GraphHttpClient.MaximumBatchSize))
            {
                var responses = await _client.SendBatchAsync(chunk.Select(id => new GraphBatchRequest(HttpMethod.Get, ObjectUrl(target, id))).ToList(), cancellationToken);
                for (var i = 0; i < chunk.Length; i++)
                {
                    var response = responses[i];
                    if (response is { IsSuccess: true, Body: { } body })
                        staged.Add(Stage(body, target, ObjectChangeType.Updated));
                    else if (response.StatusCode == HttpStatusCode.NotFound)
                        notFound.Add(chunk[i]);
                    else
                        throw new GraphRequestException(
                            $"Microsoft Graph reported a change to {target.Name} {LogSanitiser.Sanitise(chunk[i])}, but reading it failed " +
                            $"({(response.Status == 0 ? response.Error : $"HTTP {response.Status}")}). The run stops so the change is read again by the next Delta Import.",
                            response.Status == 0 ? HttpStatusCode.ServiceUnavailable : response.StatusCode);
                }
            }

            if (notFound.Count == 0)
                break;

            if (attempt >= NotFoundRetryDelays.Length)
                throw new GraphRequestException(
                    $"Microsoft Graph reported changes to {notFound.Count:N0} {target.Collection} it then could not find: " +
                    $"{string.Join(", ", notFound.Take(5).Select(id => LogSanitiser.Sanitise(id)))}{(notFound.Count > 5 ? ", ..." : "")}. " +
                    "The run stops rather than stage them as deleted or lose their changes, and the next Delta Import reads them again; " +
                    "if this persists, run a Full Import.",
                    HttpStatusCode.NotFound);

            _logger.Information("Microsoft Graph Delta Import: {Count} changed {Collection} cannot be found yet. Reading them again in {Delay}.",
                notFound.Count, target.Collection, NotFoundRetryDelays[attempt]);
            await _client.DelayAsync(NotFoundRetryDelays[attempt], cancellationToken);
            outstanding = notFound;
        }

        return staged;
    }

    private static string ObjectUrl(GraphImportTarget target, string id)
    {
        var url = $"/{target.Collection}/{Uri.EscapeDataString(id)}?$select={string.Join(',', target.Select)}";
        return target.Manager ? $"{url}&$expand=manager($select=id)" : url;
    }

    private static ConnectedSystemImportObject Deletion(GraphImportTarget target, string id) => new()
    {
        ObjectType = target.Name,
        ChangeType = ObjectChangeType.Deleted,
        Attributes = [new ConnectedSystemImportObjectAttribute { Name = GraphSchemaCatalogue.Id, Type = Models.Core.AttributeDataType.Text, StringValues = [id] }]
    };

    #endregion

    /// <summary>The run's last page: the links and what they were taken to track, for the next Delta Import.</summary>
    private ConnectedSystemImportResult Finish(ConnectedSystemImportResult result, GraphImportPlan plan)
    {
        result.PersistedConnectorData = new GraphImportState
        {
            DeltaLinks = new Dictionary<string, string>(_deltaLinks, StringComparer.OrdinalIgnoreCase),
            CapturedAt = _startedAt,
            Signatures = plan.Targets.ToDictionary(t => t.Name, t => t.Signature, StringComparer.OrdinalIgnoreCase),
            CloudManagedHashes = new Dictionary<string, string?>(_cloudManagedHashes, StringComparer.OrdinalIgnoreCase)
        }.Serialise();
        return result;
    }

    /// <summary>
    /// Asks which synchronised objects have had their Source of Authority switched to Entra ID, which Graph cannot report
    /// per object in a list: such an object still reads <c>onPremisesSyncEnabled</c> as true. Only with tracking on. One paged query per Object Type,
    /// selecting only ids. Refused (the permission is missing, or the cloud does not offer the filter), the run carries
    /// on deriving from synchronisation alone, and says so.
    /// </summary>
    /// <returns>A warning for the run, or null.</returns>
    private async Task<string?> LoadCloudManagedAsync(GraphImportTarget target, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _cloudManaged[target.Name] = ids;
        _cloudManagedHashes[target.Name] = null;

        var filter = Uri.EscapeDataString("onPremisesSyncBehavior/isCloudManaged eq true");
        string? url = $"{target.Collection}?$filter={filter}&$count=true&$select=id&$top={MaximumPageSize}";
        try
        {
            while (url != null)
            {
                var page = await _client.SendJsonAsync<GraphCollectionPage<JsonElement>>(HttpMethod.Get, url, null,
                    new GraphRequestOptions { EventualConsistency = true }, cancellationToken);
                ids.UnionWith((page?.Value ?? []).Select(ReadId).Where(id => id != null)!);
                url = page?.NextLink;
            }

            _cloudManagedHashes[target.Name] = Hash(ids);
            return null;
        }
        catch (GraphRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
        {
            _logger.Warning(ex, "Microsoft Graph refused the query for cloud-managed {Collection}.", target.Collection);
            var permission = target.IsUser ? GraphPermissions.UserSourceOfAuthority : GraphPermissions.GroupSourceOfAuthority;
            return $"Track Source of Authority Changes is on, but Microsoft Graph would not say which synchronised {target.Collection} have had their Source of Authority switched to Entra ID ({ex.ErrorCode ?? $"HTTP {(int)ex.StatusCode}"}), " +
                   $"so a switched one reads as onPremisesDirectory. Grant the {permission} application permission, or turn the setting off.";
        }
    }

    /// <summary>The set as a short, order-independent value, so the persisted state stays small however many objects it holds.</summary>
    private static string Hash(HashSet<string> ids) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ids.Select(id => id.ToLowerInvariant()).Order(StringComparer.Ordinal)))));

    private StagedObject Stage(JsonElement graphObject, GraphImportTarget target, ObjectChangeType changeType)
    {
        var read = GraphObjectReader.Read(graphObject, target.Attributes);
        var cloudManaged = _cloudManaged.GetValueOrDefault(target.Name);
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = target.Name,
            ChangeType = changeType,
            Attributes = read.Attributes
        };

        importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
        {
            Name = GraphSchemaCatalogue.SourceOfAuthority,
            Type = Models.Core.AttributeDataType.Text,
            StringValues = [GraphSourceOfAuthority.Derive(graphObject, target.IsUser, read.Id != null && cloudManaged?.Contains(read.Id) == true)]
        });

        if (target.Manager && graphObject.TryGetProperty(GraphSchemaCatalogue.Manager, out var manager) && ReadId(manager) is { } managerId)
            importObject.Attributes.Add(Reference(GraphSchemaCatalogue.Manager, [managerId]));

        // Each value that could not be converted is reported, and JIM leaves its attribute as it already held it (#2066).
        foreach (var unreadable in read.UnreadableValues)
            importObject.AddAttributeValueError(unreadable.AttributeName, unreadable.Value, unreadable.Reason);

        var hiddenMembership = string.Equals(ReadString(graphObject, GraphSchemaCatalogue.Visibility), GraphSchemaCatalogue.HiddenMembership, StringComparison.OrdinalIgnoreCase);
        return new StagedObject(read.Id, importObject, hiddenMembership);
    }

    /// <summary>
    /// Reads each group's members and owners in full, twenty requests a <c>$batch</c>, following a large group's further
    /// pages on their own. A group whose relationship cannot be read is reported against that attribute, so JIM keeps
    /// the membership it holds rather than read a partial one as removals.
    /// </summary>
    private async Task ReadRelationshipsAsync(List<StagedObject> groups, GraphImportTarget target, CancellationToken cancellationToken)
    {
        // A hidden membership is not asked for without the permission to read it: Graph's documentation does not say what
        // it answers, and an empty list would read as a group with no members.
        var hiddenFromUs = target.Members && !target.ReadsHiddenMembership;
        foreach (var group in groups.Where(g => hiddenFromUs && g.HiddenMembership))
            ReportUnreadableRelationship(group, GraphSchemaCatalogue.Members,
                $"Its membership is hidden, and reading a hidden membership needs the {GraphPermissions.MemberReadHidden} application permission, which the app registration has not been granted.");

        var requests = groups
            .Where(g => g.Id != null)
            .SelectMany(g => new[]
            {
                target.Members && !(hiddenFromUs && g.HiddenMembership) ? (Group: g, Relationship: GraphSchemaCatalogue.Members) : default,
                target.Owners ? (Group: g, Relationship: GraphSchemaCatalogue.Owners) : default
            })
            .Where(r => r.Group != null)
            .ToList();

        foreach (var chunk in requests.Chunk(GraphHttpClient.MaximumBatchSize))
        {
            var responses = await _client.SendBatchAsync(
                chunk.Select(r => new GraphBatchRequest(HttpMethod.Get, $"/groups/{Uri.EscapeDataString(r.Group.Id!)}/{r.Relationship}?$select=id&$top={MaximumPageSize}")).ToList(),
                cancellationToken);

            for (var i = 0; i < chunk.Length; i++)
                await StageRelationshipAsync(chunk[i].Group, chunk[i].Relationship, responses[i], cancellationToken);
        }
    }

    private async Task StageRelationshipAsync(StagedObject group, string relationship, GraphBatchResponse response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccess)
        {
            ReportUnreadableRelationship(group, relationship, response.Status == 0
                ? response.Error!
                : $"Microsoft Graph answered HTTP {response.Status} when asked for the group's {relationship}.");
            return;
        }

        var ids = new List<string>();
        var allowed = relationship == GraphSchemaCatalogue.Members ? new[] { UserType, GroupType } : [UserType];
        var page = response.Body?.Deserialize<GraphCollectionPage<JsonElement>>();
        try
        {
            while (page != null)
            {
                // Members JIM does not import (devices, contacts, service principals) are left out: a reference to them
                // could never resolve, and exports change membership one member at a time, so leaving them out never
                // removes them.
                ids.AddRange(page.Value.Where(m => allowed.Contains(ReadString(m, "@odata.type"))).Select(ReadId).Where(id => id != null)!);
                page = page.NextLink == null
                    ? null
                    : await _client.SendJsonAsync<GraphCollectionPage<JsonElement>>(HttpMethod.Get, page.NextLink, null, null, cancellationToken);
            }
        }
        catch (GraphRequestException ex)
        {
            _logger.Warning(ex, "Microsoft Graph import: could not read every {Relationship} of group {GroupId}.", relationship, LogSanitiser.Sanitise(group.Id));
            ReportUnreadableRelationship(group, relationship, LogSanitiser.Sanitise(ex.Message) ?? "");
            return;
        }

        if (ids.Count > 0)
            group.ImportObject.Attributes.Add(Reference(relationship, ids));
    }

    private static void ReportUnreadableRelationship(StagedObject group, string relationship, string reason) =>
        group.ImportObject.AddAttributeValueError(relationship, null,
            $"The group's {relationship} could not be read, so JIM keeps the {relationship} it already holds. {reason}");

    private static ConnectedSystemImportObjectAttribute Reference(string name, List<string> ids) =>
        new() { Name = name, Type = Models.Core.AttributeDataType.Reference, ReferenceValues = ids };

    private static string? ReadId(JsonElement element) => ReadString(element, GraphSchemaCatalogue.Id);

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record StagedObject(string? Id, ConnectedSystemImportObject ImportObject, bool HiddenMembership);

    /// <summary>An object a delta round reported, by its last appearance in the round.</summary>
    private sealed record GraphChange(string Id, bool Removed);

    /// <summary>How the run reads, settled before its first page.</summary>
    /// <param name="FellBack">A Delta Import reading every object because it could not read the changes.</param>
    private sealed record GraphImportPlan(List<GraphImportTarget> Targets, bool ReadsEverything, bool FellBack, List<string> Warnings);
}
