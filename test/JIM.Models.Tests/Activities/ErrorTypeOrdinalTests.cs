// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// <see cref="ActivityRunProfileExecutionItemErrorType"/> is persisted by ordinal on Run Profile Execution Items,
/// and those rows are the audit record of why an object failed. Inserting a value in the middle, or reordering two,
/// silently re-labels every historical error already in a customer's database. The enum is therefore append-only,
/// and this pins every existing ordinal so a reorder fails here rather than in production.
///
/// Adding a value is expected; add it to the end, and add its ordinal to the map below in the same change.
/// </summary>
[TestFixture]
public class ErrorTypeOrdinalTests
{
    /// <summary>
    /// Every value and the ordinal it is stored as. Do not edit an existing entry; append.
    /// </summary>
    private static readonly Dictionary<ActivityRunProfileExecutionItemErrorType, int> ExpectedOrdinals = new()
    {
        [ActivityRunProfileExecutionItemErrorType.NotSet] = 0,
        [ActivityRunProfileExecutionItemErrorType.AmbiguousMatch] = 1,
        [ActivityRunProfileExecutionItemErrorType.CouldNotMatchObjectType] = 2,
        [ActivityRunProfileExecutionItemErrorType.CouldNotJoinDueToExistingJoin] = 3,
        [ActivityRunProfileExecutionItemErrorType.DuplicateImportedAttributes] = 4,
        [ActivityRunProfileExecutionItemErrorType.MissingExternalIdAttributeValue] = 5,
        [ActivityRunProfileExecutionItemErrorType.UnexpectedAttribute] = 6,
        [ActivityRunProfileExecutionItemErrorType.UnresolvedReference] = 7,
        [ActivityRunProfileExecutionItemErrorType.UnsupportedExternalIdAttributeType] = 8,
        [ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed] = 9,
        [ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed] = 10,
        [ActivityRunProfileExecutionItemErrorType.CsoCreationFailed] = 11,
        [ActivityRunProfileExecutionItemErrorType.DuplicateObject] = 12,
        [ActivityRunProfileExecutionItemErrorType.InvalidGeneratedExternalId] = 13,
        [ActivityRunProfileExecutionItemErrorType.UnhandledError] = 14,
        [ActivityRunProfileExecutionItemErrorType.ExpressionEvaluationError] = 15,
        [ActivityRunProfileExecutionItemErrorType.ExpressionMissingInput] = 16,
        [ActivityRunProfileExecutionItemErrorType.MultiValuedToSingleValued] = 17,
        [ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport] = 18,
        [ActivityRunProfileExecutionItemErrorType.ImportHashVerificationFailed] = 19,
        [ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError] = 20,
        [ActivityRunProfileExecutionItemErrorType.ConnectorConfigurationError] = 21,
        [ActivityRunProfileExecutionItemErrorType.CouldNotExportDueToExistingConnectedSystemObject] = 22,
        [ActivityRunProfileExecutionItemErrorType.ClassMembershipRequirementsNotMet] = 23,

        // Unique Value Generation (#242): generation, width and Collision Remediation failures.
        [ActivityRunProfileExecutionItemErrorType.GeneratedValueExhausted] = 24,
        [ActivityRunProfileExecutionItemErrorType.GeneratedValueWidthExceeded] = 25,
        [ActivityRunProfileExecutionItemErrorType.GeneratedValueCollisionUnresolved] = 26
    };

    [Test]
    public void ErrorType_EveryValue_KeepsItsPersistedOrdinal()
    {
        var drifted = ExpectedOrdinals
            .Where(pair => (int)pair.Key != pair.Value)
            .Select(pair => $"{pair.Key} is {(int)pair.Key}, expected {pair.Value}")
            .ToList();

        Assert.That(drifted, Is.Empty,
            "an error type's ordinal changed, which silently re-labels every error row already persisted: " +
            string.Join("; ", drifted));
    }

    [Test]
    public void ErrorType_EveryDeclaredValue_IsPinned()
    {
        var unpinned = Enum.GetValues<ActivityRunProfileExecutionItemErrorType>()
            .Where(value => !ExpectedOrdinals.ContainsKey(value))
            .ToList();

        Assert.That(unpinned, Is.Empty,
            "new error type(s) not pinned in this test: " + string.Join(", ", unpinned) +
            ". Append them here with the ordinal they were assigned, so a later reorder is caught.");
    }
}
