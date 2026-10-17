// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Filtering;

/// <summary>
/// A filter or PATCH path that does not conform to the SCIM grammar. The message says where it went wrong and never
/// repeats a comparison value, so it is safe to log and to return to the client as an <c>invalidFilter</c> or
/// <c>invalidPath</c> error.
/// </summary>
public class ScimFilterException : Exception
{
    public ScimFilterException(string message) : base(message)
    {
    }
}
