// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Runs the phase declaration conformance suite (#454) against the File Connector.
/// </summary>
[TestFixture]
public class FileConnectorPhaseConformanceTests : ConnectorPhaseConformanceTests
{
    protected override IConnectorPhases CreateConnector() => new FileConnector();

    protected override ConnectedSystem CreateConnectedSystem() => new() { Name = "HR File" };

    [Test]
    public void GetPhases_ForAnExport_DeclaresTheFileWorkAsSteps()
    {
        var phases = CreateConnector().GetPhases(CreateConnectedSystem(),
            new ConnectedSystemRunProfile { Name = "Export", RunType = ConnectedSystemRunType.Export });

        Assert.That(phases.Select(p => p.Key), Is.EqualTo(new[]
        {
            FileConnectorPhases.LoadExistingFile,
            FileConnectorPhases.Merge,
            FileConnectorPhases.Write
        }));
    }

    [Test]
    public void GetPhases_ForAnImport_DeclaresOneStepBecauseItIsOnePassOverTheFile()
    {
        var phases = CreateConnector().GetPhases(CreateConnectedSystem(),
            new ConnectedSystemRunProfile { Name = "Full Import", RunType = ConnectedSystemRunType.FullImport });

        Assert.That(phases.Select(p => p.Key), Is.EqualTo(new[] { FileConnectorPhases.Read }),
            "Reading and parsing happen in one pass, so declaring more steps than that would be a fiction");
    }
}

/// <summary>
/// Runs the phase declaration conformance suite (#454) against the LDAP Connector.
/// </summary>
[TestFixture]
public class LdapConnectorPhaseConformanceTests : ConnectorPhaseConformanceTests
{
    protected override IConnectorPhases CreateConnector() => new LdapConnector();

    protected override ConnectedSystem CreateConnectedSystem() => new() { Name = "Corporate Directory" };

    [Test]
    public void GetPhases_ForADeltaImport_DeclaresMoreStepsThanAFullImport()
    {
        // A Delta Import asks the directory what changed before fetching anything, and asks
        // separately for deleted objects, so its journey is genuinely longer.
        var connector = CreateConnector();
        var connectedSystem = CreateConnectedSystem();

        var full = connector.GetPhases(connectedSystem, new ConnectedSystemRunProfile { Name = "Full Import", RunType = ConnectedSystemRunType.FullImport });
        var delta = connector.GetPhases(connectedSystem, new ConnectedSystemRunProfile { Name = "Delta Import", RunType = ConnectedSystemRunType.DeltaImport });

        Assert.That(full.Select(p => p.Key), Is.EqualTo(new[] { LdapConnectorPhases.RootDse, LdapConnectorPhases.Fetch }));
        Assert.That(delta.Select(p => p.Key), Is.EqualTo(new[]
        {
            LdapConnectorPhases.RootDse,
            LdapConnectorPhases.QueryChanges,
            LdapConnectorPhases.Fetch,
            LdapConnectorPhases.QueryDeletions
        }));
    }

    [Test]
    public void GetPhases_ForAnExport_DeclaresNothingBecausePerItemCountsSayMore()
    {
        var phases = CreateConnector().GetPhases(CreateConnectedSystem(),
            new ConnectedSystemRunProfile { Name = "Export", RunType = ConnectedSystemRunType.Export });

        Assert.That(phases, Is.Empty);
    }
}
