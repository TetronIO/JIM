// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Enums;

/// <summary>
/// What JIM still holds of an object that a history record names by id: the object itself, the record of its deletion,
/// or neither.
/// </summary>
/// <remarks>
/// History records (change records, their values and Run Profile Execution Items) name objects by id and keep that id
/// after the object goes (#2669). A reader resolves the id when it reads it, in one order: a live object, then the
/// object's own deletion record, then the referencing row's own snapshot, then "no longer held".
/// </remarks>
public enum ObjectReferenceState
{
    /// <summary>
    /// The object exists.
    /// </summary>
    Live = 0,

    /// <summary>
    /// The object has been deleted, and its deletion record is still held: its name, type and when it went are known.
    /// </summary>
    Deleted = 1,

    /// <summary>
    /// Neither the object nor a record of its deletion is held, so only the id, and whatever the referencing row kept
    /// of its own, remain.
    /// </summary>
    NotHeld = 2
}
