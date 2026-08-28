// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

public enum ActivityTargetOperationType
{
    Create = 0,
    Read = 1,
    Update = 2,
    Delete = 3,
    /// <summary>
    /// Intended for clearing all objects from a Connected System.
    /// </summary>
    Clear = 4,
    /// <summary>
    /// Intended for executing a Data Generation Template.
    /// </summary>
    Execute = 5,
    /// <summary>
    /// Relates to Connected Systems.
    /// </summary>
    ImportHierarchy = 6,
    /// <summary>
    /// Relates to Connected Systems.
    /// </summary>
    ImportSchema = 7,
    /// <summary>
    /// Reverts a Service Setting to its default value.
    /// </summary>
    Revert = 8,
    /// <summary>
    /// Records a system-wide factory reset (wipe of all customer data and configuration).
    /// </summary>
    Reset = 9,
    /// <summary>
    /// An authentication event (interactive sign-in or API key authentication); used with
    /// <see cref="ActivityTargetType.Authentication"/> security audit event Activities.
    /// </summary>
    Authenticate = 10,
    /// <summary>
    /// A read-only evaluation of a configuration change that has not been made: what it *would* do (#827). Distinct
    /// from <see cref="Read"/> and emphatically not <see cref="Update"/>, because the Activity list is where an
    /// administrator establishes what was actually done to the system, and a preview must never be mistaken for the
    /// change it was previewing.
    /// </summary>
    Preview = 11,
    /// <summary>
    /// Sets the password on a single Connected System Object. Used with
    /// <see cref="ActivityTargetType.ConnectedSystemObject"/>. The Activity records that a password was set and
    /// what the target said about it, never the password itself.
    /// </summary>
    SetPassword = 12,
    /// <summary>
    /// The data-removal task of the schema refresh decision's "Apply and Remove" option (#1485): Connected
    /// System Objects of removed Object Types marked Obsolete and stored values of removed attributes deleted,
    /// executed by the worker under this Activity with per-object results.
    /// </summary>
    SchemaRefreshRemoval = 13,

    /// <summary>
    /// An administrator asked JIM to attempt queued password changes again, having fixed whatever stopped them
    /// (#1119, requirement 22).
    /// </summary>
    RetryPasswordDelivery = 14,

    /// <summary>
    /// An administrator stopped queued password changes being delivered (#1119, requirement 22).
    /// </summary>
    CancelPasswordDelivery = 15,

    /// <summary>
    /// Reads a Connected System's objects to find out which auxiliary classes they carry (#492). Used with
    /// <see cref="ActivityTargetType.ConnectedSystem"/>. Distinct from <see cref="ImportSchema"/> because it reads
    /// objects rather than schema, and changes nothing: what it finds is recorded as suggestions an administrator
    /// may act on, never as configuration.
    /// </summary>
    DiscoverAuxiliaryClasses = 16,

    /// <summary>
    /// The queued recall half of deleting a Synchronisation Rule with contributed Metaverse attribute values
    /// (#1537): the worker withdraws the rule's contributed values by provenance (re-electing surviving
    /// contributors, staging Pending Exports), then deletes the rule as its final step. Used with
    /// <see cref="ActivityTargetType.SynchronisationRule"/>; the rule deletion itself is recorded as a child
    /// <see cref="Delete"/> Activity.
    /// </summary>
    RecallAttributeValues = 17
}
