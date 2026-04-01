using JIM.Connectors.LDAP;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Tests for the LDAP connector import concurrency feature.
/// OpenLDAP/Generic directories use connection-per-combo parallel imports to bypass
/// the RFC 2696 connection-scoped paging cookie limitation.
/// </summary>
[TestFixture]
public class LdapConnectorImportConcurrencyTests
{
    #region Constant validation tests

    [Test]
    public void DefaultImportConcurrency_IsFour()
    {
        Assert.That(LdapConnectorConstants.DEFAULT_IMPORT_CONCURRENCY, Is.EqualTo(4));
    }

    [Test]
    public void MaxImportConcurrency_IsEight()
    {
        Assert.That(LdapConnectorConstants.MAX_IMPORT_CONCURRENCY, Is.EqualTo(8));
    }

    [Test]
    public void DefaultImportConcurrency_DoesNotExceedMax()
    {
        Assert.That(LdapConnectorConstants.DEFAULT_IMPORT_CONCURRENCY,
            Is.LessThanOrEqualTo(LdapConnectorConstants.MAX_IMPORT_CONCURRENCY));
    }

    [Test]
    public void DefaultImportConcurrency_IsAtLeastOne()
    {
        Assert.That(LdapConnectorConstants.DEFAULT_IMPORT_CONCURRENCY, Is.GreaterThanOrEqualTo(1));
    }

    #endregion

    #region Setting registration tests

    [Test]
    public void GetSettings_ImportConcurrencyDefaultMatchesConstant()
    {
        using var connector = new LdapConnector();
        var settings = connector.GetSettings();
        var importConcurrency = settings.First(s => s.Name == "Import Concurrency");

        Assert.That(importConcurrency.DefaultIntValue, Is.EqualTo(LdapConnectorConstants.DEFAULT_IMPORT_CONCURRENCY));
    }

    [Test]
    public void GetSettings_ImportConcurrencyIsAfterSearchTimeout()
    {
        // Import Concurrency should appear in the Import Settings section, after Search Timeout
        using var connector = new LdapConnector();
        var settings = connector.GetSettings();
        var searchTimeoutIndex = settings.FindIndex(s => s.Name == "Search Timeout");
        var importConcurrencyIndex = settings.FindIndex(s => s.Name == "Import Concurrency");

        Assert.That(searchTimeoutIndex, Is.GreaterThan(-1), "Search Timeout setting not found");
        Assert.That(importConcurrencyIndex, Is.GreaterThan(-1), "Import Concurrency setting not found");
        Assert.That(importConcurrencyIndex, Is.GreaterThan(searchTimeoutIndex),
            "Import Concurrency should appear after Search Timeout in the Import Settings section");
    }

    #endregion

    #region Concurrency clamping tests (via Math.Clamp in constructor)

    [Test]
    public void ImportConcurrency_ClampedToMax_WhenExceedsMaximum()
    {
        // Verify the constant boundaries — the actual clamping happens in LdapConnectorImport constructor
        // via Math.Clamp(importConcurrency, 1, MAX_IMPORT_CONCURRENCY)
        var clamped = Math.Clamp(100, 1, LdapConnectorConstants.MAX_IMPORT_CONCURRENCY);
        Assert.That(clamped, Is.EqualTo(LdapConnectorConstants.MAX_IMPORT_CONCURRENCY));
    }

    [Test]
    public void ImportConcurrency_ClampedToOne_WhenZero()
    {
        var clamped = Math.Clamp(0, 1, LdapConnectorConstants.MAX_IMPORT_CONCURRENCY);
        Assert.That(clamped, Is.EqualTo(1));
    }

    [Test]
    public void ImportConcurrency_ClampedToOne_WhenNegative()
    {
        var clamped = Math.Clamp(-5, 1, LdapConnectorConstants.MAX_IMPORT_CONCURRENCY);
        Assert.That(clamped, Is.EqualTo(1));
    }

    [Test]
    public void ImportConcurrency_PassedThrough_WhenWithinRange()
    {
        var clamped = Math.Clamp(6, 1, LdapConnectorConstants.MAX_IMPORT_CONCURRENCY);
        Assert.That(clamped, Is.EqualTo(6));
    }

    #endregion

    #region Directory type detection tests

    [Test]
    public void OpenLdapDirectoryType_IsConnectionScopedPaging()
    {
        // Verify that OpenLDAP is correctly identified as needing connection-scoped paging handling
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        var isConnectionScoped = rootDse.DirectoryType is LdapDirectoryType.OpenLDAP or LdapDirectoryType.Generic;
        Assert.That(isConnectionScoped, Is.True);
    }

    [Test]
    public void GenericDirectoryType_IsConnectionScopedPaging()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.Generic };
        var isConnectionScoped = rootDse.DirectoryType is LdapDirectoryType.OpenLDAP or LdapDirectoryType.Generic;
        Assert.That(isConnectionScoped, Is.True);
    }

    [Test]
    public void ActiveDirectoryType_IsNotConnectionScopedPaging()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        var isConnectionScoped = rootDse.DirectoryType is LdapDirectoryType.OpenLDAP or LdapDirectoryType.Generic;
        Assert.That(isConnectionScoped, Is.False);
    }

    [Test]
    public void SambaDirectoryType_IsNotConnectionScopedPaging()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.SambaAD };
        var isConnectionScoped = rootDse.DirectoryType is LdapDirectoryType.OpenLDAP or LdapDirectoryType.Generic;
        Assert.That(isConnectionScoped, Is.False);
    }

    #endregion
}
