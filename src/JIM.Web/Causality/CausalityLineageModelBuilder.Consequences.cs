// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Activities.DTOs;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Utilities;
using JIM.Web.Localisation;
using JIM.Web.Resources;

namespace JIM.Web.Causality;

public static partial class CausalityLineageModelBuilder
{
    private static string Format(string template, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, template, arguments);

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
        private readonly Dictionary<CausalityLineageConsequence, string> _waitingOn = new(ReferenceEqualityComparer.Instance);
        private HashSet<Guid> _livePendingExportIds = [];

        public CausalityLineageConsequenceSummary Place(CausalConsequences consequences)
        {
            // A Pending Export is deleted once exported, so only the ones the walk found still waiting can be linked as
            // rows; every other one a later run's card names links its system's queue instead.
            _livePendingExportIds = AllCohorts(consequences.Cohorts)
                .Where(c => c.HopKind == CausalConsequenceHopKind.OutstandingPendingExport)
                .SelectMany(c => c.Members)
                .Where(m => m.PendingExportId.HasValue)
                .Select(m => m.PendingExportId!.Value)
                .ToHashSet();

            var rootScope = new Scope(null);
            foreach (var state in recordColumns.Where(c => c.SystemId.HasValue))
                rootScope.BindIfAbsent(state.SystemId!.Value, state);

            foreach (var ending in consequences.Endings)
                PlaceEnding(ending, subject);

            foreach (var cohort in consequences.Cohorts)
                PlaceCohort(cohort, rootScope, subject, deletedName: null, parentNoun: CohortNoun.None);

            return Summarise(consequences);
        }

        private void PlaceCohort(CausalConsequenceCohort cohort, Scope parentScope, ColumnState parentObject, string? deletedName,
            CohortNoun parentNoun)
        {
            var scope = new Scope(parentScope);
            var cohortObject = parentObject;
            var noun = CohortNoun.For(cohort, parentNoun);

            switch (cohort.HopKind)
            {
                case CausalConsequenceHopKind.OutstandingPendingExport:
                    cohortObject = ObjectFor(scope, cohort.ConnectedSystemId, cohort.ConnectedSystemName, isSourceSide: false);
                    var outstandingCard = OutstandingCard(cohort, out var outstandingWaitingOn);
                    Add(cohortObject, outstandingCard, outstandingWaitingOn);
                    break;

                case CausalConsequenceHopKind.ScheduledDeletion:
                    cohortObject = getIdentityColumn();
                    Add(cohortObject, new CausalityLineageConsequence
                    {
                        Kind = CausalityLineageConsequenceKind.Waiting,
                        Title = CausalityText.LineageDeletionScheduledTitle,
                        Text = CausalityText.LineageDeletionScheduledText,
                        DueAt = cohort.DueAt,
                        PillLabel = CausalityText.LineageDeletionScheduledTitle,
                        PillTone = CausalityTone.Warning
                    }, CausalityText.LineageWaitingOnScheduledDeletion);
                    break;

                default:
                    var removalObject = cohort.EdgeType == CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval
                        ? NewReferringObject(cohort, scope)
                        : null;
                    cohortObject = cohort.Representative != null
                        ? PlaceOutcomes(cohort, cohort.Representative, scope, removalObject, deletedName, noun) ?? removalObject ?? parentObject
                        : removalObject ?? parentObject;
                    if (cohort.Representative == null)
                        Add(cohortObject, UnretainedCard(cohort, noun));
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
                // Carrying on from the farthest item the walk reached shows what followed from there (#2100). A cohort of
                // several already links each member, so only a single item gets a link of its own.
                var farthestItemId = cohort.MemberCount == 1
                    ? cohort.Representative?.Id ?? cohort.Members.FirstOrDefault()?.RunProfileExecutionItemId
                    : null;
                var farthestName = cohort.Representative?.DisplayNameSnapshot ?? cohort.Members.FirstOrDefault()?.DisplayName;
                Add(cohortObject, new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.FurtherThanShown,
                    Title = CausalityText.LineageFurtherThanShownTitle,
                    Text = farthestItemId != null
                        ? CausalityText.LineageWalkStoppedAtItem
                        : CausalityText.LineageWalkStoppedAtItems,
                    LinkText = farthestItemId != null ? ContinueFromText(farthestName) : null,
                    LinkHref = farthestItemId is { } itemId ? context.Links.ItemHref(itemId) : null
                });
            }

            foreach (var consequence in cohort.Consequences)
                PlaceCohort(consequence, scope, cohortObject, deletedName, noun);
        }

        /// <summary>
        /// Builds the representative item into events and places each on its object, returning the object the cohort's
        /// own story continues on: the last record its events reached, else the Metaverse Object.
        /// </summary>
        private ColumnState? PlaceOutcomes(CausalConsequenceCohort cohort, ActivityRunProfileExecutionItem representative,
            Scope scope, ColumnState? removalObject, string? deletedName, CohortNoun noun)
        {
            var representativeContext = RepresentativeContext(cohort, representative, scope);
            var model = CausalityModelBuilder.Build(representative, representativeContext, _livePendingExportIds, chain: QueueingChain(cohort));
            var runLabel = GetRunLabel(representative.Activity);
            var occurred = representative.Activity?.Executed ?? cohort.FirstOccurred;
            var itemHref = cohort.MemberCount == 1 ? context.Links.ItemHref(representative.Id) : null;
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
                // link to it, else as this run's own staging targets are: the story's subject, arriving in that system.
                if (target is { IsConsequenceObject: true, ConsequenceHead: null })
                {
                    var recordLink = causalityEvent.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Record);
                    var subjectName = ObjectDescription.ChipName(representative.DisplayNameSnapshot, representative.ExternalIdSnapshot);
                    target.ConsequenceHead = recordLink != null
                        ? new ColumnHead(recordLink.Label, IsRoleHead: false, recordLink.Href, recordLink.ObjectTypeName)
                        : new ColumnHead(subjectName ?? target.SystemName ?? CausalityText.LineageConnectedSystemObjectFallback, IsRoleHead: false, Href: null, ObjectTypeName: null);
                }

                var isReferenceRemoval = first && removalObject != null;
                var setting = GetDecidingSetting(causalityEvent, representative);
                Add(target, new CausalityLineageConsequence
                {
                    SettingLabel = setting?.Label,
                    SettingValue = setting?.Value,
                    SettingText = setting?.Text,
                    Kind = isConfirmation ? CausalityLineageConsequenceKind.Confirmation : CausalityLineageConsequenceKind.Outcome,
                    Event = causalityEvent,
                    Title = isConfirmation
                        ? ConfirmationTitle(cohort, noun)
                        : isReferenceRemoval ? ReferenceRemovalTitle(cohort, deletedName, noun) : null,
                    Text = isConfirmation ? null : GetWhy(cohort, causalityEvent, parentEvent, first),
                    RunLabel = runLabel,
                    Occurred = occurred,
                    ActivityItemHref = itemHref,
                    Cohort = first && cohort.MemberCount > 1 ? cohort : null,
                    MembersNoun = noun.For(cohort.MemberCount),
                    PillLabel = isConfirmation
                        ? null
                        : isReferenceRemoval ? ReferenceRemovalPill(cohort) : causalityEvent.Label,
                    PillTone = causalityEvent.Operation?.Tone ?? causalityEvent.Tone,
                    MemberCount = cohort.MemberCount
                });

                if (target.Kind == CausalityLineageColumnKind.Record)
                {
                    NameFromItem(target, cohort, representative, isReferring: removalObject != null, noun, context.Links);
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
                    Title = CausalityText.LineageNothingMoreFollows,
                    Text = Format(CausalityText.LineageStaysInSystem, disconnected.SystemName ?? CausalityText.LineageItsConnectedSystem)
                });
            }

            return lastRecord ?? lastObject;
        }

        private void PlaceEnding(CausalConsequenceEndingCount ending, ColumnState target)
        {
            var systemName = ending.ConnectedSystemName ?? CausalityText.LineageItsConnectedSystem;
            var card = ending.Ending switch
            {
                CausalConsequenceEnding.WaitingForSynchronisation => new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.Waiting,
                    Title = Format(CausalityText.LineageWaitingForSynchronisationTitle, systemName),
                    Text = Format(CausalityText.LineageWaitingForSynchronisationText, systemName),
                    PillLabel = CausalityText.LineageWaitingForSynchronisationPill,
                    PillTone = CausalityTone.Warning
                },
                CausalConsequenceEnding.NothingFollowed => new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.Settled,
                    Title = CausalityText.LineageNothingFollowed,
                    Text = Format(CausalityText.LineageNothingFollowedText, systemName),
                    PillLabel = CausalityText.LineageNothingFollowed,
                    PillTone = CausalityTone.Secondary
                },
                // Nothing the item's kind could cause was recorded: there is nothing to say, and saying nothing is right.
                _ => null
            };

            if (card != null)
                Add(target, card, ending.Ending == CausalConsequenceEnding.WaitingForSynchronisation
                    ? Format(CausalityText.LineageWaitingOnSynchronisation, systemName)
                    : null);
        }

        private static CausalityLineageConsequence OutstandingCard(CausalConsequenceCohort cohort, out string? waitingOn)
        {
            var count = cohort.MemberCount;
            var systemName = cohort.ConnectedSystemName ?? CausalityText.LineageItsConnectedSystem;
            var (linkText, linkHref) = PendingExportLink(cohort);
            waitingOn = cohort.Ending switch
            {
                CausalConsequenceEnding.NeedsAttention => null,
                CausalConsequenceEnding.WaitingForRetry => CausalityText.LineageWaitingOnRetry,
                CausalConsequenceEnding.WaitingForConfirmingImport => Format(CausalityText.LineageWaitingOnImport, systemName),
                _ => Format(CausalityText.LineageWaitingOnExport, systemName)
            };
            return cohort.Ending switch
            {
                CausalConsequenceEnding.NeedsAttention => new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.NeedsAttention,
                    Title = CausalityText.LineageNeedsAttention,
                    Text = Format(count == 1 ? CausalityText.LineageNeedsAttentionTextOne : CausalityText.LineageNeedsAttentionTextOther, count),
                    LinkText = linkText,
                    LinkHref = linkHref,
                    PillLabel = CausalityText.LineageNeedsAttention,
                    PillTone = CausalityTone.Error,
                    MemberCount = count
                },
                CausalConsequenceEnding.WaitingForRetry => new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.Waiting,
                    Title = CausalityText.LineageWaitingForRetryTitle,
                    Text = Format(count == 1 ? CausalityText.LineageWaitingForRetryTextOne : CausalityText.LineageWaitingForRetryTextOther, count, systemName),
                    LinkText = linkText,
                    LinkHref = linkHref,
                    DueAt = cohort.DueAt,
                    PillLabel = CausalityText.LineageRetryPending,
                    PillTone = CausalityTone.Warning,
                    MemberCount = count
                },
                CausalConsequenceEnding.WaitingForConfirmingImport => new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.Waiting,
                    Title = Format(CausalityText.LineageWaitingForImportTitle, systemName),
                    Text = CausalityText.LineageWaitingForImportText,
                    LinkText = linkText,
                    LinkHref = linkHref,
                    PillLabel = CausalityText.LineageWaitingForImportPill,
                    PillTone = CausalityTone.Warning,
                    MemberCount = count
                },
                _ => new CausalityLineageConsequence
                {
                    Kind = CausalityLineageConsequenceKind.Waiting,
                    Title = Format(CausalityText.LineageWaitingForExportTitle, systemName),
                    Text = count == 1 ? CausalityText.LineageQueuedAsPendingExport : Format(CausalityText.LineageQueuedPendingExports, count),
                    LinkText = linkText,
                    LinkHref = linkHref,
                    PillLabel = CausalityText.LineageWaitingForExportPill,
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
                ? (CausalityText.LineageViewPendingExport, $"{queueHref}/{pendingExportId}")
                : (CausalityText.LineageViewPendingExports, queueHref);
        }

        /// <summary>
        /// A cohort whose items have since been purged: the count and the change are still known from the walk, so it is
        /// said rather than dropped.
        /// </summary>
        private static CausalityLineageConsequence UnretainedCard(CausalConsequenceCohort cohort, CohortNoun noun) => new()
        {
            Kind = CausalityLineageConsequenceKind.Outcome,
            Title = cohort.MemberCount == 1
                ? CausalityText.LineageLaterRunActedOnObject
                : Format(CausalityText.LineageLaterRunActedOnObjects, cohort.MemberCount, noun.For(cohort.MemberCount)),
            Text = CausalityText.LineageItemsNoLongerRetained,
            Occurred = cohort.FirstOccurred,
            Cohort = cohort.MemberCount > 1 ? cohort : null,
            MembersNoun = noun.For(cohort.MemberCount),
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
        private static void NameFromItem(ColumnState target, CausalConsequenceCohort cohort, ActivityRunProfileExecutionItem representative,
            bool isReferring, CohortNoun noun, ICausalityLinks links)
        {
            var isAboutTarget = isReferring || representative.Activity?.ConnectedSystemId == target.SystemId;
            if (!target.IsConsequenceObject || !isAboutTarget
                || (representative.ConnectedSystemObjectId == null && representative.ExternalIdSnapshot == null))
                return;

            if (cohort.MemberCount > 1)
            {
                // The first cohort to reach the objects names them; a later one only adds to how many there are, so an
                // import that snapshotted no type cannot rename the Groups "objects".
                // A type name is the system's own identifier and keeps its own casing; only the bare fallback needs a head
                // of its own.
                if (target.ConsequenceHead is not { IsRoleHead: true })
                {
                    var title = noun.Type == null && noun.Plural == null ? CausalityText.LineageConnectedSystemObjectsFallback : noun.For(cohort.MemberCount);
                    target.ConsequenceHead = new ColumnHead(title, IsRoleHead: true, Href: null, ObjectTypeName: null);
                }
                target.MemberCount = Math.Max(target.MemberCount ?? 0, cohort.MemberCount);
                return;
            }

            if (target.ConsequenceHead is { IsRoleHead: true })
                return;

            var name = ObjectDescription.ChipName(representative.DisplayNameSnapshot, representative.ExternalIdSnapshot);
            if (name == null)
                return;

            // Linked only while the object is there: the item keeps its id after the object goes (#2669).
            var href = LiveRecordId(cohort) is { } csoId && target.SystemId is { } systemId
                ? links.ConnectedSystemObjectHref(systemId, csoId)
                : null;

            // The first item about the object names it; a later one (a confirming import, which often snapshots no type)
            // only fills in what the name is still missing.
            if (target.IsHeadFromItem && target.ConsequenceHead is { } named)
            {
                target.ConsequenceHead = named with
                {
                    Href = named.Href ?? href,
                    ObjectTypeName = named.ObjectTypeName ?? representative.ObjectTypeSnapshot
                };
                return;
            }

            target.ConsequenceHead = new ColumnHead(name, IsRoleHead: false, href, representative.ObjectTypeSnapshot);
            target.IsHeadFromItem = true;
        }

        /// <summary>
        /// The id of the representative's Connected System Object while it is there; null once it has gone, so nothing
        /// links to it. Asked of the resolver, never of the item's id, which outlives the object (#2669).
        /// </summary>
        private static Guid? LiveRecordId(CausalConsequenceCohort cohort) =>
            cohort.RepresentativeRecord is { IsLive: true } record ? record.Id : null;

        private ColumnState ObjectFor(Scope scope, int? systemId, string? systemName, bool isSourceSide)
        {
            if (systemId is { } id && scope.Find(id) is { } existing)
                return existing;

            var created = newConsequenceObject(systemId, systemName, isSourceSide);
            if (systemId is { } newId)
                scope.Bind(newId, created);
            return created;
        }

        /// <summary>
        /// Places a card. A waiting card also records what it is waiting on, in words, for the footer's sentences; the
        /// footer never reads that back out of the card's own (translated) title.
        /// </summary>
        private void Add(ColumnState target, CausalityLineageConsequence card, string? waitingOn = null)
        {
            if (waitingOn != null)
                _waitingOn[card] = waitingOn;

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
                CsoId: LiveRecordId(cohort),
                CsoConnectedSystemId: systemId,
                CsoConnectedSystemName: systemName,
                CsoDisplayName: representative.DisplayNameSnapshot,
                CsoExternalId: representative.ExternalIdSnapshot,
                CsoObjectTypeName: representative.ObjectTypeSnapshot,
                MvoTypeName: context.MvoTypeName,
                MvoTypePluralName: context.MvoTypePluralName)
            {
                // A later run's item is read under the host's policy too, or its links would leave the explorer.
                Links = context.Links
            };
        }

        private CausalityLineageConsequenceSummary Summarise(CausalConsequences consequences)
        {
            var waiting = _placed.Where(c => c.Kind == CausalityLineageConsequenceKind.Waiting).ToList();
            var needsAttention = _placed.Count(c => c.Kind == CausalityLineageConsequenceKind.NeedsAttention);

            var sentences = waiting
                .GroupBy(WaitingOn)
                .Select(g => Format(g.Count() == 1 ? CausalityText.LineageBranchesWaitingOne : CausalityText.LineageBranchesWaitingOther, g.Count(), g.Key))
                .ToList();
            if (needsAttention > 0)
                sentences.Add(Format(needsAttention == 1 ? CausalityText.LineageBranchNeedsAttentionOne : CausalityText.LineageBranchNeedsAttentionOther, needsAttention));
            if (sentences.Count == 0 && _placed.Count > 0)
            {
                var nothingFollowed = consequences.Cohorts.Count == 0
                    && _placed.All(c => c.Kind == CausalityLineageConsequenceKind.Settled);
                sentences.Add(nothingFollowed ? CausalityText.LineageNothingFollowedFromRun : CausalityText.LineageEveryBranchEnded);
            }

            var pills = _placed
                .Where(c => c.PillLabel != null)
                .GroupBy(c => (c.PillLabel!, c.PillTone))
                .Select(g =>
                {
                    var count = g.Sum(c => c.MemberCount);
                    return new CausalityPill(count > 1 ? Format(CausalityText.LineagePillWithCount, g.Key.Item1, count) : g.Key.Item1, g.Key.PillTone);
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
        private string WaitingOn(CausalityLineageConsequence card) =>
            _waitingOn.TryGetValue(card, out var waitingOn) ? waitingOn : CausalityText.LineageWaitingOnScheduledDeletion;

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
                ? runType switch
                {
                    ConnectedSystemRunType.FullImport => CausalityText.LineageRunTypeFullImport,
                    ConnectedSystemRunType.DeltaImport => CausalityText.LineageRunTypeDeltaImport,
                    ConnectedSystemRunType.FullSynchronisation => CausalityText.LineageRunTypeFullSynchronisation,
                    ConnectedSystemRunType.DeltaSynchronisation => CausalityText.LineageRunTypeDeltaSynchronisation,
                    ConnectedSystemRunType.Export => CausalityText.LineageRunTypeExport,
                    _ => runType.ToString().SplitOnCapitalLetters()
                }
                : Helpers.GetActivityTargetTypeName(activity.TargetType);
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
                            ? CausalityText.LineageWhyReferencedDeletedOne
                            : CausalityText.LineageWhyReferencedDeletedOther;
                    case CausalEdgeType.MetaverseObjectDeletionCausedDeprovision:
                        return CausalityText.LineageWhyMetaverseObjectDeleted;
                    case CausalEdgeType.MetaverseObjectDeletionScheduleCausedDeletion:
                        return CausalityText.LineageWhyGracePeriodEnded;
                    case CausalEdgeType.ExportRejectionCausedGeneratedValueRevision:
                        return CausalityText.LineageWhyExportRejected;
                }
            }

            return parent?.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
                   && causalityEvent.Lane != CausalityLane.Identity
                ? CausalityText.LineageWhyMetaverseObjectDeleted
                : null;
        }

        /// <summary>
        /// The configured setting that chose an outcome, as it stood when the run decided: the Deprovisioning Action
        /// behind a deprovision (read from the outcome itself, since each action records its own), and the Deletion Rule
        /// and grace period behind a Metaverse Object's deletion (from the policy snapshot the deciding item recorded).
        /// Those are what an administrator would change to get a different result.
        /// </summary>
        private static (string Label, string Value, string Text)? GetDecidingSetting(
            CausalityEvent causalityEvent, ActivityRunProfileExecutionItem representative)
        {
            switch (causalityEvent.OutcomeType)
            {
                case ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued:
                    return (CausalityText.LineageSettingDeprovisioningAction, CausalityText.LineageSettingDelete, CausalityText.LineageSettingDeleteText);
                case ActivityRunProfileExecutionItemSyncOutcomeType.TargetDisconnected:
                    return (CausalityText.LineageSettingDeprovisioningAction, CausalityText.LineageSettingDisconnect, CausalityText.LineageSettingDisconnectText);
                case ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted:
                case ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled:
                    if (MvoDeletionPolicySnapshot.FromJson(representative.DeletionPolicySnapshotJson) is not { } policy)
                        return null;

                    var rule = policy.DeletionRule switch
                    {
                        MetaverseObjectDeletionRule.Manual => CausalityText.LineageDeletionRuleManual,
                        MetaverseObjectDeletionRule.WhenLastConnectorDisconnected => CausalityText.LineageDeletionRuleWhenLastConnectorDisconnected,
                        MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected => CausalityText.LineageDeletionRuleWhenAuthoritativeSourceDisconnected,
                        _ => policy.DeletionRule.ToString().SplitOnCapitalLetters()
                    };
                    var gracePeriod = policy.GracePeriod is { } period && period > TimeSpan.Zero ? period : (TimeSpan?)null;
                    var text = causalityEvent.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled
                        ? Format(CausalityText.LineageGracePeriodScheduled, gracePeriod?.ToFriendlyDuration() ?? CausalityText.LineageGracePeriodNone)
                        : gracePeriod is { } grace
                            ? Format(CausalityText.LineageGracePeriodEnded, grace.ToFriendlyDuration())
                            : CausalityText.LineageNoGracePeriod;
                    return (CausalityText.LineageSettingDeletionRule, rule, text);
                default:
                    return null;
            }
        }

        private static string ReferenceRemovalTitle(CausalConsequenceCohort cohort, string? deletedName, CohortNoun noun)
        {
            var removed = string.IsNullOrWhiteSpace(deletedName) ? CausalityText.LineageDeletedMetaverseObjectFallback : deletedName;
            var attribute = string.IsNullOrWhiteSpace(cohort.AttributeName) ? CausalityText.LineageReferenceFallback : cohort.AttributeName;
            return cohort.MemberCount == 1
                ? Format(CausalityText.LineageRemovedFrom, removed, attribute)
                : Format(CausalityText.LineageRemovedFromOnObjects, removed, attribute, cohort.MemberCount, noun.For(cohort.MemberCount));
        }

        private static string ReferenceRemovalPill(CausalConsequenceCohort cohort) =>
            string.IsNullOrWhiteSpace(cohort.AttributeName) ? CausalityText.LineageReferenceRemoved : Format(CausalityText.LineageRemovedFromAttribute, cohort.AttributeName);

        private static string ConfirmationTitle(CausalConsequenceCohort cohort, CohortNoun noun) =>
            cohort.MemberCount == 1
                ? CausalityText.LineageConfirmedByNextImport
                : Format(CausalityText.LineageConfirmedByNextImportOnObjects, cohort.MemberCount, noun.For(cohort.MemberCount));

        private static IEnumerable<CausalConsequenceCohort> AllCohorts(IEnumerable<CausalConsequenceCohort> cohorts) =>
            cohorts.SelectMany(c => AllCohorts(c.Consequences).Prepend(c));
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

    /// <summary>
    /// What a cohort's objects are called (#1811): the type its items snapshotted, else the type the branch above already
    /// gave the same objects, since a confirming import often snapshots none.
    /// </summary>
    private sealed record CohortNoun(string? Type, string? Plural)
    {
        public static readonly CohortNoun None = new(null, null);

        public static CohortNoun For(CausalConsequenceCohort cohort, CohortNoun parent) =>
            string.IsNullOrWhiteSpace(cohort.ObjectTypeName)
                ? parent
                : new CohortNoun(cohort.ObjectTypeName, cohort.ObjectTypePluralName);

        /// <summary>
        /// Singular or plural by count. A type with no curated plural is not given an invented one ("jimGroups"); it
        /// reads as "jimGroup objects", which is plural however the type is named.
        /// </summary>
        public string For(int count)
        {
            if (count == 1)
                return string.IsNullOrWhiteSpace(Type) ? CausalityText.LineageNounObject : Type;

            if (!string.IsNullOrWhiteSpace(Plural))
                return Plural;

            return string.IsNullOrWhiteSpace(Type) ? CausalityText.LineageNounObjects : Format(CausalityText.LineageNounTypeObjects, Type);
        }
    }
}
