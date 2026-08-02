// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Exceptions;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Tests for the DC-mismatch guard on USN-based LDAP delta imports (issue #230). AD/Samba AD USNs are
/// scoped to the invocationId of the domain controller that issued them, so a delta import that connects
/// to a different DC than the one that produced the persisted watermark (for example, DNS round-robin
/// against a domain name configured as Host) must fail fast rather than silently skip or re-import changes.
/// </summary>
[TestFixture]
public class LdapConnectorImportDomainControllerIdentityTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    #region InvocationId mismatch

    [Test]
    public void VerifyDomainControllerIdentity_InvocationIdMismatch_ThrowsCannotPerformDeltaImportException()
    {
        var previousInvocationId = Guid.NewGuid();
        var currentInvocationId = Guid.NewGuid();

        var previousRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
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
            LdapConnectorUtilities.VerifyDomainControllerIdentity(previousRootDse, currentRootDse, Logger));

        Assert.That(ex!.Message, Does.Contain(previousInvocationId.ToString()));
        Assert.That(ex.Message, Does.Contain(currentInvocationId.ToString()));
        Assert.That(ex.Message, Does.Contain("Full Import"));
    }

    [Test]
    public void VerifyDomainControllerIdentity_InvocationIdMatches_DoesNotThrow()
    {
        var invocationId = Guid.NewGuid();

        var previousRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = invocationId,
            DnsHostName = "dc1.jim.test"
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = invocationId,
            DnsHostName = "dc1.jim.test"
        };

        Assert.DoesNotThrow(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity(previousRootDse, currentRootDse, Logger));
    }

    #endregion

    #region Hostname fallback (pre-#230 baseline, previous InvocationId null)

    [Test]
    public void VerifyDomainControllerIdentity_PreviousInvocationIdNullAndHostnameMismatch_ThrowsCannotPerformDeltaImportException()
    {
        var previousRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
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
            LdapConnectorUtilities.VerifyDomainControllerIdentity(previousRootDse, currentRootDse, Logger));

        Assert.That(ex!.Message, Does.Contain("dc1.jim.test"));
        Assert.That(ex.Message, Does.Contain("dc2.jim.test"));
        Assert.That(ex.Message, Does.Contain("Full Import"));
    }

    [Test]
    public void VerifyDomainControllerIdentity_CurrentInvocationIdMissingAndHostnameMismatch_ThrowsCannotPerformDeltaImportException()
    {
        // The current run's invocationId query can fail (e.g. permissions) even though the previous
        // baseline captured one. The hostname pair is still comparable, so a mismatch must be caught.
        var previousRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
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
            LdapConnectorUtilities.VerifyDomainControllerIdentity(previousRootDse, currentRootDse, Logger));

        Assert.That(ex!.Message, Does.Contain("dc1.jim.test"));
        Assert.That(ex.Message, Does.Contain("dc2.jim.test"));
    }

    [Test]
    public void VerifyDomainControllerIdentity_CurrentInvocationIdMissingAndHostnameMatches_DoesNotThrow()
    {
        var previousRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = Guid.NewGuid(),
            DnsHostName = "dc1.jim.test"
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = null,
            DnsHostName = "dc1.jim.test"
        };

        Assert.DoesNotThrow(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity(previousRootDse, currentRootDse, Logger));
    }

    [Test]
    public void VerifyDomainControllerIdentity_PreviousInvocationIdNullAndHostnameMatchesCaseInsensitively_DoesNotThrow()
    {
        var previousRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = null,
            DnsHostName = "DC1.jim.test"
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = null,
            DnsHostName = "dc1.JIM.TEST"
        };

        Assert.DoesNotThrow(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity(previousRootDse, currentRootDse, Logger));
    }

    #endregion

    #region Neither identity comparable

    [Test]
    public void VerifyDomainControllerIdentity_BothInvocationIdAndHostnameNull_DoesNotThrow()
    {
        var previousRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = null,
            DnsHostName = null
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = null,
            DnsHostName = null
        };

        Assert.DoesNotThrow(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity(previousRootDse, currentRootDse, Logger));
    }

    [Test]
    public void VerifyDomainControllerIdentity_PreviousInvocationIdPresentButCurrentMissingAndNoHostnames_DoesNotThrow()
    {
        // The current run's invocationId query can fail (e.g. permissions) even though the previous
        // baseline captured one. Neither invocationId nor hostname is comparable here, so the guard
        // must not fail the import purely on missing data.
        var previousRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = Guid.NewGuid(),
            DnsHostName = null
        };
        var currentRootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = null,
            DnsHostName = null
        };

        Assert.DoesNotThrow(() =>
            LdapConnectorUtilities.VerifyDomainControllerIdentity(previousRootDse, currentRootDse, Logger));
    }

    #endregion

    #region Non-AD directories are unaffected

    [Test]
    public void UseUsnDeltaImport_OpenLdapDirectory_IsFalse_SoGuardIsNeverInvoked()
    {
        // GetDeltaImportObjectsAsync only calls VerifyDomainControllerIdentity when
        // _previousRootDse.UseUsnDeltaImport is true. Confirm OpenLDAP (accesslog-based delta
        // import) does not satisfy that gate, so the DC-mismatch guard is a no-op for it.
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };

        Assert.That(rootDse.UseUsnDeltaImport, Is.False);
    }

    #endregion
}
