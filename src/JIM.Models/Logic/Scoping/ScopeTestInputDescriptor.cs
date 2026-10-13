// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Logic.Scoping;

/// <summary>
/// An attribute a Synchronisation Rule's scoping criteria compare, described so a surface can offer the right editor
/// for its sample values (#2055).
/// </summary>
public sealed record ScopeTestInputDescriptor(string AttributeName, AttributeDataType Type, AttributePlurality Plurality);
