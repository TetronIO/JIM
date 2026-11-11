// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// The order the export's deferred pass works through its exports in (#2905): stages run one after another, each a
/// sequence of chunks loaded one at a time, with the exports of a stage largest first. An export waiting on another
/// export of the pass is in a later stage than the export it waits on, so that export has been written before the
/// waiting export's chunk is resolved.
/// </summary>
public sealed class DeferredExportPlan
{
    /// <summary>
    /// The stages, in the order they run; each holds its chunks of Pending Export ids, in the order they are loaded.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Guid[]>> Stages { get; init; } = [];

    /// <summary>
    /// Per Pending Export of the pass, the weight it carries into a batch: its attribute value change count, and at
    /// least one.
    /// </summary>
    public IReadOnlyDictionary<Guid, int> Weights { get; init; } = new Dictionary<Guid, int>();

    /// <summary>
    /// The most weight a batch takes on before it is closed, unless it holds a single export heavier than this: the batch
    /// size's worth of the pass's average export, and no more than the heaviest batch queue order would have made.
    /// </summary>
    public long BatchWeightBudget { get; init; } = long.MaxValue;

    /// <summary>
    /// How many exports of the pass wait on another export of the pass written before them.
    /// </summary>
    public int DependentCount { get; init; }

    /// <summary>
    /// Per chunk, in the order of <see cref="Chunks"/>, whether it may be prepared ahead (#2605): see
    /// <see cref="CanPrepareAhead"/>.
    /// </summary>
    public IReadOnlyList<bool> PreparableAhead { get; init; } = [];

    /// <summary>
    /// Whether the chunk at <paramref name="chunkIndex"/> (in the order of <see cref="Chunks"/>) may be prepared while the
    /// batches of the chunks before it in its stage are still being written (#2605). A stage's first chunk never is: it
    /// waits on the stages before it. Nor is a chunk holding a reference to an object whose anchor an export in an earlier
    /// chunk of its stage supplies: the pass has always resolved it against what that export had written by the time the
    /// chunk was loaded, so it is still loaded then. Any other chunk resolves the same whenever it is prepared.
    /// </summary>
    public bool CanPrepareAhead(int chunkIndex) => chunkIndex >= 0 && chunkIndex < PreparableAhead.Count && PreparableAhead[chunkIndex];

    /// <summary>
    /// Every chunk of every stage, in the order the pass loads them.
    /// </summary>
    public IEnumerable<Guid[]> Chunks => Stages.SelectMany(stage => stage);

    /// <summary>
    /// The weight <paramref name="pendingExportId"/> carries into a batch; one for an export the plan does not know.
    /// </summary>
    public int WeightOf(Guid pendingExportId) => Weights.TryGetValue(pendingExportId, out var weight) ? weight : 1;
}
