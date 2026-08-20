// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

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
    /// Supplies the containers stating where the administrator manages, which decide where the Connector may
    /// write.
    /// </summary>
    /// <param name="scopeDecidingContainers">
    /// Every container making a statement about scope, carrying its identifier, its
    /// <see cref="ConnectedSystemContainer.Scope"/> and whether it is selected or
    /// <see cref="ConnectedSystemContainer.Excluded"/>. Never null; an empty list means no scope has been stated
    /// and everything is permitted.
    ///
    /// The whole container is supplied rather than its identifier alone because scope decides what "inside" means:
    /// a <see cref="ConnectedSystemContainerScope.OneLevel"/> container is not a licence to write anywhere beneath
    /// it, only directly within it, and an implementation that assumed a subtree would permit exactly the writes
    /// the next import cannot read back.
    ///
    /// Exclusions are part of this list for the same reason (#1255), and an implementation must honour them: the
    /// most specific container covering a target decides, so an object written into an excluded branch of an
    /// otherwise selected one is refused. Treating the list as selections alone would permit writes the next
    /// import discards, which is the very failure this interface exists to prevent.
    /// </param>
    public void SetManagedScope(IReadOnlyList<ConnectedSystemContainer> scopeDecidingContainers);
}
