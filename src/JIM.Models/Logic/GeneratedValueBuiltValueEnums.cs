// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// How a value built from a generated value relates to it (Generated Value Forms, #2023), which decides what JIM does
/// for it. Values are persisted nowhere, but the ordinals are kept stable for the REST API.
/// </summary>
public enum GeneratedValueBuiltValueKind
{
    /// <summary>
    /// Built from the generated value alone, and every derived step on the way is on the generated flow's own
    /// Synchronisation Rule, so JIM can work it out while choosing the generated value: checked before the generated value
    /// is issued, and corrected with it after a refusal.
    /// </summary>
    Form = 0,

    /// <summary>
    /// Built from the generated value alone, but through a derived step on another Synchronisation Rule, which runs only in
    /// that rule's own Connected System's synchronisation: corrected with the generated value after a refusal, but not
    /// checked before it is issued.
    /// </summary>
    BuiltOnAnotherRule = 1,

    /// <summary>
    /// Built from more than one generated value. It has no single value to move on to the next candidate, so it is
    /// neither checked nor corrected; a refusal of it is an ordinary export error.
    /// </summary>
    BuiltFromSeveralGeneratedValues = 2
}
