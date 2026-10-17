// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim.Messages;

namespace JIM.Scim.Schema;

/// <summary>
/// The outcome of turning JIM attribute changes into SCIM PATCH operations.
/// </summary>
public class ScimPatchBuildResult
{
    public List<ScimPatchOperation> Operations { get; init; } = [];

    /// <summary>
    /// The names of attributes the provider's schema does not have, or that it will not accept a write
    /// to. Reported rather than dropped: an export that silently omits a change is one JIM records as
    /// applied and the provider never received.
    /// </summary>
    public List<string> UnknownAttributes { get; init; } = [];

    /// <summary>
    /// The names of write-only attributes the change would have written, such as <c>password</c> (#2091). Kept apart
    /// from unknown attributes because the remedy differs: a write-only attribute is never exported through an
    /// Attribute Flow, whatever the schema says, so re-importing the schema would not help.
    /// </summary>
    public List<string> WriteOnlyAttributes { get; init; } = [];
}
