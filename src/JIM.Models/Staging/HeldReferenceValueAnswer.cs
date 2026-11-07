// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// #2817: the database's answer to a <see cref="HeldReferenceValueQuestion"/>. It names only the held values and imported
/// values the import's comparison could still decide either way; everything else is settled by an exact match, and an exact
/// match is one the comparison agrees with (a held value naming an imported value byte for byte is kept, and an imported
/// value a held value names byte for byte is not added).
/// </summary>
/// <remarks>
/// <para>
/// A held value is matched exactly when the string it was imported as, or the External ID of the object it was resolved to
/// (the Secondary External ID where there is one), is one of the imported values. The comparison ignores case for the
/// second and not the first, so the exact match is the narrower of the two, never the wider: what it settles, the
/// comparison would settle the same way.
/// </para>
/// <para>
/// An imported value not matched exactly may still name a held value ignoring case. <see cref="RelatedValues"/> carries
/// every held value whose object's External ID the database finds equal to it through <c>lower()</c>, so the comparison
/// sees each one it would match, provided the database folds case at least as JIM does for that value
/// (<see cref="IsCompleteIgnoringCase"/>). Where it might not, the caller loads the attribute in full instead.
/// </para>
/// </remarks>
public sealed class HeldReferenceValueAnswer
{
    public HeldReferenceValueAnswer(HeldReferenceValueQuestion question)
    {
        Question = question;
    }

    /// <summary>The question answered.</summary>
    public HeldReferenceValueQuestion Question { get; }

    /// <summary>
    /// The held values no imported value matches exactly: every value the import could remove, and every value the
    /// attribute holds that is not a reference at all (one stored under another data type, or one holding nothing). The
    /// import loads these, and only these, of the attribute's values.
    /// </summary>
    public HashSet<Guid> UnmatchedValueIds { get; } = new();

    /// <summary>
    /// Held values whose object's External ID equals, ignoring case as the database folds it, an imported value in
    /// <see cref="UnmatchedIncomingValues"/>: what the comparison needs, beyond the values it loads, to decide whether such
    /// a value is already held.
    /// </summary>
    public List<HeldReferenceValue> RelatedValues { get; } = new();

    /// <summary>
    /// The External ID (Secondary External ID where there is one) of each object a value in <see cref="UnmatchedValueIds"/>
    /// or <see cref="RelatedValues"/> was resolved to, by the object's id. An object with neither has no entry.
    /// </summary>
    public Dictionary<Guid, string> ReferencedExternalIds { get; } = new();

    /// <summary>
    /// The imported values no held value matches exactly: every value the import could add.
    /// </summary>
    public HashSet<string> UnmatchedIncomingValues { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether <see cref="RelatedValues"/> holds every held value the comparison would match ignoring case. Always, where
    /// every imported value was matched exactly. Otherwise only where each unmatched value is ASCII and the database's
    /// <c>lower()</c> folds ASCII as JIM does: the comparison never equates a character outside ASCII with one inside it,
    /// so a value it matches against ASCII text is ASCII text, which such a database folds alike.
    /// </summary>
    /// <param name="databaseFoldsCaseLikeIdentifierKey">Whether the database's <c>lower()</c> was found to fold as JIM does.</param>
    public bool IsCompleteIgnoringCase(bool databaseFoldsCaseLikeIdentifierKey) =>
        UnmatchedIncomingValues.Count == 0
        || (databaseFoldsCaseLikeIdentifierKey && UnmatchedIncomingValues.All(value => value.All(char.IsAscii)));
}
