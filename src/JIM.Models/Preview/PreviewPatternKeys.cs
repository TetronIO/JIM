// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Preview;

/// <summary>
/// The patterns a Configuration Change Preview can recognise across its deltas, and how each reads (#827).
///
/// Two audiences, one place. The key is what gets persisted, returned by the REST API and matched on in a
/// PowerShell script, so it is a stable identifier and must never be reworded; the display name is what an
/// administrator reads in the portal, and may be reworded freely. Keeping the pair together stops a new detector
/// from shipping a key that nothing knows how to render.
/// </summary>
public static class PreviewPatternKeys
{
    /// <summary>The value is the same text in a different case.</summary>
    public const string CasingChanged = "CasingChanged";

    /// <summary>An address or User Principal Name keeps its local part and moves to a different domain.</summary>
    public const string EmailDomainChanged = "EmailDomainChanged";

    /// <summary>A distinguished name keeps its leaf name and moves to a different parent path.</summary>
    public const string ContainerChanged = "ContainerChanged";

    /// <summary>The value gains text at the start, and is otherwise unchanged.</summary>
    public const string PrefixAdded = "PrefixAdded";

    /// <summary>The value loses text from the start, and is otherwise unchanged.</summary>
    public const string PrefixRemoved = "PrefixRemoved";

    /// <summary>The value gains text at the end, and is otherwise unchanged.</summary>
    public const string SuffixAdded = "SuffixAdded";

    /// <summary>The value loses text from the end, and is otherwise unchanged.</summary>
    public const string SuffixRemoved = "SuffixRemoved";

    /// <summary>
    /// How <paramref name="patternKey"/> reads in a user interface, or null where there is no pattern to describe.
    /// </summary>
    /// <param name="patternKey">A key from this class, or null where no detector recognised anything.</param>
    /// <returns>
    /// The display name, or null for no pattern and for a key this build does not know. An unrecognised key is
    /// deliberately rendered as nothing rather than as itself: an internal identifier in front of an administrator
    /// is worse than a group described by its values alone, and the fixture over this class makes the case
    /// unreachable for keys declared here.
    /// </returns>
    public static string? GetDisplayName(string? patternKey) => patternKey switch
    {
        CasingChanged => "Casing changed",
        EmailDomainChanged => "Email or UPN domain changed",
        ContainerChanged => "Moved to a different container",
        PrefixAdded => "Prefix added",
        PrefixRemoved => "Prefix removed",
        SuffixAdded => "Suffix added",
        SuffixRemoved => "Suffix removed",
        _ => null
    };
}
