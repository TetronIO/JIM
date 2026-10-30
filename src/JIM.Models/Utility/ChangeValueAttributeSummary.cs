// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Utility;

/// <summary>
/// How many of one attribute's value rows on one change record are Sets, Adds and Removes, read as
/// <see cref="ChangeValueRowQuery"/> with <see cref="ChangeValueRowQuery.Normalise"/> reads them (#2498). A change to a
/// large group carries one value change per member, so its per-attribute counts come from the database rather than
/// from the values loaded with it.
/// </summary>
/// <param name="AttributeName">The attribute, as the change recorded its name.</param>
/// <param name="SetCount">How many rows are Sets, replacements included.</param>
/// <param name="AddCount">How many rows are Adds.</param>
/// <param name="RemoveCount">How many rows are Removes.</param>
/// <param name="ReplacementCount">How many of the Sets replaced a value, so carry the value they replaced.</param>
public sealed record ChangeValueAttributeSummary(string AttributeName, int SetCount, int AddCount, int RemoveCount, int ReplacementCount = 0);
