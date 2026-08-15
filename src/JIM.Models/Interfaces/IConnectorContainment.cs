// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Interfaces;

/// <summary>
/// Implemented by a Connector that can say whether one of its objects falls within the scope of one of its
/// containers.
/// </summary>
/// <remarks>
/// Container selection is JIM's statement of what it manages, and answering "which objects would this deselection
/// take out of scope?" needs the containment rule the Connector itself uses. That rule is the Connector's knowledge:
/// it is a Distinguished Name suffix for a directory, a path prefix elsewhere, and JIM has no business guessing
/// which.
///
/// A Connector that reports Containers through <see cref="IConnectorPartitions"/> should implement this too. One that does not is not
/// broken; it simply cannot be asked, and a preview says so plainly rather than reporting a zero that reads as
/// "this change would affect nothing".
///
/// The predicate must agree exactly with the search scope the Connector builds on import and with the scope it
/// enforces on export via <see cref="IConnectorManagedScope"/>. Two answers to the same question would let a preview
/// state a count the Connector then contradicts, and would let an export write where the next import cannot read.
/// </remarks>
public interface IConnectorContainment
{
    /// <summary>
    /// Whether <paramref name="objectIdentifier"/> falls within <paramref name="container"/>'s scope: what a search
    /// based on that container would return.
    /// </summary>
    /// <param name="objectIdentifier">
    /// The object's identifier in the Connected System's own terms; the Distinguished Name for a directory.
    /// </param>
    /// <param name="container">
    /// The container, including its <see cref="ConnectedSystemContainer.Scope"/>. The whole container is passed
    /// rather than its identifier alone because scope is part of the question: a
    /// <see cref="ConnectedSystemContainerScope.Subtree"/> container covers everything beneath it, while a
    /// <see cref="ConnectedSystemContainerScope.OneLevel"/> one covers only what sits directly within it and does
    /// not cover its own entry. An implementation that ignored scope would report objects as managed that no import
    /// will ever return.
    /// </param>
    /// <returns>
    /// <c>true</c> when the object is within the container's scope. Implementations must answer <c>false</c> for an
    /// empty or unparseable identifier rather than throwing: the answer decides whether an export is refused and
    /// whether a preview counts an object, and both are better served by "not in scope" than by a failed run.
    /// </returns>
    bool IsWithinContainer(string? objectIdentifier, ConnectedSystemContainer container);
}
