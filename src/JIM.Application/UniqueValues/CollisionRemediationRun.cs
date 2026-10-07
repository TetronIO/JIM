// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Exceptions;
using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.UniqueValues;

/// <summary>
/// One export run's Collision Remediation (Unique Value Generation, #242, release 4; plan decisions 8 to 12 as revised
/// at the release 4 plan review). Handed every export the Connected System rejected because a value it carried is
/// already in use, it decides what that rejection means for the generated value behind it:
/// <list type="bullet">
/// <item><description><b>Remediate</b>: Collision Remediation is on for the flow, the Connector classifies such
/// rejections, and the value is not anchored by another system (or an administrator has authorised the rename). The next
/// value is drawn through the ordinary gates, the assignment becomes Remediated, and the value is revised: in import
/// mode on the Metaverse Object (with a revision-pending record the next synchronisation drains, and derived-input marks
/// so dependants re-derive), in export mode on the queued export itself. The rejected export stays Pending, with its
/// error count untouched, and is not retried inside this run.</description></item>
/// <item><description><b>Needs Decision</b>: the value is anchored, its anchoring cannot be told, or remediation has
/// been exhausted. The assignment waits on an administrator and the export is Parked.</description></item>
/// <item><description><b>Ordinary export error</b> (a null result): the switch is off, the rejection cannot be
/// attributed to one generated value, or the rejection is of a value JIM has already replaced.</description></item>
/// </list>
/// <para>
/// Thread-safe for the export's parallel batches: the run's configuration is read once, under a gate, by whichever
/// batch meets a rejection first, and is read-only thereafter; every write goes through the calling batch's own
/// repository; and every value drawn is claimed in the process-wide reservation set under this run's owner id.
/// </para>
/// </summary>
internal sealed class CollisionRemediationRun
{
    private readonly JimApplication _application;
    private readonly ConnectedSystem _connectedSystem;
    private readonly bool _metaverseObjectChangeTrackingEnabled;
    private readonly SemaphoreSlim _contextGate = new(1, 1);
    private Context? _context;

    public CollisionRemediationRun(JimApplication application, ConnectedSystem connectedSystem, UniqueValueReservationSet reservations, bool metaverseObjectChangeTrackingEnabled)
    {
        _application = application;
        _connectedSystem = connectedSystem;
        _metaverseObjectChangeTrackingEnabled = metaverseObjectChangeTrackingEnabled;
        Options = new UniqueValueResolveOptions
        {
            Reservations = reservations,
            ReservationOwnerId = Guid.NewGuid()
        };
    }

    /// <summary>
    /// The run's resolve options: the process-wide reservation set, under an owner id of this run's own.
    /// </summary>
    public UniqueValueResolveOptions Options { get; }

    /// <summary>
    /// Whether the Connected System's Connector says it classifies "value already in use" rejections. Collision
    /// Remediation acts only on a Connector that does: anything else is an ordinary export error.
    /// </summary>
    public bool ConnectorClassifies => _connectedSystem.ConnectorDefinition?.SupportsUniquenessRejectionClassification == true;

    /// <summary>
    /// The derived flow graph derived-input marks are computed from; null until a rejection has loaded the run's
    /// configuration, or when the derived flows could not be built.
    /// </summary>
    public DerivedFlowGraph? DerivedFlowGraph => _context?.DerivedFlowGraph;

    /// <summary>
    /// Releases every value this run claimed in the process-wide reservation set. Called once the run has finished:
    /// what it issued is persisted on the assignments by then, so later runs see it through the database gates.
    /// </summary>
    public void Complete() => Options.Reservations.ReleaseAll(Options.ReservationOwnerId);

    /// <summary>
    /// Decides what a "value already in use" rejection of <paramref name="export"/> means (see the type summary), and
    /// carries the decision out through <paramref name="repository"/>. Returns null when the rejection is to be reported
    /// as an ordinary export error, in which case nothing has been changed.
    /// </summary>
    /// <param name="export">The rejected export, as the batch loaded it. On a decision, its status is updated in memory
    /// for the caller to persist with the rest of the batch.</param>
    /// <param name="rejection">What the Connector reported.</param>
    /// <param name="repository">The batch's repository.</param>
    /// <param name="marks">The batch's derived-input marks, flushed by the caller once per batch.</param>
    public async Task<GeneratedValueCollisionOutcome?> HandleAsync(
        PendingExport export, ConnectedSystemExportResult rejection, ISyncRepository repository, Func<DerivedInputMarkBatch> marks)
    {
        if (!ConnectorClassifies)
            return null;

        var context = await GetContextAsync(repository);
        var knownAttributes = context.KnownAttributes(export);
        var attribution = GeneratedValueRejectionAttribution.Attribute(
            export, rejection.RejectedAttributeName, knownAttributes, context.ExportRules, context.ImportRules, context.DerivedFlowGraph);

        if (!attribution.IsAttributed)
        {
            Log.Information("CollisionRemediation: the rejection of Pending Export {ExportId} by {ConnectedSystem} is reported as an ordinary " +
                "export error because it cannot be attributed to one generated value: {Reason}",
                export.Id, LogSanitiser.Sanitise(_connectedSystem.Name), LogSanitiser.Sanitise(attribution.UnattributableReason));
            return null;
        }

        return attribution.MetaverseAttributeId.HasValue
            ? await HandleImportModeAsync(export, attribution, context, repository, marks)
            : await HandleExportModeAsync(export, attribution, context, repository);
    }

    private async Task<GeneratedValueCollisionOutcome?> HandleImportModeAsync(
        PendingExport export, GeneratedValueRejectionAttributionResult attribution, Context context, ISyncRepository repository, Func<DerivedInputMarkBatch> marks)
    {
        var metaverseAttributeId = attribution.MetaverseAttributeId!.Value;
        var metaverseObjectId = export.SourceMetaverseObjectId ?? export.ConnectedSystemObject?.MetaverseObjectId;
        if (!metaverseObjectId.HasValue)
            return LogOrdinary(export, "the export has no Metaverse Object to revise the value on");

        var assignment = await repository.GetGeneratedValueAssignmentAsync(metaverseObjectId.Value, metaverseAttributeId);
        if (assignment == null || !context.GeneratedMappings.TryGetValue(assignment.SyncRuleMappingGenerationId, out var mapping))
            return LogOrdinary(export, "JIM does not hold a generated value for the attribute on this object");

        if (!mapping.Generation!.CollisionRemediation)
            return LogOrdinary(export, "Collision Remediation is switched off for the Attribute Flow");

        // A rejection of a value JIM has already replaced: the revision is waiting for a synchronisation to reach the
        // queued exports, and remediating again would skip a value for nothing.
        if (assignment.State == GeneratedValueAssignmentState.Remediated
            && await repository.HasGeneratedValueRevisionPendingAsync(metaverseObjectId.Value, metaverseAttributeId))
            return LogOrdinary(export, $"the value was already corrected to \"{assignment.Value}\", which the next synchronisation will carry to this export");

        var attribute = mapping.TargetMetaverseAttribute!;
        var executionItemId = Guid.NewGuid();
        var rejectedValue = assignment.Value;

        if (assignment.RemediationCount >= UniqueValueGenerationServer.MaximumRemediations && !assignment.RenameAuthorised)
            return await EnterNeedsDecisionAsync(export, assignment, attribute.Name, rejectedValue, anchoredBy: null, executionItemId, repository,
                GeneratedValueNeedsDecisionReason.RemediationLimitReached,
                $"it has already been corrected {assignment.RemediationCount} times, so JIM has stopped correcting it");

        var generationServer = new UniqueValueGenerationServer(repository);
        var participatingTargets = GeneratedValueParticipation.ComputeParticipatingTargets(mapping, context.AllExportRules);
        var verdict = await generationServer.IsAnchoredAsync(metaverseObjectId.Value, rejectedValue, TryParseNumeric(attribute.Type, rejectedValue),
            participatingTargets, _connectedSystem.Id, context.ConnectedSystems);

        if (verdict.Anchoring != GeneratedValueAnchoring.Unanchored && !assignment.RenameAuthorised)
        {
            var anchoringName = SystemName(context, verdict.ConnectedSystemId!.Value);
            var why = verdict.Anchoring == GeneratedValueAnchoring.Anchored
                ? $"{anchoringName} has already accepted it for this object, so changing it would rename an account already in use"
                : $"JIM cannot tell whether {anchoringName} already holds it, because {anchoringName} has not completed a Full Import since its connector space was cleared";
            var reason = verdict.Anchoring == GeneratedValueAnchoring.Anchored
                ? GeneratedValueNeedsDecisionReason.AnchoredElsewhere
                : GeneratedValueNeedsDecisionReason.CannotTell;
            return await EnterNeedsDecisionAsync(export, assignment, attribute.Name, rejectedValue, verdict.ConnectedSystemId, executionItemId, repository, reason, why);
        }

        var request = new GenerationRequest
        {
            Mode = GeneratedValueMode.Import,
            MetaverseObjectId = metaverseObjectId,
            MetaverseAttributeId = metaverseAttributeId,
            Generation = mapping.Generation,
            TargetType = attribute.Type,
            AttributeName = attribute.Name,
            BaseValue = BaseValueFor(assignment, mapping.Generation),
            ConnectorSpaceAttributeIds = participatingTargets.Select(t => t.AttributeId).Distinct().ToList(),
            RejectedValues = RejectedValuesFor(assignment)
        };

        var outcome = await generationServer.RegenerateAsync(request, Options);
        if (outcome.Kind != GenerationOutcomeKind.Generated)
            return await EnterNeedsDecisionAsync(export, assignment, attribute.Name, rejectedValue, anchoredBy: null, executionItemId, repository,
                GeneratedValueNeedsDecisionReason.NoValueAvailable, $"no other value could be found for it: {outcome.FailureMessage}");

        var reasonCode = assignment.RenameAuthorised ? CausalReasonCode.GeneratedValueRenameAuthorised : CausalReasonCode.GeneratedValueAlreadyInUse;
        var revised = Revise(assignment, outcome, executionItemId, _connectedSystem.Id);

        var metaverseObject = (await repository.GetMetaverseObjectsByIdsNoTrackingAsync([metaverseObjectId.Value])).SingleOrDefault();
        if (metaverseObject == null)
            return LogOrdinary(export, "the Metaverse Object no longer exists");

        var revision = new GeneratedValueRevision
        {
            Assignment = revised,
            PreviousValue = rejectedValue,
            PreviousNumericValue = TryParseNumeric(attribute.Type, rejectedValue),
            NewNumericValue = outcome.NumericValue,
            IsLongNumber = attribute.Type == AttributeDataType.LongNumber,
            RetirePreviousValue = UniqueValueGenerationServer.NeverReuses(mapping.Generation),
            ContributedBySyncRuleId = mapping.SyncRuleId,
            ContributedBySystemId = mapping.SyncRule?.ConnectedSystemId,
            RevisionPending = new GeneratedValueRevisionPending
            {
                Id = Guid.NewGuid(),
                MetaverseObjectId = metaverseObjectId.Value,
                MetaverseAttributeId = metaverseAttributeId,
                RemediatingActivityRunProfileExecutionItemId = executionItemId,
                ReasonCode = reasonCode,
                RejectedByConnectedSystemId = _connectedSystem.Id,
                RejectedByConnectedSystemName = _connectedSystem.Name,
                Created = DateTime.UtcNow
            }
        };

        var result = await _application.Metaverse.ReviseGeneratedValueAsync(repository, metaverseObject, attribute, revision, _metaverseObjectChangeTrackingEnabled);
        if (result != GeneratedValueRevisionResult.Applied)
            return LogOrdinary(export, result == GeneratedValueRevisionResult.ValueChanged
                ? "the value changed on the Metaverse Object while the export was running"
                : $"the corrected value \"{outcome.Value}\" was claimed by another object first");

        // Values derived from this one (an email, a User Principal Name) are re-derived by their hosting systems' next
        // synchronisation; the caller flushes these marks once per batch, after the batch's writes.
        marks().Collect(metaverseObject, [new MetaverseObjectAttributeValue { AttributeId = metaverseAttributeId, Attribute = attribute, MetaverseObject = metaverseObject }]);

        return Remediated(export, attribute.Name, rejectedValue, outcome.Value!, executionItemId,
            attribution.ThroughDerivation ? null : attribution.CarryingChange!.Attribute);
    }

    private async Task<GeneratedValueCollisionOutcome?> HandleExportModeAsync(
        PendingExport export, GeneratedValueRejectionAttributionResult attribution, Context context, ISyncRepository repository)
    {
        var mapping = attribution.ExportGeneratedMapping!;
        var carrying = attribution.CarryingChange!;
        if (!export.ConnectedSystemObjectId.HasValue)
            return LogOrdinary(export, "the export has no Connected System Object to revise the value on");

        if (!mapping.Generation!.CollisionRemediation)
            return LogOrdinary(export, "Collision Remediation is switched off for the Attribute Flow");

        var assignment = await repository.GetGeneratedValueAssignmentForConnectedSystemObjectAsync(export.ConnectedSystemObjectId.Value, carrying.AttributeId);
        if (assignment == null)
            return LogOrdinary(export, "JIM does not hold a generated value for the attribute on this object");

        var attribute = mapping.TargetConnectedSystemAttribute ?? carrying.Attribute;
        var carriedValue = CarriedValue(carrying);
        if (!string.Equals(carriedValue, assignment.Value, StringComparison.OrdinalIgnoreCase))
            return LogOrdinary(export, $"the export carries \"{carriedValue}\", which is no longer the value JIM holds for it");

        var executionItemId = Guid.NewGuid();
        var rejectedValue = assignment.Value;

        // An export-mode value is never anchored (plan decision 10): only this one system holds it.
        if (assignment.RemediationCount >= UniqueValueGenerationServer.MaximumRemediations && !assignment.RenameAuthorised)
            return await EnterNeedsDecisionAsync(export, assignment, attribute.Name, rejectedValue, anchoredBy: null, executionItemId, repository,
                GeneratedValueNeedsDecisionReason.RemediationLimitReached,
                $"it has already been corrected {assignment.RemediationCount} times, so JIM has stopped correcting it");

        var generationServer = new UniqueValueGenerationServer(repository);
        var request = new GenerationRequest
        {
            Mode = GeneratedValueMode.Export,
            ConnectedSystemObjectId = export.ConnectedSystemObjectId,
            ConnectedSystemObjectTypeAttributeId = carrying.AttributeId,
            Generation = mapping.Generation,
            TargetType = attribute.Type,
            AttributeName = attribute.Name,
            BaseValue = BaseValueFor(assignment, mapping.Generation),
            RejectedValues = RejectedValuesFor(assignment)
        };

        var outcome = await generationServer.RegenerateAsync(request, Options);
        if (outcome.Kind != GenerationOutcomeKind.Generated)
            return await EnterNeedsDecisionAsync(export, assignment, attribute.Name, rejectedValue, anchoredBy: null, executionItemId, repository,
                GeneratedValueNeedsDecisionReason.NoValueAvailable, $"no other value could be found for it: {outcome.FailureMessage}");

        var revised = Revise(assignment, outcome, executionItemId, _connectedSystem.Id);
        var revision = new GeneratedValueRevision
        {
            Assignment = revised,
            PreviousValue = rejectedValue,
            PreviousNumericValue = TryParseNumeric(attribute.Type, rejectedValue),
            NewNumericValue = outcome.NumericValue,
            IsLongNumber = attribute.Type == AttributeDataType.LongNumber,
            RetirePreviousValue = UniqueValueGenerationServer.NeverReuses(mapping.Generation),
            PendingExportAttributeValueChangeId = carrying.Id
        };

        var result = await repository.ApplyGeneratedValueRevisionAsync(revision);
        if (result != GeneratedValueRevisionResult.Applied)
            return LogOrdinary(export, result == GeneratedValueRevisionResult.ValueChanged
                ? "the queued value changed while the export was running"
                : $"the corrected value \"{outcome.Value}\" was claimed by another object first");

        // The batch persists the export with the rest of its results, attribute changes included: hold the new value
        // in memory too, or that write would put the rejected value straight back.
        if (outcome.NumericValue.HasValue && attribute.Type == AttributeDataType.LongNumber)
            carrying.LongValue = outcome.NumericValue;
        else if (outcome.NumericValue.HasValue)
            carrying.IntValue = (int)outcome.NumericValue.Value;
        else
            carrying.StringValue = outcome.Value;

        return Remediated(export, attribute.Name, rejectedValue, outcome.Value!, executionItemId, carrying.Attribute);
    }

    private GeneratedValueCollisionOutcome Remediated(
        PendingExport export, string attributeName, string rejectedValue, string newValue, Guid executionItemId, ConnectedSystemObjectTypeAttribute? carryingAttribute)
    {
        // Pending, not Failed, and the error count untouched: the export did nothing wrong, and is carried again once a
        // synchronisation has re-staged it with the corrected value (import mode) or as it now stands (export mode).
        // Never retried inside this run, which would send stale dependent values.
        export.Status = PendingExportStatus.Pending;
        export.LastAttemptedAt = DateTime.UtcNow;
        export.NextRetryAt = null;

        var message = $"{_connectedSystem.Name} already holds the {attributeName} \"{rejectedValue}\" for another object, so JIM corrected it to \"{newValue}\".";
        Log.Warning("CollisionRemediation: {ConnectedSystem} rejected the {Attribute} \"{RejectedValue}\" on Pending Export {ExportId} as already in use; " +
            "JIM corrected it to \"{NewValue}\". The export stays queued for the next synchronisation to re-stage.",
            LogSanitiser.Sanitise(_connectedSystem.Name), LogSanitiser.Sanitise(attributeName), LogSanitiser.Sanitise(rejectedValue), export.Id, LogSanitiser.Sanitise(newValue));

        return new GeneratedValueCollisionOutcome
        {
            Handling = GeneratedValueCollisionHandling.Remediated,
            AttributeName = attributeName,
            RejectedValue = rejectedValue,
            NewValue = newValue,
            Message = message,
            ExecutionItemId = executionItemId,
            CarryingAttribute = carryingAttribute
        };
    }

    private async Task<GeneratedValueCollisionOutcome> EnterNeedsDecisionAsync(
        PendingExport export, GeneratedValueAssignment assignment, string attributeName, string rejectedValue, int? anchoredBy,
        Guid executionItemId, ISyncRepository repository, GeneratedValueNeedsDecisionReason reason, string why)
    {
        await new UniqueValueGenerationServer(repository).EnterNeedsDecisionAsync(assignment, _connectedSystem.Id, anchoredBy, executionItemId, reason);

        var message = $"{_connectedSystem.Name} rejected the {attributeName} \"{rejectedValue}\" because another object there already holds it, " +
                      $"and JIM has not changed it: {why}. Allow the rename, retry once the conflict is resolved in {_connectedSystem.Name}, or leave it.";

        // Parked under its own status (plan decision 12): never exported, never consumed by the Connected System's bulk
        // "Retry failed exports", and not counted against its error count, until the decision releases it.
        export.Status = PendingExportStatus.Parked;
        export.LastAttemptedAt = DateTime.UtcNow;
        export.NextRetryAt = null;
        export.LastErrorMessage = message;

        Log.Warning("CollisionRemediation: {ConnectedSystem} rejected the {Attribute} \"{RejectedValue}\" on Pending Export {ExportId} as already in use; " +
            "the value needs a decision ({Why}), so the export is parked.",
            LogSanitiser.Sanitise(_connectedSystem.Name), LogSanitiser.Sanitise(attributeName), LogSanitiser.Sanitise(rejectedValue), export.Id, LogSanitiser.Sanitise(why));

        return new GeneratedValueCollisionOutcome
        {
            Handling = GeneratedValueCollisionHandling.NeedsDecision,
            AttributeName = attributeName,
            RejectedValue = rejectedValue,
            Message = message,
            ExecutionItemId = executionItemId
        };
    }

    private GeneratedValueCollisionOutcome? LogOrdinary(PendingExport export, string reason)
    {
        Log.Information("CollisionRemediation: the rejection of Pending Export {ExportId} by {ConnectedSystem} is reported as an ordinary export error: {Reason}",
            export.Id, LogSanitiser.Sanitise(_connectedSystem.Name), LogSanitiser.Sanitise(reason));
        return null;
    }

    /// <summary>
    /// The assignment as revised, on a copy: the original is left as read, so nothing in memory claims a revision the
    /// repository may yet refuse.
    /// </summary>
    private static GeneratedValueAssignment Revise(GeneratedValueAssignment assignment, GenerationOutcome outcome, Guid executionItemId, int rejectedByConnectedSystemId) => new()
    {
        Id = assignment.Id,
        MetaverseObjectId = assignment.MetaverseObjectId,
        MetaverseAttributeId = assignment.MetaverseAttributeId,
        ConnectedSystemObjectId = assignment.ConnectedSystemObjectId,
        ConnectedSystemObjectTypeAttributeId = assignment.ConnectedSystemObjectTypeAttributeId,
        SyncRuleMappingGenerationId = assignment.SyncRuleMappingGenerationId,
        Value = outcome.Value!,
        NormalisedValue = outcome.Value!.ToLowerInvariant(),
        PreviousValue = assignment.Value,
        BaseValue = assignment.BaseValue,
        State = GeneratedValueAssignmentState.Remediated,
        RemediationCount = assignment.RemediationCount + 1,
        // An authorisation covers one rename; it is spent once used. Who gave it, and when, stays on record.
        RenameAuthorised = false,
        RenameAuthorisedAt = assignment.RenameAuthorisedAt,
        RenameAuthorisedByName = assignment.RenameAuthorisedByName,
        // The system this correction answered, so the decision surfaces can count it against that system.
        RejectedByConnectedSystemId = rejectedByConnectedSystemId,
        RemediatedByActivityRunProfileExecutionItemId = executionItemId,
        RemediatedAt = DateTime.UtcNow,
        Created = assignment.Created,
        CommittedAt = assignment.CommittedAt,
        LastUpdated = DateTime.UtcNow
    };

    /// <summary>
    /// The base the next candidate is drawn from. An assignment made before the base was recorded falls back to its own
    /// value for an only-if-taken token, which is exact for a value issued without a suffix (the common case) and still
    /// yields a unique value otherwise.
    /// </summary>
    private static string? BaseValueFor(GeneratedValueAssignment assignment, SyncRuleMappingGeneration generation) =>
        assignment.BaseValue ?? (generation.TokenKind == GeneratedValueTokenKind.OnlyIfTaken ? assignment.Value : null);

    /// <summary>
    /// The values the next candidate must not be: the one just rejected, and the one an earlier remediation replaced
    /// (rejected by a target before), which a flow that reuses values would otherwise draw straight back.
    /// </summary>
    private static List<string> RejectedValuesFor(GeneratedValueAssignment assignment) =>
        assignment.PreviousValue != null ? [assignment.Value, assignment.PreviousValue] : [assignment.Value];

    private static string? CarriedValue(PendingExportAttributeValueChange change) =>
        change.StringValue ?? change.LongValue?.ToString(CultureInfo.InvariantCulture) ?? change.IntValue?.ToString(CultureInfo.InvariantCulture);

    private static long? TryParseNumeric(AttributeDataType type, string value) =>
        type is AttributeDataType.Number or AttributeDataType.LongNumber
        && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
            ? numeric
            : null;

    private static string SystemName(Context context, int connectedSystemId) =>
        context.ConnectedSystems.TryGetValue(connectedSystemId, out var system) ? system.Name : $"Connected System {connectedSystemId}";

    private async Task<Context> GetContextAsync(ISyncRepository repository)
    {
        if (_context != null)
            return _context;

        await _contextGate.WaitAsync();
        try
        {
            return _context ??= await LoadContextAsync(repository);
        }
        finally
        {
            _contextGate.Release();
        }
    }

    private async Task<Context> LoadContextAsync(ISyncRepository repository)
    {
        var allRules = await repository.GetAllSyncRulesAsync();

        DerivedFlowGraph? graph = null;
        try
        {
            graph = DerivedFlowGraphFactory.Create(allRules, []);
        }
        catch (DerivedFlowCycleException ex)
        {
            // Synchronisation refuses to run on a cycle, so a rejection reaching here predates it; attribution simply
            // does not look through derivations until the cycle is broken.
            Log.Warning("CollisionRemediation: the derived Attribute Flows contain a dependency cycle, so no rejection is attributed through a derived value: {Message}",
                LogSanitiser.Sanitise(ex.Message));
        }

        var connectedSystems = allRules
            .Select(rule => rule.ConnectedSystem)
            .OfType<ConnectedSystem>()
            .Append(_connectedSystem)
            .GroupBy(system => system.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var generatedMappings = allRules
            .SelectMany(rule => rule.AttributeFlowRules)
            .Where(mapping => mapping.Generation != null)
            .GroupBy(mapping => mapping.Generation!.Id)
            .ToDictionary(group => group.Key, group => group.First());

        return new Context(
            ExportRules: allRules.Where(rule => rule.Direction == SyncRuleDirection.Export && rule.ConnectedSystemId == _connectedSystem.Id).ToList(),
            AllExportRules: allRules.Where(rule => rule.Direction == SyncRuleDirection.Export).ToList(),
            ImportRules: allRules.Where(rule => rule.Direction == SyncRuleDirection.Import).ToList(),
            DerivedFlowGraph: graph,
            ConnectedSystems: connectedSystems,
            GeneratedMappings: generatedMappings);
    }

    /// <summary>
    /// The configuration a run's Collision Remediation reads, loaded once.
    /// </summary>
    private sealed record Context(
        List<SyncRule> ExportRules,
        List<SyncRule> AllExportRules,
        List<SyncRule> ImportRules,
        DerivedFlowGraph? DerivedFlowGraph,
        Dictionary<int, ConnectedSystem> ConnectedSystems,
        Dictionary<int, SyncRuleMapping> GeneratedMappings)
    {
        /// <summary>
        /// The attribute names the rejecting system can mean for this export: its object type's attributes, from the
        /// export rules (which load the type with its attributes) or the export's own Connected System Object.
        /// </summary>
        public IReadOnlyCollection<ConnectedSystemObjectTypeAttribute> KnownAttributes(PendingExport export)
        {
            var typeId = export.ConnectedSystemObject?.TypeId;
            var fromRules = ExportRules
                .Where(rule => !typeId.HasValue || rule.ConnectedSystemObjectTypeId == typeId)
                .SelectMany(rule => rule.ConnectedSystemObjectType?.Attributes ?? []);
            var fromObject = export.ConnectedSystemObject?.Type?.Attributes ?? [];
            var fromChanges = export.AttributeValueChanges.Select(change => change.Attribute).OfType<ConnectedSystemObjectTypeAttribute>();
            return fromRules.Concat(fromObject).Concat(fromChanges).DistinctBy(attribute => attribute.Id).ToList();
        }
    }
}
