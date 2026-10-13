// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.Scoping;

/// <summary>
/// One sample input to the Scope tester (#2055): the values an object holds for one attribute its Synchronisation
/// Rule's scoping criteria compare. The attribute is on the side the rule scopes, the Connected System Object for an
/// import rule and the Metaverse Object for an export one. No values means the object holds none.
/// </summary>
/// <param name="AttributeName">The attribute's name, as the scoped object type's schema names it.</param>
/// <param name="Values">The values, typed or as text to be read as the attribute's type.</param>
public sealed record ScopeTestInput(string AttributeName, IReadOnlyList<object> Values);
