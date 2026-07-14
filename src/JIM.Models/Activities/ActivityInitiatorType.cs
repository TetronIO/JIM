// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// Identifies the type of security principal that initiated an activity.
/// Used for audit trail purposes to distinguish between user-initiated and automated actions.
/// </summary>
public enum ActivityInitiatorType
{
    /// <summary>
    /// The initiator type has not been set. This should not occur in production.
    /// </summary>
    NotSet = 0,

    /// <summary>
    /// The activity was initiated by a user (represented as a MetaverseObject).
    /// </summary>
    User = 1,

    /// <summary>
    /// The activity was initiated via an API key (automation, CI/CD, integration testing).
    /// </summary>
    ApiKey = 2,

    /// <summary>
    /// The action was performed by the system itself (seeding, SSO initialisation, scheduled maintenance).
    /// </summary>
    System = 3,

    /// <summary>
    /// The activity was initiated by an unauthenticated, unidentified caller: a failed authentication attempt
    /// (interactive sign-in or API key) where no security principal could be established. Carries no
    /// <c>InitiatedById</c>; <c>InitiatedByName</c> is always "Anonymous".
    /// </summary>
    Anonymous = 4
}
