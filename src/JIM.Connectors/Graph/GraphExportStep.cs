// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Graph;

/// <summary>
/// One Graph request a Pending Export needs, sent inside a <c>$batch</c>. A Pending Export's create, update or delete
/// is its first step; what can only follow it (a new user's manager, members beyond the first twenty, removals) comes in
/// later rounds.
/// </summary>
/// <param name="References">For <see cref="GraphExportStepKind.AddReferences"/> and <see cref="GraphExportStepKind.RemoveReference"/>, the ids concerned.</param>
/// <param name="Attempt">How many times the step has been sent again because Graph could not yet find an object it refers to.</param>
internal sealed record GraphExportStep(
    GraphExportState State,
    GraphExportStepKind Kind,
    GraphBatchRequest Request,
    string? Relationship = null,
    IReadOnlyList<string>? References = null,
    int Attempt = 0);
