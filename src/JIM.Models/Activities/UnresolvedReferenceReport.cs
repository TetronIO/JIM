// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// What a Full Import's end-of-run reference sweep found still unresolved on one object, written onto the object's
/// execution item, which was saved with its unit before the sweep ran (#2521).
/// </summary>
/// <param name="ExecutionItemId">The item to update.</param>
/// <param name="UnresolvedReferenceCount">How many of the object's reference values are still unresolved or ambiguous.</param>
/// <param name="ErrorType">The error the item now records, or null to leave its error as written: set only where the
/// Connected System reports unresolved references as errors and the item recorded no error of its own when it was
/// written.</param>
/// <param name="ErrorMessage">The error's message, written with <paramref name="ErrorType"/>.</param>
/// <param name="ErrorMessageDetailJson">The same error as a stored message the portal shows in the viewer's language (#3039),
/// written with it; null where the error is English only.</param>
public sealed record UnresolvedReferenceReport(
    Guid ExecutionItemId,
    int UnresolvedReferenceCount,
    ActivityRunProfileExecutionItemErrorType? ErrorType,
    string? ErrorMessage,
    string? ErrorMessageDetailJson = null);
