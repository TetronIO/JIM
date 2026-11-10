// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// One deferred export waiting on another export of the same deferred pass (#2905): the referrer holds an unresolved
/// reference to the target's object, and that object's anchor is only known once the target itself has been written
/// (an anchor the target system generates), or is changed by the target (a rename).
/// </summary>
/// <param name="ReferrerId">The Pending Export holding the unresolved reference.</param>
/// <param name="TargetId">The Pending Export of the referenced object.</param>
public sealed record DeferredExportDependency(Guid ReferrerId, Guid TargetId);
