// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Models.Exchange;

/// <summary>
/// One connector-defined change an account's Pending Export holds for Exchange (#2021): which Exchange attribute, how
/// it changes, and to what.
/// </summary>
public sealed record ExchangeHeldChange(string AttributeName, PendingExportAttributeChangeType ChangeType, string? StringValue);
