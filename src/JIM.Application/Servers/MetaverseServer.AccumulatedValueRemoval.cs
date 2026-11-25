// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Interfaces;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Localisation;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Sync;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// The manual removal of values an Accumulate attribute has kept (#3059, Phase 3; PRD requirements 14 and 15). Kept as
/// its own partial file.
/// </summary>
public partial class MetaverseServer
{
    /// <summary>
    /// Removes values an Accumulate attribute holds on a Metaverse Object, by hand. All or nothing: refused, with nothing
    /// changed and nothing recorded, when the attribute is not in Accumulate mode, when a named value is not held, or when
    /// a Synchronisation Rule still supplies a named value (its next synchronisation would only add it back, so the
    /// refusal names it), or cannot be shown not to. Otherwise the values are removed through the same path as any edit
    /// outside synchronisation, so the change is recorded on the object's history as the person's and reaches the
    /// systems the object is exported to, under an Activity of its own.
    /// <para>
    /// Whether a rule still supplies a value is decided by running each enabled import rule's flows into the attribute,
    /// for every Connected System Object joined to the object and in the rule's scope, exactly as synchronisation would,
    /// on a scratch copy of the object: the value tagged on the row is not enough, since Accumulate keeps the first
    /// contributor's tag while another rule may supply the same value now.
    /// </para>
    /// </summary>
    /// <param name="metaverseObject">The object, with its Type and attribute values loaded.</param>
    /// <param name="attributeId">The Accumulate attribute.</param>
    /// <param name="valueIds">The values to remove, by value id.</param>
    /// <param name="values">The values to remove, by their text (<see cref="MetaverseAttributeValueText"/>).</param>
    /// <param name="initiatedBy">The person removing them, when signed in.</param>
    /// <param name="initiatedByApiKey">The API key removing them, when the request came from automation.</param>
    /// <exception cref="ArgumentException">Neither a person nor an API key was given.</exception>
    public async Task<AttributeValueRemovalResult> RemoveAccumulatedValuesAsync(
        MetaverseObject metaverseObject,
        int attributeId,
        IReadOnlyCollection<Guid> valueIds,
        IReadOnlyCollection<string> values,
        MetaverseObject? initiatedBy,
        ApiKey? initiatedByApiKey = null)
    {
        ArgumentNullException.ThrowIfNull(metaverseObject);
        ArgumentNullException.ThrowIfNull(valueIds);
        ArgumentNullException.ThrowIfNull(values);
        if (initiatedBy == null && initiatedByApiKey == null)
            throw new ArgumentException("A removal of attribute values must be attributed to the person or API key making it.");

        var attribute = metaverseObject.Type?.Attributes.SingleOrDefault(a => a.Id == attributeId)
                        ?? metaverseObject.AttributeValues.FirstOrDefault(av => av.AttributeId == attributeId)?.Attribute;
        if (attribute is not { ContributionMode: AttributeContributionMode.Accumulate })
        {
            return AttributeValueRemovalResult.Refused(AttributeValueRemovalOutcome.NotAccumulate, attribute == null
                ? ServerMessage.Create(nameof(ServerMessages.AttributeNotFound), attributeId.ToString())
                : ServerMessage.Create(nameof(ServerMessages.AccumulatedValueRemovalNotAccumulate), attribute.Name, attribute.ContributionMode.ToString()));
        }

        var (named, notHeld) = ResolveNamedValues(metaverseObject, attribute, valueIds, values);
        if (notHeld != null)
            return AttributeValueRemovalResult.Refused(AttributeValueRemovalOutcome.ValueNotHeld, notHeld);

        var (suppliers, undecidable) = await FindAccumulatedValueSuppliersAsync(metaverseObject, attribute, named);
        if (undecidable != null)
            return AttributeValueRemovalResult.Refused(AttributeValueRemovalOutcome.SupplyUndecidable, undecidable);
        if (suppliers.Count > 0)
        {
            var first = suppliers[0];
            return AttributeValueRemovalResult.Refused(AttributeValueRemovalOutcome.SuppliedByContributor,
                ServerMessage.Create(nameof(ServerMessages.AccumulatedValueRemovalSupplied), first.Value, first.SyncRuleName, first.ConnectedSystemName),
                suppliers);
        }

        var activity = new Activity
        {
            TargetType = ActivityTargetType.MetaverseObject,
            TargetOperationType = ActivityTargetOperationType.Update,
            TargetName = metaverseObject.Name ?? metaverseObject.Id.ToString(),
            TargetContext = attribute.Name,
            MetaverseObjectId = metaverseObject.Id,
            MessageDetail = ServerMessage.Counted(nameof(ServerMessages.AccumulatedValuesRemovedOne), named.Count, named.Count, attribute.Name)
        };
        if (initiatedByApiKey != null)
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        else
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        try
        {
            foreach (var value in named)
                metaverseObject.AttributeValues.Remove(value);

            await StageEditOutsideSynchronisationAsync(metaverseObject, additions: [], removals: named,
                activity.InitiatedByType, activity.InitiatedById, activity.InitiatedByName,
                // An administrator's change, whether made in the portal or through an API key they hold.
                MetaverseObjectChangeInitiatorType.User, activity.Id);
            await Application.Repository.Metaverse.RemoveMetaverseObjectAttributeValuesAsync(metaverseObject, named.Select(v => v.Id).ToList());
            await MarkDerivedInputChangesAsync(metaverseObject, named);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The removal is the Activity's whole work: whatever stopped it is recorded there, never left in flight.
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }

        await Application.Activities.CompleteActivityAsync(activity);
        Log.Information("RemoveAccumulatedValuesAsync: removed {Count} value(s) of attribute {AttributeId} from Metaverse Object {MvoId}, by {InitiatorType}.",
            named.Count, attribute.Id, metaverseObject.Id, activity.InitiatedByType);
        return new AttributeValueRemovalResult
        {
            Outcome = AttributeValueRemovalOutcome.Removed,
            RemovedValues = named,
            ActivityId = activity.Id
        };
    }

    /// <summary>
    /// The held values the request names, by id or by text, each once; or why not: a name matching no held value, or
    /// nothing named at all.
    /// </summary>
    private static (List<MetaverseObjectAttributeValue> Named, JimMessage? NotHeld) ResolveNamedValues(
        MetaverseObject metaverseObject, MetaverseAttribute attribute, IReadOnlyCollection<Guid> valueIds, IReadOnlyCollection<string> values)
    {
        var held = metaverseObject.AttributeValues.Where(av => av.AttributeId == attribute.Id && !av.NullValue).ToList();
        if (valueIds.Count == 0 && values.Count == 0)
            return ([], ServerMessage.Create(nameof(ServerMessages.AccumulatedValueRemovalNothingNamed), attribute.Name));

        var named = new List<MetaverseObjectAttributeValue>();
        foreach (var valueId in valueIds)
        {
            var value = held.SingleOrDefault(av => av.Id == valueId);
            if (value == null)
                return ([], ServerMessage.Create(nameof(ServerMessages.AccumulatedValueRemovalNotHeld), valueId.ToString(), attribute.Name));
            named.Add(value);
        }

        foreach (var text in values)
        {
            var value = held.FirstOrDefault(av => MetaverseAttributeValueText.Names(text, av));
            if (value == null)
                return ([], ServerMessage.Create(nameof(ServerMessages.AccumulatedValueRemovalNotHeld), text, attribute.Name));
            named.Add(value);
        }

        return (named.Distinct().ToList(), null);
    }

    /// <summary>
    /// The Synchronisation Rules that would add a named value back at their next synchronisation: every enabled import
    /// rule flowing into the attribute, run for each Connected System Object joined to the object and in the rule's scope,
    /// on a scratch copy of the object. Or why that cannot be decided: a scope or an expression that cannot be evaluated,
    /// which refuses the removal rather than guessing (#2250).
    /// </summary>
    private async Task<(List<AccumulatedValueSupplier> Suppliers, JimMessage? Undecidable)> FindAccumulatedValueSuppliersAsync(
        MetaverseObject metaverseObject, MetaverseAttribute attribute, IReadOnlyCollection<MetaverseObjectAttributeValue> named)
    {
        var suppliers = new List<AccumulatedValueSupplier>();
        if (metaverseObject.Type == null)
            return (suppliers, null);

        var rules = (await Application.Repository.ConnectedSystems.GetImportSyncRulesForMetaverseObjectTypeAsync(metaverseObject.Type.Id))
            .Where(rule => rule.Enabled && rule.AttributeFlowRules.Any(m => m.TargetMetaverseAttributeId == attribute.Id))
            .ToList();
        if (rules.Count == 0)
            return (suppliers, null);

        var namedTexts = named.Select(MetaverseAttributeValueText.Of).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var expressionEvaluator = new DynamicExpressoEvaluator();
        var objectTypesBySystem = new Dictionary<int, List<ConnectedSystemObjectType>>();
        var systemNames = new Dictionary<int, string>();

        // An obsolete object leaves at its system's next synchronisation, so it supplies nothing.
        var joined = (await Application.SyncRepo.GetConnectedSystemObjectsByMetaverseObjectIdAsync(metaverseObject.Id))
            .Where(cso => cso.Status != ConnectedSystemObjectStatus.Obsolete)
            .ToList();
        foreach (var joinedObject in joined)
        {
            var joinedRules = rules.Where(r => r.ConnectedSystemId == joinedObject.ConnectedSystemId && r.ConnectedSystemObjectTypeId == joinedObject.TypeId).ToList();
            if (joinedRules.Count == 0)
                continue;

            var cso = await Application.SyncRepo.GetConnectedSystemObjectWithAttributeValuesAsync(joinedObject.ConnectedSystemId, joinedObject.Id) ?? joinedObject;
            if (!objectTypesBySystem.TryGetValue(cso.ConnectedSystemId, out var objectTypes))
                objectTypesBySystem[cso.ConnectedSystemId] = objectTypes = await Application.Repository.ConnectedSystems.GetObjectTypesAsync(cso.ConnectedSystemId);
            cso.Type ??= objectTypes.Single(t => t.Id == cso.TypeId);

            foreach (var rule in joinedRules)
            {
                HashSet<string> supplied;
                try
                {
                    if (!Application.ScopingEvaluation.IsCsoInScopeForImportRule(cso, rule))
                        continue;
                    supplied = SuppliedTexts(cso, rule, attribute, metaverseObject.Type, objectTypes, expressionEvaluator);
                }
                catch (Exception ex) when (ex is ScopingCriterionFailedException or SyncExpressionEvaluationException or InvalidOperationException)
                {
                    Log.Warning(ex, "RemoveAccumulatedValuesAsync: cannot tell whether Synchronisation Rule {SyncRuleId} supplies attribute {AttributeId} " +
                        "for Metaverse Object {MvoId}; the removal is refused.", rule.Id, attribute.Id, metaverseObject.Id);
                    return (suppliers, ServerMessage.Create(nameof(ServerMessages.AccumulatedValueRemovalUndecidable),
                        rule.Name, await SystemNameAsync(cso.ConnectedSystemId), ex.Message));
                }

                foreach (var text in namedTexts.Where(supplied.Contains))
                    suppliers.Add(new AccumulatedValueSupplier(text, rule.Id, rule.Name, cso.ConnectedSystemId, await SystemNameAsync(cso.ConnectedSystemId)));
            }
        }

        return (suppliers, null);

        async Task<string> SystemNameAsync(int connectedSystemId)
        {
            if (systemNames.TryGetValue(connectedSystemId, out var name))
                return name;
            var system = await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
            return systemNames[connectedSystemId] = system?.Name ?? connectedSystemId.ToString();
        }
    }

    /// <summary>
    /// The values <paramref name="rule"/>'s flows into <paramref name="attribute"/> supply from <paramref name="cso"/>, as
    /// text: the flows run through the synchronisation's own mapping code onto a scratch object holding nothing, so
    /// Accumulate adds every supplied value there and the real object is never touched.
    /// </summary>
    private static HashSet<string> SuppliedTexts(ConnectedSystemObject cso, SyncRule rule, MetaverseAttribute attribute,
        MetaverseObjectType metaverseObjectType, IReadOnlyList<ConnectedSystemObjectType> objectTypes, IExpressionEvaluator expressionEvaluator)
    {
        var scratch = new MetaverseObject { Id = cso.MetaverseObjectId ?? Guid.Empty, Type = metaverseObjectType };
        var joinedTo = cso.MetaverseObject;
        cso.MetaverseObject = scratch;
        try
        {
            foreach (var mapping in rule.AttributeFlowRules.Where(m => m.TargetMetaverseAttributeId == attribute.Id))
            {
                mapping.TargetMetaverseAttribute ??= attribute;
                SyncEngine.ProcessMapping(cso, mapping, objectTypes, expressionEvaluator, skipReferenceAttributes: true,
                    contributingSystemId: cso.ConnectedSystemId, mvoObjectTypeId: metaverseObjectType.Id);
            }
        }
        finally
        {
            cso.MetaverseObject = joinedTo;
        }

        return scratch.PendingAttributeValueAdditions
            .Where(av => av.AttributeId == attribute.Id)
            .Select(MetaverseAttributeValueText.Of)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }
}
