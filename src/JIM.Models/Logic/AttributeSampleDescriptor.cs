// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Logic;

/// <summary>
/// An attribute a configuration tester takes a sample for (#2055), described so a surface can offer the right editor
/// for its values.
/// </summary>
public sealed record AttributeSampleDescriptor(string AttributeName, AttributeDataType Type, AttributePlurality Plurality);
