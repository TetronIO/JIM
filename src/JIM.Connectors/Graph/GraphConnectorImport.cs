// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.Graph;

/// <summary>
/// Reads Microsoft Entra ID users and groups for a Full Import, a page at a time. Held for the length of the run, so
/// what is learned once per Object Type (the delta baseline, which objects are cloud-managed) is learned once.
/// </summary>
/// <remarks>
/// A page of groups is completed before it is returned: each group's members (and owners) are read in full through
/// <c>$batch</c>, so a group is never staged with part of its membership, which JIM would read as removals.
/// </remarks>
internal sealed class GraphConnectorImport
{
    /// <summary>Graph's largest page for users and groups.</summary>
    public const int MaximumPageSize = 999;

    private const string UserType = "#microsoft.graph.user";
    private const string GroupType = "#microsoft.graph.group";

    private readonly GraphHttpClient _client;
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _deltaLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _cloudManaged = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public GraphConnectorImport(GraphHttpClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<ConnectedSystemImportResult> ImportPageAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        GraphImportPosition position,
        IConnectorProgress progress,
        CancellationToken cancellationToken)
    {
        // Every page of a Full Import reads every object in scope, so JIM detects deletions by absence (#2720).
        var result = new ConnectedSystemImportResult { CompleteRead = true };
        var targets = GraphImportTarget.For(connectedSystem);
        if (position.TargetIndex >= targets.Count)
            return result;

        cancellationToken.ThrowIfCancellationRequested();
        var target = targets[position.TargetIndex];

        if (position.NextLink == null)
        {
            await progress.EnterPhaseAsync(GraphConnectorPhases.Prepare, $"Preparing to read {target.Collection}...");
            _deltaLinks[target.Name] = await TakeDeltaBaselineAsync(target, cancellationToken);
            result.WarningMessage = await LoadCloudManagedAsync(target, cancellationToken);
        }

        await progress.EnterPhaseAsync(GraphConnectorPhases.Fetch, $"Fetching {target.Collection}...");
        var url = position.NextLink ?? FirstPageUrl(target, runProfile.PageSize);
        var page = await _client.SendJsonAsync<GraphCollectionPage<JsonElement>>(HttpMethod.Get, url, null, null, cancellationToken)
                   ?? new GraphCollectionPage<JsonElement>();

        var cloudManaged = _cloudManaged.GetValueOrDefault(target.Name) ?? [];
        var staged = page.Value.Select(o => Stage(o, target, cloudManaged)).ToList();
        if (target.Members || target.Owners)
            await ReadRelationshipsAsync(staged, target, cancellationToken);

        result.ImportObjects.AddRange(staged.Select(s => s.ImportObject));
        await progress.ReportObjectsReadAsync(result.ImportObjects.Count);

        if (page.NextLink != null)
            result.PaginationTokens.Add(new GraphImportPosition(position.TargetIndex, page.NextLink).ToToken());
        else if (position.TargetIndex + 1 < targets.Count)
            result.PaginationTokens.Add(new GraphImportPosition(position.TargetIndex + 1, null).ToToken());
        else
            result.PersistedConnectorData = new GraphImportState { DeltaLinks = new(_deltaLinks, StringComparer.OrdinalIgnoreCase), CapturedAt = _startedAt }.Serialise();

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

    /// <summary>
    /// Asks which synchronised objects have been converted to cloud management, which Graph cannot report per object in
    /// a list: such an object still reads <c>onPremisesSyncEnabled</c> as true. One paged query per Object Type,
    /// selecting only ids. Refused (the permission is missing, or the cloud does not offer the filter), the run carries
    /// on deriving from synchronisation alone, and says so.
    /// </summary>
    /// <returns>A warning for the run, or null.</returns>
    private async Task<string?> LoadCloudManagedAsync(GraphImportTarget target, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _cloudManaged[target.Name] = ids;

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

            return null;
        }
        catch (GraphRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
        {
            _logger.Warning(ex, "Microsoft Graph refused the query for cloud-managed {Collection}.", target.Collection);
            var permission = target.IsUser ? GraphPermissions.UserSyncBehaviourRead : GraphPermissions.GroupSyncBehaviourRead;
            return $"JIM could not ask Microsoft Graph which synchronised {target.Collection} have moved to cloud management ({ex.ErrorCode ?? $"HTTP {(int)ex.StatusCode}"}), " +
                   $"so their sourceOfAuthority comes from onPremisesSyncEnabled alone, and one converted to cloud management reads as onPremisesDirectory. " +
                   $"Grant the {permission} application permission to tell them apart.";
        }
    }

    private static StagedObject Stage(JsonElement graphObject, GraphImportTarget target, HashSet<string> cloudManaged)
    {
        var read = GraphObjectReader.Read(graphObject, target.Attributes);
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = target.Name,
            // A Full Import asserts the object exists; JIM decides whether that is a create or an update.
            ChangeType = ObjectChangeType.Added,
            Attributes = read.Attributes
        };

        importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
        {
            Name = GraphSchemaCatalogue.SourceOfAuthority,
            Type = Models.Core.AttributeDataType.Text,
            StringValues = [GraphSourceOfAuthority.Derive(graphObject, target.IsUser, read.Id != null && cloudManaged.Contains(read.Id))]
        });

        if (target.Manager && graphObject.TryGetProperty(GraphSchemaCatalogue.Manager, out var manager) && ReadId(manager) is { } managerId)
            importObject.Attributes.Add(Reference(GraphSchemaCatalogue.Manager, [managerId]));

        // Each value that could not be converted is reported, and JIM leaves its attribute as it already held it (#2066).
        foreach (var unreadable in read.UnreadableValues)
            importObject.AddAttributeValueError(unreadable.AttributeName, unreadable.Value, unreadable.Reason);

        return new StagedObject(read.Id, importObject);
    }

    /// <summary>
    /// Reads each group's members and owners in full, twenty requests a <c>$batch</c>, following a large group's further
    /// pages on their own. A group whose relationship cannot be read is reported against that attribute, so JIM keeps
    /// the membership it holds rather than read a partial one as removals.
    /// </summary>
    private async Task ReadRelationshipsAsync(List<StagedObject> groups, GraphImportTarget target, CancellationToken cancellationToken)
    {
        var requests = groups
            .Where(g => g.Id != null)
            .SelectMany(g => new[]
            {
                target.Members ? (Group: g, Relationship: GraphSchemaCatalogue.Members) : default,
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

    private sealed record StagedObject(string? Id, ConnectedSystemImportObject ImportObject);
}
