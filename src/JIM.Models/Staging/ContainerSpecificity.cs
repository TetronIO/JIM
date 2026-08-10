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
        Func<string?, ConnectedSystemContainer, bool> isWithinContainer)
    {
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(isWithinContainer);

        if (containers.Count == 0 || string.IsNullOrEmpty(objectIdentifier))
            return null;

        var matches = containers.Where(container => isWithinContainer(objectIdentifier, container)).ToList();
        if (matches.Count <= 1)
            return matches.FirstOrDefault();

        // The most specific match is the one holding no other match: anything holding another match is an ancestor
        // of it, and an ancestor is the more general statement of the two.
        return matches.FirstOrDefault(candidate => !matches.Any(other =>
                   !ReferenceEquals(other, candidate) && isWithinContainer(other.ExternalId, candidate)))
               ?? matches[0];
    }
}
