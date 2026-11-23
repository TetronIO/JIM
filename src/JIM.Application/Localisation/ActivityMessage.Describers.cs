// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Resources;
using JIM.Application.Servers;
using JIM.Models.Localisation;
using JIM.Models.Staging;
using JIM.Models.Sync;

namespace JIM.Application.Localisation;

/// <summary>
/// The Activity texts that other layers describe in English (Run Profile enablement, import completeness, the results of a
/// clear, a reconciliation sweep and a history clean-up), as messages (#3039). Each says exactly what the English
/// description says; the tests compare the two.
/// </summary>
public static partial class ActivityMessage
{
    /// <summary>Why a run of a disabled Run Profile is refused: <see cref="RunProfileEnablement.DescribeRunRefusal"/> as a message.</summary>
    public static JimMessage RunRefusal(ConnectedSystemRunProfile runProfile)
    {
        ArgumentNullException.ThrowIfNull(runProfile);

        var whyDisabled = runProfile is { Enabled: false, DisabledReason: ConnectedSystemRunProfileDisabledReason.PartitionRemoved }
            ? Create(nameof(ActivityMessages.DisabledPartitionRemoved), runProfile.DisabledPartitionName)
            : Create(nameof(ActivityMessages.DisabledByAdministrator));
        var remedy = runProfile.NeedsScopeToEnable()
            ? Create(nameof(ActivityMessages.RemedyEnableChoosingPartition))
            : Create(nameof(ActivityMessages.RemedyEnable));

        return Create(nameof(ActivityMessages.RunRefusal), runProfile.Name, whyDisabled, remedy);
    }

    /// <summary>
    /// Why a synchronisation of a Connected System marked import incomplete is refused:
    /// <see cref="ImportCompleteness.DescribeSynchronisationRefusal"/> as a message.
    /// </summary>
    public static JimMessage ImportIncompleteRefusal(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(runProfile);

        return Create(nameof(ActivityMessages.ImportIncompleteRefusal), runProfile.Name, connectedSystem.Name,
            BeganWriting(connectedSystem), ImportIncompleteRemedy(connectedSystem));
    }

    /// <summary>
    /// What an import that completed says when it cannot take down the import-incomplete marker:
    /// <see cref="ImportCompleteness.DescribeStillIncompleteAfterImport"/> as a message.
    /// </summary>
    public static JimMessage ImportStillIncomplete(ConnectedSystem connectedSystem, bool readOnlyChanges, bool scopedToOnePartition)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        var thisRun = readOnlyChanges ? Create(nameof(ActivityMessages.ImportStillIncompleteDelta))
            : scopedToOnePartition ? Create(nameof(ActivityMessages.ImportStillIncompleteOnePartition))
            : Create(nameof(ActivityMessages.ImportStillIncompleteNoObjects));

        return Create(nameof(ActivityMessages.ImportStillIncomplete), BeganWriting(connectedSystem), thisRun, ImportIncompleteRemedy(connectedSystem));
    }

    private static MessageArgument BeganWriting(ConnectedSystem connectedSystem) =>
        connectedSystem.ImportIncompleteSince is { } startedWritingAt
            ? Create(nameof(ActivityMessages.ImportIncompleteBeganWriting),
                startedWritingAt.ToString("dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " UTC")
            : string.Empty;

    private static JimMessage ImportIncompleteRemedy(ConnectedSystem connectedSystem) =>
        Create(nameof(ActivityMessages.ImportIncompleteRemedy),
            connectedSystem.Partitions?.Any(p => p.Selected) == true
                ? Create(nameof(ActivityMessages.ImportIncompleteRemedyPartitions))
                : string.Empty);

    /// <summary>
    /// The Delta Import starting points a clear removed: <see cref="ClearConnectedSystemResult.DescribeDeltaImportReset"/>
    /// as a message; null when it removed none.
    /// </summary>
    public static JimMessage? DeltaImportReset(ClearConnectedSystemResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var partitions = Counted(nameof(ActivityMessages.PartitionCountOne), result.PartitionWatermarksRemoved, result.PartitionWatermarksRemoved);
        return (result.ImportWatermarkRemoved, result.PartitionWatermarksRemoved > 0) switch
        {
            (true, true) => Create(nameof(ActivityMessages.DeltaImportResetBoth), partitions),
            (true, false) => Create(nameof(ActivityMessages.DeltaImportResetSystem)),
            (false, true) => Create(nameof(ActivityMessages.DeltaImportResetPartitions), partitions),
            _ => null
        };
    }

    /// <summary>What a Temporal Scope Reconciliation sweep says about itself: <see cref="ScopeReconciliationResult.Describe"/> as a message; null when nothing is unusual.</summary>
    public static JimMessage? ScopeReconciliation(ScopeReconciliationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var parts = new List<JimMessage>();
        if (result.FullPassRules > 0)
            parts.Add(Create(nameof(ActivityMessages.ScopeReconciliationFullPass), result.FullPassObjectsEvaluated, result.FullPassRules));
        if (result.UndecidableObjects > 0)
            parts.Add(Create(nameof(ActivityMessages.ScopeReconciliationUndecidable), result.UndecidableObjects));

        return Spaced(parts);
    }

    /// <summary>What the history retention clean-up removed, in brief.</summary>
    public static JimMessage RetentionCleanup(ChangeHistoryServer.ChangeHistoryCleanupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var parts = new List<JimMessage>();

        void Add(int count, string oneKey)
        {
            if (count > 0)
                parts.Add(Counted(oneKey, count, Plain(count)));
        }

        Add(result.CsoChangesDeleted, nameof(ActivityMessages.RetentionCsoChangesOne));
        Add(result.MvoChangesDeleted, nameof(ActivityMessages.RetentionMvoChangesOne));
        Add(result.PreviewsDeleted, nameof(ActivityMessages.RetentionPreviewsOne));
        Add(result.ActivitiesDeleted, nameof(ActivityMessages.RetentionActivitiesOne));
        Add(result.ConfigurationChangeActivitiesDeleted, nameof(ActivityMessages.RetentionConfigurationActivitiesOne));
        Add(result.SecurityEventActivitiesDeleted, nameof(ActivityMessages.RetentionSecurityActivitiesOne));
        Add(result.PasswordEventActivitiesDeleted, nameof(ActivityMessages.RetentionPasswordActivitiesOne));
        Add(result.PasswordQueueRecordsDeleted, nameof(ActivityMessages.RetentionPasswordQueueOne));
        Add(result.ExchangeQueueRecordsDeleted, nameof(ActivityMessages.RetentionExchangeQueueOne));

        return Listed(parts) is { } removed
            ? Create(nameof(ActivityMessages.RetentionRemoved), removed)
            : Create(nameof(ActivityMessages.RetentionNothing));
    }
}
