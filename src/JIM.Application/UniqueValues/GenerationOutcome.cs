// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Application.UniqueValues;

/// <summary>
/// What <see cref="UniqueValueGenerationServer.ResolveAsync"/> decided for one <see cref="GenerationRequest"/>
/// (Unique Value Generation, #242). Exactly one request in, exactly one outcome out, in the same order, so a
/// caller can zip requests and outcomes by index without needing <see cref="GenerationRequest.CallerState"/>
/// for that alone (though it remains available for the caller's own correlation).
/// </summary>
/// <param name="Request">The request this outcome resolves.</param>
/// <param name="Kind">Which resolution step produced this outcome.</param>
/// <param name="Value">
/// The text value (numbers rendered with <see cref="System.Globalization.CultureInfo.InvariantCulture"/>);
/// null on every failure kind (<see cref="GenerationOutcomeKind.Exhausted"/>,
/// <see cref="GenerationOutcomeKind.WidthExceeded"/>, <see cref="GenerationOutcomeKind.NoBaseValue"/>,
/// <see cref="GenerationOutcomeKind.AdoptionConflict"/>) and for <see cref="GenerationOutcomeKind.Waiting"/>.
/// </param>
/// <param name="NumericValue">
/// Set alongside <paramref name="Value"/> for a <see cref="Models.Core.AttributeDataType.Number"/> or
/// <see cref="Models.Core.AttributeDataType.LongNumber"/> target; null for a Text target and for every
/// failure kind.
/// </param>
/// <param name="Assignment">
/// Unsaved for <see cref="GenerationOutcomeKind.Generated"/> and <see cref="GenerationOutcomeKind.Adopted"/>
/// (the caller persists it, once the object itself is persisted, through
/// <see cref="UniqueValueGenerationServer.CommitAssignmentsAsync"/>); the existing, already-persisted
/// assignment for <see cref="GenerationOutcomeKind.Sticky"/>; null on every failure kind and for
/// <see cref="GenerationOutcomeKind.Waiting"/>.
/// </param>
/// <param name="FailureMessage">
/// For a failure kind: one sentence naming the attribute, the last candidate tried (where one was), and the
/// gate or reason that rejected it. Null otherwise.
/// </param>
public sealed record GenerationOutcome(
    GenerationRequest Request,
    GenerationOutcomeKind Kind,
    string? Value,
    long? NumericValue,
    GeneratedValueAssignment? Assignment,
    string? FailureMessage);
