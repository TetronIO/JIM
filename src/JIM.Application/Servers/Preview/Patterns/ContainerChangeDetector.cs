// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;
using System.Buffers;

namespace JIM.Application.Servers.Preview.Patterns;

/// <summary>
/// Recognises a distinguished name keeping its leaf name and moving to a different parent path (#827 Phase 4b).
///
/// An organisational unit move reads as an unintelligible pair of long strings without this: two distinguished
/// names differing somewhere in the middle are exactly what a human cannot diff at a glance, and it is what a
/// scope or join change most often produces in bulk.
///
/// Shape recognition is deliberately conservative. Every comma-separated part must look like a relative name
/// (a type, an equals sign, a value), so prose that merely contains a comma is never mistaken for a directory path;
/// a value whose embedded comma is escaped fails that check and the detector stays silent, which costs a label and
/// never produces a wrong one.
/// </summary>
public class ContainerChangeDetector : IPreviewPatternDetector
{
    /// <summary>
    /// What an attribute type may be spelled with: letters and digits cover the named types, the dot and hyphen
    /// cover object identifiers and the hyphenated types some directories define.
    /// </summary>
    private static readonly SearchValues<char> AttributeTypeCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-.");

    public string? Detect(PreviewPatternCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!TrySplit(candidate.OldValue, out var oldLeaf, out var oldParent) ||
            !TrySplit(candidate.NewValue, out var newLeaf, out var newParent))
        {
            return null;
        }

        // Both halves matter. A leaf that also changed means the object was renamed as well as moved, and
        // "moved to a different container" would be a half-truth about the more interesting of the two.
        return oldLeaf.Equals(newLeaf, StringComparison.Ordinal) &&
               !oldParent.Equals(newParent, StringComparison.Ordinal)
            ? PreviewPatternKeys.ContainerChanged
            : null;
    }

    /// <summary>
    /// Splits a distinguished name into its leaf relative name and the parent path beneath it, and refuses anything
    /// that is not recognisably a distinguished name with a parent.
    /// </summary>
    private static bool TrySplit(string? value, out ReadOnlySpan<char> leaf, out ReadOnlySpan<char> parent)
    {
        leaf = default;
        parent = default;

        if (string.IsNullOrEmpty(value))
            return false;

        var firstComma = value.IndexOf(',');
        if (firstComma <= 0 || firstComma == value.Length - 1)
            return false;

        var span = value.AsSpan();
        var start = 0;
        while (true)
        {
            var comma = value.IndexOf(',', start);
            var end = comma < 0 ? value.Length : comma;
            if (!LooksLikeRelativeName(span[start..end]))
                return false;

            if (comma < 0)
                break;

            start = comma + 1;
        }

        leaf = span[..firstComma];
        parent = span[(firstComma + 1)..];
        return true;
    }

    /// <summary>Whether one comma-separated part reads as "type=value" with an attribute type that could be one.</summary>
    private static bool LooksLikeRelativeName(ReadOnlySpan<char> part)
    {
        part = part.Trim();

        var equals = part.IndexOf('=');
        if (equals <= 0 || equals == part.Length - 1)
            return false;

        var type = part[..equals];
        return char.IsAsciiLetterOrDigit(type[0]) && !type.ContainsAnyExcept(AttributeTypeCharacters);
    }
}
