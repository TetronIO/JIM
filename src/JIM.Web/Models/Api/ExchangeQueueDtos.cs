// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// One queued Exchange change, as the REST API and PowerShell list it: the recipient declared for an account, and where
/// the change stands in reaching Exchange.
/// </summary>
/// <remarks>#2021, Phase 6b. Built from <c>PendingExchangeChangeHeader</c>.</remarks>
public class PendingExchangeChangeResponse
{
    /// <summary>
    /// The queued change's ID, which identifies it when retrying it.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The change's own Activity, under which every attempt, retry and outcome is recorded.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// The Connected System whose Exchange the change is for.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// That Connected System's name.
    /// </summary>
    public string ConnectedSystemName { get; set; } = string.Empty;

    /// <summary>
    /// The account in that Connected System the change makes a recipient, or null once the account has been deleted.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The Metaverse Object whose account it is, or null once the Metaverse Object has been deleted.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// That Metaverse Object's display name.
    /// </summary>
    public string? MetaverseObjectDisplayName { get; set; }

    /// <summary>
    /// The plural name of that Metaverse Object's type, such as People.
    /// </summary>
    public string? MetaverseObjectTypePluralName { get; set; }

    /// <summary>
    /// The kind of recipient declared: Mailbox, SharedMailbox, RoomMailbox, EquipmentMailbox, MailUser, MailContact,
    /// MailEnabledGroup or None.
    /// </summary>
    public string RecipientType { get; set; } = string.Empty;

    /// <summary>
    /// Where the change stands: Queued (waiting for its first attempt), Delivering (Exchange is applying it now),
    /// Retrying (waiting out a backoff after a failure that may clear by itself), Parked (JIM has stopped trying, so it
    /// waits on a person), Refused (JIM refused the declared state before asking Exchange) or NotMade (it would change an
    /// existing recipient's type, which JIM does not do). A refused or not-made change holds until its
    /// declared state changes.
    /// </summary>
    public ExchangeQueueState State { get; set; }

    /// <summary>
    /// How the last attempt failed, or null for a change not attempted since it was queued or retried.
    /// </summary>
    public ExchangeChangeFailureKind? FailureKind { get; set; }

    /// <summary>
    /// Exchange's own words for the last failure, which say where the remedy lies, or JIM's reason for a refusal.
    /// </summary>
    public string? FailureMessage { get; set; }

    /// <summary>
    /// How many attempts have failed since the change was queued or last retried.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// When the change was queued.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When it was last attempted, or null if it has not been since it was queued or retried.
    /// </summary>
    public DateTime? LastAttemptedAt { get; set; }

    /// <summary>
    /// When the current run of failures began. JIM parks a change whose failures have gone on for 24 hours.
    /// </summary>
    public DateTime? FirstFailedAt { get; set; }

    /// <summary>
    /// When the next attempt falls due, or null for a change due now or no longer attempted.
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>
    /// Whether the Exchange delivery lane would attempt the change now.
    /// </summary>
    public bool Due { get; set; }

    /// <summary>
    /// Whether it can be retried: it is parked, or retrying after an attempt. A change not yet attempted is already due,
    /// one being delivered is getting its attempt, and a refused one waits on its declared state changing.
    /// </summary>
    public bool CanRetry { get; set; }

    /// <summary>Builds the response from a queue row, reading "due" as at <paramref name="asOf"/>.</summary>
    public static PendingExchangeChangeResponse FromHeader(PendingExchangeChangeHeader header, DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(header);

        return new PendingExchangeChangeResponse
        {
            Id = header.Id,
            ActivityId = header.ActivityId,
            ConnectedSystemId = header.ConnectedSystemId,
            ConnectedSystemName = header.ConnectedSystemName,
            ConnectedSystemObjectId = header.ConnectedSystemObjectId,
            MetaverseObjectId = header.MetaverseObjectId,
            MetaverseObjectDisplayName = header.MetaverseObjectDisplayName,
            MetaverseObjectTypePluralName = header.MetaverseObjectTypePluralName,
            RecipientType = header.RecipientType,
            State = header.State.ToQueueState(),
            FailureKind = header.FailureKind,
            FailureMessage = header.FailureMessage,
            AttemptCount = header.AttemptCount,
            CreatedAt = header.CreatedAt,
            LastAttemptedAt = header.LastAttemptedAt,
            FirstFailedAt = header.FirstFailedAt,
            NextRetryAt = header.NextRetryAt,
            Due = header.Status == PendingExchangeChangeStatus.Pending && (header.NextRetryAt == null || header.NextRetryAt <= asOf),
            CanRetry = header.CanRetry
        };
    }
}

/// <summary>
/// Which queued Exchange changes a retry applies to. Criteria combine; a request naming none must set
/// <c>applyToAllChanges</c>.
/// </summary>
public class ExchangeQueueActionRequest : IValidatableObject
{
    /// <summary>
    /// The most changes one request may name.
    /// </summary>
    /// <remarks>A cap because the identifiers become one list in the database; a caller with more wants a filter.</remarks>
    public const int MaximumIds = 1000;

    /// <summary>
    /// Restrict to one Connected System, or omit for every system.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// Restrict to changes in one state, or omit for every state.
    /// </summary>
    public ExchangeQueueState? State { get; set; }

    /// <summary>
    /// Restrict to changes whose last attempt failed this way, or omit for every kind.
    /// </summary>
    public ExchangeChangeFailureKind? FailureKind { get; set; }

    /// <summary>
    /// Restrict to one Metaverse Object's changes, or omit for every Metaverse Object.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// Restrict to one account's change, or omit for every account.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// Restrict to these changes. Combines with the other criteria rather than replacing them.
    /// </summary>
    public IReadOnlyCollection<Guid>? Ids { get; set; }

    /// <summary>
    /// Free-text search over the Metaverse Object's display name and the Connected System's name, applied exactly as the
    /// list applies it.
    /// </summary>
    [StringLength(200, ErrorMessage = "Search text must not exceed 200 characters.")]
    public string? SearchText { get; set; }

    /// <summary>
    /// Confirms that a request naming no criteria at all is meant to act on the entire queue, so an empty body cannot
    /// retry everything by mistake.
    /// </summary>
    public bool ApplyToAllChanges { get; set; }

    private bool HasCriteria =>
        ConnectedSystemId.HasValue ||
        State.HasValue ||
        FailureKind.HasValue ||
        MetaverseObjectId.HasValue ||
        ConnectedSystemObjectId.HasValue ||
        Ids is { Count: > 0 } ||
        !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Translates the request into the filter the application layer takes.</summary>
    public PendingExchangeChangeFilter ToFilter() => new()
    {
        ConnectedSystemId = ConnectedSystemId,
        State = State?.ToChangeState(),
        FailureKind = FailureKind,
        MetaverseObjectId = MetaverseObjectId,
        ConnectedSystemObjectId = ConnectedSystemObjectId,
        Ids = Ids,
        SearchText = SearchText
    };

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Ids is { Count: > MaximumIds })
        {
            yield return new ValidationResult(
                $"No more than {MaximumIds} Exchange changes may be named in one request; narrow the request with a filter instead.",
                [nameof(Ids)]);
        }

        if (!HasCriteria && !ApplyToAllChanges)
        {
            yield return new ValidationResult(
                "This request names no Exchange changes. Supply at least one criterion, or set applyToAllChanges to act on the entire queue.",
                [nameof(ApplyToAllChanges)]);
        }
    }
}

/// <summary>
/// What a retry did.
/// </summary>
public class ExchangeQueueActionResponse
{
    /// <summary>
    /// How many queued Exchange changes were made due again. Lower than the number named is not an error: a change
    /// delivered, claimed or already retried since the caller read the queue no longer matches.
    /// </summary>
    public int AffectedCount { get; set; }
}
