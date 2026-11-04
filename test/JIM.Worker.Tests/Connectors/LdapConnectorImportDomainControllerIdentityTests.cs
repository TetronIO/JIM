// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Tests for the DC-mismatch guard on USN-based LDAP delta imports (issue #230). AD/Samba AD USNs are
/// scoped to the invocationId of the domain controller that issued them, so a delta import that connects
/// to a different DC than the one that issued a Partition's watermark (for example, DNS round-robin
/// against a domain name configured as Host) must fail fast rather than silently skip or re-import changes.
/// Each Partition's watermark carries the DC that issued it (#2715).
/// </summary>
[TestFixture]
public class LdapConnectorImportDomainControllerIdentityTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    private static readonly ConnectedSystemPartition Partition = new() { Id = 1, Name = "jim.test", ExternalId = "DC=jim,DC=test", Selected = true };

    #region InvocationId mismatch

    [Test]
    public void VerifyDomainControllerIdentity_InvocationIdMismatch_ThrowsCannotPerformDeltaImportException()
    {
        var previousInvocationId = Guid.NewGuid();
        var currentInvocationId = Guid.NewGuid();

        var previous = new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Usn,
            Usn = 100,
            InvocationId = previousInvocationId,
            DnsHostName = "dc1.jim.test"
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = currentInvocationId,
            DnsHostName = "dc2.jim.test"
        };

        var ex = Assert.Throws<CannotPerformDeltaImportException>(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity([Partition], previous, currentRootDse, Logger));

        // Scenario 025 reads the refusal for all three of these; the partition is what a watermark per partition adds.
        Assert.That(ex!.Message, Does.Contain("invocationId has changed"));
        Assert.That(ex.Message, Does.Contain(previousInvocationId.ToString()));
        Assert.That(ex.Message, Does.Contain(currentInvocationId.ToString()));
        Assert.That(ex.Message, Does.Contain("Full Import"));
        Assert.That(ex.Message, Does.Contain("the partition jim.test"));
    }

    [Test]
    public void VerifyDomainControllerIdentity_InvocationIdMismatchOnTheSameServer_ExplainsTheRestoreItsConsequenceAndTheRemedy()
    {
        // The lab reverts a domain controller to a checkpoint before every scenario: same name, new invocationId.
        var previous = new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Usn,
            Usn = 100,
            InvocationId = Guid.NewGuid(),
            DnsHostName = "dc1.jim.test"
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = Guid.NewGuid(),
            DnsHostName = "dc1.jim.test"
        };

        var ex = Assert.Throws<CannotPerformDeltaImportException>(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity([Partition], previous, currentRootDse, Logger));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain("restored from a backup or snapshot"), "the usual cause");
            Assert.That(ex.Message, Does.Contain("silently miss changes"), "why the run must not carry on");
            Assert.That(ex.Message, Does.Contain("Run a Full Import that reads the partition jim.test to re-establish the delta baseline"), "what to do, for which partition");
            Assert.That(ex.Message, Does.Contain("DNS round-robin"), "the other cause, when Host is a domain name");
        }
    }

    [Test]
    public void VerifyDomainControllerIdentity_InvocationIdMatches_DoesNotThrow()
    {
        var invocationId = Guid.NewGuid();

        var previous = new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Usn,
            Usn = 100,
            InvocationId = invocationId,
            DnsHostName = "dc1.jim.test"
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = invocationId,
            DnsHostName = "dc1.jim.test"
        };

        Assert.That(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity([Partition], previous, currentRootDse, Logger), Throws.Nothing);
    }

    #endregion

    #region Hostname fallback (pre-#230 baseline, previous InvocationId null)

    [Test]
    public void VerifyDomainControllerIdentity_PreviousInvocationIdNullAndHostnameMismatch_ThrowsCannotPerformDeltaImportException()
    {
        var previous = new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Usn,
            Usn = 100,
            InvocationId = null,
            DnsHostName = "dc1.jim.test"
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = Guid.NewGuid(),
            DnsHostName = "dc2.jim.test"
        };

        var ex = Assert.Throws<CannotPerformDeltaImportException>(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity([Partition], previous, currentRootDse, Logger));

        Assert.That(ex!.Message, Does.Contain("dc1.jim.test"));
        Assert.That(ex.Message, Does.Contain("dc2.jim.test"));
        Assert.That(ex.Message, Does.Contain("Full Import"));
        Assert.That(ex.Message, Does.Contain("the partition jim.test"));
    }

    [Test]
    public void VerifyDomainControllerIdentity_CurrentInvocationIdMissingAndHostnameMismatch_ThrowsCannotPerformDeltaImportException()
    {
        // The current run's invocationId query can fail (e.g. permissions) even though the previous
        // baseline captured one. The hostname pair is still comparable, so a mismatch must be caught.
        var previous = new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Usn,
            Usn = 100,
            InvocationId = Guid.NewGuid(),
            DnsHostName = "dc1.jim.test"
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = null,
            DnsHostName = "dc2.jim.test"
        };

        var ex = Assert.Throws<CannotPerformDeltaImportException>(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity([Partition], previous, currentRootDse, Logger));

        Assert.That(ex!.Message, Does.Contain("dc1.jim.test"));
        Assert.That(ex.Message, Does.Contain("dc2.jim.test"));
    }

    [Test]
    public void VerifyDomainControllerIdentity_CurrentInvocationIdMissingAndHostnameMatches_DoesNotThrow()
    {
        var previous = new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Usn,
            Usn = 100,
            InvocationId = Guid.NewGuid(),
            DnsHostName = "dc1.jim.test"
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = null,
            DnsHostName = "dc1.jim.test"
        };

        Assert.That(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity([Partition], previous, currentRootDse, Logger), Throws.Nothing);
    }

    [Test]
    public void VerifyDomainControllerIdentity_PreviousInvocationIdNullAndHostnameMatchesCaseInsensitively_DoesNotThrow()
    {
        var previous = new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Usn,
            Usn = 100,
            InvocationId = null,
            DnsHostName = "DC1.jim.test"
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = null,
            DnsHostName = "dc1.JIM.TEST"
        };

        Assert.That(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity([Partition], previous, currentRootDse, Logger), Throws.Nothing);
    }

    #endregion

    #region Neither identity comparable

    [Test]
    public void VerifyDomainControllerIdentity_BothInvocationIdAndHostnameNull_DoesNotThrow()
    {
        var previous = new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Usn,
            Usn = 100,
            InvocationId = null,
            DnsHostName = null
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = null,
            DnsHostName = null
        };

        Assert.That(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity([Partition], previous, currentRootDse, Logger), Throws.Nothing);
    }

    [Test]
    public void VerifyDomainControllerIdentity_PreviousInvocationIdPresentButCurrentMissingAndNoHostnames_DoesNotThrow()
    {
        // The current run's invocationId query can fail (e.g. permissions) even though the previous
        // baseline captured one. Neither invocationId nor hostname is comparable here, so the guard
        // must not fail the import purely on missing data.
        var previous = new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Usn,
            Usn = 100,
            InvocationId = Guid.NewGuid(),
            DnsHostName = null
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = null,
            DnsHostName = null
        };

        Assert.That(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity([Partition], previous, currentRootDse, Logger), Throws.Nothing);
    }

    #endregion

    #region Non-AD directories are unaffected

    [Test]
    public void DeltaSourceKind_OpenLdapDirectory_IsAccesslog_SoTheUsnContinuityCheckIsNeverInvoked()
    {
        // Only the USN change source verifies domain controller identity. Confirm OpenLDAP reads its
        // changes from the accesslog source instead, so the DC-mismatch guard is a no-op for it.
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };

        Assert.That(rootDse.DeltaSourceKind, Is.EqualTo(LdapDeltaSourceKind.Accesslog));
        Assert.That(rootDse.IsActiveDirectoryFamily, Is.False);
    }

    #endregion
}
