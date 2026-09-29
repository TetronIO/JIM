// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// A non-blocking save-time warning about one Metaverse-Derived Attribute Flow (#1750).
/// </summary>
/// <param name="Mapping">The mapping the warning is about.</param>
/// <param name="Message">The administrator-facing message.</param>
public sealed record DerivedFlowWarning(SyncRuleMapping Mapping, string Message);
