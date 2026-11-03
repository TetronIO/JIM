// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Text.Json;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A directory that keeps a changelog (389 Directory Server, and the generic fallback) records every write in one flat,
/// numbered log shared by every partition, while JIM holds a watermark for each partition (#2715). These tests drive
/// the whole import against a scripted 389 Directory Server to show that a Delta Import reads the log once, from the
/// oldest of its partitions' watermarks, judges each change by the watermark of the partition it lies in, and that a
/// Delta Import of one partition leaves another's changes for that partition's own Delta Import to read.
/// </summary>
[TestFixture]
public class LdapConnectorImportChangelogPartitionWatermarkTests
{
    private const string ChangelogDn = "cn=changelog";
    private const string CorpDn = "dc=corp,dc=local";
    private const string PartnersDn = "dc=partners,dc=local";
    private const string ServerName = "ds1.corp.local";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    /// <summary>The changelog's records by change number: the kind of write and the entry it was made to.</summary>
    private SortedDictionary<long, (string ChangeType, string TargetDn)> _changelog = null!;

    /// <summary>The entries the directory holds now, by DN, which a change read fetches its targets from.</summary>
    private Dictionary<string, SearchResultEntry> _entries = null!;

    /// <summary>The oldest change number the changelog still holds, as its rootDSE advertises it; null for the lowest it holds.</summary>
    private long? _firstChangeNumber;

    private List<SearchRequest> _sent = null!;
    private Mock<ILdapOperationExecutor> _executor = null!;
    private ConnectedSystem _connectedSystem = null!;

    [SetUp]
    public void SetUp()
    {
        _changelog = [];
        _entries = new Dictionary<string, SearchResultEntry>(StringComparer.OrdinalIgnoreCase);
        _firstChangeNumber = null;
        _sent = [];
        _connectedSystem = ConnectedSystemWith(Partition(1, CorpDn, "corp.local"), Partition(2, PartnersDn, "partners.local"));

        _executor = new Mock<ILdapOperationExecutor>();
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) => Answer(request));
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Returns((DirectoryRequest request, TimeSpan _) => Answer(request));
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_ChangelogDeltaOfOnePartitionThenAnother_TheSecondImportsTheChangeTheFirstPassedAsync()
    {
        // The #2715 timeline, on a changelog. An unscoped Full Import left both partitions at change 1200. Then pat
        // changes in partners.local (1201) and cal in corp.local (1202). Delta Import (Corp) reads from 1201 and moves
        // corp.local to 1202; Delta Import (Partners) must still read pat's change, which the first run passed over.
        Write(1200, "modify", CorpPerson("ann"));
        var held = new Dictionary<int, string?> { [1] = ChangelogWatermark(1200), [2] = ChangelogWatermark(1200) };
        Write(1201, "modify", PartnersPerson("pat"));
        Write(1202, "modify", CorpPerson("cal"));

        var corpRun = await NewImport(WatermarksOf(held, 1), targetedPartition: Corp()).GetDeltaImportObjectsAsync();
        Record(corpRun, held);
        var partnersRun = await NewImport(WatermarksOf(held, 2), targetedPartition: Partners()).GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ImportedDns(corpRun), Is.EqualTo(new[] { CorpPerson("cal") }), "the corp.local run imports only corp.local's change");
            Assert.That(corpRun.PartitionWatermarks?.Keys, Is.EquivalentTo(new[] { 1 }), "it moves only corp.local's watermark");
            Assert.That(ChangeNumberOf(corpRun, 1), Is.EqualTo(1202));
            Assert.That(ImportedDns(partnersRun), Is.EqualTo(new[] { PartnersPerson("pat") }), "the partners.local run imports the change the corp.local run passed over");
            Assert.That(ChangeReadFilters(), Is.EqualTo(new[] { "(changeNumber>=1201)", "(changeNumber>=1201)" }), "each run reads from its own partition's watermark");
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_ChangelogUnscopedDeltaWithDifferentWatermarks_ImportsOnlyWhatEachPartitionHasNotReadAsync()
    {
        // Delta Import (Corp) has read corp.local up to 1202; partners.local was last read at 1200. An unscoped Delta
        // Import reads the log from 1201, the change after the oldest, and judges each change by its own partition.
        Write(1200, "modify", CorpPerson("ann"));
        Write(1201, "modify", PartnersPerson("pat"));
        Write(1202, "modify", CorpPerson("cal"));
        Write(1203, "modify", CorpPerson("dee"));
        var held = new Dictionary<int, string?> { [1] = ChangelogWatermark(1202), [2] = ChangelogWatermark(1200) };

        var result = await NewImport(held).GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ChangeReadFilters(), Is.EqualTo(new[] { "(changeNumber>=1201)" }), "the log is read once, from the oldest watermark");
            Assert.That(ImportedDns(result), Is.EqualTo(new[] { PartnersPerson("pat"), CorpPerson("dee") }),
                "cal's change at 1202 was read by corp.local's last import, so it is not fetched again");
            Assert.That(result.PartitionWatermarks?.Keys, Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(ChangeNumberOf(result, 1), Is.EqualTo(1203));
            Assert.That(ChangeNumberOf(result, 2), Is.EqualTo(1203));
        }
    }

    [Test]
    public void GetDeltaImportObjectsAsync_ChangelogTrimmedPastOnePartitionsWatermark_RefusesNamingThatPartitionAsync()
    {
        // The changelog now starts at 1250. partners.local was last read at 1300, so it has lost nothing; corp.local was
        // last read at 1100, so the changes between were trimmed before it read them.
        Write(1100, "modify", CorpPerson("ann"));
        Write(1300, "modify", PartnersPerson("pat"));
        Write(1301, "modify", CorpPerson("cal"));
        _firstChangeNumber = 1250;
        var held = new Dictionary<int, string?>
        {
            [1] = new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Changelog, ChangeNumber = 1100 }.Serialise(),
            [2] = ChangelogWatermark(1300)
        };

        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(() => NewImport(held).GetDeltaImportObjectsAsync());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain("the partition corp.local").And.Not.Contain("partners.local"), "only the partition whose watermark was trimmed past is named");
            Assert.That(failure.Message, Does.Contain("starts at change number 1250"));
            Assert.That(ChangeReadFilters(), Is.Empty, "nothing is read while a watermark is untrustworthy");
        }
    }

    #region Helpers

    /// <summary>
    /// The scripted 389 Directory Server: its rootDSE advertises the changelog and the change numbers it holds, the
    /// changelog and the Retro Changelog plug-in can be read, a change read answers every record from the number asked
    /// for, a record read by its own name answers its changeTime, and an entry read by its DN answers as it is now.
    /// </summary>
    private DirectoryResponse Answer(DirectoryRequest directoryRequest)
    {
        var request = (SearchRequest)directoryRequest;
        _sent.Add(request);
        var dn = request.DistinguishedName;

        if (string.IsNullOrEmpty(dn))
            return RootDse();

        if (request.Scope == SearchScope.OneLevel && Same(dn, ChangelogDn))
            return ChangeRead(request.Filter.ToString()!);

        if (request.Scope != SearchScope.Base)
            return LdapTestResponses.EmptySearchResponse();

        if (Same(dn, ChangelogDn))
            return LdapTestResponses.SearchResponseWith(ChangelogDn, ("objectClass", "top"));

        if (Same(dn, LdapConnectorConstants.RETRO_CHANGELOG_PLUGIN_DN))
            return LdapTestResponses.SearchResponseWith(dn, ("objectClass", "top"), (LdapConnectorConstants.RETRO_CHANGELOG_LOG_DELETED_ATTRIBUTE, "on"));

        if (ChangeNumberNamedBy(dn) is { } changeNumber)
        {
            return _changelog.ContainsKey(changeNumber)
                ? LdapTestResponses.SearchResponseWith(dn, ("changeTime", ChangeTimeOf(changeNumber)))
                : LdapTestResponses.EmptySearchResponse();
        }

        return _entries.TryGetValue(dn, out var entry)
            ? LdapTestResponses.SearchResponseWithEntries(entry)
            : LdapTestResponses.EmptySearchResponse();
    }

    private SearchResponse RootDse()
    {
        var newest = _changelog.Count == 0 ? 0 : _changelog.Keys.Max();
        var oldest = _firstChangeNumber ?? (_changelog.Count == 0 ? 0 : _changelog.Keys.Min());
        return LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues(string.Empty,
            ("vendorName", ["389 Project"]),
            ("namingContexts", [CorpDn, PartnersDn]),
            ("changelog", [ChangelogDn]),
            ("firstChangeNumber", [oldest.ToString(CultureInfo.InvariantCulture)]),
            ("lastChangeNumber", [newest.ToString(CultureInfo.InvariantCulture)])));
    }

    /// <summary>Every record from the change number a change read's filter asks for, as the read asks for them.</summary>
    private SearchResponse ChangeRead(string filter)
    {
        const string prefix = "(changeNumber>=";
        Assert.That(filter, Does.StartWith(prefix).And.EndWith(")"), "a change read asks for the records from a change number");
        var from = long.Parse(filter[prefix.Length..^1], CultureInfo.InvariantCulture);

        return LdapTestResponses.SearchResponseWithEntries(_changelog
            .Where(record => record.Key >= from)
            .Select(record => LdapTestResponses.Entry($"changeNumber={record.Key},{ChangelogDn}",
                ("changeNumber", record.Key.ToString(CultureInfo.InvariantCulture)),
                ("changeType", record.Value.ChangeType),
                ("targetDN", record.Value.TargetDn)))
            .ToArray());
    }

    /// <summary>A write the changelog records, and the entry as it now stands.</summary>
    private void Write(long changeNumber, string changeType, string targetDn)
    {
        _changelog[changeNumber] = (changeType, targetDn);
        _entries[targetDn] = LdapTestResponses.EntryWithValues(targetDn,
            ("objectClass", ["top", "person", "inetOrgPerson"]),
            ("entryUUID", [Guid.NewGuid().ToString()]));
    }

    private static long? ChangeNumberNamedBy(string dn)
    {
        const string prefix = "changeNumber=";
        var suffix = "," + ChangelogDn;
        return dn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && dn.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? long.Parse(dn[prefix.Length..^suffix.Length], CultureInfo.InvariantCulture)
            : null;
    }

    private static string ChangeTimeOf(long changeNumber) => $"20261103{changeNumber:D6}Z";

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>The filters of the change reads the runs sent, in order.</summary>
    private List<string> ChangeReadFilters() =>
        _sent.Where(r => r.Scope == SearchScope.OneLevel && Same(r.DistinguishedName, ChangelogDn))
            .Select(r => r.Filter.ToString()!)
            .ToList();

    private static List<string> ImportedDns(ConnectedSystemImportResult result) =>
        result.ImportObjects.Select(importObject => importObject.Attributes.Single(a => a.Name == "distinguishedName").StringValues.Single()).ToList();

    private static long? ChangeNumberOf(ConnectedSystemImportResult result, int partitionId) =>
        LdapPartitionWatermark.Parse(result.PartitionWatermarks![partitionId], Logger)?.ChangeNumber;

    /// <summary>What JIM records at the end of a run: the watermark it reached for each partition it names, the others kept.</summary>
    private static void Record(ConnectedSystemImportResult result, Dictionary<int, string?> held)
    {
        foreach (var (partitionId, watermark) in result.PartitionWatermarks!)
            held[partitionId] = watermark;
    }

    /// <summary>What JIM hands a run: the watermark it holds for each partition the run targets.</summary>
    private static Dictionary<int, string?> WatermarksOf(Dictionary<int, string?> held, params int[] partitionIds) =>
        partitionIds.ToDictionary(id => id, id => held[id]);

    private static string ChangelogWatermark(long changeNumber) =>
        new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Changelog, ChangeNumber = changeNumber, ChangeTime = ChangeTimeOf(changeNumber) }.Serialise();

    private static string CorpPerson(string uid) => $"uid={uid},ou=people,{CorpDn}";

    private static string PartnersPerson(string uid) => $"uid={uid},ou=people,{PartnersDn}";

    private ConnectedSystemPartition Corp() => _connectedSystem.Partitions!.Single(p => p.Id == 1);

    private ConnectedSystemPartition Partners() => _connectedSystem.Partitions!.Single(p => p.Id == 2);

    private static ConnectedSystemPartition Partition(int id, string dn, string name) => new()
    {
        Id = id,
        ExternalId = dn,
        Name = name,
        Selected = true,
        Containers = [new ConnectedSystemContainer { Id = id * 10, ExternalId = "ou=people," + dn, Name = "people", Selected = true }]
    };

    /// <summary>A Connected System importing inetOrgPerson from the given partitions, each with its people Container selected.</summary>
    private static ConnectedSystem ConnectedSystemWith(params ConnectedSystemPartition[] partitions)
    {
        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "inetOrgPerson",
            Selected = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "entryUUID", Type = AttributeDataType.Text, IsExternalId = true, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "objectClass", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "distinguishedName", Type = AttributeDataType.Text, Selected = true }
            ]
        };

        return new ConnectedSystem { Name = "Directory", ObjectTypes = [objectType], Partitions = [.. partitions] };
    }

    private LdapConnectorImport NewImport(IReadOnlyDictionary<int, string?> partitionWatermarks, ConnectedSystemPartition? targetedPartition = null)
    {
        var record = JsonSerializer.Serialize(new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.DirectoryServer389, DnsHostName = ServerName });
        var import = new LdapConnectorImport(_connectedSystem,
            new ConnectedSystemRunProfile { Name = "Delta Import", RunType = ConnectedSystemRunType.DeltaImport, PageSize = 500, Partition = targetedPartition },
            new LdapConnection("localhost"), null, 1, [], record, partitionWatermarks, null, ServerName, _ => true, Logger,
            CancellationToken.None, new RecordingConnectorProgress());
        import.Executor = _executor.Object;
        return import;
    }

    #endregion
}
