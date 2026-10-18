// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scim;
using JIM.Scim.Messages;

namespace JIM.Scim.Service;

/// <summary>
/// A list request's filter in the store's terms (<see cref="ScimStoreFilterTranslator"/>), or why it is refused, never
/// both.
/// </summary>
public sealed class ScimFilterTranslation
{
    /// <summary>
    /// The resources the filter selects; null where it was refused.
    /// </summary>
    public ScimStoreFilter? Filter { get; init; }

    /// <summary>
    /// Why the filter is refused (<c>400</c> <c>invalidFilter</c>), naming attributes and operators, never a literal.
    /// </summary>
    public ScimError? Error { get; init; }
}
