// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Whether a value built from a generated value is checked before the generated value is issued, and if not, why
/// (Generated Value Forms, #2023): the "Values built from ..." table's "Before issuing" column, and the switch's text.
/// </summary>
public enum GeneratedValueBuiltValueBeforeIssuing
{
    /// <summary>Checked with each candidate: a candidate whose value is already held is passed over.</summary>
    Checked = 0,

    /// <summary>Built on another Synchronisation Rule, which runs only once the person is in that rule's Connected System.</summary>
    BuiltOnAnotherRule = 1,

    /// <summary>The value's own Keep unique switch is off.</summary>
    KeepUniqueOff = 2,

    /// <summary>Built from more than one generated value, so it has no single value to move on with.</summary>
    BuiltFromSeveral = 3,

    /// <summary>Bound only for a Connected System the generated value's exclusions leave out.</summary>
    Excluded = 4
}
