// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Logic;

/// <summary>
/// An export flow writing a Connector-defined Attribute alongside one writing the attribute it is realised through, in
/// the same Synchronisation Rule and from a different source (#2756). Saving the rule warns about it.
/// </summary>
/// <param name="ConnectorDefinedFlow">The flow writing the Connector-defined Attribute.</param>
/// <param name="ConnectorDefinedAttribute">The Connector-defined Attribute.</param>
/// <param name="SourceFlow">The flow writing the attribute it declares as its source.</param>
/// <param name="SourceAttribute">That source attribute.</param>
public sealed record ConnectorDefinedAttributeOverlap(
    SyncRuleMapping ConnectorDefinedFlow,
    ConnectedSystemObjectTypeAttribute ConnectorDefinedAttribute,
    SyncRuleMapping SourceFlow,
    ConnectedSystemObjectTypeAttribute SourceAttribute);
