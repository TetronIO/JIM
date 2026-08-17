// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Which of several Containers has the final say over an object.
/// </summary>
/// <remarks>
/// Membership across a set of Containers was an OR: any Container admitting the object put it in scope. That answers
/// "is this object in scope?" and nothing else, and it is why the model cannot currently express "manage this branch
/// except that part of it" (#1255): the including ancestor admits every object beneath it, so it wins an OR
/// unconditionally and no Container deeper down can overrule it.
///
/// Ranking replaces the OR. The Container that decides is the most specific one admitting the object, and
/// specificity is asked in the Connector's own terms, by asking whether one Container's identifier falls within
/// another's: a Container that holds another matching Container is, by definition, the less specific of the two.
/// Nothing here knows what containment means, which is <see cref="Interfaces.IConnectorContainment"/>'s whole point,
/// and no Container hierarchy has to be loaded or navigated for the question to be answerable, which is what lets
/// the Connector paths use this over a flat collection.
/// </remarks>
public static class ContainerSpecificity
{
    /// <summary>
    /// The Container that decides whether <paramref name="objectIdentifier"/> is in scope, or <c>null</c> where no
    /// Container admits it at all.
    /// </summary>
    /// <param name="objectIdentifier">The object's identifier in the Connected System's own terms.</param>
    /// <param name="containers">The Containers to choose between, in any order.</param>
    /// <param name="isWithinContainer">
    /// The containment rule, which must be the Connector's own: <see cref="Interfaces.IConnectorContainment"/> for
    /// callers holding a Connector, and the Connector's internal predicate for callers inside one.
    /// </param>
    /// <remarks>
    /// Every Container is tested, where the OR this replaced stopped at the first match. That is the cost of being
    /// able to rank at all, and it is bounded by the number of selected Containers rather than by directory size.
    /// The ranking pass beyond that runs only where more than one Container matched, which for a hierarchy means
    /// nested selections and for one object is typically none.
    ///
    /// Two Containers that admit the same object while neither contains the other cannot arise from a hierarchy,
    /// where every Container admitting an object lies on one path down to it. A Connector whose containment is not a
    /// tree could produce it, and the first such Container in <paramref name="containers"/> is returned. Any caller
    /// that comes to attach *opposing* meanings to Containers (an exclusion, #1255) must resolve that tie
    /// deliberately rather than inherit this one, because "whichever we saw first" is not a defensible answer to
    /// "is this object managed?".
    /// </remarks>
    public static ConnectedSystemContainer? ResolveMostSpecific(
        string? objectIdentifier,
        IReadOnlyCollection<ConnectedSystemContainer> containers,
        Func<string?, ConnectedSystemContainer, bool> isWithinContainer) =>
        ResolveMostSpecificMatches(objectIdentifier, containers, isWithinContainer).FirstOrDefault();

    /// <summary>
    /// Whether <paramref name="objectIdentifier"/> is in the scope these Containers describe: the Container with the
    /// final say over it admits it rather than carving it out.
    /// </summary>
    /// <param name="objectIdentifier">The object's identifier in the Connected System's own terms.</param>
    /// <param name="containers">
    /// Every Container making a statement about scope, selections and exclusions alike. Supplying only the
    /// selections answers a different and wrong question: an exclusion the caller left out cannot carve anything
    /// out, so the object comes back in scope.
    /// </param>
    /// <param name="isWithinContainer">The containment rule, which must be the Connector's own.</param>
    /// <param name="isExcluded">
    /// Whether a Container carves its scope out rather than admitting it. Defaults to the Container's own
    /// <see cref="ConnectedSystemContainer.Excluded"/> flag, which is what a caller working from the stored
    /// configuration wants. A caller evaluating a *proposed* selection must supply its own, because the stored
    /// flags are precisely what the proposal is asking to change; taking them at face value would answer the
    /// question the administrator is trying to move away from.
    /// </param>
    /// <returns>
    /// <c>false</c> where no Container admits the object at all. An empty collection is therefore out of scope
    /// rather than unconstrained; a caller holding no Container-level opinion has no scope to narrow and does not
    /// ask this question.
    /// </returns>
    /// <remarks>
    /// Where two Containers admit the object and neither holds the other, ranking has nothing to separate them, and
    /// <see cref="ResolveMostSpecific"/> is explicit that a caller attaching opposing meanings must resolve that tie
    /// deliberately. This one resolves it to <b>excluded wins</b>: importing an object an administrator excluded is
    /// the worse of the two failures, and it is the direction every other synchronisation-integrity decision in JIM
    /// already leans. Such a tie cannot arise from a hierarchy, where every Container admitting an object lies on
    /// one path down to it.
    /// </remarks>
    public static bool IsInScope(
        string? objectIdentifier,
        IReadOnlyCollection<ConnectedSystemContainer> containers,
        Func<string?, ConnectedSystemContainer, bool> isWithinContainer,
        Func<ConnectedSystemContainer, bool>? isExcluded = null)
    {
        var carvesOut = isExcluded ?? (static container => container.Excluded);
        var deciding = ResolveMostSpecificMatches(objectIdentifier, containers, isWithinContainer);

        return deciding.Count > 0 && !deciding.Any(carvesOut);
    }

    /// <summary>
    /// The Containers with the final say over an object: those admitting it that no other admitting Container is
    /// more specific than. One of them in a hierarchy; more only where containment is not one.
    /// </summary>
    private static List<ConnectedSystemContainer> ResolveMostSpecificMatches(
        string? objectIdentifier,
        IReadOnlyCollection<ConnectedSystemContainer> containers,
        Func<string?, ConnectedSystemContainer, bool> isWithinContainer)
    {
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(isWithinContainer);

        if (containers.Count == 0 || string.IsNullOrEmpty(objectIdentifier))
            return [];

        var matches = containers.Where(container => isWithinContainer(objectIdentifier, container)).ToList();
        if (matches.Count <= 1)
            return matches;

        // The most specific match is the one holding no other match: anything holding another match is an ancestor
        // of it, and an ancestor is the more general statement of the two.
        var mostSpecific = matches.Where(candidate => !matches.Any(other =>
            !ReferenceEquals(other, candidate) && isWithinContainer(other.ExternalId, candidate))).ToList();

        // Containment that admits every match into every other leaves nothing to rank, so every match still has a
        // say. Ranking cannot narrow the field, and silently picking one would hide that.
        return mostSpecific.Count > 0 ? mostSpecific : matches;
    }
}
