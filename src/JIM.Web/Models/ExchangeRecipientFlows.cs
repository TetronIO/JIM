// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Web.Models;

/// <summary>
/// What the Exchange recipients panel on an export Synchronisation Rule's Attribute Flow tab shows (#2021, PRD requirement
/// 4): one row per Exchange attribute the rule's Object Type carries, each with the Attribute Flow that sets it, or what
/// happens without one.
/// </summary>
/// <remarks>
/// The panel stores nothing of its own: Attribute Flows stay the only configuration, and it finds them by their target
/// alone, never by reading an expression. Schema discovery adds the Exchange attributes only while the Connected System
/// manages Exchange recipients, so their presence on the Object Type is what decides that the panel shows.
/// </remarks>
public sealed class ExchangeRecipientFlows
{
    // What happens to each attribute when no Attribute Flow sets it, as the Exchange step reads the declared state.
    private static readonly Dictionary<string, string> WithoutFlow = new(StringComparer.Ordinal)
    {
        [ExchangeRecipientAttributes.RecipientType] = "Not set, so objects are not recipients",
        [ExchangeRecipientAttributes.Alias] = "Not set, so mailNickname is used",
        [ExchangeRecipientAttributes.Database] = "Not set, so Exchange chooses",
        [ExchangeRecipientAttributes.PrimarySmtpAddress] = "Not set, so Email Address Policies apply",
        [ExchangeRecipientAttributes.ExternalAddress] = "Not set; mail users and mail contacts need one"
    };

    private ExchangeRecipientFlows(IReadOnlyList<ExchangeRecipientFlowRow> rows)
    {
        Rows = rows;
    }

    /// <summary>One row per Exchange attribute the rule's Object Type carries, in the order the Connector defines them.</summary>
    public IReadOnlyList<ExchangeRecipientFlowRow> Rows { get; }

    /// <summary>Whether any Attribute Flow sets an Exchange attribute, enabled or not.</summary>
    public bool IsSetUp => Rows.Any(r => r.Mapping != null);

    /// <summary>The rule's Attribute Flows to Exchange attributes: what stopping recipient provisioning removes.</summary>
    public IReadOnlyList<SyncRuleMapping> Flows => Rows.Where(r => r.Mapping != null).Select(r => r.Mapping!).ToList();

    /// <summary>
    /// The panel for <paramref name="rule"/>, or null where it does not belong: an import rule, or an Object Type with no Exchange attributes (its Connected System does not manage Exchange recipients).
    /// </summary>
    public static ExchangeRecipientFlows? For(SyncRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Direction != SyncRuleDirection.Export || rule.ConnectedSystemObjectType is not { } objectType)
            return null;

        // Connector-defined only: a directory attribute can never carry one of these names, which contain spaces, but the
        // flag is what says the Connector added it.
        var rows = ExchangeRecipientAttributes.All
            .Select(name => objectType.Attributes.FirstOrDefault(a => a.IsConnectorDefined && string.Equals(a.Name, name, StringComparison.Ordinal)))
            .Where(attribute => attribute != null)
            .Select(attribute => new ExchangeRecipientFlowRow(attribute!, FlowTo(rule, attribute!), WithoutFlow[attribute!.Name]))
            .ToList();

        return rows.Count == 0 ? null : new ExchangeRecipientFlows(rows);
    }

    // An export rule allows one Attribute Flow per target attribute, so the first is the only one. A flow added in this
    // edit session may carry the id without the navigation, or the navigation without the id.
    private static SyncRuleMapping? FlowTo(SyncRule rule, ConnectedSystemObjectTypeAttribute attribute) =>
        rule.AttributeFlowRules.FirstOrDefault(m =>
            ReferenceEquals(m.TargetConnectedSystemAttribute, attribute) ||
            (m.TargetConnectedSystemAttribute?.Id ?? m.TargetConnectedSystemAttributeId) == attribute.Id);
}

/// <summary>
/// One Exchange attribute on the panel: the Attribute Flow that sets it, if any, and what happens when none does.
/// </summary>
public sealed record ExchangeRecipientFlowRow(ConnectedSystemObjectTypeAttribute Attribute, SyncRuleMapping? Mapping, string WithoutFlow);
