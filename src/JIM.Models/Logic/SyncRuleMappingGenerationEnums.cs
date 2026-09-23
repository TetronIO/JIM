// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// The kind of uniqueness token a generated Attribute Flow appends to its base value (Unique Value Generation,
/// #242). The token is what turns a candidate base value into a value JIM can guarantee is free; which token is
/// chosen decides both how a collision is resolved and how <see cref="SyncRuleMappingGeneration"/>'s other
/// settings are interpreted.
/// </summary>
public enum GeneratedValueTokenKind
{
    /// <summary>
    /// The bare base value is tried first; only when it is already taken does JIM append a collision suffix
    /// (<see cref="GeneratedValueSuffixStyle"/>, starting from <see cref="SyncRuleMappingGeneration.SuffixStart"/>).
    /// Requires a base expression: with nothing to try first, there is nothing to suffix.
    /// </summary>
    OnlyIfTaken = 0,

    /// <summary>
    /// The token is the next number drawn from the target attribute's <see cref="GeneratedValueSequence"/>
    /// counter, always appended (never suffixing a collision), and never reused once issued.
    /// </summary>
    Sequence = 1,

    /// <summary>
    /// The token is drawn from a cryptographic random source (<see cref="GeneratedValueRandomFormat"/>), always
    /// appended. A clash is rare but possible, and is handled by drawing again within the attempt limit.
    /// </summary>
    Random = 2
}

/// <summary>
/// How an only-if-taken collision suffix is rendered.
/// </summary>
public enum GeneratedValueSuffixStyle
{
    /// <summary>
    /// A decimal number, e.g. "2", "3".
    /// </summary>
    Number = 0,

    /// <summary>
    /// A single lower-case letter, e.g. "b", "c" (26-value ceiling; see
    /// <see cref="SyncRuleMappingGenerationValidator"/>).
    /// </summary>
    Letter = 1
}

/// <summary>
/// What a sequence token does when the next number would no longer fit the configured
/// <see cref="SyncRuleMappingGeneration.FixedWidth"/>.
/// </summary>
public enum GeneratedValueWidthOverflowBehaviour
{
    /// <summary>
    /// Stop the object with an attributed error naming the attribute and the number that overran the width.
    /// The default, because a downstream system with a fixed-length field would otherwise silently receive a
    /// value it cannot store.
    /// </summary>
    StopAndReport = 0,

    /// <summary>
    /// Allow the number to grow past the configured width; the width becomes a minimum (zero-padding floor)
    /// rather than a limit.
    /// </summary>
    AllowLonger = 1
}

/// <summary>
/// The shape of a random uniqueness token, drawn from <see cref="System.Security.Cryptography.RandomNumberGenerator"/>.
/// </summary>
public enum GeneratedValueRandomFormat
{
    /// <summary>
    /// A GUID (36 characters including hyphens). <see cref="SyncRuleMappingGeneration.RandomLength"/> must be null.
    /// </summary>
    Guid = 0,

    /// <summary>
    /// A lower-case hexadecimal string of <see cref="SyncRuleMappingGeneration.RandomLength"/> characters.
    /// </summary>
    Hex = 1,

    /// <summary>
    /// A decimal digit string of <see cref="SyncRuleMappingGeneration.RandomLength"/> characters. The only random
    /// format a Number target accepts (see <see cref="SyncRuleMappingGenerationValidator"/>).
    /// </summary>
    Digits = 2
}
