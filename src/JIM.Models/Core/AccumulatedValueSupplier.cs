// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// A Synchronisation Rule that still supplies a value an Accumulate attribute holds (#3059): the reason a removal of the
/// value by hand is refused, since the rule's next synchronisation would add it back.
/// </summary>
/// <param name="Value">The value, as text.</param>
/// <param name="SyncRuleId">The Synchronisation Rule supplying it.</param>
/// <param name="SyncRuleName">The Synchronisation Rule's name.</param>
/// <param name="ConnectedSystemId">The Connected System the rule reads from.</param>
/// <param name="ConnectedSystemName">The Connected System's name.</param>
public sealed record AccumulatedValueSupplier(string Value, int SyncRuleId, string SyncRuleName, int ConnectedSystemId, string ConnectedSystemName);
