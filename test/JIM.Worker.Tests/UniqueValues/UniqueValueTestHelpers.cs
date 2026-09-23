// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// Shared construction helpers for Unique Value Generation (#242) tests: a fresh
/// <see cref="SyncRuleMappingGeneration"/> per call (with its own id, so
/// <c>GetGeneratedValueAssignmentsForGenerationAsync</c> scoping behaves as it would in production), and
/// <see cref="GenerationRequest"/> builders for import and export mode with sensible defaults a test overrides
/// only what it cares about.
/// </summary>
internal static class UniqueValueTestHelpers
{
    private static int _nextGenerationId = 1;
    private static int _nextAttributeId = 1000;

    /// <summary>
    /// A fresh, distinct attribute id, so tests that do not care about a specific id never collide with one
    /// another by accident.
    /// </summary>
    public static int NextAttributeId() => Interlocked.Increment(ref _nextAttributeId);

    public static SyncRuleMappingGeneration Generation(
        GeneratedValueTokenKind tokenKind = GeneratedValueTokenKind.OnlyIfTaken,
        GeneratedValueSuffixStyle suffixStyle = GeneratedValueSuffixStyle.Number,
        int suffixStart = 1,
        long sequenceStart = 1,
        int sequenceIncrement = 1,
        int? fixedWidth = null,
        GeneratedValueWidthOverflowBehaviour onWidthExceeded = GeneratedValueWidthOverflowBehaviour.StopAndReport,
        GeneratedValueRandomFormat randomFormat = GeneratedValueRandomFormat.Guid,
        int? randomLength = null,
        string? separator = null,
        int attemptLimit = 1000) => new()
    {
        Id = Interlocked.Increment(ref _nextGenerationId),
        TokenKind = tokenKind,
        SuffixStyle = suffixStyle,
        SuffixStart = suffixStart,
        SequenceStart = sequenceStart,
        SequenceIncrement = sequenceIncrement,
        FixedWidth = fixedWidth,
        OnWidthExceeded = onWidthExceeded,
        RandomFormat = randomFormat,
        RandomLength = randomLength,
        Separator = separator,
        AttemptLimit = attemptLimit
    };

    public static GenerationRequest ImportRequest(
        SyncRuleMappingGeneration generation,
        int metaverseAttributeId,
        Guid? metaverseObjectId = null,
        string? baseValue = "joe.bloggs",
        string? adoptableValue = null,
        AttributeDataType targetType = AttributeDataType.Text,
        string attributeName = "Account Name",
        IReadOnlyCollection<int>? connectorSpaceAttributeIds = null,
        bool stickyOnly = false) => new()
    {
        Mode = GeneratedValueMode.Import,
        MetaverseObjectId = metaverseObjectId,
        MetaverseAttributeId = metaverseAttributeId,
        Generation = generation,
        TargetType = targetType,
        AttributeName = attributeName,
        BaseValue = baseValue,
        AdoptableValue = adoptableValue,
        ConnectorSpaceAttributeIds = connectorSpaceAttributeIds ?? [],
        StickyOnly = stickyOnly
    };

    public static GenerationRequest ExportRequest(
        SyncRuleMappingGeneration generation,
        int connectedSystemObjectTypeAttributeId,
        Guid? connectedSystemObjectId = null,
        string? baseValue = "joe.bloggs",
        string? adoptableValue = null,
        AttributeDataType targetType = AttributeDataType.Text,
        string attributeName = "Login Name",
        bool stickyOnly = false) => new()
    {
        Mode = GeneratedValueMode.Export,
        ConnectedSystemObjectId = connectedSystemObjectId,
        ConnectedSystemObjectTypeAttributeId = connectedSystemObjectTypeAttributeId,
        Generation = generation,
        TargetType = targetType,
        AttributeName = attributeName,
        BaseValue = baseValue,
        AdoptableValue = adoptableValue,
        StickyOnly = stickyOnly
    };

    public static UniqueValueResolveOptions Options(UniqueValueReservationSet? reservations = null, Guid? ownerId = null, bool dryRun = false, int sequenceBlockSize = 100) => new()
    {
        DryRun = dryRun,
        Reservations = reservations ?? new UniqueValueReservationSet(),
        ReservationOwnerId = ownerId ?? Guid.NewGuid(),
        SequenceBlockSize = sequenceBlockSize
    };
}
