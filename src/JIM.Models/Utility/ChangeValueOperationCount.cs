// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;

namespace JIM.Models.Utility;

/// <summary>
/// How many of one change record's value rows are of one operation, counted as <see cref="ChangeValueRowQuery"/>
/// with <see cref="ChangeValueRowQuery.Normalise"/> reads them (#2498).
/// </summary>
public sealed record ChangeValueOperationCount(Guid ChangeId, ChangeValueOperation Operation, int Count);
