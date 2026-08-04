// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Application.Servers.Preview.Patterns;

/// <summary>
/// Recognises an address or User Principal Name keeping its local part and moving to a different domain
/// (#827 Phase 4b).
///
/// This is the domain cutover, the single most common bulk identity change there is, and the one where a preview
/// most needs to distinguish "every mailbox is being re-addressed" from "every mailbox is being renamed". It is
/// therefore strict about the local part: if that moved too, the change is not a domain cutover and saying so would
/// hide the part an administrator would care about more.
/// </summary>
public class EmailDomainChangeDetector : IPreviewPatternDetector
{
    public string? Detect(PreviewPatternCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!TrySplit(candidate.OldValue, out var oldLocal, out var oldDomain) ||
            !TrySplit(candidate.NewValue, out var newLocal, out var newDomain))
        {
            return null;
        }

        return oldLocal.Equals(newLocal, StringComparison.Ordinal) &&
               !oldDomain.Equals(newDomain, StringComparison.Ordinal)
            ? PreviewPatternKeys.EmailDomainChanged
            : null;
    }

    /// <summary>
    /// Splits an address-shaped value into its local part and domain, and refuses anything it is not sure of.
    /// One at-sign, with text on both sides: everything else (a fragment such as "@contoso.com", a value that
    /// merely happens to contain an at-sign) is left to another detector or to no detector at all.
    /// </summary>
    private static bool TrySplit(string? value, out ReadOnlySpan<char> local, out ReadOnlySpan<char> domain)
    {
        local = default;
        domain = default;

        if (string.IsNullOrEmpty(value))
            return false;

        var at = value.IndexOf('@');
        if (at <= 0 || at == value.Length - 1 || value.IndexOf('@', at + 1) >= 0)
            return false;

        local = value.AsSpan(0, at);
        domain = value.AsSpan(at + 1);
        return true;
    }
}
