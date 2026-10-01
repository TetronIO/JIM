// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// One entry of the Attribute Flow dialog's Insert attribute menu (#1750).
/// </summary>
/// <param name="Id">The attribute's id, unique within its group.</param>
/// <param name="Name">The attribute's name.</param>
/// <param name="Accessor">What choosing it inserts, for example <c>mv["Account Name"]</c>.</param>
/// <param name="DisabledReason">Why the entry is listed but cannot be chosen, as a short label beside its name; null
/// when it can.</param>
/// <param name="DisabledExplanation">The same reason as a sentence, for the entry's tooltip.</param>
public sealed record InsertableAttribute(int Id, string Name, string Accessor, string? DisabledReason, string? DisabledExplanation = null);
