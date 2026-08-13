// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// How many entries one excluded Container caused an import to read from the Connected System and discard
/// (#1255).
/// </summary>
/// <param name="ContainerId">
/// The excluded Container. The Container's id rather than its name or external id, because this travels to the
/// Activity's stat counters and has to survive both the Container being renamed and a Distinguished Name longer
/// than the counter key can hold; the name is resolved for display at read time.
/// </param>
/// <param name="EntriesDiscarded">
/// Entries read and thrown away because this Container carved them out. Per import call, so a paged import
/// reports the same Container once per page and the counts accumulate.
/// </param>
public sealed record ExclusionDiscardCount(int ContainerId, int EntriesDiscarded);
