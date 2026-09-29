// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A Delta Import on Active Directory and Samba AD reads what changed by comparing uSNChanged against a persisted
/// highestCommittedUSN, but a USN stream belongs to one invocationId. A domain controller restored from a backup, a
/// snapshot or a checkpoint is issued a new invocationId and its USNs restart from the restored point, so changes
/// made after the restore reuse USN values the watermark has already passed and a Delta Import would silently miss
/// them. These tests drive the whole import (root DSE read, continuity check, change search, persisted state)
/// against a scripted directory, because the guarantees that matter are about the order of those steps and about
/// what the persisted state carries, which no test of one step can show.
/// </summary>
[TestFixture]
public class LdapConnectorImportInvocationIdTests
{
    private const string PartitionDn = "DC=corp,DC=local";
    private const string ContainerDn = "OU=Users,DC=corp,DC=local";
    private const string NtdsSettingsDn = "CN=NTDS Settings,CN=DC1,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=local";
    private const string Dc1 = "dc1.corp.local";
    private const string Dc2 = "dc2.corp.local";
    private const string SambaVendorName = "Samba Team (http://www.samba.org)";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    private static readonly Guid Before = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AfterRestore = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private ScriptedDirectory _directory = null!;
    private Mock<ILdapOperationExecutor> _executor = null!;
    private List<SearchRequest> _sent = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = new ScriptedDirectory { InvocationId = Before, HighestCommittedUsn = 100, DnsHostName = Dc1 };
        _sent = [];

        _executor = new Mock<ILdapOperationExecutor>();
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) => Answer(request));
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Returns((DirectoryRequest request, TimeSpan _) => Answer(request));
        _executor.Setup(x => x.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .ReturnsAsync((DirectoryRequest request) => Answer(request));
    }

    #region Delta Import: the invocationId changed

    // LdapDirectoryType is internal, so a public test method names the family it is run against instead.
    [TestCase(nameof(LdapDirectoryType.ActiveDirectory))]
    [TestCase(nameof(LdapDirectoryType.SambaAD))]
    public void GetDeltaImportObjectsAsync_InvocationIdDiffersFromTheWatermarks_FailsBeforeReadingAnyChange(string directoryTypeName)
    {
        // The directory was restored: same name, new invocationId, USNs back at the restored point.
        var directoryType = Enum.Parse<LdapDirectoryType>(directoryTypeName);
        _directory.VendorName = VendorNameOf(directoryType);
        _directory.InvocationId = AfterRestore;
        _directory.HighestCommittedUsn = 90;
        var import = NewImport(ScopedConnectedSystem(), ConnectedSystemRunType.DeltaImport, PersistedState(Before, usn: 100, directoryType));

        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(() => import.GetDeltaImportObjectsAsync());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain(Before.ToString()).And.Contain(AfterRestore.ToString()), "the error names both invocationIds");
            Assert.That(failure.Message, Does.Contain("restored from a backup or snapshot"));
            Assert.That(failure.Message, Does.Contain("Full Import"));
            Assert.That(_sent.Select(r => r.DistinguishedName ?? string.Empty), Is.EqualTo(new[] { string.Empty, NtdsSettingsDn }),
                "only the root DSE and the NTDS Settings object are read: no readiness check, no change search, no tombstone search");
        }
    }

    [Test]
    public void GetDeltaImportObjectsAsync_InvocationIdDiffersAndThePinWasCleared_NamesTheInvocationIdsNotOnlyTheServer()
    {
        // The pinned domain controller could not be reached, so the pin was cleared and the run went through Host to a
        // different domain controller. The watermark came from dc1; this connection is to dc2.
        _directory.InvocationId = AfterRestore;
        _directory.DnsHostName = Dc2;
        var import = NewImport(ScopedConnectedSystem(), ConnectedSystemRunType.DeltaImport, PersistedState(Before, usn: 100, pin: null), connectedServer: "corp.local");

        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(() => import.GetDeltaImportObjectsAsync());

        Assert.That(failure!.Message, Does.Contain(Before.ToString()).And.Contain(AfterRestore.ToString()));
    }

    #endregion

    #region Delta Import: the invocationId is the same, or not known

    [Test]
    public async Task GetDeltaImportObjectsAsync_InvocationIdMatches_ReadsChangesFromTheWatermarkAndAdvancesItAsync()
    {
        _directory.HighestCommittedUsn = 250;
        var import = NewImport(ScopedConnectedSystem(), ConnectedSystemRunType.DeltaImport, PersistedState(Before, usn: 100));

        var result = await import.GetDeltaImportObjectsAsync();

        var recorded = Read(result.PersistedConnectorData);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent.Any(r => r.DistinguishedName == ContainerDn && r.Filter?.ToString() == "(&(objectClass=user)(uSNChanged>=101))"), Is.True,
                "the change search reads from the stored watermark");
            Assert.That(recorded.InvocationId, Is.EqualTo(Before));
            Assert.That(recorded.HighestCommittedUsn, Is.EqualTo(250));
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_StateWithoutAnInvocationId_AcceptsItAndRecordsTheCurrentOneAsync()
    {
        // State written before the invocationId was recorded has no such property at all.
        var legacyState = WithoutProperty(PersistedState(Before, usn: 100), nameof(LdapConnectorRootDse.InvocationId));
        Assert.That(Read(legacyState).InvocationId, Is.Null, "precondition: the legacy state genuinely carries no invocationId");
        _directory.InvocationId = AfterRestore;
        _directory.HighestCommittedUsn = 180;
        var import = NewImport(ScopedConnectedSystem(), ConnectedSystemRunType.DeltaImport, legacyState);

        var result = await import.GetDeltaImportObjectsAsync();

        var recorded = Read(result.PersistedConnectorData);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent.Any(r => r.DistinguishedName == ContainerDn), Is.True, "unknown identity is accepted, so the changes are read");
            Assert.That(recorded.InvocationId, Is.EqualTo(AfterRestore), "and the current invocationId is recorded for the next run to compare against");
            Assert.That(recorded.HighestCommittedUsn, Is.EqualTo(180));
        }
    }

    [Test]
    public void GetDeltaImportObjectsAsync_StateWithoutAnInvocationIdFromAnotherHost_StillFailsOnTheServerName()
    {
        // Deliberate: an unknown invocationId is accepted only while nothing else says the server differs. A different
        // dnsHostName is conclusive by itself, so the weaker check is kept for state that predates the invocationId.
        var legacyState = WithoutProperty(PersistedState(Before, usn: 100), nameof(LdapConnectorRootDse.InvocationId));
        _directory.DnsHostName = Dc2;
        var import = NewImport(ScopedConnectedSystem(), ConnectedSystemRunType.DeltaImport, legacyState, connectedServer: Dc2);

        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(() => import.GetDeltaImportObjectsAsync());

        Assert.That(failure!.Message, Does.Contain(Dc1).And.Contain(Dc2));
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_CurrentInvocationIdUnreadable_ProceedsBecauseIdentityUnknownIsNotAFailureAsync()
    {
        _directory.NtdsSettingsReadRefused = true;
        var import = NewImport(ScopedConnectedSystem(), ConnectedSystemRunType.DeltaImport, PersistedState(Before, usn: 100));

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent.Any(r => r.DistinguishedName == ContainerDn), Is.True);
            Assert.That(Read(result.PersistedConnectorData).InvocationId, Is.Null);
        }
    }

    #endregion

    #region Pinned domain controller

    [Test]
    public async Task GetDeltaImportObjectsAsync_ConnectedThroughThePinnedServer_ComparesAgainstTheServerTheWatermarkCameFromAsync()
    {
        _directory.HighestCommittedUsn = 140;
        var import = NewImport(ScopedConnectedSystem(), ConnectedSystemRunType.DeltaImport, PersistedState(Before, usn: 100, pin: Dc1), connectedServer: Dc1);

        var result = await import.GetDeltaImportObjectsAsync();

        var recorded = Read(result.PersistedConnectorData);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded.PinnedDirectoryServer, Is.EqualTo(Dc1), "the pin travels with the watermark and the invocationId it belongs to");
            Assert.That(recorded.InvocationId, Is.EqualTo(Before));
            Assert.That(recorded.DnsHostName, Is.EqualTo(Dc1));
        }
    }

    #endregion

    #region Full Import

    [TestCase(nameof(LdapDirectoryType.ActiveDirectory))]
    [TestCase(nameof(LdapDirectoryType.SambaAD))]
    public async Task GetFullImportObjectsAsync_ActiveDirectoryFamily_RecordsTheInvocationIdAlongsideTheWatermarkAsync(string directoryTypeName)
    {
        var directoryType = Enum.Parse<LdapDirectoryType>(directoryTypeName);
        _directory.VendorName = VendorNameOf(directoryType);
        _directory.HighestCommittedUsn = 4200;
        var import = NewImport(EmptyConnectedSystem(), ConnectedSystemRunType.FullImport, persistedState: null);

        var result = await import.GetFullImportObjectsAsync();

        var recorded = Read(result.PersistedConnectorData);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded.DirectoryType, Is.EqualTo(directoryType));
            Assert.That(recorded.InvocationId, Is.EqualTo(Before));
            Assert.That(recorded.HighestCommittedUsn, Is.EqualTo(4200));
            Assert.That(recorded.DnsHostName, Is.EqualTo(Dc1));
            Assert.That(recorded.PinnedDirectoryServer, Is.EqualTo(Dc1), "the invocationId is per domain controller, so the pin recorded is the one it was read from");
        }
    }

    [Test]
    public async Task RestoreLifecycle_DeltaImportFailsAfterARestoreAndAFullImportReestablishesTheBaselineAsync()
    {
        // 1. A Full Import baselines the directory as it is.
        var baseline = (await NewImport(EmptyConnectedSystem(), ConnectedSystemRunType.FullImport, persistedState: null).GetFullImportObjectsAsync()).PersistedConnectorData;
        Assert.That(Read(baseline).InvocationId, Is.EqualTo(Before));

        // 2. A Delta Import against the unchanged directory is clean and moves the watermark on.
        _directory.HighestCommittedUsn = 120;
        var afterDelta = (await NewImport(ScopedConnectedSystem(), ConnectedSystemRunType.DeltaImport, baseline).GetDeltaImportObjectsAsync()).PersistedConnectorData;
        Assert.That(Read(afterDelta).HighestCommittedUsn, Is.EqualTo(120));

        // 3. The domain controller is restored: a new invocationId, and USNs back where the backup left them.
        _directory.InvocationId = AfterRestore;
        _directory.HighestCommittedUsn = 90;
        _sent.Clear();
        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(
            () => NewImport(ScopedConnectedSystem(), ConnectedSystemRunType.DeltaImport, afterDelta).GetDeltaImportObjectsAsync());
        Assert.That(failure!.Message, Does.Contain(Before.ToString()).And.Contain(AfterRestore.ToString()));
        Assert.That(_sent.Any(r => r.DistinguishedName == ContainerDn), Is.False, "nothing was read while the watermark was untrustworthy");

        // 4. A Full Import re-establishes the baseline against the restored directory.
        var rebaselined = (await NewImport(EmptyConnectedSystem(), ConnectedSystemRunType.FullImport, afterDelta).GetFullImportObjectsAsync()).PersistedConnectorData;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Read(rebaselined).InvocationId, Is.EqualTo(AfterRestore));
            Assert.That(Read(rebaselined).HighestCommittedUsn, Is.EqualTo(90));
        }

        // 5. The next Delta Import is clean, reading from the restored directory's own USN stream.
        _sent.Clear();
        await NewImport(ScopedConnectedSystem(), ConnectedSystemRunType.DeltaImport, rebaselined).GetDeltaImportObjectsAsync();
        Assert.That(_sent.Any(r => r.DistinguishedName == ContainerDn && r.Filter?.ToString() == "(&(objectClass=user)(uSNChanged>=91))"), Is.True);
    }

    #endregion

    #region Helpers

    /// <summary>What the scripted directory says about itself, changed between steps to stand for a restore.</summary>
    private sealed class ScriptedDirectory
    {
        internal Guid InvocationId { get; set; }
        internal long HighestCommittedUsn { get; set; }
        internal string DnsHostName { get; set; } = Dc1;
        internal string? VendorName { get; set; }
        internal bool NtdsSettingsReadRefused { get; set; }
    }

    private DirectoryResponse Answer(DirectoryRequest directoryRequest)
    {
        var request = (SearchRequest)directoryRequest;
        _sent.Add(request);

        if (request.Attributes.Contains("HighestCommittedUSN"))
            return LdapTestResponses.SearchResponseWithEntries(RootDseEntry());

        if (string.Equals(request.DistinguishedName, NtdsSettingsDn, StringComparison.OrdinalIgnoreCase))
        {
            if (_directory.NtdsSettingsReadRefused)
                throw new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access rights");

            return LdapTestResponses.SearchResponseWithBinary(NtdsSettingsDn, ("invocationId", [_directory.InvocationId.ToByteArray()]));
        }

        return LdapTestResponses.EmptySearchResponse();
    }

    private SearchResultEntry RootDseEntry()
    {
        var attributes = new List<(string Name, string[] Values)>
        {
            ("DNSHostName", [_directory.DnsHostName]),
            ("HighestCommittedUSN", [_directory.HighestCommittedUsn.ToString()]),
            ("supportedCapabilities", [LdapConnectorConstants.LDAP_CAP_ACTIVE_DIRECTORY_OID]),
            ("dsServiceName", [NtdsSettingsDn]),
            ("namingContexts", [PartitionDn])
        };
        if (_directory.VendorName != null)
            attributes.Add(("vendorName", [_directory.VendorName]));

        return LdapTestResponses.EntryWithValues(string.Empty, attributes.ToArray());
    }

    /// <summary>Samba AD is told apart from Active Directory by its vendorName; both advertise the Active Directory capability.</summary>
    private static string? VendorNameOf(LdapDirectoryType directoryType) => directoryType == LdapDirectoryType.SambaAD ? SambaVendorName : null;

    private LdapConnectorImport NewImport(ConnectedSystem connectedSystem, ConnectedSystemRunType runType, string? persistedState, string connectedServer = Dc1)
    {
        var import = new LdapConnectorImport(connectedSystem,
            new ConnectedSystemRunProfile { Name = runType.ToString(), RunType = runType, PageSize = 500 },
            new LdapConnection("localhost"), null, 1, [], persistedState, null, connectedServer, _ => true, Logger,
            CancellationToken.None, new RecordingConnectorProgress());
        import.Executor = _executor.Object;
        return import;
    }

    /// <summary>A Connected System with nothing selected, so a Full Import stops after recording the baseline.</summary>
    private static ConnectedSystem EmptyConnectedSystem() => new() { Name = "Active Directory", ObjectTypes = [], Partitions = [] };

    private static ConnectedSystem ScopedConnectedSystem() => new()
    {
        Name = "Active Directory",
        ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 1,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                    new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Text, Selected = true }
                ]
            }
        ],
        Partitions =
        [
            new ConnectedSystemPartition
            {
                Id = 1,
                ExternalId = PartitionDn,
                Name = "corp.local",
                Selected = true,
                Containers = [new ConnectedSystemContainer { Id = 1, ExternalId = ContainerDn, Name = "Users", Selected = true }]
            }
        ]
    };

    private static string PersistedState(Guid? invocationId, long usn, LdapDirectoryType directoryType = LdapDirectoryType.ActiveDirectory, string? pin = Dc1) =>
        JsonSerializer.Serialize(new LdapConnectorRootDse
        {
            DirectoryType = directoryType,
            DnsHostName = Dc1,
            HighestCommittedUsn = usn,
            InvocationId = invocationId,
            PinnedDirectoryServer = pin
        });

    private static string WithoutProperty(string json, string propertyName)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove(propertyName);
        return node.ToJsonString();
    }

    private static LdapConnectorRootDse Read(string? json)
    {
        Assert.That(json, Is.Not.Null, "the import returned no persisted connector data");
        return JsonSerializer.Deserialize<LdapConnectorRootDse>(json!)!;
    }

    #endregion
}
