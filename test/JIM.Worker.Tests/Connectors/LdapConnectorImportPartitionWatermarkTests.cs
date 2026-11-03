// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Text.Json;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Each LDAP Partition keeps its own Delta Import watermark, which JIM holds and hands back at the start of each run
/// (#2715). These tests drive the whole import against a scripted directory to show what the import does with them,
/// whichever change source reads the directory: when it reads changes and when it reads every object instead, that
/// every page of a run decides alike, and that a run returns a watermark for the Partitions it read and no other.
/// </summary>
[TestFixture]
public class LdapConnectorImportPartitionWatermarkTests
{
    private const string AccesslogDn = "cn=accesslog";
    private const string CorpDn = "dc=corp,dc=local";
    private const string PartnersDn = "dc=partners,dc=local";
    private const string CorpPeopleDn = "ou=people," + CorpDn;
    private const string PartnersPeopleDn = "ou=people," + PartnersDn;
    private const string NtdsSettingsDn = "CN=NTDS Settings,CN=DC1,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=local";
    private const string Dc1 = "dc1.corp.local";
    private const string Watermark = "20260921080000.000000Z";
    private const string EmeaDn = "dc=emea," + CorpDn;
    private const string EmeaPeopleDn = "ou=people," + EmeaDn;
    private const string Alice = "uid=alice," + CorpPeopleDn;
    private const string Cal = "uid=cal," + CorpPeopleDn;
    private const string Dan = "uid=dan," + CorpPeopleDn;
    private const string Pat = "uid=pat," + PartnersPeopleDn;
    private const string Eve = "uid=eve," + EmeaPeopleDn;
    private const string Zoe = "uid=zoe," + EmeaPeopleDn;
    private const string Max = "uid=max," + EmeaPeopleDn;
    private const string UnderAnotherSuffix = "uid=bob,ou=people,dc=other,dc=local";
    private const string At0900 = "20260921090000.000001Z";
    private const string At0910 = "20260921091000.000001Z";
    private const string At0915 = "20260921091500.000001Z";
    private const string At0920 = "20260921092000.000001Z";
    private const string At0930 = "20260921093000.000001Z";
    private const string At0940 = "20260921094000.000001Z";
    private static readonly Guid Dc1InvocationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Dc2InvocationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly byte[] Cookie = [1, 2, 3, 4];
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    private LdapDirectoryType _directoryType;
    private List<SearchRequest> _sent = null!;
    private Mock<ILdapOperationExecutor> _executor = null!;

    /// <summary>Each write the scripted OpenLDAP accesslog holds, in the order it logged them.</summary>
    private List<LoggedWrite> _logged = null!;

    /// <summary>Whether the scripted change read leaves a cookie on its first page, so the run reads a second.</summary>
    private bool _changeReadHasASecondPage;

    [SetUp]
    public void SetUp()
    {
        _directoryType = LdapDirectoryType.OpenLDAP;
        _sent = [];
        _logged = [];
        _changeReadHasASecondPage = false;

        _executor = new Mock<ILdapOperationExecutor>();
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) => Answer(request));
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Returns((DirectoryRequest request, TimeSpan _) => Answer(request));
        _executor.Setup(x => x.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .ReturnsAsync((DirectoryRequest request) => Answer(request));
    }

    #region Reading every object instead of changes

    [Test]
    public async Task GetDeltaImportObjectsAsync_APartitionWithoutAWatermark_ReadsEveryObjectInTheRunsPartitionsAndNamesItAsync()
    {
        // corp.local has a watermark; partners.local, newly selected, has none. The run cannot read changes in one
        // partition and every object in the other (#2720 decides deletions for the whole run), so it reads every
        // object in both, and says which partition had none.
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP),
            new Dictionary<int, string?> { [1] = AccesslogWatermark(Watermark), [2] = null });

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.True, "JIM detects deletions by absence across the run's partitions");
            Assert.That(result.WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(result.WarningMessage, Does.Contain("JIM holds no Delta Import watermark for the partition partners.local"));
            Assert.That(result.WarningMessage, Does.Not.Contain("corp.local"), "only the partition that had none is named");
            Assert.That(FullReadSearchBases(), Is.EquivalentTo(new[] { CorpPeopleDn, PartnersPeopleDn }), "every object in both of the run's partitions is read");
            Assert.That(ChangeReads(), Is.Empty, "no changes are read");
            Assert.That(_sent.Count(r => string.IsNullOrEmpty(r.DistinguishedName)), Is.EqualTo(1),
                "the full read carries on from the first page's read of the directory, rather than reading it and taking the watermarks again");
            Assert.That(result.PartitionWatermarks?.Keys, Is.EquivalentTo(new[] { 1, 2 }), "both partitions are given a watermark to read changes after next time");
            Assert.That(result.PartitionWatermarks!.Values, Has.None.Null);
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_NoConnectedSystemRecord_ReadsEveryObjectInsteadOfFailingAsync()
    {
        // A Delta Import that is the Connected System's first run has nothing recorded about the directory at all. It
        // used to stop with "Run a full import first"; it now does what a Partition without a watermark does.
        var import = NewImport(ConnectedSystemRunType.DeltaImport, persistedState: null,
            new Dictionary<int, string?> { [1] = null, [2] = null });

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.True);
            Assert.That(result.WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(result.WarningMessage, Does.Contain("the partitions corp.local and partners.local"));
            Assert.That(FullReadSearchBases(), Is.EquivalentTo(new[] { CorpPeopleDn, PartnersPeopleDn }));
            Assert.That(Read(result.PersistedConnectorData).DirectoryType, Is.EqualTo(LdapDirectoryType.OpenLDAP), "the run records what the directory is");
            Assert.That(result.PartitionWatermarks?.Keys, Is.EquivalentTo(new[] { 1, 2 }));
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_UnreadableConnectedSystemRecord_ReadsEveryObjectInsteadOfFailingAsync()
    {
        var import = NewImport(ConnectedSystemRunType.DeltaImport, persistedState: "{ not a record",
            new Dictionary<int, string?> { [1] = AccesslogWatermark(Watermark), [2] = AccesslogWatermark(Watermark) });

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.True, "with nothing to say what the directory was, no watermark can be read against it");
            Assert.That(result.WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(ChangeReads(), Is.Empty);
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_WatermarkOfAnotherKind_ReadsEveryObjectAsync()
    {
        // The directory is OpenLDAP, read through its accesslog. corp.local's watermark was taken by another change
        // source (the Connected System once reached a directory that keeps a changelog): whatever positions it carries,
        // it is not one this directory's changes can be read after.
        var otherKind = new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Changelog,
            ChangeNumber = 1207,
            AccesslogTimestamp = Watermark,
            AccesslogTimestampIsLogged = true
        }.Serialise();
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP),
            new Dictionary<int, string?> { [1] = otherKind, [2] = AccesslogWatermark(Watermark) });

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.True);
            Assert.That(result.WarningMessage, Does.Contain("the partition corp.local"));
            Assert.That(ChangeReads(), Is.Empty);
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_FallbackOnALaterPage_StaysAFullReadAsync()
    {
        // Active Directory pages a full read across calls. Page 2 is handed the same record and the same watermarks as
        // page 1 (JIM fixes both for the run), and corp.local still has none, so page 2 carries on the full read from
        // the directory's cookie rather than reading changes: a page that answered differently would fail the run.
        _directoryType = LdapDirectoryType.ActiveDirectory;
        var connectedSystem = ConnectedSystemWith(Partition(1, CorpDn, "corp.local", CorpPeopleDn));
        var objectType = connectedSystem.ObjectTypes!.Single();
        var container = connectedSystem.Partitions!.Single().Containers!.Single();
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.ActiveDirectory),
            new Dictionary<int, string?> { [1] = null }, connectedSystem,
            paginationTokens: [new ConnectedSystemPaginationToken(LdapConnectorUtilities.GetPaginationTokenName(container, objectType), Cookie)]);

        var result = await import.GetDeltaImportObjectsAsync();

        var resumed = _sent.SingleOrDefault(r => r.DistinguishedName == CorpPeopleDn);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.True, "every page of the fallback says it read every object");
            Assert.That(result.WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(resumed?.Filter, Is.EqualTo("(objectClass=user)"), "the container's full read resumes, not a uSNChanged search");
            Assert.That(resumed?.Controls.OfType<PageResultRequestControl>().Single().Cookie, Is.EqualTo(Cookie));
            Assert.That(_sent.Any(r => string.IsNullOrEmpty(r.DistinguishedName)), Is.False, "the directory is read on the first page only");
            Assert.That(result.PartitionWatermarks, Is.Null, "the first page returned the watermarks; later pages return none");
        }
    }

    #endregion

    #region A run that targets no Partition

    [Test]
    public async Task GetDeltaImportObjectsAsync_RunTargetsNoPartition_ReadsNothingAndSaysSoWithoutFallingBackAsync()
    {
        // No partition is selected (or the hierarchy was never retrieved). There is nothing to read changes in, and
        // reading "every object" would read nothing while claiming a complete read; the run reads nothing, as a Full
        // Import of the same Run Profile does, and says why.
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP),
            new Dictionary<int, string?>(), ConnectedSystemWith());

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.False, "nothing was read, so nothing JIM holds can be judged missing");
            Assert.That(result.WarningErrorType, Is.Not.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(result.WarningMessage, Does.Contain("no selected partition"));
            Assert.That(result.ImportObjects, Is.Empty);
            Assert.That(ChangeReads(), Is.Empty);
            Assert.That(FullReadSearchBases(), Is.Empty);
            Assert.That(result.PersistedConnectorData, Is.Not.Null, "the directory is still read and recorded, as on any run");
            Assert.That(result.PartitionWatermarks, Is.Empty, "no partition is given a watermark");
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_RunTargetsNoPartitionAndNoConnectedSystemRecord_ReadsNothingRatherThanFallingBackAsync()
    {
        var import = NewImport(ConnectedSystemRunType.DeltaImport, persistedState: null, new Dictionary<int, string?>(), ConnectedSystemWith());

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.False);
            Assert.That(result.WarningErrorType, Is.Not.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(result.WarningMessage, Does.Contain("no selected partition"));
        }
    }

    #endregion

    #region What a run returns

    [Test]
    public async Task GetFullImportObjectsAsync_ScopedToOnePartition_ReturnsAWatermarkForThatPartitionOnlyAsync()
    {
        // A Full Import of corp.local reads nothing in partners.local, so it must not move partners.local's starting
        // point: the scoped Full Import half of #2715.
        var connectedSystem = ConnectedSystemWith(Partition(1, CorpDn, "corp.local", CorpPeopleDn), Partition(2, PartnersDn, "partners.local", PartnersPeopleDn));
        var import = NewImport(ConnectedSystemRunType.FullImport, RecordOf(LdapDirectoryType.OpenLDAP),
            new Dictionary<int, string?> { [1] = AccesslogWatermark(Watermark) }, connectedSystem, targetedPartition: connectedSystem.Partitions![0]);

        var result = await import.GetFullImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PartitionWatermarks?.Keys, Is.EquivalentTo(new[] { 1 }));
            Assert.That(FullReadSearchBases(), Is.EqualTo(new[] { CorpPeopleDn }));
        }
    }

    #endregion

    #region Reading each Partition's changes from its own watermark (OpenLDAP accesslog)

    [Test]
    public async Task GetDeltaImportObjectsAsync_TwoPartitionsWithDifferentWatermarks_ReadsEachFromItsOwnAsync()
    {
        // corp.local was last read at 09:20 and partners.local at 09:00. Each partition's clause carries its own
        // watermark, so the directory returns only what each has not read, rather than corp.local's writes since 09:00.
        Log(At0900, Alice);
        Log(At0910, Pat);
        Log(At0920, Cal);
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP), WatermarksOf((1, At0920), (2, At0900)));

        await import.GetDeltaImportObjectsAsync();

        Assert.That(ChangeReads().Single().Filter, Is.EqualTo(ChangeReadFilter((CorpDn, At0920), (PartnersDn, At0900))));
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_WriteBeforeOrAtItsPartitionsWatermark_IsNotImportedAsync()
    {
        // Dan's 09:10 write is before corp.local's watermark and Cal's 09:20 write is the one it names, so the run that
        // read corp.local to 09:20 imported both; Pat's 09:15 write is after partners.local's 09:00 watermark.
        Log(At0900, Alice);
        Log(At0910, Dan);
        Log(At0915, Pat);
        Log(At0920, Cal);
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP), WatermarksOf((1, At0920), (2, At0900)));

        await import.GetDeltaImportObjectsAsync();

        Assert.That(Fetched(), Is.EqualTo(new[] { Pat }));
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_NestedSuffixes_JudgesAWriteByTheLongestMatchingPartitionAsync()
    {
        // emea.corp.local is a suffix of its own beneath corp.local (a subordinate database), so its entries lie
        // within both, and corp.local's clause returns emea.corp.local's writes since corp.local's watermark. Each is
        // judged by emea.corp.local's own watermark: Eve's 09:20 write was read by the run that took 09:30.
        Log(At0900, Alice);
        Log(At0910, Cal);
        Log(At0920, Eve);
        Log(At0930, Zoe);
        Log(At0940, Max);
        var connectedSystem = ConnectedSystemWith(Partition(1, CorpDn, "corp.local", CorpPeopleDn), Partition(3, EmeaDn, "emea.corp.local", EmeaPeopleDn));
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP), WatermarksOf((1, At0900), (3, At0930)), connectedSystem);

        await import.GetDeltaImportObjectsAsync();

        Assert.That(Fetched(), Is.EqualTo(new[] { Cal, Max }));
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_DeltaOfOnePartitionThenDeltaOfAnother_TheSecondImportsTheChangeTheFirstPassedAsync()
    {
        // The timeline #2715 was found by. An unscoped Full Import at 09:00 left both partitions a watermark. Pat
        // changed in partners.local at 09:10 and Cal in corp.local at 09:20. Delta Import (Corp) at 09:30 reads Cal's
        // change and moves corp.local's watermark past Pat's; Delta Import (Partners) at 09:40 must still read Pat's.
        Log(At0900, Alice);
        Log(At0910, Pat);
        Log(At0920, Cal);
        var held = WatermarksOf((1, At0900), (2, At0900));
        var connectedSystem = ConnectedSystemWith(Partition(1, CorpDn, "corp.local", CorpPeopleDn), Partition(2, PartnersDn, "partners.local", PartnersPeopleDn));
        var corp = connectedSystem.Partitions![0];
        var partners = connectedSystem.Partitions![1];

        var corpsRun = await NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP), HandedTo(held, corp), connectedSystem, corp)
            .GetDeltaImportObjectsAsync();
        var corpsFetches = Fetched();
        Record(held, corpsRun);
        _sent.Clear();

        var partnersRun = await NewImport(ConnectedSystemRunType.DeltaImport, corpsRun.PersistedConnectorData, HandedTo(held, partners), connectedSystem, partners)
            .GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corpsFetches, Is.EqualTo(new[] { Cal }), "Delta Import (Corp) reads corp.local's change alone");
            Assert.That(ReachedFor(corpsRun, 1), Is.EqualTo(At0920), "and moves corp.local's watermark past Pat's write");
            Assert.That(held[2], Is.EqualTo(AccesslogWatermark(At0900)), "partners.local keeps the watermark it had");
            Assert.That(Fetched(), Is.EqualTo(new[] { Pat }), "Delta Import (Partners) imports the change Delta Import (Corp) passed");
            Assert.That(partnersRun.WarningErrorType, Is.Null);
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_ScopedFullImportThenDeltaOfAnotherPartition_TheDeltaImportsTheChangeTheFullImportPassedAsync()
    {
        // The scoped Full Import half of #2715: a Full Import of corp.local at 09:30 reads every object in corp.local
        // and takes a watermark past Pat's 09:10 write to partners.local, which it never read.
        Log(At0900, Alice);
        Log(At0910, Pat);
        Log(At0920, Cal);
        var held = WatermarksOf((1, At0900), (2, At0900));
        var connectedSystem = ConnectedSystemWith(Partition(1, CorpDn, "corp.local", CorpPeopleDn), Partition(2, PartnersDn, "partners.local", PartnersPeopleDn));
        var corp = connectedSystem.Partitions![0];
        var partners = connectedSystem.Partitions![1];

        var fullImport = await NewImport(ConnectedSystemRunType.FullImport, RecordOf(LdapDirectoryType.OpenLDAP), HandedTo(held, corp), connectedSystem, corp)
            .GetFullImportObjectsAsync();
        Record(held, fullImport);
        _sent.Clear();

        await NewImport(ConnectedSystemRunType.DeltaImport, fullImport.PersistedConnectorData, HandedTo(held, partners), connectedSystem, partners)
            .GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fullImport.PartitionWatermarks!.Keys, Is.EquivalentTo(new[] { 1 }), "the Full Import gives a watermark to the partition it read alone");
            Assert.That(Fetched(), Is.EqualTo(new[] { Pat }));
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_Capture_WalksFromTheOldestTargetWatermarkAsync()
    {
        // The directory cannot sort, so the next watermarks are found by walking the log. Every write any target
        // partition has yet to read is at or after the oldest of their watermarks, so the walk starts there (#2704),
        // within the run's partitions (#2712), once for the run rather than once per partition.
        Log(At0900, Alice);
        Log(At0910, Pat);
        Log(At0920, Cal);
        Log(At0930, Pat);
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP), WatermarksOf((1, At0920), (2, At0900)));

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Walks(), Is.EqualTo(new[] { WalkFrom(At0900, CorpDn, PartnersDn) }));
            Assert.That(ReachedFor(result, 1), Is.EqualTo(At0930), "the latest write within the run's partitions is after corp.local's watermark");
            Assert.That(ReachedFor(result, 2), Is.EqualTo(At0930), "and after partners.local's");
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_NothingNewWithinAQuietPartition_AdvancesItWithTheLogAsync()
    {
        // partners.local has been quiet: its last import took 09:30, another suffix's write. corp.local's latest write
        // is 09:10, older than that, so it is no watermark for partners.local; nor is standing still, which the purge
        // would overtake (#2712). partners.local takes the latest write anywhere from its own watermark instead.
        Log(At0900, Alice);
        Log(At0910, Cal);
        Log(At0930, UnderAnotherSuffix);
        Log(At0940, UnderAnotherSuffix);
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP), WatermarksOf((1, At0900), (2, At0930)));

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReachedFor(result, 1), Is.EqualTo(At0910), "corp.local takes its latest write");
            Assert.That(ReachedFor(result, 2), Is.EqualTo(At0940), "partners.local advances with the log, never back to an older write");
            Assert.That(Walks(), Is.EqualTo(new[] { WalkFrom(At0900, CorpDn, PartnersDn), WalkFrom(At0930) }),
                "the quiet partition's walk starts from its own watermark");
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_LaterPage_SendsTheSameFilterSoThePagingCookieStaysValidAsync()
    {
        // OpenLDAP rejects a paging cookie presented with a search other than the one it was issued for. Page 2 is
        // handed the same watermarks as page 1 (JIM fixes them for the run), so it asks exactly the same question.
        _changeReadHasASecondPage = true;
        Log(At0900, Alice);
        Log(At0910, Pat);
        Log(At0920, Cal);
        var handed = WatermarksOf((1, At0920), (2, At0900));

        var firstPage = await NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP), handed).GetDeltaImportObjectsAsync();
        var firstPagesRead = ChangeReads().Single();
        _sent.Clear();
        await NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP), handed, paginationTokens: firstPage.PaginationTokens).GetDeltaImportObjectsAsync();
        var secondPagesRead = ChangeReads().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondPagesRead.Controls.OfType<PageResultRequestControl>().Single().Cookie, Is.EqualTo(Cookie), "page 2 resumes from the directory's cursor");
            Assert.That(secondPagesRead.Filter, Is.EqualTo(firstPagesRead.Filter), "with the question page 1 asked");
            Assert.That(firstPagesRead.Filter, Is.EqualTo(ChangeReadFilter((CorpDn, At0920), (PartnersDn, At0900))));
        }
    }

    #endregion

    #region Continuity of each Partition's watermark (Active Directory)

    [Test]
    public void GetDeltaImportObjectsAsync_OnePartitionReadLastThroughAnotherDomainController_RefusesNamingThatPartitionAsync()
    {
        // corp.local was last read through dc1, which this run reached; partners.local was last read through dc2. A
        // single Connected System-level watermark recorded only the last run's domain controller, so partners.local's
        // USN would have been read against dc1 unnoticed.
        _directoryType = LdapDirectoryType.ActiveDirectory;
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.ActiveDirectory),
            new Dictionary<int, string?> { [1] = UsnWatermark(100, Dc1InvocationId), [2] = UsnWatermark(100, Dc2InvocationId) });

        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(() => import.GetDeltaImportObjectsAsync());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain("invocationId has changed").And.Contain(Dc2InvocationId.ToString()).And.Contain(Dc1InvocationId.ToString()));
            Assert.That(failure.Message, Does.Contain("the partition partners.local").And.Not.Contain("corp.local"));
            Assert.That(ChangeReads(), Is.Empty, "nothing is read while a watermark is untrustworthy");
        }
    }

    [Test]
    public void GetDeltaImportObjectsAsync_PartitionsHoldingOneWatermark_AreRefusedTogetherAsync()
    {
        _directoryType = LdapDirectoryType.ActiveDirectory;
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.ActiveDirectory),
            new Dictionary<int, string?> { [1] = UsnWatermark(100, Dc2InvocationId), [2] = UsnWatermark(100, Dc2InvocationId) });

        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(() => import.GetDeltaImportObjectsAsync());

        Assert.That(failure!.Message, Does.Contain("the partitions corp.local and partners.local"));
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_EveryPartitionHasAWatermarkFromThisDomainController_ReadsEachFromItsOwnAsync()
    {
        _directoryType = LdapDirectoryType.ActiveDirectory;
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.ActiveDirectory),
            new Dictionary<int, string?> { [1] = UsnWatermark(100, Dc1InvocationId), [2] = UsnWatermark(300, Dc1InvocationId) });

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.False);
            Assert.That(_sent.Single(r => r.DistinguishedName == CorpPeopleDn).Filter, Is.EqualTo("(&(objectClass=user)(uSNChanged>=101))"));
            Assert.That(_sent.Single(r => r.DistinguishedName == PartnersPeopleDn).Filter, Is.EqualTo("(&(objectClass=user)(uSNChanged>=301))"));
            Assert.That(result.PartitionWatermarks?.Keys, Is.EquivalentTo(new[] { 1, 2 }));
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The scripted directory: OpenLDAP with no sssvlv overlay and an accesslog holding <see cref="_logged"/>, or
    /// Active Directory reached at dc1. Every search of a container, and every read of an object, finds nothing.
    /// </summary>
    private DirectoryResponse Answer(DirectoryRequest directoryRequest)
    {
        var request = (SearchRequest)directoryRequest;
        _sent.Add(request);

        if (string.IsNullOrEmpty(request.DistinguishedName))
            return _directoryType == LdapDirectoryType.ActiveDirectory ? ActiveDirectoryRootDse() : OpenLdapRootDse();

        if (string.Equals(request.DistinguishedName, NtdsSettingsDn, StringComparison.OrdinalIgnoreCase))
            return LdapTestResponses.SearchResponseWithBinary(NtdsSettingsDn, ("invocationId", [Dc1InvocationId.ToByteArray()]));

        if (request.Controls.OfType<SortRequestControl>().Any())
            throw new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.UnavailableCriticalExtension), "critical extension is unavailable");

        if (!request.DistinguishedName.EndsWith(AccesslogDn, StringComparison.OrdinalIgnoreCase))
            return LdapTestResponses.EmptySearchResponse();

        // The readiness check reads cn=accesslog itself, and the continuity check a watermark's own entry, which is
        // there while the write it names is still logged.
        if (request.Scope == SearchScope.Base)
        {
            var held = string.Equals(request.DistinguishedName, AccesslogDn, StringComparison.OrdinalIgnoreCase)
                || _logged.Any(write => string.Equals(request.DistinguishedName, EntryDnOf(write.ReqStart), StringComparison.OrdinalIgnoreCase));
            return held
                ? LdapTestResponses.SearchResponseWith(request.DistinguishedName)
                : throw new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.NoSuchObject), "no such object");
        }

        var filter = request.Filter.ToString()!;
        var matching = _logged.Where(write => ScriptedAccesslogFilter.Matches(filter, write.ReqStart, write.ReqDn)).ToList();

        // The watermark walk asks for reqStart alone.
        var paging = request.Controls.OfType<PageResultRequestControl>().SingleOrDefault();
        if (paging == null)
            return LdapTestResponses.SearchResponseWithEntries(matching.Select(write => LdapTestResponses.Entry(EntryDnOf(write.ReqStart), ("reqStart", write.ReqStart))).ToArray());

        // The change read: every matching write on its first page, and, when scripted to, a cookie for a second page,
        // which the directory then answers with nothing more.
        var firstPage = paging.Cookie is not { Length: > 0 };
        return LdapTestResponses.SearchResponseWithPagingCookie(firstPage && _changeReadHasASecondPage ? Cookie : [],
            firstPage ? matching.Select(ChangeEntryOf).ToArray() : []);
    }

    private static string EntryDnOf(string reqStart) => $"reqStart={reqStart},{AccesslogDn}";

    private static SearchResultEntry ChangeEntryOf(LoggedWrite write) => write.EntryUuid == null
        ? LdapTestResponses.Entry(EntryDnOf(write.ReqStart), ("reqStart", write.ReqStart), ("reqType", write.ReqType), ("reqDN", write.ReqDn))
        : LdapTestResponses.Entry(EntryDnOf(write.ReqStart), ("reqStart", write.ReqStart), ("reqType", write.ReqType), ("reqDN", write.ReqDn), ("reqEntryUUID", write.EntryUuid));

    /// <summary>A write the scripted accesslog holds: when it started, the entry it was made to, and what kind of write it was.</summary>
    private sealed record LoggedWrite(string ReqStart, string ReqDn, string ReqType = "modify", string? EntryUuid = null);

    /// <summary>Logs a modify of the given entry.</summary>
    private void Log(string reqStart, string reqDn) => _logged.Add(new LoggedWrite(reqStart, reqDn));

    /// <summary>The objects the change read kept, by the reads of their current state it made, in the order it made them.</summary>
    private List<string> Fetched() =>
        _sent.Where(r => r.Scope == SearchScope.Base
                && !string.IsNullOrEmpty(r.DistinguishedName)
                && !r.DistinguishedName.EndsWith(AccesslogDn, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(r.DistinguishedName, NtdsSettingsDn, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.DistinguishedName)
            .ToList();

    /// <summary>The filters of the accesslog watermark walk's searches, in the order the import sent them.</summary>
    private List<string> Walks() =>
        _sent.Where(r => string.Equals(r.DistinguishedName, AccesslogDn, StringComparison.OrdinalIgnoreCase)
                && r.Scope == SearchScope.OneLevel
                && !r.Controls.OfType<PageResultRequestControl>().Any()
                && !r.Controls.OfType<SortRequestControl>().Any())
            .Select(r => r.Filter.ToString()!)
            .ToList();

    /// <summary>The filter of a change read that gives each partition, by its DN, its own reqStart bound.</summary>
    private static string ChangeReadFilter(params (string PartitionDn, string From)[] partitions) =>
        $"(&(objectClass=auditWriteObject)(reqResult=0)(|{string.Concat(partitions.Select(p => $"(&(reqDN:dnSubtreeMatch:={p.PartitionDn})(reqStart>={p.From}))"))}))";

    /// <summary>The watermark walk's search for the writes at or after a reqStart, anywhere or within the given partitions.</summary>
    private static string WalkFrom(string reqStart, params string[] partitionDns) => partitionDns.Length == 0
        ? $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={reqStart}))"
        : $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={reqStart})(|{string.Concat(partitionDns.Select(dn => $"(reqDN:dnSubtreeMatch:={dn})"))}))";

    /// <summary>The accesslog watermark JIM holds for each partition, by id: a logged write's reqStart.</summary>
    private static Dictionary<int, string?> WatermarksOf(params (int PartitionId, string ReqStart)[] watermarks) =>
        watermarks.ToDictionary(w => w.PartitionId, w => (string?)AccesslogWatermark(w.ReqStart));

    /// <summary>
    /// What the import processor records at the end of a run that completes: each watermark the run returned replaces
    /// the one JIM held for that partition, a null clears it, and a partition the run left out keeps what it has.
    /// </summary>
    private static void Record(Dictionary<int, string?> held, ConnectedSystemImportResult result)
    {
        Assert.That(result.PartitionWatermarks, Is.Not.Null, "the run returned no partition watermarks");
        foreach (var (partitionId, value) in result.PartitionWatermarks!)
        {
            if (value == null)
                held.Remove(partitionId);
            else
                held[partitionId] = value;
        }
    }

    /// <summary>What the import processor hands a run: the watermark JIM holds for each partition it targets, or null.</summary>
    private static Dictionary<int, string?> HandedTo(Dictionary<int, string?> held, params ConnectedSystemPartition[] targetPartitions) =>
        targetPartitions.ToDictionary(partition => partition.Id, partition => held.GetValueOrDefault(partition.Id));

    /// <summary>The accesslog position a run reached for a partition.</summary>
    private static string? ReachedFor(ConnectedSystemImportResult result, int partitionId) =>
        LdapPartitionWatermark.Parse(result.PartitionWatermarks?.GetValueOrDefault(partitionId), Logger)?.AccesslogTimestamp;

    private static SearchResponse OpenLdapRootDse() =>
        LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry(string.Empty, ("vendorName", "OpenLDAP")));

    private static SearchResponse ActiveDirectoryRootDse() =>
        LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues(string.Empty,
            ("DNSHostName", [Dc1]),
            ("HighestCommittedUSN", ["500"]),
            ("supportedCapabilities", [LdapConnectorConstants.LDAP_CAP_ACTIVE_DIRECTORY_OID]),
            ("dsServiceName", [NtdsSettingsDn]),
            ("namingContexts", [CorpDn, PartnersDn])));

    /// <summary>The containers a full read searched, in the order it searched them.</summary>
    private List<string> FullReadSearchBases() =>
        _sent.Where(r => r.Filter?.ToString()?.StartsWith("(objectClass=", StringComparison.Ordinal) == true && r.Scope != SearchScope.Base)
            .Select(r => r.DistinguishedName)
            .ToList();

    /// <summary>The searches that read changes: the accesslog's write operations, or Active Directory's uSNChanged.</summary>
    private List<SearchRequest> ChangeReads() =>
        _sent.Where(r => r.Filter?.ToString() is { } filter
                && (filter.Contains("uSNChanged", StringComparison.Ordinal) || (filter.Contains("auditWriteObject", StringComparison.Ordinal) && r.Controls.OfType<PageResultRequestControl>().Any())))
            .ToList();

    private static ConnectedSystemPartition Partition(int id, string dn, string name, string containerDn) => new()
    {
        Id = id,
        ExternalId = dn,
        Name = name,
        Selected = true,
        Containers = [new ConnectedSystemContainer { Id = id * 10, ExternalId = containerDn, Name = "people", Selected = true }]
    };

    /// <summary>A Connected System with one selected Object Type and the given partitions, each with a selected Container.</summary>
    private ConnectedSystem ConnectedSystemWith(params ConnectedSystemPartition[] partitions)
    {
        var objectType = _directoryType == LdapDirectoryType.ActiveDirectory
            ? new ConnectedSystemObjectType
            {
                Id = 1,
                Name = "user",
                Selected = true,
                Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true }]
            }
            : new ConnectedSystemObjectType
            {
                Id = 1,
                Name = "inetOrgPerson",
                Selected = true,
                Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "entryUUID", Type = AttributeDataType.Text, IsExternalId = true, Selected = true }]
            };

        return new ConnectedSystem { Name = "Directory", ObjectTypes = [objectType], Partitions = [.. partitions] };
    }

    private LdapConnectorImport NewImport(ConnectedSystemRunType runType, string? persistedState, IReadOnlyDictionary<int, string?> partitionWatermarks,
        ConnectedSystem? connectedSystem = null, ConnectedSystemPartition? targetedPartition = null, List<ConnectedSystemPaginationToken>? paginationTokens = null)
    {
        connectedSystem ??= ConnectedSystemWith(Partition(1, CorpDn, "corp.local", CorpPeopleDn), Partition(2, PartnersDn, "partners.local", PartnersPeopleDn));
        var import = new LdapConnectorImport(connectedSystem,
            new ConnectedSystemRunProfile { Name = runType.ToString(), RunType = runType, PageSize = 500, Partition = targetedPartition },
            new LdapConnection("localhost"), null, 1, paginationTokens ?? [], persistedState, partitionWatermarks, null, Dc1, _ => true, Logger,
            CancellationToken.None, new RecordingConnectorProgress());
        import.Executor = _executor.Object;
        return import;
    }

    /// <summary>The Connected System's record of the directory the last import reached.</summary>
    private static string RecordOf(LdapDirectoryType directoryType) =>
        JsonSerializer.Serialize(new LdapConnectorRootDse { DirectoryType = directoryType, DnsHostName = Dc1, InvocationId = Dc1InvocationId });

    private static string AccesslogWatermark(string reqStart) =>
        new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Accesslog, AccesslogTimestamp = reqStart, AccesslogTimestampIsLogged = true }.Serialise();

    private static string UsnWatermark(long usn, Guid invocationId) =>
        new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Usn, Usn = usn, InvocationId = invocationId, DnsHostName = Dc1 }.Serialise();

    private static LdapConnectorRootDse Read(string? json)
    {
        Assert.That(json, Is.Not.Null, "the import returned no persisted connector data");
        return JsonSerializer.Deserialize<LdapConnectorRootDse>(json!)!;
    }

    #endregion
}
