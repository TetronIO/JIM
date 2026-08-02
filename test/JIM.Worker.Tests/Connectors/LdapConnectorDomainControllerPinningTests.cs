// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using System.Text.Json;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Tests for domain controller discovery and pinning (issue #230 Phase 2 slice 2): resolving which server
/// a connection should use (Preferred Domain Controller setting, then a persisted pin, then Host), deciding
/// what an import should (re)pin to, merging a pin update into persisted connector data without disturbing
/// the watermark fields, and invalidating a pin when the pinned domain controller becomes unreachable.
/// </summary>
[TestFixture]
public class LdapConnectorDomainControllerPinningTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    #region ResolveEffectiveServer: priority order

    [Test]
    public void ResolveEffectiveServer_PreferredSettingConfigured_UsesPreferredSettingEvenWithAPinPresent()
    {
        var persistedData = JsonSerializer.Serialize(new LdapConnectorRootDse { PinnedDirectoryServer = "pinned-dc.jim.test" });

        var (server, source) = LdapConnectorUtilities.ResolveEffectiveServer(
            "preferred-dc.jim.test", persistedData, "host.jim.test", Logger);

        Assert.That(server, Is.EqualTo("preferred-dc.jim.test"));
        Assert.That(source, Is.EqualTo(LdapServerResolutionSource.PreferredSetting));
    }

    [Test]
    public void ResolveEffectiveServer_NoPreferredSettingButPinPresent_UsesPin()
    {
        var persistedData = JsonSerializer.Serialize(new LdapConnectorRootDse { PinnedDirectoryServer = "pinned-dc.jim.test" });

        var (server, source) = LdapConnectorUtilities.ResolveEffectiveServer(
            null, persistedData, "host.jim.test", Logger);

        Assert.That(server, Is.EqualTo("pinned-dc.jim.test"));
        Assert.That(source, Is.EqualTo(LdapServerResolutionSource.Pinned));
    }

    [Test]
    public void ResolveEffectiveServer_NoPreferredSettingAndNoPin_FallsBackToHost()
    {
        var (server, source) = LdapConnectorUtilities.ResolveEffectiveServer(
            null, null, "host.jim.test", Logger);

        Assert.That(server, Is.EqualTo("host.jim.test"));
        Assert.That(source, Is.EqualTo(LdapServerResolutionSource.Host));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void ResolveEffectiveServer_BlankPreferredSetting_FallsThroughToPin(string blankPreferredSetting)
    {
        var persistedData = JsonSerializer.Serialize(new LdapConnectorRootDse { PinnedDirectoryServer = "pinned-dc.jim.test" });

        var (server, source) = LdapConnectorUtilities.ResolveEffectiveServer(
            blankPreferredSetting, persistedData, "host.jim.test", Logger);

        Assert.That(server, Is.EqualTo("pinned-dc.jim.test"));
        Assert.That(source, Is.EqualTo(LdapServerResolutionSource.Pinned));
    }

    [Test]
    public void ResolveEffectiveServer_BlankPreferredSettingAndNoPin_FallsThroughToHost()
    {
        var (server, source) = LdapConnectorUtilities.ResolveEffectiveServer(
            "  ", null, "host.jim.test", Logger);

        Assert.That(server, Is.EqualTo("host.jim.test"));
        Assert.That(source, Is.EqualTo(LdapServerResolutionSource.Host));
    }

    [Test]
    public void ResolveEffectiveServer_PersistedDataHasNoPin_FallsBackToHost()
    {
        // e.g. a non-AD-family directory, or a baseline recorded while a Preferred Domain Controller was configured.
        var persistedData = JsonSerializer.Serialize(new LdapConnectorRootDse { PinnedDirectoryServer = null, HighestCommittedUsn = 123 });

        var (server, source) = LdapConnectorUtilities.ResolveEffectiveServer(
            null, persistedData, "host.jim.test", Logger);

        Assert.That(server, Is.EqualTo("host.jim.test"));
        Assert.That(source, Is.EqualTo(LdapServerResolutionSource.Host));
    }

    #endregion

    #region ResolveEffectiveServer: malformed persisted data tolerated

    [Test]
    public void ResolveEffectiveServer_MalformedPersistedData_TreatsAsNoPinAndFallsBackToHost()
    {
        const string malformedJson = "{ this is not valid JSON";

        var (server, source) = LdapConnectorUtilities.ResolveEffectiveServer(
            null, malformedJson, "host.jim.test", Logger);

        Assert.That(server, Is.EqualTo("host.jim.test"));
        Assert.That(source, Is.EqualTo(LdapServerResolutionSource.Host));
    }

    [Test]
    public void ResolveEffectiveServer_MalformedPersistedDataButPreferredSettingConfigured_UsesPreferredSetting()
    {
        // The preferred setting short-circuits before persisted data is even looked at, so malformed
        // data must not surface as a failure here either.
        const string malformedJson = "{ this is not valid JSON";

        var (server, source) = LdapConnectorUtilities.ResolveEffectiveServer(
            "preferred-dc.jim.test", malformedJson, "host.jim.test", Logger);

        Assert.That(server, Is.EqualTo("preferred-dc.jim.test"));
        Assert.That(source, Is.EqualTo(LdapServerResolutionSource.PreferredSetting));
    }

    #endregion

    #region ResolvePinnedDirectoryServerForImport

    [Test]
    public void ResolvePinnedDirectoryServerForImport_AdFamilyNoPreferredSetting_PinsToDnsHostName()
    {
        var result = LdapConnectorUtilities.ResolvePinnedDirectoryServerForImport(
            useUsnDeltaImport: true, preferredDomainController: null, dnsHostName: "dc1.jim.test");

        Assert.That(result, Is.EqualTo("dc1.jim.test"));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void ResolvePinnedDirectoryServerForImport_AdFamilyBlankPreferredSetting_PinsToDnsHostName(string blankPreferredSetting)
    {
        var result = LdapConnectorUtilities.ResolvePinnedDirectoryServerForImport(
            useUsnDeltaImport: true, preferredDomainController: blankPreferredSetting, dnsHostName: "dc1.jim.test");

        Assert.That(result, Is.EqualTo("dc1.jim.test"));
    }

    [Test]
    public void ResolvePinnedDirectoryServerForImport_AdFamilyWithPreferredSettingConfigured_ClearsPin()
    {
        // The setting owns DC selection; a stale pin from a previous configuration must not survive.
        var result = LdapConnectorUtilities.ResolvePinnedDirectoryServerForImport(
            useUsnDeltaImport: true, preferredDomainController: "preferred-dc.jim.test", dnsHostName: "dc1.jim.test");

        Assert.That(result, Is.Null);
    }

    [Test]
    public void ResolvePinnedDirectoryServerForImport_NonAdFamilyNoPreferredSetting_NeverPins()
    {
        // OpenLDAP/Generic: pinning is meaningless, regardless of the setting.
        var result = LdapConnectorUtilities.ResolvePinnedDirectoryServerForImport(
            useUsnDeltaImport: false, preferredDomainController: null, dnsHostName: "ldap1.jim.test");

        Assert.That(result, Is.Null);
    }

    [Test]
    public void ResolvePinnedDirectoryServerForImport_NonAdFamilyWithPreferredSettingConfigured_NeverPins()
    {
        var result = LdapConnectorUtilities.ResolvePinnedDirectoryServerForImport(
            useUsnDeltaImport: false, preferredDomainController: "preferred.jim.test", dnsHostName: "ldap1.jim.test");

        Assert.That(result, Is.Null);
    }

    #endregion

    #region MergePinnedDirectoryServerIntoPersistedData: watermark preservation

    [Test]
    public void MergePinnedDirectoryServerIntoPersistedData_UpdatesOnlyThePin_PreservesEveryWatermarkFieldExactly()
    {
        var invocationId = Guid.NewGuid();
        var original = new LdapConnectorRootDse
        {
            DnsHostName = "dc1.jim.test",
            HighestCommittedUsn = 987654321,
            LastChangeNumber = 42,
            LastAccesslogTimestamp = "20260326183000.000000Z",
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            InvocationId = invocationId,
            VendorName = "Microsoft",
            PinnedDirectoryServer = "old-dc.jim.test"
        };
        var persistedData = JsonSerializer.Serialize(original);

        var updatedJson = LdapConnectorUtilities.MergePinnedDirectoryServerIntoPersistedData(
            persistedData, "new-dc.jim.test", LdapDirectoryType.Generic, Logger);
        var updated = JsonSerializer.Deserialize<LdapConnectorRootDse>(updatedJson);

        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.PinnedDirectoryServer, Is.EqualTo("new-dc.jim.test"), "The pin must be updated to the new value");

        // Every other field must survive byte-for-byte in meaning: a regressed watermark corrupts delta imports.
        Assert.That(updated.HighestCommittedUsn, Is.EqualTo(original.HighestCommittedUsn));
        Assert.That(updated.LastChangeNumber, Is.EqualTo(original.LastChangeNumber));
        Assert.That(updated.LastAccesslogTimestamp, Is.EqualTo(original.LastAccesslogTimestamp));
        Assert.That(updated.InvocationId, Is.EqualTo(original.InvocationId));
        Assert.That(updated.DnsHostName, Is.EqualTo(original.DnsHostName));
        Assert.That(updated.DirectoryType, Is.EqualTo(original.DirectoryType));
        Assert.That(updated.VendorName, Is.EqualTo(original.VendorName));
    }

    [Test]
    public void MergePinnedDirectoryServerIntoPersistedData_ClearingThePin_PreservesEveryWatermarkFieldExactly()
    {
        // The invalidation path: same guarantee, but the new value is null (clearing the pin).
        var invocationId = Guid.NewGuid();
        var original = new LdapConnectorRootDse
        {
            HighestCommittedUsn = 111,
            LastChangeNumber = 22,
            LastAccesslogTimestamp = "20260101000000.000000Z",
            DirectoryType = LdapDirectoryType.SambaAD,
            InvocationId = invocationId,
            PinnedDirectoryServer = "stale-dc.jim.test"
        };
        var persistedData = JsonSerializer.Serialize(original);

        var updatedJson = LdapConnectorUtilities.MergePinnedDirectoryServerIntoPersistedData(
            persistedData, null, LdapDirectoryType.Generic, Logger);
        var updated = JsonSerializer.Deserialize<LdapConnectorRootDse>(updatedJson);

        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.PinnedDirectoryServer, Is.Null);
        Assert.That(updated.HighestCommittedUsn, Is.EqualTo(original.HighestCommittedUsn));
        Assert.That(updated.LastChangeNumber, Is.EqualTo(original.LastChangeNumber));
        Assert.That(updated.LastAccesslogTimestamp, Is.EqualTo(original.LastAccesslogTimestamp));
        Assert.That(updated.InvocationId, Is.EqualTo(original.InvocationId));
        Assert.That(updated.DirectoryType, Is.EqualTo(original.DirectoryType));
    }

    [Test]
    public void MergePinnedDirectoryServerIntoPersistedData_NoPreviousData_CreatesMinimalRecordWithPinAndFallbackDirectoryType()
    {
        var updatedJson = LdapConnectorUtilities.MergePinnedDirectoryServerIntoPersistedData(
            null, "new-dc.jim.test", LdapDirectoryType.ActiveDirectory, Logger);
        var updated = JsonSerializer.Deserialize<LdapConnectorRootDse>(updatedJson);

        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.PinnedDirectoryServer, Is.EqualTo("new-dc.jim.test"));
        Assert.That(updated.DirectoryType, Is.EqualTo(LdapDirectoryType.ActiveDirectory));
        Assert.That(updated.HighestCommittedUsn, Is.Null);
    }

    [Test]
    public void MergePinnedDirectoryServerIntoPersistedData_MalformedPreviousData_TolerantlyCreatesMinimalRecord()
    {
        const string malformedJson = "{ not json";

        var updatedJson = LdapConnectorUtilities.MergePinnedDirectoryServerIntoPersistedData(
            malformedJson, "new-dc.jim.test", LdapDirectoryType.ActiveDirectory, Logger);
        var updated = JsonSerializer.Deserialize<LdapConnectorRootDse>(updatedJson);

        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.PinnedDirectoryServer, Is.EqualTo("new-dc.jim.test"));
        Assert.That(updated.DirectoryType, Is.EqualTo(LdapDirectoryType.ActiveDirectory));
    }

    #endregion

    #region PinnedDirectoryServer backward compatibility

    [Test]
    public void LdapConnectorRootDse_DeserialisingPreExistingJsonWithoutThePinnedProperty_DefaultsToNull()
    {
        // Old persisted JSON predating the pin property. This is the intended compatibility path.
        var oldJson = JsonSerializer.Serialize(new
        {
            DnsHostName = "dc1.jim.test",
            HighestCommittedUsn = 555,
            DirectoryType = LdapDirectoryType.ActiveDirectory
        });

        var rootDse = JsonSerializer.Deserialize<LdapConnectorRootDse>(oldJson);

        Assert.That(rootDse, Is.Not.Null);
        Assert.That(rootDse!.PinnedDirectoryServer, Is.Null);
        Assert.That(rootDse.HighestCommittedUsn, Is.EqualTo(555));
    }

    #endregion

    #region GetSettings: Preferred Domain Controller

    [Test]
    public void GetSettings_ContainsPreferredDomainControllerSetting()
    {
        using var connector = new LdapConnector();
        var settings = connector.GetSettings();
        var setting = settings.FirstOrDefault(s => s.Name == "Preferred Domain Controller");

        Assert.That(setting, Is.Not.Null);
        Assert.That(setting!.Required, Is.False);
        Assert.That(setting.Category, Is.EqualTo(ConnectedSystemSettingCategory.Connectivity));
        Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.String));
    }

    #endregion

    #region Connection-level resolution and invalidation

    /// <summary>
    /// Builds the minimal setting values needed to attempt (and fail) a connection. Port 1 on the loopback
    /// interface refuses immediately rather than waiting out a timeout - the same pattern used by
    /// <c>LdapConnectorSynchronisationContextTests</c>. Maximum Retries is 0 so the failure is immediate.
    /// </summary>
    private static List<ConnectedSystemSettingValue> BuildRefusedConnectionSettingValues(string host, string? preferredDomainController = null)
    {
        var values = new List<ConnectedSystemSettingValue>
        {
            NewSetting("Host", stringValue: host),
            NewSetting("Port", intValue: 1),
            NewSetting("Connection Timeout", intValue: 2),
            NewSetting("Username", stringValue: "cn=admin,dc=example,dc=org"),
            NewSetting("Password", encryptedValue: "adminpassword"),
            NewSetting("Authentication Type", stringValue: "Simple"),
            NewSetting("Maximum Retries", intValue: 0)
        };

        if (preferredDomainController != null)
            values.Add(NewSetting("Preferred Domain Controller", stringValue: preferredDomainController));

        return values;
    }

    private static ConnectedSystemSettingValue NewSetting(string name, string? stringValue = null, string? encryptedValue = null, int? intValue = null, bool checkboxValue = false)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name },
            StringValue = stringValue,
            StringEncryptedValue = encryptedValue,
            IntValue = intValue,
            CheckboxValue = checkboxValue
        };
    }

    [Test]
    public void OpenImportConnection_WhenResolvedViaPinAndTheConnectionFails_CloseImportConnectionReturnsThePinCleared()
    {
        using var connector = new LdapConnector();
        var pinnedPersistedData = JsonSerializer.Serialize(new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            PinnedDirectoryServer = "127.0.0.1", // nothing listening on port 1 below
            HighestCommittedUsn = 123456,
            InvocationId = Guid.NewGuid()
        });

        var settingValues = BuildRefusedConnectionSettingValues("host-not-used.jim.test");

        Assert.That(() => connector.OpenImportConnection(settingValues, pinnedPersistedData, Logger), Throws.Exception);

        var closeResult = connector.CloseImportConnection();

        Assert.That(closeResult, Is.Not.Null, "A failed connection through a pin must invalidate it");
        var updated = JsonSerializer.Deserialize<LdapConnectorRootDse>(closeResult!);
        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.PinnedDirectoryServer, Is.Null, "The pin must be cleared");
        Assert.That(updated.HighestCommittedUsn, Is.EqualTo(123456), "The watermark must survive the invalidation untouched");
    }

    [Test]
    public void OpenImportConnection_WhenResolvedViaHostAndTheConnectionFails_CloseImportConnectionReturnsNull()
    {
        using var connector = new LdapConnector();
        var settingValues = BuildRefusedConnectionSettingValues("127.0.0.1");

        // No persisted data at all, so resolution falls back to Host.
        Assert.That(() => connector.OpenImportConnection(settingValues, null, Logger), Throws.Exception);

        var closeResult = connector.CloseImportConnection();

        Assert.That(closeResult, Is.Null, "A connection resolved via Host has no pin to invalidate");
    }

    [Test]
    public void OpenImportConnection_WhenResolvedViaPreferredSettingAndTheConnectionFails_CloseImportConnectionReturnsNull()
    {
        using var connector = new LdapConnector();
        var pinnedPersistedData = JsonSerializer.Serialize(new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            PinnedDirectoryServer = "127.0.0.1"
        });

        // A configured Preferred Domain Controller outranks the pin, and its own failure is not a pin to invalidate.
        var settingValues = BuildRefusedConnectionSettingValues("host-not-used.jim.test", preferredDomainController: "127.0.0.1");

        Assert.That(() => connector.OpenImportConnection(settingValues, pinnedPersistedData, Logger), Throws.Exception);

        var closeResult = connector.CloseImportConnection();

        Assert.That(closeResult, Is.Null, "A connection resolved via the Preferred Domain Controller setting has no pin to invalidate");
    }

    [Test]
    public void CloseImportConnection_AfterASuccessfulOpenIsNeverReached_DoesNotThrow()
    {
        // Belt-and-braces: closing without ever having opened must remain safe, as it was before this slice.
        using var connector = new LdapConnector();

        Assert.DoesNotThrow(() => connector.CloseImportConnection());
    }

    #endregion
}
