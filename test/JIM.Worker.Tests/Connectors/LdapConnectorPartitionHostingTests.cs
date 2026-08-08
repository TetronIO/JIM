// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Tests for the partition-hosting guard on AD/Samba AD imports (issue #230). AD's crossRef-based
/// partition discovery lists every domain in the forest, including domains the connected domain
/// controller does not host. Selecting one of those foreign partitions silently returns zero objects
/// (the DC does not chase referrals), which violates JIM's synchronisation integrity rules. The guard
/// fails fast instead.
/// </summary>
[TestFixture]
public class LdapConnectorPartitionHostingTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    #region Foreign partition selected

    [Test]
    public void VerifyPartitionsAreHostedByConnectedServer_PartitionNotInNamingContexts_ThrowsPartitionNotHostedException()
    {
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            DnsHostName = "dc1.corp.example.com",
            NamingContexts = ["DC=corp,DC=example,DC=com"]
        };

        var foreignPartition = new ConnectedSystemPartition { Name = "fabrikam.local", ExternalId = "DC=fabrikam,DC=local" };
        var selectedPartitions = new List<ConnectedSystemPartition> { foreignPartition };

        var ex = Assert.Throws<PartitionNotHostedException>(() =>
            LdapConnectorUtilities.VerifyPartitionsAreHostedByConnectedServer(currentRootDse, selectedPartitions, Logger));

        Assert.That(ex!.Message, Does.Contain("fabrikam.local"));
        Assert.That(ex.Message, Does.Contain("dc1.corp.example.com"));
    }

    [Test]
    public void VerifyPartitionsAreHostedByConnectedServer_MultipleForeignPartitions_NamesEveryOffendingPartition()
    {
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            DnsHostName = "dc1.corp.example.com",
            NamingContexts = ["DC=corp,DC=example,DC=com"]
        };

        var selectedPartitions = new List<ConnectedSystemPartition>
        {
            new() { Name = "fabrikam.local", ExternalId = "DC=fabrikam,DC=local" },
            new() { Name = "contoso.local", ExternalId = "DC=contoso,DC=local" }
        };

        var ex = Assert.Throws<PartitionNotHostedException>(() =>
            LdapConnectorUtilities.VerifyPartitionsAreHostedByConnectedServer(currentRootDse, selectedPartitions, Logger));

        Assert.That(ex!.Message, Does.Contain("fabrikam.local"));
        Assert.That(ex.Message, Does.Contain("contoso.local"));
    }

    #endregion

    #region Hosted partitions pass

    [Test]
    public void VerifyPartitionsAreHostedByConnectedServer_AllPartitionsHosted_DoesNotThrow()
    {
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            DnsHostName = "dc1.corp.example.com",
            NamingContexts = ["DC=corp,DC=example,DC=com", "CN=Configuration,DC=corp,DC=example,DC=com"]
        };

        var hostedPartition = new ConnectedSystemPartition { Name = "corp.example.com", ExternalId = "DC=corp,DC=example,DC=com" };
        var selectedPartitions = new List<ConnectedSystemPartition> { hostedPartition };

        Assert.DoesNotThrow(() =>
            LdapConnectorUtilities.VerifyPartitionsAreHostedByConnectedServer(currentRootDse, selectedPartitions, Logger));
    }

    [Test]
    public void VerifyPartitionsAreHostedByConnectedServer_DnMatchIsCaseInsensitive_DoesNotThrow()
    {
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            DnsHostName = "dc1.corp.example.com",
            NamingContexts = ["dc=CORP,dc=Example,dc=COM"]
        };

        var hostedPartition = new ConnectedSystemPartition { Name = "corp.example.com", ExternalId = "DC=corp,DC=example,DC=com" };
        var selectedPartitions = new List<ConnectedSystemPartition> { hostedPartition };

        Assert.DoesNotThrow(() =>
            LdapConnectorUtilities.VerifyPartitionsAreHostedByConnectedServer(currentRootDse, selectedPartitions, Logger));
    }

    #endregion

    #region Missing data never fails an import

    [Test]
    public void VerifyPartitionsAreHostedByConnectedServer_NamingContextsNull_LogsWarningAndDoesNotThrow()
    {
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            DnsHostName = "dc1.corp.example.com",
            NamingContexts = null
        };

        var selectedPartitions = new List<ConnectedSystemPartition>
        {
            new() { Name = "fabrikam.local", ExternalId = "DC=fabrikam,DC=local" }
        };

        Assert.DoesNotThrow(() =>
            LdapConnectorUtilities.VerifyPartitionsAreHostedByConnectedServer(currentRootDse, selectedPartitions, Logger));
    }

    [Test]
    public void VerifyPartitionsAreHostedByConnectedServer_NamingContextsEmpty_DoesNotThrow()
    {
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            DnsHostName = "dc1.corp.example.com",
            NamingContexts = []
        };

        var selectedPartitions = new List<ConnectedSystemPartition>
        {
            new() { Name = "fabrikam.local", ExternalId = "DC=fabrikam,DC=local" }
        };

        Assert.DoesNotThrow(() =>
            LdapConnectorUtilities.VerifyPartitionsAreHostedByConnectedServer(currentRootDse, selectedPartitions, Logger));
    }

    #endregion

    #region Non-AD-family directories are unaffected

    [Test]
    public void VerifyPartitionsAreHostedByConnectedServer_OpenLdapDirectory_NeverThrowsRegardlessOfNamingContexts()
    {
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.OpenLDAP,
            DnsHostName = "ldap1.corp.example.com",
            NamingContexts = ["dc=corp,dc=example,dc=com"]
        };

        var selectedPartitions = new List<ConnectedSystemPartition>
        {
            new() { Name = "fabrikam.local", ExternalId = "DC=fabrikam,DC=local" }
        };

        Assert.DoesNotThrow(() =>
            LdapConnectorUtilities.VerifyPartitionsAreHostedByConnectedServer(currentRootDse, selectedPartitions, Logger));
    }

    #endregion
}
