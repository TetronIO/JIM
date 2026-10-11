// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using JimUtilities = JIM.Utilities.Utilities;

namespace JIM.Web.Causality;

public static partial class CausalityLineageModelBuilder
{
    /// <summary>
    /// Places the downward walk (#1811) on the Lineage's objects: each cohort's representative item is built into events
    /// exactly as its own page would build them, and each event lands on the object it happened to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Objects are keyed by object, not by system. A consequence in a Connected System lands on the nearest object the
    /// branch above it already reached in that system (the account a Synchronisation queued a deprovision for is the
    /// account the export deleted), and on a new object where there is none. A reference removal always starts a new
    /// object, because the Groups that referenced a deleted Metaverse Object are not the deleted object's own account,
    /// even when they live in the same Connected System.
    /// </para>
    /// <para>
    /// Every branch ends in a card saying where it stopped, so an object with nothing after this run never has to be
    /// read as "nothing will happen".
    /// </para>
    /// </remarks>
    private sealed class ConsequencePlacement(
        CausalityPageContext context,
        Func<ColumnState> getIdentityColumn,
        Func<int?, string?, bool, ColumnState> newConsequenceObject,
        IReadOnlyList<ColumnState> recordColumns,
        ColumnState subject)
    {
        private int _sequence;
        private bool _furtherThanShown;
        private readonly List<CausalityLineageConsequence> _placed = [];

        public CausalityLineageConsequenceSummary Place(CausalConsequences consequences)
        {
            var rootScope = new Scope(null);
            foreach (var state in recordColumns.Where(c => c.SystemId.HasValue))
                rootScope.BindIfAbsent(state.SystemId!.Value, state);

            foreach (var ending in consequences.Endings)
                PlaceEnding(ending, subject);

            foreach (var cohort in consequences.Cohorts)
                PlaceCohort(cohort, rootScope, subject, deletedName: null);

            return Summarise(consequences);
        }

        private void PlaceCohort(CausalConsequenceCohort cohort, Scope parentScope, ColumnState parentObject, string? deletedName)
        {
            var scope = new Scope(parentScope);
            var cohortObject = parentObject;

            switch (cohort.HopKind)
            {
                case CausalConsequenceHopKind.OutstandingPendingExport:
                    cohortObject = ObjectFor(scope, cohort.ConnectedSystemId, cohort.ConnectedSystemName, isSourceSide: false);
                    Add(cohortObject, OutstandingCard(cohort));
                    break;

                case CausalConsequenceHopKind.ScheduledDeletion:
                    cohortObject = getIdentityColumn();
                    Add(cohortObject, new CausalityLineageConsequence
                    {
                        Kind = CausalityLineageConsequenceKind.Waiting,
                        Title = "Deletion scheduled",
                        Text = "Its grace period is running. It will be deleted when the grace period ends, unless it joins a Connected System Object again first.",
                        DueAt = cohort.DueAt,
                        PillLabel = "Deletion scheduled",
                        PillTone = CausalityTone.Warning
                    });
                    break;

                default:
                    var removalObject = cohort.EdgeType == CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval
                        ? NewReferringObject(cohort, scope)
                        : null;
                    cohortObject = cohort.Representative != null
                        ? PlaceOutcomes(cohort, cohort.Representative, scope, removalObject, deletedName) ?? removalObject ?? parentObject
                        : removalObject ?? parentObject;
                    if (cohort.Representative == null)
                        Add(cohortObject, UnretainedCard(cohort));
                    deletedName = cohort.Representative?.SyncOutcomes
                        .FirstOrDefault(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted)?.TargetEntityDescription
                        ?? deletedName;
                    break;
            }

            foreach (var ending in cohort.Endings)
                PlaceEnding(ending, cohortObject);

            if (cohort.IsFurtherThanShown)
            {
                _furtherThanShown = true;
                Add(cohortObject, new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.FurtherThanShown,
                    Title = "This goes further than shown",
                    Text = "Open the last item above to carry on from there."
                });
            }

            foreach (var consequence in cohort.Consequences)
                PlaceCohort(consequence, scope, cohortObject, deletedName);
        }

        /// <summary>
        /// Builds the representative item into events and places each on its object, returning the object the cohort's
        /// own story continues on: the last record its events reached, else the Metaverse Object.
        /// </summary>
        private ColumnState? PlaceOutcomes(CausalConsequenceCohort cohort, ActivityRunProfileExecutionItem representative,
            Scope scope, ColumnState? removalObject, string? deletedName)
        {
            var representativeContext = RepresentativeContext(cohort, representative, scope);
            var model = CausalityModelBuilder.Build(representative, representativeContext, chain: QueueingChain(cohort));
            var runLabel = GetRunLabel(representative.Activity);
            var occurred = representative.Activity?.Executed ?? cohort.FirstOccurred;
            var itemHref = cohort.MemberCount == 1 ? $"/activity/item/{representative.Id}" : null;
            var isConfirmation = cohort.HopKind == CausalConsequenceHopKind.Confirmation;

            var events = Flatten(model.Roots, parent: null)
                // A failed export's own failure says what happened; the Exported above it would claim the change landed.
                .Where(e => !(e.Event.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Exported
                              && e.Event.Children.Any(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed)))
                .ToList();
            if (isConfirmation)
            {
                // A confirmation is one fact however the import recorded it.
                var confirmed = events.FirstOrDefault(e => e.Event.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed);
                events = confirmed.Event != null ? [confirmed] : events.Take(1).ToList();
            }

            ColumnState? lastRecord = null;
            ColumnState? lastObject = null;
            var first = true;
            foreach (var (causalityEvent, parentEvent) in events)
            {
                var target = causalityEvent.Lane == CausalityLane.Identity
                    ? getIdentityColumn()
                    : removalObject ?? ObjectFor(scope, causalityEvent.SystemId ?? representative.Activity?.ConnectedSystemId,
                        causalityEvent.SystemName, isSourceSide: causalityEvent.Lane == CausalityLane.Source);

                // Until an item about the object names it, an object reached by an event is named by the event's own
                // link to it, or by its system.
                if (target is { IsConsequenceObject: true, ConsequenceHead: null })
                {
                    var recordLink = causalityEvent.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Record);
                    target.ConsequenceHead = recordLink != null
                        ? new ColumnHead(recordLink.Label, IsRoleHead: false, recordLink.Href, recordLink.ObjectTypeName)
                        : new ColumnHead(target.SystemName ?? "Connected System Object", IsRoleHead: false, Href: null, ObjectTypeName: null);
                }

                var isReferenceRemoval = first && removalObject != null;
                Add(target, new CausalityLineageConsequence
                {
                    Kind = isConfirmation ? CausalityLineageConsequenceKind.Confirmation : CausalityLineageConsequenceKind.Outcome,
                    Event = causalityEvent,
                    Title = isConfirmation
                        ? ConfirmationTitle(cohort)
                        : isReferenceRemoval ? ReferenceRemovalTitle(cohort, deletedName) : null,
                    Text = isConfirmation ? null : GetWhy(cohort, causalityEvent, parentEvent, first),
                    RunLabel = runLabel,
                    Occurred = occurred,
                    ActivityItemHref = itemHref,
                    Cohort = first && cohort.MemberCount > 1 ? cohort : null,
                    PillLabel = isConfirmation
                        ? null
                        : isReferenceRemoval ? ReferenceRemovalPill(cohort) : causalityEvent.Label,
                    PillTone = causalityEvent.Operation?.Tone ?? causalityEvent.Tone,
                    MemberCount = cohort.MemberCount
                });

                if (target.Kind == CausalityLineageColumnKind.Record)
                {
                    NameFromItem(target, cohort, representative, isReferring: removalObject != null);
                    lastRecord = target;
                }

                lastObject = target;
                first = false;
            }

            // A Disconnect leaves the account where it is, unmanaged; with nothing to export, nothing follows for it, and
            // the column says so rather than ending on a card that reads as though more were coming.
            foreach (var disconnected in events
                         .Where(e => e.Event.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.TargetDisconnected)
                         .Select(e => ObjectFor(scope, e.Event.SystemId, e.Event.SystemName, isSourceSide: false))
                         .Distinct())
            {
                Add(disconnected, new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.Settled,
                    Title = "Nothing more follows for this Connected System Object",
                    Text = $"It stays in {disconnected.SystemName ?? "its Connected System"}, and JIM no longer manages it."
                });
            }

            return lastRecord ?? lastObject;
        }

        private void PlaceEnding(CausalConsequenceEndingCount ending, ColumnState target)
        {
            var systemName = ending.ConnectedSystemName ?? "its Connected System";
            var card = ending.Ending switch
            {
                CausalConsequenceEnding.WaitingForSynchronisation => new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.Waiting,
                    Title = $"Waiting for the next Synchronisation of {systemName}",
                    Text = $"No Synchronisation of {systemName} has run since this import, so nothing has acted on the change yet.",
                    PillLabel = "Waiting for a Synchronisation",
                    PillTone = CausalityTone.Warning
                },
                CausalConsequenceEnding.NothingFollowed => new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.Settled,
                    Title = "Nothing followed",
                    Text = $"A Synchronisation of {systemName} has run since this import and changed nothing as a result.",
                    PillLabel = "Nothing followed",
                    PillTone = CausalityTone.Secondary
                },
                // Nothing the item's kind could cause was recorded: there is nothing to say, and saying nothing is right.
                _ => null
            };

            if (card != null)
                Add(target, card);
        }

        private static CausalityLineageConsequence OutstandingCard(CausalConsequenceCohort cohort)
        {
            var count = cohort.MemberCount;
            var systemName = cohort.ConnectedSystemName ?? "its Connected System";
            var exports = count == 1 ? "1 Pending Export" : $"{count} Pending Exports";
            var (linkText, linkHref) = PendingExportLink(cohort);
            return cohort.Ending switch
            {
                CausalConsequenceEnding.NeedsAttention => new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.NeedsAttention,
                    Title = "Needs attention",
                    Text = $"{exports} {(count == 1 ? "has" : "have")} stopped, and nothing more will happen until someone acts on {(count == 1 ? "it" : "them")}.",
                    LinkText = linkText,
                    LinkHref = linkHref,
                    PillLabel = "Needs attention",
                    PillTone = CausalityTone.Error,
                    MemberCount = count
                },
                CausalConsequenceEnding.WaitingForRetry => new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.Waiting,
                    Title = "Waiting for a retry",
                    Text = $"{exports} will be retried on the next Export of {systemName}.",
                    LinkText = linkText,
                    LinkHref = linkHref,
                    DueAt = cohort.DueAt,
                    PillLabel = "Retry pending",
                    PillTone = CausalityTone.Warning,
                    MemberCount = count
                },
                CausalConsequenceEnding.WaitingForConfirmingImport => new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.Waiting,
                    Title = $"Waiting for the next import of {systemName}",
                    Text = $"Exported, and waiting for an import to confirm the change landed.",
                    LinkText = linkText,
                    LinkHref = linkHref,
                    PillLabel = "Waiting for a confirming import",
                    PillTone = CausalityTone.Warning,
                    MemberCount = count
                },
                _ => new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.Waiting,
                    Title = $"Waiting for the next Export of {systemName}",
                    Text = count == 1 ? "The change is queued as a Pending Export." : $"{exports} are queued.",
                    LinkText = linkText,
                    LinkHref = linkHref,
                    PillLabel = "Waiting for an Export",
                    PillTone = CausalityTone.Warning,
                    MemberCount = count
                }
            };
        }

        /// <summary>
        /// The Pending Export a waiting card links: the row itself where there is one, else the system's queue.
        /// </summary>
        private static (string? Text, string? Href) PendingExportLink(CausalConsequenceCohort cohort)
        {
            if (cohort.ConnectedSystemId is not { } systemId)
                return (null, null);

            var queueHref = $"/admin/connected-systems/{systemId}/pending-exports";
            return cohort.MemberCount == 1 && cohort.Members.FirstOrDefault()?.PendingExportId is { } pendingExportId
                ? ("View Pending Export", $"{queueHref}/{pendingExportId}")
                : ("View Pending Exports", queueHref);
        }

        /// <summary>
        /// A cohort whose items have since been purged: the count and the change are still known from the walk, so it is
        /// said rather than dropped.
        /// </summary>
        private static CausalityLineageConsequence UnretainedCard(CausalConsequenceCohort cohort) => new()
        {
            Kind = CausalityLineageConsequenceKind.Outcome,
            Title = cohort.MemberCount == 1
                ? "A later run acted on this object"
                : $"A later run acted on {cohort.MemberCount} {Noun(cohort)}",
            Text = "The items it recorded are no longer retained.",
            Occurred = cohort.FirstOccurred,
            Cohort = cohort.MemberCount > 1 ? cohort : null,
            MemberCount = cohort.MemberCount
        };

        /// <summary>
        /// The object a reference removal happened to: the Groups (or whatever referenced the deleted object), new
        /// however many objects the branch has already reached in their Connected System.
        /// </summary>
        private ColumnState NewReferringObject(CausalConsequenceCohort cohort, Scope scope)
        {
            var (systemId, systemName) = cohort.Representative is { } representative
                ? ReferringSystem(cohort, representative)
                : (cohort.ConnectedSystemId, cohort.ConnectedSystemName);
            var created = newConsequenceObject(systemId, systemName, false);
            if (systemId is { } id)
                scope.Bind(id, created);
            return created;
        }

        /// <summary>
        /// The Connected System a removal was staged in: the edge names the system whose disconnection deleted the
        /// object, so the Pending Export the item staged is what says where the referring object lives.
        /// </summary>
        private (int? SystemId, string? SystemName) ReferringSystem(CausalConsequenceCohort cohort, ActivityRunProfileExecutionItem representative)
        {
            var model = CausalityModelBuilder.Build(representative, RepresentativeContext(cohort, representative, scope: null));
            var staged = model.Roots.FirstOrDefault(e => e.Lane == CausalityLane.Downstream && e.SystemId.HasValue);
            return staged != null ? (staged.SystemId, staged.SystemName) : (cohort.ConnectedSystemId, cohort.ConnectedSystemName);
        }

        /// <summary>
        /// Names an object from an item that acted on it: the items of a cohort are about the objects they happened to,
        /// so their snapshots name the object better than an event's link can (a queued deprovision knows only the
        /// account's id). A cohort of several is a role, headed by its exact count.
        /// </summary>
        private void NameFromItem(ColumnState target, CausalConsequenceCohort cohort, ActivityRunProfileExecutionItem representative, bool isReferring)
        {
            var isAboutTarget = isReferring || representative.Activity?.ConnectedSystemId == target.SystemId;
            if (!target.IsConsequenceObject || !isAboutTarget
                || (representative.ConnectedSystemObjectId == null && representative.ExternalIdSnapshot == null))
                return;

            if (cohort.MemberCount > 1)
            {
                target.ConsequenceHead = new ColumnHead(Capitalise(Noun(cohort)), IsRoleHead: true, Href: null, ObjectTypeName: null);
                target.MemberCount = Math.Max(target.MemberCount ?? 0, cohort.MemberCount);
                return;
            }

            if (target.ConsequenceHead is { IsRoleHead: true })
                return;

            var name = ObjectDescription.ChipName(representative.DisplayNameSnapshot, representative.ExternalIdSnapshot);
            if (name == null)
                return;

            var href = representative.ConnectedSystemObjectId is { } csoId && target.SystemId is { } systemId
                ? JimUtilities.GetConnectedSystemObjectHref(systemId, csoId)
                : null;
            target.ConsequenceHead = new ColumnHead(name, IsRoleHead: false, href, representative.ObjectTypeSnapshot);
        }

        private ColumnState ObjectFor(Scope scope, int? systemId, string? systemName, bool isSourceSide)
        {
            if (systemId is { } id && scope.Find(id) is { } existing)
                return existing;

            var created = newConsequenceObject(systemId, systemName, isSourceSide);
            if (systemId is { } newId)
                scope.Bind(newId, created);
            return created;
        }

        private void Add(ColumnState target, CausalityLineageConsequence card)
        {
            target.Consequences.Add((card, _sequence++));
            _placed.Add(card);
        }

        /// <summary>
        /// The page context the representative item is built in: its own record and run, so its events name their
        /// systems and link their objects as they would on the item's own page.
        /// </summary>
        private CausalityPageContext RepresentativeContext(CausalConsequenceCohort cohort, ActivityRunProfileExecutionItem representative, Scope? scope)
        {
            var systemId = representative.Activity?.ConnectedSystemId;
            var systemName = systemId == cohort.ConnectedSystemId
                ? cohort.ConnectedSystemName
                : systemId is { } id ? scope?.Find(id)?.SystemName : null;
            return new CausalityPageContext(
                ConnectedSystemId: systemId,
                ConnectedSystemName: systemName,
                RunProfileName: representative.Activity?.TargetName,
                CsoId: representative.ConnectedSystemObjectId,
                CsoConnectedSystemId: systemId,
                CsoConnectedSystemName: systemName,
                CsoDisplayName: representative.DisplayNameSnapshot,
                CsoExternalId: representative.ExternalIdSnapshot,
                CsoObjectTypeName: representative.ObjectTypeSnapshot,
                MvoTypeName: context.MvoTypeName,
                MvoTypePluralName: context.MvoTypePluralName);
        }

        private CausalityLineageConsequenceSummary Summarise(CausalConsequences consequences)
        {
            var waiting = _placed.Where(c => c.Kind == CausalityLineageConsequenceKind.Waiting).ToList();
            var needsAttention = _placed.Count(c => c.Kind == CausalityLineageConsequenceKind.NeedsAttention);

            var sentences = waiting
                .GroupBy(WaitingOn)
                .Select(g => $"{g.Count()} {(g.Count() == 1 ? "branch is" : "branches are")} waiting on {g.Key}.")
                .ToList();
            if (needsAttention > 0)
                sentences.Add($"{needsAttention} {(needsAttention == 1 ? "branch needs" : "branches need")} attention.");
            if (sentences.Count == 0 && _placed.Count > 0)
            {
                var nothingFollowed = consequences.Cohorts.Count == 0
                    && _placed.All(c => c.Kind == CausalityLineageConsequenceKind.Settled);
                sentences.Add(nothingFollowed ? "Nothing followed from this run." : "Every branch has run to its end.");
            }

            var pills = _placed
                .Where(c => c.PillLabel != null)
                .GroupBy(c => (c.PillLabel!, c.PillTone))
                .Select(g =>
                {
                    var count = g.Sum(c => c.MemberCount);
                    return new CausalityPill(count > 1 ? $"{g.Key.Item1} · {count}" : g.Key.Item1, g.Key.PillTone);
                })
                .ToList();

            return new CausalityLineageConsequenceSummary
            {
                CheckedAt = consequences.CheckedAt,
                HasWaitingBranches = waiting.Count > 0,
                IsFurtherThanShown = _furtherThanShown || consequences.IsTruncatedByDepth,
                HasAny = _placed.Count > 0,
                StateSentences = sentences,
                Pills = pills
            };
        }

        /// <summary>
        /// What a waiting card is waiting on, as the footer phrases it.
        /// </summary>
        private static string WaitingOn(CausalityLineageConsequence card)
        {
            const string synchronisationPrefix = "Waiting for the next Synchronisation of ";
            const string exportPrefix = "Waiting for the next Export of ";
            const string importPrefix = "Waiting for the next import of ";
            var title = card.DisplayTitle;
            if (title.StartsWith(synchronisationPrefix, StringComparison.Ordinal))
                return $"a Synchronisation of {title[synchronisationPrefix.Length..]}";
            if (title.StartsWith(exportPrefix, StringComparison.Ordinal))
                return $"an Export of {title[exportPrefix.Length..]}";
            if (title.StartsWith(importPrefix, StringComparison.Ordinal))
                return $"an import of {title[importPrefix.Length..]}";
            return card.PillLabel == "Retry pending" ? "a retry" : "a scheduled deletion";
        }

        private static IEnumerable<(CausalityEvent Event, CausalityEvent? Parent)> Flatten(IEnumerable<CausalityEvent> events, CausalityEvent? parent)
        {
            foreach (var causalityEvent in events)
            {
                yield return (causalityEvent, parent);
                foreach (var child in Flatten(causalityEvent.Children, causalityEvent))
                    yield return child;
            }
        }

        /// <summary>
        /// A chain carrying the queueing edge's decision, so an export's event states what it did (deleted, created,
        /// changes applied) exactly as the export item's own page does.
        /// </summary>
        private static CausalChain? QueueingChain(CausalConsequenceCohort cohort)
        {
            return cohort.EdgeType == CausalEdgeType.PendingExportQueueingCausedExportExecution
                ? new CausalChain { Cohorts = [new CausalChainCohort { EdgeType = CausalEdgeType.PendingExportQueueingCausedExportExecution, ReasonCode = cohort.ReasonCode }] }
                : null;
        }

        private static string? GetRunLabel(Activity? activity)
        {
            if (activity == null)
                return null;

            return activity.ConnectedSystemRunType is { } runType and not ConnectedSystemRunType.NotSet
                ? runType.ToString().SplitOnCapitalLetters()
                : activity.TargetType.ToString().SplitOnCapitalLetters();
        }

        /// <summary>
        /// Why an outcome happened, where the walk knows something the event does not say for itself: the hop that led
        /// to it, or the deletion it sits beneath.
        /// </summary>
        private static string? GetWhy(CausalConsequenceCohort cohort, CausalityEvent causalityEvent, CausalityEvent? parent, bool isFirst)
        {
            if (isFirst)
            {
                switch (cohort.EdgeType)
                {
                    case CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval:
                        return cohort.MemberCount == 1
                            ? "The Metaverse Object it referenced was deleted."
                            : "The Metaverse Object they referenced was deleted.";
                    case CausalEdgeType.MetaverseObjectDeletionCausedDeprovision:
                        return "Its Metaverse Object was deleted.";
                    case CausalEdgeType.MetaverseObjectDeletionScheduleCausedDeletion:
                        return "The grace period its Deletion Rule allowed had ended.";
                    case CausalEdgeType.ExportRejectionCausedGeneratedValueRevision:
                        return "The export of its generated value was rejected.";
                }
            }

            return parent?.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
                   && causalityEvent.Lane != CausalityLane.Identity
                ? "Its Metaverse Object was deleted."
                : null;
        }

        private static string ReferenceRemovalTitle(CausalConsequenceCohort cohort, string? deletedName)
        {
            var removed = string.IsNullOrWhiteSpace(deletedName) ? "a deleted Metaverse Object" : deletedName;
            var attribute = string.IsNullOrWhiteSpace(cohort.AttributeName) ? "a reference" : cohort.AttributeName;
            return cohort.MemberCount == 1
                ? $"Removed {removed} from {attribute}"
                : $"Removed {removed} from {attribute} on {cohort.MemberCount} {Noun(cohort)}";
        }

        private static string ReferenceRemovalPill(CausalConsequenceCohort cohort) =>
            string.IsNullOrWhiteSpace(cohort.AttributeName) ? "Reference removed" : $"Removed from {cohort.AttributeName}";

        private static string ConfirmationTitle(CausalConsequenceCohort cohort) =>
            cohort.MemberCount == 1
                ? "Confirmed by the next import"
                : $"Confirmed by the next import, on {cohort.MemberCount} {Noun(cohort)}";

        /// <summary>
        /// The cohort's objects, singular or plural by count, from the type the items snapshotted.
        /// </summary>
        private static string Noun(CausalConsequenceCohort cohort)
        {
            var noun = cohort.MemberCount == 1
                ? cohort.ObjectTypeName
                : cohort.ObjectTypePluralName ?? cohort.ObjectTypeName;
            if (!string.IsNullOrWhiteSpace(noun))
                return noun;

            return cohort.MemberCount == 1 ? "object" : "objects";
        }

        private static string Capitalise(string text) =>
            text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>
    /// The objects a branch has reached, by Connected System, inherited down the branch.
    /// </summary>
    private sealed class Scope(Scope? parent)
    {
        private readonly Dictionary<int, ColumnState> _objects = new();

        public ColumnState? Find(int systemId) =>
            _objects.TryGetValue(systemId, out var state) ? state : parent?.Find(systemId);

        public void Bind(int systemId, ColumnState state) => _objects[systemId] = state;

        public void BindIfAbsent(int systemId, ColumnState state) => _objects.TryAdd(systemId, state);
    }
}
