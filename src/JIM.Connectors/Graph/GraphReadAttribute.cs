// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Connectors.Graph;

/// <summary>
/// An attribute to read from a Graph object, and where its values are: the property, and for a value inside a complex
/// property (<c>onPremisesExtensionAttributes</c>), the member.
/// </summary>
internal sealed record GraphReadAttribute(string Name, AttributeDataType Type, AttributePlurality Plurality, string Property, string? Member = null);
