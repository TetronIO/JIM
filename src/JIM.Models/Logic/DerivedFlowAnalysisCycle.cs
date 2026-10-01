// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// The dependency loop a proposed Attribute Flow would close (#1750), link by link, so the portal can list it rather
/// than print it as one sentence. Read from the analysed flow round to the flow that reads it.
/// </summary>
/// <param name="Message">The error the save would refuse with for this loop, exactly as it appears in
/// <see cref="DerivedFlowAnalysis.Errors"/>, so a surface listing the links can leave that one error out of the rest.</param>
/// <param name="Links">The flows on the loop, starting with the analysed one; each reads the attribute the next one
/// writes, and the last reads the attribute the first writes.</param>
public sealed record DerivedFlowAnalysisCycle(string Message, IReadOnlyList<DerivedFlowAnalysisCycleLink> Links);
