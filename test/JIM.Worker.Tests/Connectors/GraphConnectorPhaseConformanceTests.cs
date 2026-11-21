// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Graph;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Runs the phase declaration conformance suite (#454) against the Microsoft Graph Connector.
/// </summary>
[TestFixture]
public class GraphConnectorPhaseConformanceTests : ConnectorPhaseConformanceTests
{
    protected override IConnectorPhases CreateConnector() => new GraphConnector();

    protected override ConnectedSystem CreateConnectedSystem() => new() { Name = "Entra ID" };
}
