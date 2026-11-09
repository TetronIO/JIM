// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What a Pending Export attribute change held, of what can change between a batch being sent and its results being
/// written, besides the sent mark itself (#2851): the value, which a Connector may replace as it writes (the LDAP
/// Connector records the default it writes for a protected attribute it cannot clear), and the two holds the batch's
/// results set. Taken as the batch is marked Executing, so the export-result writer writes only the changes that differ.
/// </summary>
/// <param name="StringValue">The change's <see cref="PendingExportAttributeValueChange.StringValue"/>.</param>
/// <param name="AwaitsInitialPassword">The change's <see cref="PendingExportAttributeValueChange.AwaitsInitialPassword"/>.</param>
/// <param name="AwaitsExchange">The change's <see cref="PendingExportAttributeValueChange.AwaitsExchange"/>.</param>
public readonly record struct PendingExportAttributeValueChangeState(string? StringValue, bool AwaitsInitialPassword, bool AwaitsExchange)
{
    /// <summary>
    /// The change's state as it stands.
    /// </summary>
    public static PendingExportAttributeValueChangeState Of(PendingExportAttributeValueChange change) =>
        new(change.StringValue, change.AwaitsInitialPassword, change.AwaitsExchange);

    /// <summary>
    /// The state of every change of <paramref name="pendingExports"/> as it stands, by instance: a batch's changes are
    /// the instances its results are decided on, and a change created by a test or by staging may not have its id yet.
    /// </summary>
    public static IReadOnlyDictionary<PendingExportAttributeValueChange, PendingExportAttributeValueChangeState> Snapshot(
        IEnumerable<PendingExport> pendingExports)
    {
        var states = new Dictionary<PendingExportAttributeValueChange, PendingExportAttributeValueChangeState>(ReferenceEqualityComparer.Instance);
        foreach (var change in pendingExports.SelectMany(pe => pe.AttributeValueChanges))
            states[change] = Of(change);
        return states;
    }
}
