// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Resources;

namespace JIM.Web;

/// <summary>
/// The wording <c>TermHint</c> shows for each <see cref="Term"/>: the term's display name, a one or two
/// sentence definition, and the anchor of its entry on the docs glossary page (#1670). This is the single
/// source of that wording so a portal hint and the docs glossary (docs/reference/glossary.md) cannot drift
/// apart; every definition's text is copied verbatim from the glossary entry's opening sentence(s).
/// <see cref="Definitions"/> is asserted against the live glossary file by GlossaryTermConsistencyTests
/// (test/JIM.Web.Tests/), which fails the build if a definition's text stops appearing on the page, or if
/// its anchor stops existing there.
/// </summary>
public static class TermDefinitions
{
    /// <summary>
    /// One term's portal-facing wording: what <c>TermHint</c> renders, and where it links.
    /// </summary>
    /// <param name="DisplayName">The term's name, exactly as the glossary heads its entry (minus any abbreviation).</param>
    /// <param name="Text">The definition, copied verbatim from the glossary entry's opening sentence(s).</param>
    /// <param name="GlossaryAnchor">The glossary entry's anchor, without the leading <c>#</c>.</param>
    public sealed record Entry(string DisplayName, string Text, string GlossaryAnchor);

    /// <summary>
    /// The wording for every <see cref="Term"/> <c>TermHint</c> can render.
    /// </summary>
    public static IReadOnlyDictionary<Term, Entry> Definitions => new Dictionary<Term, Entry>
    {
        [Term.Metaverse] = new Entry(
            DisplayText.TermMetaverseName,
            DisplayText.TermMetaverseText,
            "metaverse"),

        [Term.ConnectorSpace] = new Entry(
            DisplayText.TermConnectorSpaceName,
            DisplayText.TermConnectorSpaceText,
            "connector-space"),

        [Term.Projection] = new Entry(
            DisplayText.TermProjectionName,
            DisplayText.TermProjectionText,
            "projection"),

        [Term.Join] = new Entry(
            DisplayText.TermJoinName,
            DisplayText.TermJoinText,
            "join"),

        [Term.AttributeFlow] = new Entry(
            DisplayText.TermAttributeFlowName,
            DisplayText.TermAttributeFlowText,
            "attribute-flow"),

        [Term.PendingExport] = new Entry(
            DisplayText.TermPendingExportName,
            DisplayText.TermPendingExportText,
            "pending-export"),

        [Term.ObjectMatchingRule] = new Entry(
            DisplayText.TermObjectMatchingRuleName,
            DisplayText.TermObjectMatchingRuleText,
            "object-matching-rule"),

        [Term.Scoping] = new Entry(
            DisplayText.TermScopingName,
            DisplayText.TermScopingText,
            "scoping"),

        [Term.ContainerScope] = new Entry(
            DisplayText.TermContainerScopeName,
            DisplayText.TermContainerScopeText,
            "container-scope"),

        [Term.AttributePriority] = new Entry(
            DisplayText.TermAttributePriorityName,
            DisplayText.TermAttributePriorityText,
            "attribute-priority")
    };

    /// <summary>
    /// The wording for <paramref name="term"/>. Every <see cref="Term"/> value has an entry; there is nothing
    /// to fall back to if one is ever missing, so this throws rather than returning a placeholder that would
    /// silently ship an unexplained hint.
    /// </summary>
    public static Entry For(Term term) => Definitions[term];
}
