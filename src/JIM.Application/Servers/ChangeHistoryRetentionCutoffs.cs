// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Servers;

/// <summary>
/// The retention cutoffs one history cleanup pass works to: the instant before which each class of record is
/// eligible for removal, plus the per-type batch cap.
/// <para>
/// A parameter object rather than a positional argument list because every cutoff is a <see cref="DateTime"/>,
/// so a transposed pair compiles silently and then deletes the wrong history under the wrong period. There were
/// four of them before Password Synchronisation added a fifth (#1119), and four was already too many to read at
/// a call site.
/// </para>
/// <para>
/// Build one with <see cref="ChangeHistoryServer.GetRetentionCutoffsAsync"/>, which reads each period from its
/// Service Setting, so the scheduled step and the API endpoint cannot drift apart in how they derive them.
/// </para>
/// </summary>
public sealed class ChangeHistoryRetentionCutoffs
{
    /// <summary>
    /// Governs Connected System Object changes, Metaverse Object changes, configuration change previews, and
    /// every Activity not claimed by one of the classes below.
    /// </summary>
    public required DateTime General { get; init; }

    /// <summary>
    /// Governs Activities carrying a versioned configuration snapshot; typically far longer than
    /// <see cref="General"/>, because these ARE the configuration change history.
    /// </summary>
    public required DateTime ConfigurationChange { get; init; }

    /// <summary>
    /// Governs Authentication Activities: the security audit trail.
    /// </summary>
    public required DateTime SecurityEvent { get; init; }

    /// <summary>
    /// Governs initial-password work records that reached a terminal state (#1121). Not an audit trail; the
    /// account's Activity is, and outlives it.
    /// </summary>
    public required DateTime InitialPassword { get; init; }

    /// <summary>
    /// Governs Password Synchronisation Activities and terminal Pending Password Changes (#1119).
    /// </summary>
    public required DateTime PasswordEvent { get; init; }

    /// <summary>
    /// The most records of any one type a single pass may remove. Bounds each statement so a deployment with a
    /// large backlog drains over several passes rather than in one long transaction.
    /// </summary>
    public required int MaxRecordsPerType { get; init; }
}
