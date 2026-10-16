// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;
namespace JIM.Models.Activities.DTOs;

/// <summary>
/// One Run Profile Execution Item in a list: what happened to one object in a Run Profile execution. The item's own
/// endpoint adds the error message and the rest of its detail.
/// </summary>
public class ActivityRunProfileExecutionItemHeader
{
    /// <summary>
    /// The item's unique identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The object's external ID: its Connected System Object's current value, or the value recorded with the item
    /// where that object no longer exists.
    /// </summary>
    public string? ExternalIdValue { get; set; }

    /// <summary>
    /// The object's display name: its Connected System Object's current value, or the name recorded with the item
    /// where that object no longer exists.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The object's type: its Connected System Object's current type, or the type recorded with the item where that
    /// object no longer exists.
    /// </summary>
    public string? ConnectedSystemObjectType { get; set; }

    /// <summary>
    /// What happened to the object.
    /// </summary>
    public ObjectChangeType ObjectChangeType { get; set; }

    /// <summary>
    /// The kind of failure; <c>NotSet</c> or null when the object did not fail.
    /// </summary>
    public ActivityRunProfileExecutionItemErrorType? ErrorType { get; set; }

    /// <summary>
    /// The Metaverse Object the item is about, where it records one rather than a Connected System Object (an export
    /// scope review). Null otherwise.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// Outcome types with counts, for example <c>Projected:1,AttributeFlow:12,PendingExportCreated:2</c>. Null for
    /// an item recorded before outcomes were tracked, or when outcome tracking is disabled.
    /// </summary>
    /// <remarks>Denormalised for stat chip rendering in list views.</remarks>
    public string? OutcomeSummary { get; set; }
}