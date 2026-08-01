// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// One declared phase of a Run Profile execution: a step an administrator sees in the Activity's
/// phase stepper (#454). Declarations are made up-front, before the run begins, which is what
/// allows the stepper to show what is still to come rather than only what has happened.
/// </summary>
/// <param name="Key">Stable identifier for the phase, used by the worker to enter it. Never shown to administrators.</param>
/// <param name="Name">The administrator-facing step label, for example "Saving changes".</param>
/// <param name="HostsConnectorPhases">
/// True for the one phase during which the Connector runs. A Connector's own declared phases nest
/// inside this step, so the top-level step count stays the same whichever Connector is in use.
/// </param>
public record RunProfilePhase(string Key, string Name, bool HostsConnectorPhases = false);
