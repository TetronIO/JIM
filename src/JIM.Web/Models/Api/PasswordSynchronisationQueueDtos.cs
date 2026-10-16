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
    /// The queued change's ID, which identifies it when retrying or cancelling it.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The Metaverse Object whose password this is.
    /// </summary>
    public Guid MetaverseObjectId { get; set; }

    /// <summary>
    /// That Metaverse Object's display name.
    /// </summary>
    public string? MetaverseObjectDisplayName { get; set; }

    /// <summary>
    /// The plural name of that Metaverse Object's type, such as People.
    /// </summary>
    /// <remarks>
    /// A link to the Metaverse Object is built from it, so a caller rendering a list does not need a read per row
    /// to make one.
    /// </remarks>
    public string? MetaverseObjectTypePluralName { get; set; }

    /// <summary>
    /// The Connected System the change is queued for.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// That Connected System's name.
    /// </summary>
    public string ConnectedSystemName { get; set; } = string.Empty;

    /// <summary>
    /// Where the change has got to: Pending (JIM still intends to deliver it), Delivering (the Password Delivery
    /// Service has claimed it and is delivering it now), Parked (the target refused it, or it ran out of attempts,
    /// so it waits on a person), Expired (it outlived its time to live and can no longer be delivered), or
    /// Cancelled (an administrator stopped it).
    /// </summary>
    public PendingPasswordChangeStatus Status { get; set; }

    /// <summary>
    /// Where the change came from: <c>Explicit</c> (an administrator's named set), <c>Propagated</c> (JIM
    /// carried it to every configured system after the person's own password change), or <c>Provisioned</c>
    /// (the first password for an account an export has just created).
    /// </summary>
    public PendingPasswordChangeOrigin Origin { get; set; }

    /// <summary>
    /// The Synchronisation Rule whose Initial Password this change delivers a Connected System Object's first password
    /// under, or null where it is no Connected System Object's first password. A <c>Provisioned</c> change generates its
    /// password from the rule at each delivery attempt; a change carrying a first password in place of the Initial
    /// Password keeps its own password and takes only the rule's settings for enabling the Connected System Object and
    /// for whether its password never expires.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The Connected System Object this change also gives its first password, or null. Set when the change was already
    /// waiting as JIM provisioned that Connected System Object, or replaced its Initial Password before it was
    /// delivered; delivering it then applies the provisioning Synchronisation Rule's Initial Password settings for
    /// enabling the Connected System Object and for whether its password never expires. Cancelling such a change
    /// leaves that Connected System Object without a password from JIM. For a <c>Provisioned</c> change, the Connected
    /// System Object it is the Initial Password of.
    /// </summary>
    /// <remarks>#2160. Read from <c>PendingPasswordChange.ProvisionedConnectedSystemObjectId</c>.</remarks>
    public Guid? ProvisionedConnectedSystemObjectId { get; set; }

    /// <summary>
    /// Whether a delivery pass would attempt this change right now. It tells a change waiting out a retry backoff
    /// from one that is due but not yet reached, which <c>Status</c> alone cannot. It is never true for a held
    /// change, whose Connected System is switched off.
    /// </summary>
    public bool Due { get; set; }

    /// <summary>
    /// Whether the change is waiting on somebody switching Password Synchronisation back on for its Connected
    /// System, rather than on JIM. A configured system that is switched off accumulates queued changes instead of
    /// discarding them, and enabling it delivers what accumulated; until then nothing is attempted, so the
    /// remedy is a person's rather than a retry's.
    /// </summary>
    public bool Held { get; set; }

    /// <summary>
    /// How the last attempt failed; null for a change that has not been attempted.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; set; }

    /// <summary>
    /// The target's own words for why the last attempt failed, which say where the remedy lies; null for a change
    /// that has not been attempted.
    /// </summary>
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
    /// When an administrator cancelled the change, or null where nobody has.
    /// </summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>
    /// Who cancelled the change; null where nobody has, and for a cancellation made with an API key, which has no
    /// person behind it.
    /// </summary>
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
            MetaverseObjectTypePluralName = header.MetaverseObjectTypePluralName,
            ConnectedSystemId = header.ConnectedSystemId,
            ConnectedSystemName = header.ConnectedSystemName,
            Status = header.Status,
            Origin = header.Origin,
            SyncRuleId = header.SyncRuleId,
            ProvisionedConnectedSystemObjectId = header.ProvisionedConnectedSystemObjectId,
            Due = header.IsDue(asOf),
            Held = header.IsHeld,
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
