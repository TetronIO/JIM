// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Web.Causality;

/// <summary>
/// The parsed content of a <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned"/>
/// or <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAdopted"/> outcome's
/// DetailMessage (Unique Value Generation, #242), which the worker writes as "{attributeName}: {value}"
/// (<c>SyncTaskProcessorBase.ResolvePendingGeneratedValuesAsync</c>). Both fields are null when the message is
/// missing or does not match that shape, so a caller can fall back to a generic sentence rather than guessing.
/// </summary>
/// <param name="AttributeName">The generated attribute's display name.</param>
/// <param name="Value">The value JIM generated or adopted.</param>
public sealed record GeneratedValueDetail(string? AttributeName, string? Value);
