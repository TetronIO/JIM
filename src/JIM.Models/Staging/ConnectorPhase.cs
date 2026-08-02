// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// One phase a Connector performs inside the JIM phase that hosts it, declared up-front by
/// <see cref="JIM.Models.Interfaces.IConnectorPhases"/> so an administrator can see the Connector's
/// internal journey (and what of it is still to come) rather than a single frozen message.
/// </summary>
/// <param name="Key">
/// The Connector's own stable identifier for the phase, for example "load-existing-file". Unique
/// within the Connector; JIM qualifies it before storing it, so it cannot collide with a JIM phase.
/// </param>
/// <param name="Name">
/// The administrator-facing step label, in the administrator's language rather than the Connector's
/// internals: "Loading existing export file", not "LoadExistingFileContent".
/// </param>
public record ConnectorPhase(string Key, string Name);
