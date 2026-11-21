// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Graph;

/// <summary>A value Graph returned that could not be converted to its attribute's type, and why.</summary>
internal sealed record GraphUnreadableValue(string AttributeName, string? Value, string Reason);
