// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Interfaces;

/// <summary>
/// Implemented by a Connector that can say whether one of its objects sits inside one of its containers.
/// </summary>
/// <remarks>
/// Container selection is JIM's statement of what it manages, and answering "which objects would a deselection take
/// out of scope?" needs the containment rule the Connector itself uses. That rule is the Connector's knowledge: it
/// is a Distinguished Name suffix for a directory, a path prefix elsewhere, and JIM has no business guessing which.
///
/// A Connector implementing <see cref="IConnectorContainers"/> should implement this too. One that does not is not
/// broken; it simply cannot be asked, and a preview says so plainly rather than reporting a zero that reads as
/// "this change would affect nothing".
///
/// The predicate must agree exactly with the scope the Connector enforces on export via
/// <see cref="IConnectorManagedScope"/>, and with the search scope it builds on import. Two answers to the same
/// question would let a preview state a count the Connector then contradicts.
/// </remarks>
public interface IConnectorContainment
{
    /// <summary>
    /// Whether <paramref name="objectExternalId"/> is at or beneath <paramref name="containerExternalId"/>, which
    /// is what selecting a container means: the container and its whole subtree.
    /// </summary>
    /// <param name="objectExternalId">
    /// The object's identifier in the Connected System's own terms; the Distinguished Name for a directory.
    /// </param>
    /// <param name="containerExternalId">The container's identifier, as carried on the Connected System Container.</param>
    /// <returns>
    /// <c>true</c> when the object is in the container's subtree. Implementations must answer <c>false</c> for an
    /// empty or unparseable identifier rather than throwing: the answer decides whether an export is refused and
    /// whether a preview counts an object, and both are better served by "not in scope" than by a failed run.
    /// </returns>
    bool IsWithinContainer(string? objectExternalId, string? containerExternalId);
}
