// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Service;

/// <summary>
/// One reference a resource holds, as the store gives it back: the resource referred to and its type's name.
/// </summary>
public sealed record ScimHeldReference(Guid TargetId, string TargetTypeName);
