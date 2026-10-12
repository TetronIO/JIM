// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// The object a causal timeline (#2100) is read for. One subject covers the object live and deleted alike: a live
/// object has no deletion record, and a deleted one no rows under its own id, so the read takes every source at once
/// and whichever applies answers.
/// </summary>
/// <param name="Kind">Whether the id names a Connected System Object or a Metaverse Object.</param>
/// <param name="Id">The object's id, which a deleted object's records keep after the object itself is gone.</param>
public sealed record CausalTimelineSubject(CausalTimelineSubjectKind Kind, Guid Id);
