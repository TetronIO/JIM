// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// A Run Profile execution's progress as it stood at one moment, as written to the database: the message, the counters
/// and the steps that changed. Written as one unit, so the three never disagree with one another (#2521).
/// </summary>
/// <param name="ActivityId">The Activity the progress belongs to.</param>
/// <param name="Message">The Activity's message.</param>
/// <param name="ObjectsProcessed">The Activity's count of objects processed.</param>
/// <param name="ObjectsToProcess">The Activity's count of objects to process.</param>
/// <param name="Phases">
/// The steps whose state changed since progress was last written, each a copy taken at that moment, so nothing the run does
/// afterwards can change what is written.
/// </param>
public sealed record ActivityProgressWrite(
    Guid ActivityId,
    string? Message,
    int ObjectsProcessed,
    int ObjectsToProcess,
    IReadOnlyList<ActivityPhase> Phases);
