// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// #2705: one External ID a Delta Import asks JIM about, in the form the import's lookup keys carry it: the anchor
/// attribute the value is held under, whether that attribute is case-exact, and the value's comparable form (text folded
/// unless the attribute is case-exact; numbers, decimals and GUIDs in their canonical text). The key it stands for is
/// <c>CsoLookupKey.For(connectedSystemId, AttributeId, ValueKey)</c>, the same key the whole-system lookup builds for a
/// stored value, so the two lookups answer one question.
/// </summary>
/// <param name="AttributeId">The anchor attribute: an Object Type's External ID attribute.</param>
/// <param name="CaseExact">Whether that attribute is declared case-exact, which decides how its text values compare.</param>
/// <param name="ValueKey">The value in its comparable form (<c>CsoLookupKey.ValueKey</c>).</param>
public readonly record struct CsoExternalIdProbe(int AttributeId, bool CaseExact, string ValueKey);
