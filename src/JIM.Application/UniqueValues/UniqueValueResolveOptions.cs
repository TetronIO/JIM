// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Threading;
using JIM.Models.Transactional;

namespace JIM.Application.UniqueValues;

/// <summary>
/// How <see cref="UniqueValueGenerationServer.ResolveAsync"/> should behave for a run, plus that run's shared
/// state (Unique Value Generation, #242). One instance is created once per synchronisation run (or once per
/// Sync Preview session) and reused for every <see cref="UniqueValueGenerationServer.ResolveAsync"/> and
/// <see cref="UniqueValueGenerationServer.CommitAssignmentsAsync"/> call the run makes: the worker calls
/// <c>ResolveAsync</c> once per object as it processes a page, not once per page, so the caches below are what
/// keep that from costing a sequence reservation and an assignment query per object. Every cache member is
/// populated by the server, never by the caller; a caller only ever sets the public, caller-facing properties.
/// <para>
/// The caches are thread-safe (<see cref="ConcurrentDictionary{TKey,TValue}"/> and
/// <see cref="ConcurrentBag{T}"/> throughout) because release 4's parallel export batches, and any other
/// caller that resolves concurrently within one run, share a single instance.
/// </para>
/// </summary>
public sealed class UniqueValueResolveOptions
{
    /// <summary>
    /// Sync Preview: when true, <see cref="UniqueValueGenerationServer.ResolveAsync"/> checks only the local
    /// gates (no probe; release 3 is out of scope here regardless), performs no repository writes of any kind
    /// (in particular, no sequence block is reserved; <see cref="SequenceAllocator"/> reads the sequence and
    /// simulates the block locally instead), and releases whatever it claims in <see cref="Reservations"/>
    /// before each resolve call returns, using a throwaway owner id rather than <see cref="ReservationOwnerId"/>.
    /// A dry run never has anything to commit, so <see cref="UniqueValueGenerationServer.CommitAssignmentsAsync"/>
    /// is never called for one.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// The process-wide reservation set candidates are checked and claimed against. The worker holds one
    /// instance for its whole lifetime; a test typically constructs a fresh one per test.
    /// </summary>
    public required UniqueValueReservationSet Reservations { get; init; }

    /// <summary>
    /// The id under which this run's claims are held in <see cref="Reservations"/>: normally the Run Profile
    /// Execution's id. Every <see cref="UniqueValueGenerationServer.ResolveAsync"/> call for the same run must
    /// pass the same id, or claims made by one call would not be recognised as this run's own by the next.
    /// Ignored by a dry run's own reservation claims (see <see cref="DryRun"/>), but still required: a dry run
    /// still needs an id to hand <see cref="UniqueValueReservationSet.ReleaseAll"/> if a caller chooses to
    /// release under it for its own bookkeeping.
    /// </summary>
    public required Guid ReservationOwnerId { get; init; }

    /// <summary>
    /// How many numbers <see cref="SequenceAllocator"/> reserves in one block when an attribute's cached block
    /// runs out (plan "The service": "reserves a block per page with an atomic increment"). A higher value
    /// means fewer <c>ReserveGeneratedValueSequenceBlockAsync</c> round trips across a run at the cost of a
    /// larger gap left behind by numbers the run never issues (plan decision 3: expected and undocumented
    /// nowhere else because it is already covered there). Defaults to 100, which comfortably covers a typical
    /// worker page without over-reserving on a small run.
    /// </summary>
    public int SequenceBlockSize { get; init; } = 100;

    /// <summary>
    /// Per attribute (keyed the same way as <see cref="GeneratedValueSequence"/>: exactly one of the tuple's
    /// two ids set), the numbers reserved in the current block that have not yet been drawn. Read and refilled
    /// by <see cref="SequenceAllocator"/> only.
    /// </summary>
    internal ConcurrentDictionary<(int? MetaverseAttributeId, int? ConnectedSystemObjectTypeAttributeId), ConcurrentQueue<long>> SequenceBlocks { get; } = new();

    /// <summary>
    /// One refill at a time per attribute: guards <see cref="SequenceBlocks"/> so two concurrent callers
    /// emptying the same attribute's block do not both reserve a fresh one. <see cref="SequenceAllocator"/>
    /// only.
    /// </summary>
    internal ConcurrentDictionary<(int? MetaverseAttributeId, int? ConnectedSystemObjectTypeAttributeId), SemaphoreSlim> SequenceRefillGates { get; } = new();

    /// <summary>
    /// A dry run's simulated counter position per attribute, so a second dry-run block for the same attribute
    /// continues from where the first left off without re-reading the real counter or the attribute's highest
    /// existing value a second time. <see cref="SequenceAllocator"/> only; never populated outside
    /// <see cref="DryRun"/>.
    /// </summary>
    internal ConcurrentDictionary<(int? MetaverseAttributeId, int? ConnectedSystemObjectTypeAttributeId), long> SimulatedSequenceNext { get; } = new();

    /// <summary>
    /// Every live assignment already known for a Metaverse Object (import mode), across all of its generated
    /// attributes: populated by <see cref="UniqueValueGenerationServer.PrefetchAssignmentsAsync"/> ahead of a
    /// page, and by <see cref="UniqueValueGenerationServer.ResolveAsync"/> and
    /// <see cref="UniqueValueGenerationServer.CommitAssignmentsAsync"/> as they discover or create assignments,
    /// so a later call in the same run sees them without a query. Presence of a key means "known" (the bag may
    /// be empty); absence means the object's assignments have not been looked up yet this run.
    /// </summary>
    internal ConcurrentDictionary<Guid, ConcurrentBag<GeneratedValueAssignment>> KnownMetaverseAssignments { get; } = new();

    /// <summary>
    /// The export-mode counterpart of <see cref="KnownMetaverseAssignments"/>, keyed on Connected System Object.
    /// </summary>
    internal ConcurrentDictionary<Guid, ConcurrentBag<GeneratedValueAssignment>> KnownConnectedSystemAssignments { get; } = new();
}
