// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// Resources whose payload holds a string matching <paramref name="Text"/>.
/// </summary>
/// <param name="Path">The JSON members from the payload's root to the attribute: <c>["displayName"]</c>,
/// <c>["name", "familyName"]</c>, or an extension's under its URN.</param>
/// <param name="MultiValued">Whether the attribute at <paramref name="Path"/> is an array, any one of whose values (or
/// entries' <paramref name="EntryMember"/>) may match.</param>
/// <param name="EntryMember">For a multi-valued complex attribute, the member of each entry that is compared
/// (<c>emails.value</c>); otherwise null.</param>
/// <param name="Match">How the stored string must match <paramref name="Text"/>.</param>
/// <param name="Text">The string to match.</param>
/// <param name="CaseExact">Whether the comparison has regard to case.</param>
public sealed record ScimStoreTextFilter(
    IReadOnlyList<string> Path,
    bool MultiValued,
    string? EntryMember,
    ScimStoreTextMatch Match,
    string Text,
    bool CaseExact) : ScimStoreFilter;
