// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;

namespace JIM.Web.Causality;

/// <summary>
/// One card in a Lineage object's After this run section (#1811): what a later run did to the object, or where the
/// branch through it stopped. Everything is precomputed by <see cref="CausalityLineageModelBuilder"/>, so the view
/// renders it without deriving anything.
/// </summary>
public sealed class CausalityLineageConsequence
{
    /// <summary>
    /// What the card says.
    /// </summary>
    public CausalityLineageConsequenceKind Kind { get; init; }

    /// <summary>
    /// The recorded event an outcome or confirmation card shows, built from the cohort's representative item exactly as
    /// that item's own page would build it, so its links, chips and attribute detail are the same. Null for a card
    /// about where a branch stopped.
    /// </summary>
    public CausalityEvent? Event { get; init; }

    /// <summary>
    /// The card's title where it speaks for more than the event's own label (a removal across a cohort of Groups, a
    /// waiting card); null to use the event's label.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// The title the card shows.
    /// </summary>
    public string DisplayTitle => Title ?? Event?.Label ?? string.Empty;

    /// <summary>
    /// The line beneath the title: why an outcome happened, or what a waiting or settled branch is waiting on.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// The kind of run that recorded the outcome ("Full Synchronisation", "Export").
    /// </summary>
    public string? RunLabel { get; init; }

    /// <summary>
    /// When the run that recorded the outcome ran, in UTC; null for a card about where a branch stopped.
    /// </summary>
    public DateTime? Occurred { get; init; }

    /// <summary>
    /// The item that recorded the outcome, where the card speaks for one; a cohort's members link their own.
    /// </summary>
    public string? ActivityItemHref { get; init; }

    /// <summary>
    /// The cohort the card speaks for, where it speaks for more than one object: its exact count, its first page of
    /// members and the key that pages the rest.
    /// </summary>
    public CausalConsequenceCohort? Cohort { get; init; }

    /// <summary>
    /// What <see cref="Cohort"/>'s members are called, singular or plural by its count ("Groups").
    /// </summary>
    public string? MembersNoun { get; init; }

    /// <summary>
    /// The configured setting that decided the outcome, where one did ("Deletion Rule", "Deprovisioning Action"): what an
    /// administrator would change to get a different result. Null where no setting chose it.
    /// </summary>
    public string? SettingLabel { get; init; }

    /// <summary>
    /// What <see cref="SettingLabel"/> was set to when the run decided ("Delete", "When Authoritative Source Disconnected").
    /// </summary>
    public string? SettingValue { get; init; }

    /// <summary>
    /// What that setting meant for the outcome.
    /// </summary>
    public string? SettingText { get; init; }

    /// <summary>
    /// A link the card offers (a Pending Export, the queue it sits in).
    /// </summary>
    public string? LinkText { get; init; }

    /// <summary>
    /// Where <see cref="LinkText"/> goes.
    /// </summary>
    public string? LinkHref { get; init; }

    /// <summary>
    /// When a waiting branch is due to move on, in UTC: a retry, or the end of a grace period.
    /// </summary>
    public DateTime? DueAt { get; init; }

    /// <summary>
    /// What the summary band's After this run strip calls this card's kind of change, or null where it adds nothing to
    /// the strip (a confirmation, a branch going further than shown).
    /// </summary>
    public string? PillLabel { get; init; }

    /// <summary>
    /// The strip pill's tone.
    /// </summary>
    public CausalityTone PillTone { get; init; }

    /// <summary>
    /// How many objects the card speaks for, for the strip's counts.
    /// </summary>
    public int MemberCount { get; init; } = 1;
}
