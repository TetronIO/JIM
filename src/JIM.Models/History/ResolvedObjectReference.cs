// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;

namespace JIM.Models.History;

/// <summary>
/// A Connected System Object or Metaverse Object named by id from a history record, resolved to what JIM still holds of
/// it: the live object, else its deletion record, else nothing but the id (#2669).
/// </summary>
/// <remarks>
/// <para>
/// History records name objects by id and keep that id after the object goes. Every reader resolves one in the same
/// order: a live object; otherwise the object's own deletion record (its name, its type, when it went); otherwise the
/// referencing row's own snapshot (an execution item's name snapshot, a change value's distinguished name); otherwise
/// "no longer held by JIM". This type carries the first two steps, read in one batch per kind of object; the caller
/// holds the snapshot, and <see cref="DisplayText"/> applies the last two.
/// </para>
/// <para>
/// "Is it live?" is answered here by the object table alone, never by a null id: an id that names no object is not
/// evidence of anything but that.
/// </para>
/// <para>
/// The windowed change value reads resolve the same order in SQL, and a database test holds the two to one answer.
/// </para>
/// </remarks>
public sealed class ResolvedObjectReference
{
    /// <summary>
    /// The id the history record holds.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// What JIM still holds of the object.
    /// </summary>
    public ObjectReferenceState State { get; init; }

    /// <summary>
    /// What a reference to the object reads as. For a live Connected System Object, its name, else its external ID,
    /// else its secondary external ID, where each is held as text; for a live Metaverse Object, its display name; for a deleted
    /// object, the name its deletion record kept, else (a Connected System Object) the external ID it kept. Null when
    /// none of these is known, and always null when the object is not held.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The object's name alone: a live object's display name, or the name its deletion record kept. Null when the
    /// object had none, or is not held.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// A Connected System Object's external ID: the live object's, or the one its deletion record kept. Always null
    /// for a Metaverse Object.
    /// </summary>
    public string? ExternalId { get; init; }

    /// <summary>
    /// The object's type id, live or as its deletion record kept it; null when not known.
    /// </summary>
    public int? ObjectTypeId { get; init; }

    /// <summary>
    /// The object's type name, live or as its deletion record kept it; null when not known.
    /// </summary>
    public string? ObjectTypeName { get; init; }

    /// <summary>
    /// A Metaverse Object's type plural name, live or as its deletion record kept it; null when not known, and always
    /// for a Connected System Object.
    /// </summary>
    public string? ObjectTypePluralName { get; init; }

    /// <summary>
    /// A Connected System Object's Connected System, live or as its deletion record kept it. Always null for a
    /// Metaverse Object.
    /// </summary>
    public int? ConnectedSystemId { get; init; }

    /// <summary>
    /// The name of <see cref="ConnectedSystemId"/>'s Connected System, while that system exists. Always null for a
    /// Metaverse Object.
    /// </summary>
    public string? ConnectedSystemName { get; init; }

    /// <summary>
    /// When the object was deleted, from its deletion record; null unless <see cref="State"/> is Deleted.
    /// </summary>
    public DateTime? DeletedAt { get; init; }

    /// <summary>
    /// Whether the object exists.
    /// </summary>
    public bool IsLive => State == ObjectReferenceState.Live;

    /// <summary>
    /// A reference to an object JIM holds nothing of.
    /// </summary>
    public static ResolvedObjectReference NotHeld(Guid id) => new() { Id = id, State = ObjectReferenceState.NotHeld };

    /// <summary>
    /// What the reference reads as, by the one resolution order: the live object's or deletion record's
    /// <see cref="Label"/>, then the referencing row's own <paramref name="snapshot"/>, then the id as text.
    /// </summary>
    /// <param name="snapshot">What the referencing row kept of the object itself, such as a change value's
    /// distinguished name; null when it kept nothing.</param>
    public string DisplayText(string? snapshot)
    {
        if (!string.IsNullOrEmpty(Label))
            return Label;

        return !string.IsNullOrEmpty(snapshot) ? snapshot : Id.ToString();
    }
}
