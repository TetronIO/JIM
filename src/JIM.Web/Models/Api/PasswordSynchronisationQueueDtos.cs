// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// One queued password change, as the REST API and PowerShell list it (#1119, requirement 33).
/// <para>
/// <b>Carries no password.</b> The queued value is encrypted in the database and has no representation on any
/// surface; the model-layer <see cref="PendingPasswordChangeHeader"/> this is built from has nowhere to put one
/// either, which is what keeps that true by construction rather than by review.
/// </para>
/// </summary>
public class PendingPasswordChangeResponse
{
    /// <summary>
    /// The unique identifier of the queued change, as passed back to the retry and cancel endpoints.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The identity whose password this is, and its display name.
    /// </summary>
    public Guid MetaverseObjectId { get; set; }

    /// <inheritdoc cref="MetaverseObjectId"/>
    public string? MetaverseObjectDisplayName { get; set; }

    /// <summary>
    /// The Connected System the change is queued for, and its name.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <inheritdoc cref="ConnectedSystemId"/>
    public string ConnectedSystemName { get; set; } = string.Empty;

    /// <summary>
    /// Where the change has got to: Pending (JIM still intends to deliver it), Parked (the target refused it, or
    /// it ran out of attempts, so it waits on a person), Expired (it outlived its time to live and can no longer
    /// be delivered), or Cancelled (an administrator stopped it).
    /// </summary>
    public PendingPasswordChangeStatus Status { get; set; }

    /// <summary>
    /// Whether a delivery pass would attempt this change right now. Distinguishes a change waiting out a retry
    /// backoff from one that is due and simply has not been reached, which <see cref="Status"/> alone cannot.
    /// </summary>
    public bool Due { get; set; }

    /// <summary>
    /// How the last attempt failed, and the target's own words, which is what says where the remedy lives. Both
    /// null for a change that has not been attempted.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; set; }

    /// <inheritdoc cref="FailureReason"/>
    public string? TargetMessage { get; set; }

    /// <summary>
    /// How many delivery attempts have been made.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// When the next attempt falls due, or null for a change that is due now or is no longer being attempted.
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>
    /// When the change was queued.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When delivery was last attempted, or null where it never has been.
    /// </summary>
    public DateTime? LastAttemptedAt { get; set; }

    /// <summary>
    /// When the change stops being deliverable, whether or not it has been delivered by then.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When an administrator cancelled the change, and who, or null where nobody has. The name is null for a
    /// cancellation made with an API key, which has no person behind it.
    /// </summary>
    public DateTime? CancelledAt { get; set; }

    /// <inheritdoc cref="CancelledAt"/>
    public string? CancelledByName { get; set; }

    /// <summary>
    /// Projects a queue row for the API, resolving <see cref="Due"/> against the moment the window was read so
    /// every row in one response answers the question as at the same instant.
    /// </summary>
    public static PendingPasswordChangeResponse FromHeader(PendingPasswordChangeHeader header, DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(header);

        return new PendingPasswordChangeResponse
        {
            Id = header.Id,
            MetaverseObjectId = header.MetaverseObjectId,
            MetaverseObjectDisplayName = header.MetaverseObjectDisplayName,
            ConnectedSystemId = header.ConnectedSystemId,
            ConnectedSystemName = header.ConnectedSystemName,
            Status = header.Status,
            Due = header.IsDue(asOf),
            FailureReason = header.FailureReason,
            TargetMessage = header.TargetMessage,
            AttemptCount = header.AttemptCount,
            NextRetryAt = header.NextRetryAt,
            CreatedAt = header.CreatedAt,
            LastAttemptedAt = header.LastAttemptedAt,
            ExpiresAt = header.ExpiresAt,
            CancelledAt = header.CancelledAt,
            CancelledByName = header.CancelledByName
        };
    }
}

/// <summary>
/// Which queued password changes a retry or cancel applies to (#1119, requirement 33).
/// <para>
/// The criteria are combined, not alternatives: "these three identifiers, if they are still Parked" is
/// expressible, and is the right shape for an administrator acting on what a list showed them a moment ago. A
/// row that has moved on since simply does not match, rather than being acted on regardless.
/// </para>
/// </summary>
public class PasswordQueueActionRequest : IValidatableObject
{
    /// <summary>
    /// The largest number of changes that may be named in one request. A cap rather than no limit because
    /// identifiers become an <c>IN</c> list in the database; a caller with more than this to act on wants a
    /// filter, not a longer list.
    /// </summary>
    public const int MaximumIds = 1000;

    /// <summary>
    /// Restrict to one Connected System, or omit for every system.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// Restrict to changes in one state, or omit for every state.
    /// </summary>
    public PendingPasswordChangeStatus? Status { get; set; }

    /// <summary>
    /// Restrict to changes whose last attempt failed this way, or omit for every reason.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; set; }

    /// <summary>
    /// Restrict to one identity's queued changes, or omit for every identity.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// Restrict to these specific changes. Combines with the other criteria rather than replacing them.
    /// </summary>
    public IReadOnlyCollection<Guid>? Ids { get; set; }

    /// <summary>
    /// Free-text search over the identity's display name and the Connected System's name.
    /// </summary>
    [StringLength(200, ErrorMessage = "Search text must not exceed 200 characters.")]
    public string? SearchText { get; set; }

    /// <summary>
    /// Confirms that a request naming no criteria at all is meant to act on the entire queue.
    /// <para>
    /// Required because the alternative default is worse: an empty body would otherwise cancel every queued
    /// password change in the deployment, silently and from a typo. Acting on the whole queue is a legitimate
    /// thing to want; saying so is not much to ask for it.
    /// </para>
    /// </summary>
    public bool ApplyToAllChanges { get; set; }

    /// <summary>
    /// Whether any criterion narrows this request.
    /// </summary>
    private bool HasCriteria =>
        ConnectedSystemId.HasValue ||
        Status.HasValue ||
        FailureReason.HasValue ||
        MetaverseObjectId.HasValue ||
        Ids is { Count: > 0 } ||
        !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    /// Translates the request into the filter the application layer takes.
    /// </summary>
    public PendingPasswordChangeFilter ToFilter()
    {
        return new PendingPasswordChangeFilter
        {
            ConnectedSystemId = ConnectedSystemId,
            Status = Status,
            FailureReason = FailureReason,
            MetaverseObjectId = MetaverseObjectId,
            Ids = Ids,
            SearchText = SearchText
        };
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Ids is { Count: > MaximumIds })
        {
            yield return new ValidationResult(
                $"No more than {MaximumIds} password changes may be named in one request; narrow the request with a filter instead.",
                [nameof(Ids)]);
        }

        if (!HasCriteria && !ApplyToAllChanges)
        {
            yield return new ValidationResult(
                "This request names no password changes. Supply at least one criterion, or set applyToAllChanges to act on the entire queue.",
                [nameof(ApplyToAllChanges)]);
        }
    }
}

/// <summary>
/// What a retry or cancel did.
/// </summary>
public class PasswordQueueActionResponse
{
    /// <summary>
    /// How many queued password changes the action applied to.
    /// <para>
    /// May be lower than the number of identifiers a caller named, and that is not an error: a change delivered,
    /// expired or already cancelled between the caller reading the queue and acting on it no longer matches.
    /// </para>
    /// </summary>
    public int AffectedCount { get; set; }
}
