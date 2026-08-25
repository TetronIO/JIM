// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What JIM has worked out an object's class membership attribute must say, for one export.
/// </summary>
/// <remarks>
/// See <see cref="ObjectTypeTags.Keys.ClassMembershipAttribute"/> for why JIM computes this rather than an
/// administrator flowing it.
/// </remarks>
public class ClassMembershipPlan
{
    /// <summary>
    /// The attribute carrying class membership in the Connected System, i.e. <c>objectClass</c>. Null when the
    /// Connected System has no such concept, in which case there is nothing to write and nothing to enforce.
    /// </summary>
    public string? AttributeName { get; set; }

    /// <summary>
    /// The class names to write. On a create this is the object's whole membership; on an update it is only what
    /// the object does not carry yet, because an update names the classes being added rather than restating the
    /// ones already there.
    /// </summary>
    public List<string> ClassesToWrite { get; set; } = [];

    /// <summary>
    /// Attributes the Connected System requires for a class in <see cref="ClassesToWrite"/> that this export
    /// neither writes nor finds already on the object.
    /// </summary>
    /// <remarks>
    /// Non-empty means the export must be refused. Sending it would have the Connected System reject the change on
    /// JIM's behalf, with an error naming its own internals rather than the attributes an administrator has to flow.
    /// </remarks>
    public List<string> MissingRequiredAttributes { get; set; } = [];

    /// <summary>
    /// Whether this export has anything to say about class membership at all.
    /// </summary>
    public bool HasChanges => AttributeName != null && ClassesToWrite.Count > 0;

    public override string ToString()
    {
        return AttributeName == null ? "no class membership" : $"{AttributeName}: {string.Join(", ", ClassesToWrite)}";
    }
}
