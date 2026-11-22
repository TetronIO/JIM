// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.Graph;

/// <summary>
/// Writes Pending Exports to Microsoft Entra ID through <c>$batch</c>, twenty requests a call. Each Pending Export's
/// create, update or delete goes in the first round; what can only follow it (a new user's manager, members beyond the
/// twenty a create carries, removals) goes in later rounds, batched across every Pending Export alike.
/// </summary>
/// <remarks>
/// <para>
/// JIM records one result per Pending Export and sends a failed one again whole, so every step is safe to send again: a
/// member already present counts as added, and a member, manager or object already gone counts as removed or deleted.
/// </para>
/// <para>
/// Graph is eventually consistent: an object created moments earlier, often by the same export (a group nested in the
/// next, a manager), can be refused as not found when another write refers to it. Such a write is sent again after 2, 5
/// and 10 seconds, as a Delta Import reads a changed object again, and only then reported as a Missing Dependency for
/// JIM to send again on a later export. A refused create made nothing, so sending it again cannot make a second object.
/// </para>
/// <para>
/// A create that succeeded reports its new id whatever its follow-ups did. Failing it would have JIM send the create
/// again and make a second object; a follow-up left unwritten is found by the confirming import, which stages it again.
/// </para>
/// </remarks>
internal sealed class GraphConnectorExport
{
    private readonly GraphHttpClient _client;
    private readonly bool _disableUsersInsteadOfDeleting;
    private readonly ILogger _logger;

    /// <summary>How long to wait before each time a write refused for a reference Graph cannot find yet is sent again.</summary>
    internal static readonly TimeSpan[] MissingReferenceRetryDelays = GraphConnectorImport.NotFoundRetryDelays;

    public GraphConnectorExport(GraphHttpClient client, bool disableUsersInsteadOfDeleting, ILogger logger)
    {
        _client = client;
        _disableUsersInsteadOfDeleting = disableUsersInsteadOfDeleting;
        _logger = logger;
    }

    /// <summary>Writes the Pending Exports and returns one result for each, in the same order.</summary>
    /// <param name="cancellationToken">
    /// Observed only before anything is sent. Once the first batch is on its way the rest of the call completes, since
    /// a later step may be the one that finishes an earlier one.
    /// </param>
    public async Task<List<ConnectedSystemExportResult>> ExportAsync(IList<PendingExport> pendingExports, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var states = pendingExports.Select(GraphExportState.Plan).ToList();
        var steps = new List<GraphExportStep>();
        foreach (var state in states.Where(s => s.Failure == null))
            steps.AddRange(await FirstStepsAsync(state));

        var batches = 0;
        while (steps.Count > 0)
        {
            var next = new List<GraphExportStep>();
            foreach (var chunk in steps.Chunk(GraphHttpClient.MaximumBatchSize))
            {
                batches++;
                var responses = await _client.SendBatchAsync(chunk.Select(s => s.Request).ToList(), CancellationToken.None);
                for (var i = 0; i < chunk.Length; i++)
                    next.AddRange(await HandleAsync(chunk[i], responses[i]));
            }

            // A step sent again (Attempt above zero) waits for replication first; the longest wait due covers them all.
            var retryAttempt = next.Count == 0 ? 0 : next.Max(s => s.Attempt);
            if (retryAttempt > 0)
            {
                var delay = MissingReferenceRetryDelays[retryAttempt - 1];
                _logger.Information("Microsoft Graph export: {Count} write(s) refer to objects Graph cannot find yet. Sending them again in {Delay}.",
                    next.Count(s => s.Attempt > 0), delay);
                await _client.DelayAsync(delay, CancellationToken.None);
            }

            steps = next;
        }

        var results = states.Select(Result).ToList();
        _logger.Information("Microsoft Graph export: {Total} Pending Export(s) in {Batches} batch(es): {Succeeded} succeeded, {Failed} failed.",
            results.Count, batches, results.Count(r => r.Success), results.Count(r => !r.Success));
        return results;
    }

    /// <summary>A Pending Export's first step, or, for an update that changes no property, its follow-ups straight away.</summary>
    private async Task<List<GraphExportStep>> FirstStepsAsync(GraphExportState state)
    {
        switch (state.PendingExport.ChangeType)
        {
            case PendingExportChangeType.Create:
                return [new GraphExportStep(state, GraphExportStepKind.Create, new GraphBatchRequest(HttpMethod.Post, $"/{state.Collection}", CreateBody(state)))];

            case PendingExportChangeType.Delete when state.IsUser && _disableUsersInsteadOfDeleting:
                return [new GraphExportStep(state, GraphExportStepKind.Disable,
                    new GraphBatchRequest(HttpMethod.Patch, ObjectPath(state), new JsonObject { ["accountEnabled"] = false }))];

            case PendingExportChangeType.Delete:
                return [new GraphExportStep(state, GraphExportStepKind.Delete, new GraphBatchRequest(HttpMethod.Delete, ObjectPath(state)))];

            default:
                return state.Properties.Count > 0
                    ? [new GraphExportStep(state, GraphExportStepKind.Update, new GraphBatchRequest(HttpMethod.Patch, ObjectPath(state), state.Properties.DeepClone()))]
                    : await FollowUpsAsync(state);
        }
    }

    /// <summary>
    /// A create's body: its properties, a new user's password, and a new group's first twenty members and owners, which
    /// Graph takes on the create itself.
    /// </summary>
    private JsonObject CreateBody(GraphExportState state)
    {
        var body = (JsonObject)state.Properties.DeepClone();
        if (state.IsUser)
        {
            body["passwordProfile"] = new JsonObject
            {
                ["password"] = GraphPasswordGenerator.Generate(),
                ["forceChangePasswordNextSignIn"] = true
            };
            return body;
        }

        foreach (var (relationship, additions) in new[] { GraphSchemaCatalogue.Members, GraphSchemaCatalogue.Owners }
                     .Select(r => (Relationship: r, Additions: state.Additions.GetValueOrDefault(r)))
                     .Where(r => r.Additions is { Count: > 0 }))
        {
            var pending = additions!;
            var bound = pending.Take(GraphHttpClient.MaximumBatchSize).ToList();
            body[$"{relationship}@odata.bind"] = new JsonArray(bound.Select(id => (JsonNode)JsonValue.Create(ReferenceUrl(id))).ToArray());
            pending.RemoveRange(0, bound.Count);
        }

        return body;
    }

    private async Task<List<GraphExportStep>> HandleAsync(GraphExportStep step, GraphBatchResponse response)
    {
        var state = step.State;
        switch (step.Kind)
        {
            case GraphExportStepKind.Create:
                if (!response.IsSuccess)
                {
                    // Only a create that binds members or owners refers to anything; a 404 on any other is not a missing reference.
                    var bindsReferences = step.Request.Body is JsonObject body && body.Any(p => p.Key.EndsWith("@odata.bind", StringComparison.Ordinal));
                    if (bindsReferences && CanSendAgain(step, response))
                        return [Again(step)];

                    state.Failure = GraphExportErrorClassifier.Classify(response, $"create the {Singular(state)}");
                    if (bindsReferences && GraphExportErrorClassifier.IsMissingReference(response))
                        state.Failure.ErrorType = ConnectedSystemExportErrorType.MissingDependency;
                    return [];
                }

                state.Id = response.Body is { ValueKind: JsonValueKind.Object } created && created.TryGetProperty("id", out var id) ? id.GetString() : null;
                if (string.IsNullOrWhiteSpace(state.Id))
                {
                    state.Failure = ConnectedSystemExportResult.Failed($"Microsoft Graph created the {Singular(state)} but did not return its id.");
                    return [];
                }

                state.Created = true;
                return await FollowUpsAsync(state);

            case GraphExportStepKind.Update:
                if (!response.IsSuccess)
                {
                    state.Failure = GraphExportErrorClassifier.Classify(response, $"update the {Singular(state)}");
                    return [];
                }

                return await FollowUpsAsync(state);

            case GraphExportStepKind.Delete or GraphExportStepKind.Disable:
                if (response.IsSuccess || response.StatusCode == HttpStatusCode.NotFound)
                {
                    state.SetsAccountEnabledState |= step.Kind == GraphExportStepKind.Disable && response.IsSuccess;
                    return [];
                }

                state.Failure = GraphExportErrorClassifier.Classify(response, step.Kind == GraphExportStepKind.Delete ? $"delete the {Singular(state)}" : "disable the user");
                return [];

            case GraphExportStepKind.RemoveManager or GraphExportStepKind.RemoveReference:
                if (!response.IsSuccess && response.StatusCode != HttpStatusCode.NotFound)
                    state.FollowUpFailures.Add(GraphExportErrorClassifier.Classify(response, Describe(step)));
                return [];

            case GraphExportStepKind.SetManager:
                if (response.IsSuccess)
                    return [];
                if (CanSendAgain(step, response))
                    return [Again(step)];
                state.FollowUpFailures.Add(MissingOrClassified(response, step));
                return [];

            case GraphExportStepKind.AddReferences:
                return HandleAddReferences(step, response);

            default:
                throw new InvalidOperationException($"Unhandled Microsoft Graph export step {step.Kind}.");
        }
    }

    /// <summary>
    /// Graph adds a <c>PATCH</c>'s members all or none: one already present or not yet there refuses them all, and the
    /// answer does not say which. Such a refusal is sent again one member at a time, so the rest are added and only the
    /// one at fault is reported.
    /// </summary>
    private List<GraphExportStep> HandleAddReferences(GraphExportStep step, GraphBatchResponse response)
    {
        if (response.IsSuccess || GraphExportErrorClassifier.IsAlreadyPresent(response) && step.References!.Count == 1)
            return [];

        if (step.References!.Count > 1 && response.Status == (int)HttpStatusCode.BadRequest)
            return step.References.Select(reference => AddOne(step.State, step.Relationship!, reference)).ToList();

        if (CanSendAgain(step, response))
            return [Again(step)];

        step.State.FollowUpFailures.Add(MissingOrClassified(response, step));
        return [];
    }

    /// <summary>Whether a refusal is for a reference Graph cannot find yet, and the step has waits left.</summary>
    private static bool CanSendAgain(GraphExportStep step, GraphBatchResponse response) =>
        GraphExportErrorClassifier.IsMissingReference(response) && step.Attempt < MissingReferenceRetryDelays.Length;

    private static GraphExportStep Again(GraphExportStep step) => step with { Attempt = step.Attempt + 1 };

    private static ConnectedSystemExportResult MissingOrClassified(GraphBatchResponse response, GraphExportStep step)
    {
        var classified = GraphExportErrorClassifier.Classify(response, Describe(step));
        if (GraphExportErrorClassifier.IsMissingReference(response))
            classified.ErrorType = ConnectedSystemExportErrorType.MissingDependency;
        return classified;
    }

    /// <summary>
    /// What follows a Pending Export's first step once the object's id is known: the manager, members and owners to add
    /// (members twenty a request, owners one at a time) and every removal.
    /// </summary>
    private async Task<List<GraphExportStep>> FollowUpsAsync(GraphExportState state)
    {
        var steps = new List<GraphExportStep>();
        if (state.Manager is { } manager)
        {
            steps.Add(manager.Length == 0
                ? new GraphExportStep(state, GraphExportStepKind.RemoveManager, new GraphBatchRequest(HttpMethod.Delete, $"{ObjectPath(state)}/manager/$ref"))
                : new GraphExportStep(state, GraphExportStepKind.SetManager,
                    new GraphBatchRequest(HttpMethod.Put, $"{ObjectPath(state)}/manager/$ref", new JsonObject { ["@odata.id"] = new Uri(_client.Cloud.ApiBaseUrl, $"users/{Uri.EscapeDataString(manager)}").ToString() }),
                    References: [manager]));
        }

        foreach (var relationship in state.RemoveAll)
        {
            var keep = state.Additions.GetValueOrDefault(relationship) ?? [];
            var held = await ReadRelationshipAsync(state, relationship);
            if (held == null)
                continue;

            var removals = state.Removals.TryGetValue(relationship, out var existing) ? existing : state.Removals[relationship] = [];
            removals.AddRange(held.Where(id => !keep.Contains(id, StringComparer.OrdinalIgnoreCase) && !removals.Contains(id, StringComparer.OrdinalIgnoreCase)));
        }

        foreach (var (relationship, additions) in state.Additions)
        {
            if (relationship == GraphSchemaCatalogue.Members)
            {
                steps.AddRange(additions.Chunk(GraphHttpClient.MaximumBatchSize).Select(chunk => new GraphExportStep(state, GraphExportStepKind.AddReferences,
                    new GraphBatchRequest(HttpMethod.Patch, ObjectPath(state),
                        new JsonObject { ["members@odata.bind"] = new JsonArray(chunk.Select(id => (JsonNode)JsonValue.Create(ReferenceUrl(id))).ToArray()) }),
                    relationship, chunk)));
            }
            else
            {
                steps.AddRange(additions.Select(id => AddOne(state, relationship, id)));
            }
        }

        foreach (var (relationship, removals) in state.Removals)
        {
            steps.AddRange(removals.Select(id => new GraphExportStep(state, GraphExportStepKind.RemoveReference,
                new GraphBatchRequest(HttpMethod.Delete, $"{ObjectPath(state)}/{relationship}/{Uri.EscapeDataString(id)}/$ref"), relationship, [id])));
        }

        // Each follow-up is built once: a create's second round, or an update's, never repeats them.
        state.Additions.Clear();
        state.Removals.Clear();
        state.RemoveAll.Clear();
        return steps;
    }

    private GraphExportStep AddOne(GraphExportState state, string relationship, string reference) =>
        new(state, GraphExportStepKind.AddReferences,
            new GraphBatchRequest(HttpMethod.Post, $"{ObjectPath(state)}/{relationship}/$ref", new JsonObject { ["@odata.id"] = ReferenceUrl(reference) }),
            relationship, [reference]);

    /// <summary>
    /// Every member or owner Graph holds, for a change that removes them all: Graph has no request that empties a
    /// relationship, so each is removed in turn. Read now rather than from what JIM holds, which for a large group is
    /// loaded only in part and names members by JIM's own ids.
    /// </summary>
    private async Task<List<string>?> ReadRelationshipAsync(GraphExportState state, string relationship)
    {
        var ids = new List<string>();
        string? url = $"{ObjectPath(state).TrimStart('/')}/{relationship}?$select=id&$top={GraphConnectorImport.MaximumPageSize.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            while (url != null)
            {
                var page = await _client.SendJsonAsync<GraphCollectionPage<JsonElement>>(HttpMethod.Get, url, null, null, CancellationToken.None);
                ids.AddRange((page?.Value ?? []).Select(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .Where(id => id != null)!);
                url = page?.NextLink;
            }

            return ids;
        }
        catch (GraphRequestException ex)
        {
            _logger.Warning(ex, "Microsoft Graph export: could not read the {Relationship} of group {GroupId} to remove them all.", relationship, LogSanitiser.Sanitise(state.Id));
            state.FollowUpFailures.Add(ConnectedSystemExportResult.Failed(
                $"Every {relationship[..^1]} was to be removed, but Microsoft Graph would not list them: {ex.Message}",
                ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? ConnectedSystemExportErrorType.AccessDenied : ConnectedSystemExportErrorType.General));
            return null;
        }
    }

    private ConnectedSystemExportResult Result(GraphExportState state)
    {
        if (state.Failure != null)
            return state.Failure;

        if (state.Created)
        {
            foreach (var failure in state.FollowUpFailures)
            {
                _logger.Warning("Microsoft Graph export: created {ObjectType} {Id}, but a follow-up failed and is left for the confirming import to stage again: {Error}",
                    Singular(state), LogSanitiser.Sanitise(state.Id), LogSanitiser.Sanitise(failure.ErrorMessage));
            }

            var created = ConnectedSystemExportResult.Succeeded(state.Id);
            created.SetsAccountEnabledState = state.SetsAccountEnabledState;
            return created;
        }

        if (state.FollowUpFailures.Count > 0)
        {
            var first = state.FollowUpFailures[0];
            var more = state.FollowUpFailures.Count - 1;
            return ConnectedSystemExportResult.Failed(
                more == 0 ? first.ErrorMessage! : $"{first.ErrorMessage} ({more:N0} more change(s) to this {Singular(state)} also failed.)",
                first.ErrorType ?? ConnectedSystemExportErrorType.General);
        }

        var succeeded = ConnectedSystemExportResult.Succeeded();
        succeeded.SetsAccountEnabledState = state.SetsAccountEnabledState;
        return succeeded;
    }

    private static string ObjectPath(GraphExportState state) => $"/{state.Collection}/{Uri.EscapeDataString(state.Id!)}";

    private string ReferenceUrl(string id) => new Uri(_client.Cloud.ApiBaseUrl, $"directoryObjects/{Uri.EscapeDataString(id)}").ToString();

    private static string Singular(GraphExportState state) => state.IsUser ? "user" : "group";

    private static string Describe(GraphExportStep step) => step.Kind switch
    {
        GraphExportStepKind.SetManager => $"set the user's manager to {LogSanitiser.Sanitise(step.References?[0])}",
        GraphExportStepKind.RemoveManager => "remove the user's manager",
        GraphExportStepKind.AddReferences => step.References!.Count == 1
            ? $"add {LogSanitiser.Sanitise(step.References[0])} to the group's {step.Relationship}"
            : $"add {step.References.Count} {step.Relationship} to the group",
        GraphExportStepKind.RemoveReference => $"remove {LogSanitiser.Sanitise(step.References?[0])} from the group's {step.Relationship}",
        _ => step.Kind.ToString()
    };
}
