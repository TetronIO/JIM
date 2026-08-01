// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers what a Connector declares about itself and how those declarations reach its Connector Definition
/// (#1122 adds the schema standard alongside the existing capability flags). Both the create and the
/// startup-reconcile paths in <see cref="SeedingServer"/> apply the same declarations, so they share one
/// method; these tests guard that method, which is where a newly-added declaration would otherwise be
/// forgotten on one path and silently never reach existing deployments.
/// </summary>
[TestFixture]
public class ConnectorDeclarationSyncTests
{
    /// <summary>
    /// A Connector's declarations, as the seeding pass sees them.
    /// </summary>
    private sealed class DeclaredCapabilities : IConnectorCapabilities
    {
        public bool SupportsFullImport { get; init; }
        public bool SupportsDeltaImport { get; init; }
        public bool SupportsExport { get; init; }
        public bool SupportsPartitions { get; init; }
        public bool SupportsPartitionContainers { get; init; }
        public bool SupportsSecondaryExternalId { get; init; }
        public bool SupportsUserSelectedExternalId { get; init; }
        public bool SupportsUserSelectedAttributeTypes { get; init; }
        public bool SupportsAutoConfirmExport { get; init; }
        public bool SupportsParallelExport { get; init; }
        public bool SupportsPaging { get; init; }
        public bool SupportsFilePaths { get; init; }
        public bool SupportsPasswordSet { get; init; }
        public bool SupportsPasswordPolicyDiscovery { get; init; }
        public AttributeStandard SchemaStandard { get; init; }
    }

    private static DeclaredCapabilities AllDeclared() => new()
    {
        SupportsFullImport = true,
        SupportsDeltaImport = true,
        SupportsExport = true,
        SupportsPartitions = true,
        SupportsPartitionContainers = true,
        SupportsSecondaryExternalId = true,
        SupportsUserSelectedExternalId = true,
        SupportsUserSelectedAttributeTypes = true,
        SupportsAutoConfirmExport = true,
        SupportsParallelExport = true,
        SupportsPaging = true,
        SupportsFilePaths = true,
        SupportsPasswordSet = true,
        SupportsPasswordPolicyDiscovery = true,
        SchemaStandard = AttributeStandard.Ldap
    };

    [Test]
    public void ApplyConnectorDeclarations_EmptyDefinition_CopiesEveryDeclaration()
    {
        var definition = new ConnectorDefinition { Name = "Test Connector" };

        var changed = SeedingServer.ApplyConnectorDeclarations(AllDeclared(), definition);

        Assert.That(changed, Is.True);
        Assert.That(definition.SupportsFullImport, Is.True);
        Assert.That(definition.SupportsDeltaImport, Is.True);
        Assert.That(definition.SupportsExport, Is.True);
        Assert.That(definition.SupportsPartitions, Is.True);
        Assert.That(definition.SupportsPartitionContainers, Is.True);
        Assert.That(definition.SupportsSecondaryExternalId, Is.True);
        Assert.That(definition.SupportsUserSelectedExternalId, Is.True);
        Assert.That(definition.SupportsUserSelectedAttributeTypes, Is.True);
        Assert.That(definition.SupportsAutoConfirmExport, Is.True);
        Assert.That(definition.SupportsParallelExport, Is.True);
        Assert.That(definition.SupportsPaging, Is.True);
        Assert.That(definition.SupportsFilePaths, Is.True);
        Assert.That(definition.SupportsPasswordSet, Is.True);
        Assert.That(definition.SupportsPasswordPolicyDiscovery, Is.True);
        Assert.That(definition.SchemaStandard, Is.EqualTo(AttributeStandard.Ldap));
    }

    [Test]
    public void ApplyConnectorDeclarations_ConvergedDefinition_ReportsNoChange()
    {
        var definition = new ConnectorDefinition { Name = "Test Connector" };
        SeedingServer.ApplyConnectorDeclarations(AllDeclared(), definition);

        var changed = SeedingServer.ApplyConnectorDeclarations(AllDeclared(), definition);

        Assert.That(changed, Is.False);
    }

    [Test]
    public void ApplyConnectorDeclarations_SchemaStandardDrift_IsDetectedAndCorrected()
    {
        // A deployment upgraded from a JIM version whose LDAP Connector declared no standard.
        var definition = new ConnectorDefinition { Name = "LDAP Connector", SchemaStandard = AttributeStandard.NotSet };
        var declared = new DeclaredCapabilities { SchemaStandard = AttributeStandard.Ldap };

        var changed = SeedingServer.ApplyConnectorDeclarations(declared, definition);

        Assert.That(changed, Is.True);
        Assert.That(definition.SchemaStandard, Is.EqualTo(AttributeStandard.Ldap));
    }

    [Test]
    public void ApplyConnectorDeclarations_CapabilityDrift_IsDetectedAndCorrected()
    {
        var definition = new ConnectorDefinition { Name = "Test Connector", SupportsPaging = true };
        var declared = new DeclaredCapabilities { SupportsPaging = false };

        var changed = SeedingServer.ApplyConnectorDeclarations(declared, definition);

        Assert.That(changed, Is.True);
        Assert.That(definition.SupportsPaging, Is.False);
    }

    [Test]
    public void LdapConnector_DeclaresTheLdapVocabulary()
    {
        Assert.That(new LdapConnector().SchemaStandard, Is.EqualTo(AttributeStandard.Ldap));
    }

    [Test]
    public void FileConnector_DeclaresNoVocabulary()
    {
        // A delimited file has no schema standard of its own; the editor falls back to matching attribute
        // names against every standard rather than claiming the file speaks one.
        Assert.That(new FileConnector().SchemaStandard, Is.EqualTo(AttributeStandard.NotSet));
    }
}
