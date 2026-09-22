// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
    public static readonly IReadOnlyDictionary<Term, Entry> Definitions = new Dictionary<Term, Entry>
    {
        [Term.Metaverse] = new Entry(
            "Metaverse",
            "The central, authoritative repository within JIM. The Metaverse holds one Metaverse Object for each real-world thing JIM manages, aggregated from every Connected System via Synchronisation Rules.",
            "metaverse"),

        [Term.ConnectorSpace] = new Entry(
            "Connector Space",
            "The staging area where Connected System Objects reside before and after synchronisation. The Connector Space acts as a buffer between external systems and the Metaverse, ensuring that changes are validated before they are applied.",
            "connector-space"),

        [Term.Projection] = new Entry(
            "Projection",
            "Creating a new Metaverse Object when no existing one matches an incoming Connected System Object. Projection is enabled per Synchronisation Rule.",
            "projection"),

        [Term.Join] = new Entry(
            "Join",
            "Linking a Connected System Object to an existing Metaverse Object. A Join happens when an Object Matching Rule finds that an incoming Connected System Object corresponds to one that already exists.",
            "join"),

        [Term.AttributeFlow] = new Entry(
            "Attribute Flow",
            "A rule that maps an attribute between a Connected System Object and a Metaverse Object. Attribute Flows define how data moves during synchronisation, including any transformations applied via expressions.",
            "attribute-flow"),

        [Term.PendingExport] = new Entry(
            "Pending Export",
            "A queued change waiting to be sent to a target system. Pending Exports are created during synchronisation and held until an export Run Profile is executed, at which point they are applied to the Connected System.",
            "pending-export"),

        [Term.ObjectMatchingRule] = new Entry(
            "Object Matching Rule",
            "A rule that decides whether an incoming Connected System Object corresponds to an existing Metaverse Object. Import matching joins a Connected System Object to a Metaverse Object; export matching joins a Metaverse Object being provisioned to an account that already exists in the target system, rather than creating a duplicate.",
            "object-matching-rule"),

        [Term.Scoping] = new Entry(
            "Scoping",
            "The Scoping Criteria that decide which objects a Synchronisation Rule applies to. An object outside the criteria is left alone by that rule, whatever its Attribute Flows and Object Matching Rules say.",
            "scoping"),

        [Term.ContainerScope] = new Entry(
            "Container Scope",
            "How far beneath a selected Container objects are imported from: the whole subtree beneath it (the default), or only the objects held directly within it.",
            "container-scope"),

        [Term.AttributePriority] = new Entry(
            "Attribute Priority",
            "The deterministic precedence that decides which Connected System's value wins when several contribute the same Metaverse Object attribute. Each contributing inbound Synchronisation Rule holds a priority for the attribute, and the highest-priority contributor that has a value sets it, so the result never depends on the order synchronisations happen to run in.",
            "attribute-priority")
    };

    /// <summary>
    /// The wording for <paramref name="term"/>. Every <see cref="Term"/> value has an entry; there is nothing
    /// to fall back to if one is ever missing, so this throws rather than returning a placeholder that would
    /// silently ship an unexplained hint.
    /// </summary>
    public static Entry For(Term term) => Definitions[term];
}
