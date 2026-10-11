// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.UniqueValues;

/// <summary>
/// Why a generated value waits (Generated Value Forms, #2023): a value built from it needs another input the object has
/// no value for yet, so the value built from a candidate cannot be worked out, and the candidate cannot be checked. The
/// generated value waits, as it already waits for its own inputs, until the input arrives.
/// </summary>
/// <param name="InputName">The input with no value: a Metaverse attribute (UPN Suffix), or a Connected System
/// attribute the building flow reads.</param>
/// <param name="NeededBy">The value built from the generated value that needs it (User Principal Name).</param>
public sealed record GeneratedValueFormWait(string InputName, string NeededBy);
