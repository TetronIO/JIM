// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// Reports that saving a <see cref="SyncRuleMappingGeneration"/> whose <see cref="SyncRuleMappingGeneration.SequenceStart"/>
/// stood above the target attribute's <see cref="GeneratedValueSequence"/> counter moved that counter forward
/// (Unique Value Generation, #242, plan decision 3: "the save moves the counter forward and the response
/// reports it"). A lower or equal start value has no effect and produces no instance of this type.
/// </summary>
/// <param name="From">The counter's next number before the save.</param>
/// <param name="To">The counter's next number after the save (the mapping's configured <see cref="SyncRuleMappingGeneration.SequenceStart"/>).</param>
public record SequenceSkippedAhead(long From, long To);
