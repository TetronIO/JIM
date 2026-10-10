// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Models.Sync;

/// <summary>
/// What a Sequence needs to skip a run of numbers already held in one step (#2031): the target attribute, where to
/// start looking, the sequence's step, and how its numbers are written into a value. Answered by
/// <c>ISyncRepository.GetSequenceHeldRunAsync</c> with the run and who holds each number in it, so one lookup serves
/// every object that reached the run and each decides in memory, as the uniqueness gates would, which holdings are
/// its own.
/// <para>
/// The answer is a place to resume drawing, never a value to issue: the gates still check whatever number the
/// sequence draws next. That is what keeps the lookup safe where it is less exact than the gates (it cannot see the
/// numbers claimed in memory by the run, or held only in a target system that is probed rather than imported): a
/// number it wrongly reports free is simply rejected again, at the cost of one attempt.
/// </para>
/// </summary>
public sealed record SequenceSkipQuery
{
    /// <summary>The Metaverse attribute for an import-mode Sequence. Exactly one of this and
    /// <see cref="ConnectedSystemObjectTypeAttributeId"/> is set.</summary>
    public int? MetaverseAttributeId { get; init; }

    /// <summary>The Connected System attribute for an export-mode Sequence.</summary>
    public int? ConnectedSystemObjectTypeAttributeId { get; init; }

    /// <summary>The lowest number the run can start at.</summary>
    public required long From { get; init; }

    /// <summary>The sequence's step: only numbers <see cref="From"/> plus a multiple of it are considered, since no
    /// other number is one the sequence would draw from here.</summary>
    public required int Increment { get; init; }

    /// <summary>
    /// The target attribute is a Number or Long Number, so the attribute's own values (and the participating
    /// targets' values) are read as numbers, as the numeric gates read them. Generated value assignments and retired
    /// values are text either way, and are read through <see cref="TryParseHeldNumber"/>.
    /// </summary>
    public required bool NumericTarget { get; init; }

    /// <summary>The lower-cased text a value carries before its number: the base value and separator.</summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>The lower-cased text a value carries after its number: the part of the base value from its first
    /// <c>@</c>, or nothing.</summary>
    public string Suffix { get; init; } = string.Empty;

    /// <summary>The sequence's padded width, when set: a held number only counts when it is written exactly as the
    /// sequence would write it.</summary>
    public int? FixedWidth { get; init; }

    /// <summary>Import mode: the participating targets' attributes, as the connector-space gate reads them.</summary>
    public IReadOnlyCollection<int> ConnectorSpaceAttributeIds { get; init; } = [];

    /// <summary>
    /// The lower-cased value the sequence writes for <paramref name="number"/>: <see cref="Prefix"/>, the number
    /// zero-padded to <see cref="FixedWidth"/> when it fits (otherwise unpadded), and <see cref="Suffix"/>. The exact
    /// inverse of <see cref="TryParseHeldNumber"/>, so a lookup can find a number's holders through the stores'
    /// lower-cased value indexes.
    /// </summary>
    public string WriteNumber(long number)
    {
        var written = number.ToString(CultureInfo.InvariantCulture);
        if (FixedWidth.HasValue && written.Length <= FixedWidth.Value)
            written = written.PadLeft(FixedWidth.Value, '0');

        return Prefix + written + Suffix;
    }

    /// <summary>
    /// Reads the sequence number out of a lower-cased held value: the value must carry <see cref="Prefix"/> and
    /// <see cref="Suffix"/> around a run of digits, written exactly as the sequence writes that number (zero-padded to
    /// <see cref="FixedWidth"/> when it fits, otherwise unpadded). Anything else holds no number this sequence could
    /// draw. The PostgreSQL lookup applies the same rule in SQL.
    /// </summary>
    public bool TryParseHeldNumber(string normalisedValue, out long number)
    {
        number = 0;

        if (normalisedValue.Length <= Prefix.Length + Suffix.Length
            || !normalisedValue.StartsWith(Prefix, StringComparison.Ordinal)
            || !normalisedValue.EndsWith(Suffix, StringComparison.Ordinal))
            return false;

        var token = normalisedValue.Substring(Prefix.Length, normalisedValue.Length - Prefix.Length - Suffix.Length);
        if (token.Length > 18 || !token.All(char.IsAsciiDigit))
            return false;

        number = long.Parse(token, CultureInfo.InvariantCulture);
        return normalisedValue == WriteNumber(number);
    }
}
