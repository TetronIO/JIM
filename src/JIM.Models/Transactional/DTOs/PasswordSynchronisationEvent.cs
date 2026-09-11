// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// One password change recorded against an identity, and what each Connected System did with it (#1119,
/// requirement 25).
/// <para>
/// Built from Activities rather than from the queue, because the queue row is deleted the moment the password
/// arrives. Reading the queue alone would show an identity's failures and none of its successes, which is the
/// most misleading possible view of whether their password propagated.
/// </para>
/// <para>
/// <b>Carries no password.</b> Neither the Activity nor this projection of it has ever held one.
/// </para>
/// </summary>
public class PasswordSynchronisationEvent
{
    /// <summary>
    /// The Activity recording the change. It is the durable record and outlives every queue row it produced.
    /// </summary>
    public Guid ActivityId { get; set; }

    public DateTime Created { get; set; }

    /// <summary>
    /// Who made the change, and what kind of principal they were: an administrator at a screen, or the API key
    /// an automation presented. A synchronised password change usually starts in a self-service portal or a
    /// service desk tool rather than in JIM, so the API key case is the common one.
    /// </summary>
    public string? InitiatedByName { get; set; }

    /// <inheritdoc cref="InitiatedByName"/>
    public ActivityInitiatorType InitiatedByType { get; set; }

    /// <summary>
    /// What the change said it did, in the words the Activity recorded.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Which way the change was aimed (#1635): an administrator's explicit set of named accounts, or a password
    /// propagated to every configured Connected System. Read back from the Activity's TargetContext, where
    /// <c>SetPasswordAsync</c> writes the origin's name; null for an Activity written before origins were
    /// recorded, which the panel shows without a kind chip rather than with a guessed one.
    /// </summary>
    public PendingPasswordChangeOrigin? Origin { get; set; }

    /// <summary>
    /// One entry per Connected System the change reached, oldest first. Empty where the change was queued for no
    /// system, or where none has been attempted yet.
    /// </summary>
    public IReadOnlyList<PasswordSynchronisationEventOutcome> Outcomes { get; set; } = [];
}

/// <summary>
/// What one Connected System did with one password change.
/// </summary>
public class PasswordSynchronisationEventOutcome
{
    public Guid ActivityId { get; set; }

    public int? ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = string.Empty;

    /// <summary>
    /// How it went, carried verbatim rather than reduced to a boolean: an attempt that is still running is a real
    /// state, distinct from one that succeeded and one that was refused, and there is a window in which a reader
    /// can see it.
    /// </summary>
    public ActivityStatus Status { get; set; }

    /// <summary>
    /// The target's own words on a refusal, which is what says where the remedy lives. Null where it succeeded.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// What happened, in the words the Activity recorded.
    /// </summary>
    public string? Message { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// Whether the Connected System took the password. Null while the attempt is still running.
    /// </summary>
    public bool? Succeeded => Status switch
    {
        ActivityStatus.Complete => true,
        ActivityStatus.CompleteWithWarning => true,
        ActivityStatus.CompleteWithError => false,
        ActivityStatus.FailedWithError => false,
        ActivityStatus.Cancelled => false,
        _ => null
    };
}
