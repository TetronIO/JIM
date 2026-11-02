// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Transactional;

/// <summary>
/// One Exchange change owed to one directory account (#2021): the Exchange queue.
/// <para>
/// After an export writes an account to Active Directory, the recipient that account should be in Exchange (a mailbox,
/// a mail user, a mail-enabled group and so on) is applied separately, by the Exchange delivery lane, because Exchange
/// is a second system with its own availability and its own failure modes, and an export must not wait on it or fail
/// because of it. This row is what the export leaves behind for the lane. It is modelled on the Password
/// Synchronisation queue (<see cref="PendingPasswordChange"/>) and kept physically separate from it, so the password
/// queue's secrecy invariants stay its own.
/// </para>
/// <para>
/// Three invariants shape it:
/// </para>
/// <para>
/// <b>The row holds the latest declared state for its account, never a replayable sequence.</b> A unique index on the
/// Connected System Object makes that a database guarantee: staging a different declared state supersedes the row in
/// place (<see cref="ExchangeChangeStagingDisposition.Superseded"/>), so Exchange is only ever asked for what the account
/// should be now, and staging the same change (the same declared state, refused or not alike) leaves the row as it is
/// (<see cref="ExchangeChangeStagingDisposition.Unchanged"/>).
/// </para>
/// <para>
/// <b>Success deletes the row.</b> The queue is work outstanding; the Activity (<see cref="ActivityId"/>) is the
/// history, and it outlives the row.
/// </para>
/// <para>
/// <b>No secret is ever stored here.</b> The credential JIM signs in to Exchange with belongs to the Connected System's
/// settings and is read at delivery; nothing on this row needs protecting beyond what the directory already shows.
/// </para>
/// </summary>
public class PendingExchangeChange
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The Connected System whose directory the account lives in, and whose Exchange settings the change is delivered
    /// with.
    /// <para>
    /// Denormalised rather than reached through the Connected System Object, as the Password Synchronisation queue's
    /// is: "what is outstanding on this system?" is asked on every delivery pass and every list page, and must not need
    /// a join. It also keeps the row answerable once its account has gone.
    /// </para>
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The directory account the change is for. The queue's coalescing key: one change per account.
    /// <para>
    /// Set to null by the database when the account is deleted, rather than taking the row with it: a change that
    /// stopped being owed must say so, and the delivery lane withdraws a row whose account has gone, recording why
    /// under the change's Activity. Staging always names an account.
    /// </para>
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The Metaverse Object the account is joined to, or null where it has none (or it has since been deleted, which
    /// sets this to null in the database). Recorded so an identity's outstanding Exchange work can be shown beside it.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// The Synchronisation Rule whose Attribute Flow declared this state, or null where the rule has since been
    /// deleted, which sets this to null in the database. The delivery lane withdraws a change whose rule has gone.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The recipient the account should be: one of None, Mailbox, SharedMailbox, RoomMailbox, EquipmentMailbox,
    /// MailUser, MailContact or MailEnabledGroup. None asks for the account to stop being a recipient.
    /// <para>
    /// Held as the text the Exchange Recipient Type attribute flowed, rather than an enumeration, so a value the
    /// delivery lane does not recognise is still recorded and reported exactly as it was declared.
    /// </para>
    /// </summary>
    public required string RecipientType { get; set; }

    /// <summary>
    /// The Exchange alias declared for the recipient, or null to leave it to Exchange.
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>
    /// The mailbox database declared for a mailbox, by name, or null to leave the choice to Exchange.
    /// </summary>
    public string? Database { get; set; }

    /// <summary>
    /// The primary SMTP address declared for the recipient, or null to let Email Address Policies decide it.
    /// </summary>
    public string? PrimarySmtpAddress { get; set; }

    /// <summary>
    /// The external address declared for a mail user or mail contact, or null where the recipient type has none.
    /// </summary>
    public string? ExternalAddress { get; set; }

    /// <summary>
    /// The domain controller the export wrote the account to, or null where the export did not say.
    /// <para>
    /// Exchange is told to use the same domain controller, so it finds the account the export has only just written
    /// rather than waiting for replication to carry it elsewhere. A host name, never a credential.
    /// </para>
    /// </summary>
    public string? DomainController { get; set; }

    public PendingExchangeChangeStatus Status { get; set; } = PendingExchangeChangeStatus.Pending;

    /// <summary>
    /// How the last attempt failed, or null before one has failed.
    /// </summary>
    public ExchangeChangeFailureKind? FailureKind { get; set; }

    /// <summary>
    /// Exchange's own words on the last failure, or, for a change JIM refused before asking Exchange
    /// (<see cref="PendingExchangeChangeStatus.Refused"/>), JIM's reason; null otherwise. Why it was refused is the most
    /// useful thing an administrator can be shown.
    /// </summary>
    public string? FailureMessage { get; set; }

    public int AttemptCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastAttemptedAt { get; set; }

    /// <summary>
    /// When the current run of failures began, or null while the change has not failed since it was last staged or
    /// retried. A change still failing 24 hours after this is parked.
    /// </summary>
    public DateTime? FirstFailedAt { get; set; }

    /// <summary>
    /// When the next delivery attempt falls due, or null for a change that is due now.
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>
    /// When the Exchange delivery lane claimed this change, or null while nobody holds it. A lease rather than a lock,
    /// as a password delivery's claim is (<see cref="PendingPasswordChange.ClaimLease"/>); set and cleared only in the
    /// database.
    /// </summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>
    /// The service instance holding the claim (host name plus a per-process id), or null while nobody does.
    /// </summary>
    [MaxLength(200)]
    public string? ClaimedBy { get; set; }

    /// <summary>
    /// The change's own Activity: a follow-up of the export item that wrote the account, under which every attempt,
    /// supersession and withdrawal is recorded. Re-pointed when a newer declared state supersedes the row, so the row
    /// always names the Activity for the state it is carrying.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// When an administrator cancelled this change, or null if nobody has.
    /// </summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>
    /// Who cancelled it, mirroring the Activity initiator fields, as the Password Synchronisation queue does. Both are
    /// nullable because the API-key path has no person behind it.
    /// </summary>
    public Guid? CancelledById { get; set; }

    /// <inheritdoc cref="CancelledById"/>
    public string? CancelledByName { get; set; }

    /// <summary>
    /// Whether <paramref name="other"/> declares exactly the state this row declares: the recipient type, alias,
    /// database, primary SMTP address and external address, compared ordinally, with null and empty treated as the
    /// same (both mean "nothing declared").
    /// <para>
    /// What decides whether a re-stage leaves the row as it is, whatever its status
    /// (<see cref="ExchangeChangeStagingDisposition.Unchanged"/>). The staging statement makes the same comparison in
    /// SQL; this is its in-memory twin.
    /// </para>
    /// </summary>
    public bool HasSameDeclaredStateAs(PendingExchangeChange other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return SameDeclaredValue(RecipientType, other.RecipientType)
               && SameDeclaredValue(Alias, other.Alias)
               && SameDeclaredValue(Database, other.Database)
               && SameDeclaredValue(PrimarySmtpAddress, other.PrimarySmtpAddress)
               && SameDeclaredValue(ExternalAddress, other.ExternalAddress);
    }

    /// <summary>
    /// Whether staging <paramref name="offered"/> over this row is the same change, so the row is left as it is
    /// (<see cref="ExchangeChangeStagingDisposition.Unchanged"/>): the same declared state
    /// (<see cref="HasSameDeclaredStateAs"/>), refused or not alike. A refusal is part of what the change is: the same
    /// declared state refused before and deliverable now (its cause gone, as a group made universal), or the reverse,
    /// is a different change, and supersedes the row rather than leaving it refused, or deliverable, for ever. The
    /// staging statement makes the same comparison in SQL; this is its in-memory twin.
    /// </summary>
    public bool IsSameChangeAs(PendingExchangeChange offered)
    {
        ArgumentNullException.ThrowIfNull(offered);

        return HasSameDeclaredStateAs(offered)
               && (Status == PendingExchangeChangeStatus.Refused) == (offered.Status == PendingExchangeChangeStatus.Refused);
    }

    private static bool SameDeclaredValue(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);

    public override string ToString() =>
        $"Exchange change {Id} ({RecipientType}) for Connected System Object {ConnectedSystemObjectId}: {Status}";
}
