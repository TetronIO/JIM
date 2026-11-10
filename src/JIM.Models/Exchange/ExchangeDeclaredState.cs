// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Models.Exchange;

/// <summary>
/// An object's declared Exchange recipient state as one export leaves it (#2021): the values the last import read for
/// the five connector-defined Exchange attributes, with the export's changes to them laid over, plus what the rules need
/// to judge it (the recipient type the import read, and the group's scope).
/// </summary>
public sealed record ExchangeDeclaredState
{
    /// <summary>The name Active Directory gives a group's type and scope.</summary>
    public const string GroupTypeAttributeName = "groupType";

    public string? RecipientType { get; init; }

    public string? Alias { get; init; }

    public string? Database { get; init; }

    public string? PrimarySmtpAddress { get; init; }

    public string? ExternalAddress { get; init; }

    /// <summary>
    /// The recipient type the last import read back, or null where it read none (a Create, or an object that is not a
    /// recipient), which the rules take as None.
    /// </summary>
    public string? ImportedRecipientType { get; init; }

    /// <summary>The group's <c>groupType</c> after the export, or null where JIM does not know it.</summary>
    public long? GroupType { get; init; }

    /// <summary>
    /// The Synchronisation Rule whose Attribute Flow declared the state: the one behind the recipient type change, else
    /// behind any other Exchange attribute change, else the rule that provisioned the object. Null where the export
    /// names none.
    /// </summary>
    public int? DeclaringSyncRuleId { get; init; }

    /// <summary>
    /// Whether <paramref name="changes"/> change any of the five connector-defined Exchange attributes: only an export
    /// that does has anything to say to Exchange.
    /// </summary>
    public static bool IsDeclaredBy(IEnumerable<PendingExportAttributeValueChange> changes) =>
        changes.Any(IsExchangeAttributeChange);

    /// <summary>
    /// The state <paramref name="export"/> declares for its object: the object's current values, overlaid with the
    /// changes its Pending Export still holds for Exchange from earlier exports, overlaid with <paramref name="changes"/>
    /// (what this export wrote). The held changes matter where Exchange has not acted: an account whose mailbox parked
    /// reads back as no recipient, and an export correcting only its database must still declare the mailbox, as the
    /// delivery lane's <see cref="Of(IReadOnlyDictionary{string, string?}, IEnumerable{ExchangeHeldChange})"/> does.
    /// An Add or Update sets a value and a Remove or RemoveAll clears it; removals are applied first in each layer, so a
    /// single-valued replace written as a removal and an addition lands on the addition whichever order it was staged in.
    /// </summary>
    public static ExchangeDeclaredState From(PendingExport export, IReadOnlyCollection<PendingExportAttributeValueChange> changes)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(changes);
        var currentValues = export.ConnectedSystemObject?.AttributeValues ?? [];

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var value in currentValues.Where(v => v.Attribute is { IsConnectorDefined: true } a && ExchangeRecipientAttributes.All.Contains(a.Name)))
            values[value.Attribute.Name] = value.StringValue;

        var imported = values.GetValueOrDefault(ExchangeRecipientAttributes.RecipientType);

        var writtenNow = changes.ToHashSet();
        var held = export.AttributeValueChanges
            .Where(c => !writtenNow.Contains(c) && c.AwaitsExchange && c.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation)
            .Where(IsExchangeAttributeChange)
            .ToList();
        LayOver(values, held);

        var exchangeChanges = changes.Where(IsExchangeAttributeChange).ToList();
        LayOver(values, exchangeChanges);

        var groupType = currentValues
            .Where(v => IsGroupType(v.Attribute))
            .Select(v => NumberOf(v.IntValue, v.LongValue, v.StringValue))
            .FirstOrDefault();
        var groupTypeChanges = changes.Where(c => IsGroupType(c.Attribute)).ToList();
        if (groupTypeChanges.Count > 0)
        {
            var written = groupTypeChanges.FirstOrDefault(c => !IsRemoval(c));
            groupType = written == null ? null : NumberOf(written.IntValue, written.LongValue, written.StringValue);
        }

        var declaringRule = exchangeChanges.FirstOrDefault(c => c.Attribute.Name == ExchangeRecipientAttributes.RecipientType && c.SyncRuleId.HasValue)?.SyncRuleId
                            ?? exchangeChanges.FirstOrDefault(c => c.SyncRuleId.HasValue)?.SyncRuleId
                            ?? export.ProvisioningSyncRuleId;

        return new ExchangeDeclaredState
        {
            RecipientType = values.GetValueOrDefault(ExchangeRecipientAttributes.RecipientType),
            Alias = values.GetValueOrDefault(ExchangeRecipientAttributes.Alias),
            Database = values.GetValueOrDefault(ExchangeRecipientAttributes.Database),
            PrimarySmtpAddress = values.GetValueOrDefault(ExchangeRecipientAttributes.PrimarySmtpAddress),
            ExternalAddress = values.GetValueOrDefault(ExchangeRecipientAttributes.ExternalAddress),
            ImportedRecipientType = imported,
            GroupType = groupType,
            DeclaringSyncRuleId = declaringRule
        };
    }

    private static void LayOver(Dictionary<string, string?> values, IReadOnlyCollection<PendingExportAttributeValueChange> exchangeChanges)
    {
        foreach (var change in exchangeChanges.Where(IsRemoval))
            values[change.Attribute.Name] = null;
        foreach (var change in exchangeChanges.Where(c => !IsRemoval(c)))
            values[change.Attribute.Name] = change.StringValue;
    }

    /// <summary>
    /// An object's declared state as it stands now, for the delivery lane (Phase 5): the values the last import read
    /// for the five Exchange attributes (<paramref name="importedValues"/>), with the changes its Pending Export still
    /// holds for Exchange (<paramref name="heldChanges"/>) laid over, removals first, as <see cref="From"/> lays an
    /// export's changes over. Only the five values are set; the rules' inputs are staging's business.
    /// </summary>
    public static ExchangeDeclaredState Of(IReadOnlyDictionary<string, string?> importedValues, IEnumerable<ExchangeHeldChange> heldChanges)
    {
        ArgumentNullException.ThrowIfNull(importedValues);
        ArgumentNullException.ThrowIfNull(heldChanges);

        var values = new Dictionary<string, string?>(importedValues, StringComparer.Ordinal);
        var held = heldChanges.Where(c => ExchangeRecipientAttributes.All.Contains(c.AttributeName)).ToList();
        foreach (var change in held.Where(c => IsRemovalType(c.ChangeType)))
            values[change.AttributeName] = null;
        foreach (var change in held.Where(c => !IsRemovalType(c.ChangeType)))
            values[change.AttributeName] = change.StringValue;

        return new ExchangeDeclaredState
        {
            RecipientType = values.GetValueOrDefault(ExchangeRecipientAttributes.RecipientType),
            Alias = values.GetValueOrDefault(ExchangeRecipientAttributes.Alias),
            Database = values.GetValueOrDefault(ExchangeRecipientAttributes.Database),
            PrimarySmtpAddress = values.GetValueOrDefault(ExchangeRecipientAttributes.PrimarySmtpAddress),
            ExternalAddress = values.GetValueOrDefault(ExchangeRecipientAttributes.ExternalAddress)
        };
    }

    /// <summary>
    /// The state a queued change declares, as the five values Exchange is asked to converge on.
    /// </summary>
    public static ExchangeDeclaredState Of(PendingExchangeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return new ExchangeDeclaredState
        {
            RecipientType = change.RecipientType,
            Alias = change.Alias,
            Database = change.Database,
            PrimarySmtpAddress = change.PrimarySmtpAddress,
            ExternalAddress = change.ExternalAddress
        };
    }

    private static bool IsRemovalType(PendingExportAttributeChangeType changeType) =>
        changeType is PendingExportAttributeChangeType.Remove or PendingExportAttributeChangeType.RemoveAll;

    /// <summary>
    /// Whether <paramref name="change"/> sets one of the five connector-defined Exchange attributes.
    /// </summary>
    public static bool IsExchangeAttributeChange(PendingExportAttributeValueChange change) =>
        change.Attribute is { IsConnectorDefined: true } attribute && ExchangeRecipientAttributes.All.Contains(attribute.Name);

    private static bool IsRemoval(PendingExportAttributeValueChange change) =>
        change.ChangeType is PendingExportAttributeChangeType.Remove or PendingExportAttributeChangeType.RemoveAll;

    private static bool IsGroupType(ConnectedSystemObjectTypeAttribute? attribute) =>
        attribute is { IsConnectorDefined: false } && string.Equals(attribute.Name, GroupTypeAttributeName, StringComparison.OrdinalIgnoreCase);

    private static long? NumberOf(int? intValue, long? longValue, string? stringValue) =>
        intValue ?? longValue ?? (long.TryParse(stringValue, out var parsed) ? parsed : null);
}
