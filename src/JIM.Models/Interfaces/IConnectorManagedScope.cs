// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector to be told which containers the administrator manages, so that it can refuse to write
/// outside them.
/// </summary>
/// <remarks>
/// Container selection is the administrator's statement of what JIM manages, and it was only ever applied on the
/// way in. An Export Attribute Flow that moves an object into an unselected container (moving disabled accounts to
/// their own organisational unit, say) wrote it somewhere JIM cannot read back: the export was never confirmed by a
/// subsequent import, the next Full Import treated the object as deleted, and the following synchronisation
/// disconnected it and either orphaned the entry or deleted and re-provisioned it. JIM churned objects it had
/// exported itself.
///
/// Implement this where the Connected System has a containment model the Connector understands; whether a given
/// identifier sits within a container is the Connector's knowledge, not the framework's. JIM calls
/// <see cref="SetManagedScope"/> before an export when the Connected System has container selections, and does not
/// call it at all when there are none, so an unset scope must permit everything.
/// </remarks>
public interface IConnectorManagedScope
{
    /// <summary>
    /// Supplies the external identifiers of the containers the administrator has selected, whose subtrees the
    /// Connector may write to.
    /// </summary>
    /// <param name="selectedContainerExternalIds">
    /// The selected containers' external identifiers (Distinguished Names for a directory). Never null; an empty
    /// list means no scope has been stated and everything is permitted.
    /// </param>
    public void SetManagedScope(IReadOnlyList<string> selectedContainerExternalIds);
}
