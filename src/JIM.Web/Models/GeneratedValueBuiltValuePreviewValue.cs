// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// One value built from a generated value, as the generated flow's preview works it out for one candidate (Generated
/// Value Forms, #2023).
/// </summary>
/// <param name="Name">The value's attribute: a Metaverse attribute (Email), or the Connected System attribute an export
/// expression writes.</param>
/// <param name="Value">The value for the candidate; null when it has none, or could not be worked out.</param>
/// <param name="Error">Why it could not be worked out; null when it was.</param>
public sealed record GeneratedValueBuiltValuePreviewValue(string Name, string? Value, string? Error);
