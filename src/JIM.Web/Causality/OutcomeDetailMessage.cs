// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The parsed content of a sync outcome's DetailMessage, which is an overloaded channel: Provisioned
/// and PendingExportCreated outcomes store "csId" or "csId|csoTypeName" for hyperlinking, while other
/// outcome types store plain contextual text (e.g. deletion reasoning).
/// </summary>
/// <param name="ConnectedSystemId">The Connected System id when the message carries the id channel.</param>
/// <param name="CsoTypeName">The CSO type name when the id channel includes one (e.g. "4|person").</param>
/// <param name="PlainMessage">The message verbatim when it is not the id channel.</param>
public sealed record OutcomeDetailMessage(int? ConnectedSystemId, string? CsoTypeName, string? PlainMessage);
