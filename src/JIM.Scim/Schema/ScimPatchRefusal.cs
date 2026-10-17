// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim.Messages;

namespace JIM.Scim.Schema;

/// <summary>
/// Stops a PATCH request at the operation that cannot be applied. <see cref="ScimPatchApplier"/> turns it into the
/// request's error, so a refusal never escapes the applier. The detail names attributes, never values.
/// </summary>
internal sealed class ScimPatchRefusal : Exception
{
    public ScimPatchRefusal(string scimType, string detail) : base(detail)
    {
        ScimType = scimType;
    }

    public string ScimType { get; }

    public ScimError ToError() => ScimError.ForStatus(400, Message, ScimType);
}
