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
using Serilog.Core;
using Serilog.Events;
using System.Collections;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The accesslog change source an OpenLDAP Delta Import reads through, as extracted from the import: the watermark
/// it captures from cn=accesslog (by server-side sort when the directory allows it, by walking the size limit
/// forward when it does not), the reading of write operations at or after that watermark into import objects, and
/// what happens when cn=accesslog cannot be read at all: the readiness check names it, the watermark stays empty
/// rather than being invented, and a Delta Import refuses rather than importing nothing and calling it "no changes".
/// </summary>
[TestFixture]
public class LdapAccesslogDeltaSourceTests
{
    private const string PartitionDn = "dc=corp,dc=local";
    private const string PeopleDn = "ou=people,dc=corp,dc=local";
    private const string SecondPartitionDn = "dc=partners,dc=local";
    private const string SecondPeopleDn = "ou=people," + SecondPartitionDn;
    private const string WithinThePartition = "uid=alice," + PeopleDn;
    private const string UnderAnotherSuffix = "uid=bob,ou=people,dc=other,dc=local";
    private const string Watermark = "20260921080000.000000Z";
    private const string EarlierWrite = "20260921070000.000001Z";
    private const string AccesslogFilter = "(&(objectClass=auditWriteObject)(reqResult=0))";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    #region Watermark capture

    [Test]
    public async Task CaptureWatermarksAsync_ServerSideSortSupported_GivesTheLatestReqStartFromASingleEntryToEveryPartition()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSortedSearch(r)), Timeout))
            .Returns(LdapTestResponses.SearchResponseWith("reqStart=20260921090000.000001Z,cn=accesslog", ("reqStart", "20260921090000.000001Z")));

        var captured = await CaptureEachAsync(Source(executor), (PartitionDn, null), (SecondPartitionDn, LoggedWatermark()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured.Values.Select(w => w?.AccesslogTimestamp), Is.EqualTo(new[] { "20260921090000.000001Z", "20260921090000.000001Z" }));
            Assert.That(captured.Values.Select(w => w?.AccesslogTimestampIsLogged), Is.All.True, "the watermark is a logged entry's reqStart, so the next Delta Import can read it back");
            executor.Verify(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSortedSearch(r) && ((SearchRequest)r).SizeLimit == 1), Timeout), Times.Once,
                "the sort asks for the single latest entry rather than the whole log, once for the run");
            executor.VerifyNoOtherCalls();
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_SortRefused_FallsBackToTheSizeLimitWalk()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSortedSearch(r)), Timeout))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.UnavailableCriticalExtension), "critical extension is unavailable"));
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearchWithFilter(r, AccesslogFilter)), Timeout))
            .Returns(LdapTestResponses.SearchResponseWithEntries(
                AccesslogEntry("20260921090000.000001Z"),
                AccesslogEntry("20260921090500.000002Z"),
                AccesslogEntry("20260921090200.000003Z")));

        var captured = await CaptureAsync(Source(executor), previous: null, PartitionDn);

        Assert.That(captured?.AccesslogTimestamp, Is.EqualTo("20260921090500.000002Z"), "the latest reqStart, whatever order the directory returned them in");
    }

    [Test]
    public async Task CaptureWatermarksAsync_SizeLimitExceeded_WalksForwardFromTheLatestTimestampSeen()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSortedSearch(r)), Timeout))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.UnwillingToPerform), "unwilling to perform"));
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearchWithFilter(r, AccesslogFilter)), Timeout))
            .Throws(new DirectoryOperationException(PartialResponse(
                AccesslogEntry("20260921090000.000001Z"),
                AccesslogEntry("20260921090100.000002Z")), "size limit exceeded"));
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearchWithFilter(r, "(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>=20260921090100.000002Z))")), Timeout))
            .Returns(LdapTestResponses.SearchResponseWithEntries(
                AccesslogEntry("20260921090100.000002Z"),
                AccesslogEntry("20260921090200.000003Z")));

        var captured = await CaptureAsync(Source(executor), previous: null, PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo("20260921090200.000003Z"));
            Assert.That(captured?.AccesslogTimestampIsLogged, Is.True, "found by walking the size limit, it is still a logged entry's reqStart");
            executor.Verify(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearch(r)), Timeout), Times.Exactly(3), "the sort attempt, the capped first batch and the narrowed second");
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_AccesslogReadableButEmpty_RecordsANowTimestampAsync()
    {
        // The one case a generated timestamp is right for: the accesslog answered, and holds nothing (a snapshot
        // restore clears it). Without a baseline the next Delta Import would fall back to a Full Import for nothing.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout)).Returns(LdapTestResponses.EmptySearchResponse());
        var log = new CapturingSink();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var captured = await CaptureAsync(Source(executor, log), previous: null, PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured?.AccesslogTimestamp, Does.Match(@"^\d{14}\.\d{6}Z$"), "a generalised-time watermark so the next Delta Import has a baseline");
            Assert.That(ParseGeneralisedTime(captured!.AccesslogTimestamp!), Is.InRange(before, DateTime.UtcNow.AddSeconds(1)));
            Assert.That(captured?.AccesslogTimestampIsLogged, Is.False, "a generated timestamp names no entry, so there is nothing to read back");
            Assert.That(log.Events.Any(e => e.Level == LogEventLevel.Information && e.MessageTemplate.Text.Contains("empty")), Is.True,
                "an empty accesslog is ordinary and is said so at Information, not as a warning");
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_AccesslogRefused_LeavesEveryPartitionsWatermarkNullAsync()
    {
        // A refusal used to be swallowed and a generated timestamp recorded, exactly as for an empty accesslog. That
        // gave the next Delta Import a baseline it had no right to, so it ran, read nothing, and reported no changes.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access rights"));
        var log = new CapturingSink();

        var captured = await CaptureEachAsync(Source(executor, log), (PartitionDn, null), (SecondPartitionDn, LoggedWatermark()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured.Keys, Is.EqualTo(new[] { 1, 2 }), "every target partition is answered");
            Assert.That(captured.Values, Is.All.Null, "no baseline, so each partition's next Delta Import reads every object in it and says why");
            Assert.That(log.Events.Any(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("cn=accesslog") && e.RenderMessage().Contains("insufficient access rights")), Is.True);
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_AccesslogNotFound_LeavesTheWatermarkNullAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.NoSuchObject), "no such object"));
        var log = new CapturingSink();

        var captured = await CaptureAsync(Source(executor, log), previous: null, PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured, Is.Null, "a directory without the overlay has no accesslog to take a watermark from");
            Assert.That(log.Events.Any(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("cn=accesslog")), Is.True);
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_AccesslogNotFoundAsLdapException32_LeavesTheWatermarkNullAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout)).Throws(new LdapException(32, "no such object"));

        var captured = await CaptureAsync(Source(executor), previous: null, PartitionDn);

        Assert.That(captured, Is.Null);
    }

    [Test]
    public async Task CaptureWatermarksAsync_ConnectionFailure_LeavesTheWatermarkNullAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout)).Throws(new LdapException(81, "server down"));
        var log = new CapturingSink();

        var captured = await CaptureAsync(Source(executor, log), previous: null, PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured, Is.Null, "an unknown is never turned into a baseline");
            Assert.That(log.Events.Any(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("cn=accesslog")), Is.True);
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_RunTargetsNoPartition_TakesNoneAndReadsNothing()
    {
        var executor = new Mock<ILdapOperationExecutor>();

        var captured = await CaptureEachAsync(Source(executor));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured, Is.Empty);
            executor.VerifyNoOtherCalls();
        }
    }

    #endregion

    #region Watermark capture: from the previous watermarks

    [Test]
    public async Task CaptureWatermarksAsync_PreviousWatermarkAndNoSort_WalksFromThePreviousWatermarkAlone()
    {
        // Without the sort, the walk used to read the whole log from its first entry, in a log every suffix on the
        // server shares: 543,561 entries and 7 to 10 seconds on every import at 500,000 objects, where the same search
        // from the previous watermark read 4,235 entries in 62 ms (#2704). The latest write is at or after the previous
        // watermark whenever anything is, so starting there leaves out nothing the whole log would have answered.
        var accesslog = new ScriptedAccesslog { Logged = { EarlierWrite, Watermark, "20260921090500.000002Z", "20260921090200.000003Z" } };

        var captured = await CaptureAsync(Source(accesslog.Executor()), LoggedWatermark(), PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { WalkWithinThePartitionFrom(Watermark) }), "one search, from the previous watermark, rather than the whole log");
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo("20260921090500.000002Z"), "the latest reqStart, whatever order the directory logged them in");
            Assert.That(captured?.AccesslogTimestampIsLogged, Is.True);
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_NothingWrittenSinceThePreviousWatermark_TakesItAgain()
    {
        // A quiet directory still logs the entry the last import ended at, so the walks from it find that entry and
        // nothing newer: the watermark is unchanged.
        var accesslog = new ScriptedAccesslog { Logged = { EarlierWrite, Watermark } };

        var captured = await CaptureAsync(Source(accesslog.Executor()), LoggedWatermark(), PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { WalkWithinThePartitionFrom(Watermark), WalkFrom(Watermark) }));
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo(Watermark));
            Assert.That(captured?.AccesslogTimestampIsLogged, Is.True);
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_FirstImport_WalksTheWholeLog()
    {
        // With no previous watermark there is nowhere for a narrower search to start, so the whole log is read, every
        // suffix's writes included, as on every first import.
        var accesslog = new ScriptedAccesslog { Logged = { EarlierWrite, Watermark, { "20260921090500.000002Z", UnderAnotherSuffix } } };

        var captured = await CaptureAsync(Source(accesslog.Executor()), previous: null, PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { AccesslogFilter }), "with nothing to start from, the whole log is read");
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo("20260921090500.000002Z"));
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_PreviousRecordWithoutAnAccesslogWatermark_WalksTheWholeLog()
    {
        // The record the last import left when cn=accesslog could not be read, or one from a directory that keeps its
        // changes elsewhere, names no point in this log to start from.
        var accesslog = new ScriptedAccesslog { Logged = { EarlierWrite, Watermark } };
        var previous = new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Accesslog, AccesslogTimestamp = null };

        await CaptureAsync(Source(accesslog.Executor()), previous, PartitionDn);

        Assert.That(accesslog.Walk, Is.EqualTo(new[] { AccesslogFilter }));
    }

    [Test]
    public async Task CaptureWatermarksAsync_OnePartitionWithoutAWatermark_WalksTheWholeLogOnceForEveryPartition()
    {
        // partners.local has no watermark, so nothing short of the whole log is sure to hold its latest write; one walk
        // of it answers corp.local too.
        var accesslog = new ScriptedAccesslog { Logged = { EarlierWrite, Watermark, "20260921090000.000001Z", { "20260921090500.000002Z", UnderAnotherSuffix } } };

        var captured = await CaptureEachAsync(Source(accesslog.Executor()), (PartitionDn, LoggedWatermark()), (SecondPartitionDn, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { AccesslogFilter }));
            Assert.That(captured.Values.Select(w => w?.AccesslogTimestamp), Is.EqualTo(new[] { "20260921090500.000002Z", "20260921090500.000002Z" }));
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_SizeLimitStillCuttingTheWalkShortAfterAHundredSearches_WarnsThatTheWatermarkMayNotBeTheLatest()
    {
        // The walk stops after 100 searches, so that a directory answering the same entries forever cannot hold the
        // import. What it has reached by then may fall short of the end of the log, and that is said, not left silent.
        var logged = Timestamps(new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc), 150);
        var accesslog = new ScriptedAccesslog { SizeLimit = 2 };
        accesslog.Logged.AddRange(logged);
        var log = new CapturingSink();

        var captured = await CaptureAsync(Source(accesslog.Executor(), log), previous: null, PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Has.Count.EqualTo(100));
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo(logged[100]), "the latest the walk reached, short of the end of the log");
            Assert.That(log.Events.Any(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains(logged[100]) && e.RenderMessage().Contains("may not be the latest")), Is.True,
                "a watermark short of the end of the log is said so");
        }
    }

    #endregion

    #region Watermark capture: within the run's partitions

    [Test]
    public async Task CaptureWatermarksAsync_PreviousWatermarkAndSelectedPartition_WalksThePartitionsWritesFromThePreviousWatermarkAlone()
    {
        // cn=accesslog is shared by every suffix on the server, so the walk from the previous watermark still read every
        // suffix's writes since: at 500,000 objects, after the initial export to another suffix, it matched 543,561
        // entries and the next Delta Import spent 12 s on it, where the same search within the partition matched 5,007
        // (#2712). The change read is confined to the partitions, so the latest write within them is where it can start.
        const string partitionsLatest = "20260921090000.000001Z", otherSuffixesLatest = "20260921090500.000002Z";
        var accesslog = new ScriptedAccesslog { Logged = { EarlierWrite, Watermark, partitionsLatest, { otherSuffixesLatest, UnderAnotherSuffix } } };

        var captured = await CaptureAsync(Source(accesslog.Executor()), LoggedWatermark(), PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={Watermark})(|(reqDN:dnSubtreeMatch:={PartitionDn})))" }),
                "one search, from the previous watermark and within the partition: the confinement the change read has");
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo(partitionsLatest), "the partition's latest write, not the other suffix's");
            Assert.That(captured?.AccesslogTimestampIsLogged, Is.True, "a logged write, so the next Delta Import can read it back");
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_WriteBurstUnderAnotherSuffix_IsLeftOutOfTheSearchesAnswer()
    {
        // The directory leaves the other suffix's writes on the server, so a burst there costs the walk nothing: one
        // search, never cut short by a size limit the burst alone would exceed many times over.
        const string partitionsWrite = "20260921083000.000001Z";
        var accesslog = new ScriptedAccesslog { SizeLimit = 10, Logged = { Watermark } };
        accesslog.Logged.AddRange(Timestamps(new DateTime(2026, 9, 21, 8, 10, 0, DateTimeKind.Utc), 50), UnderAnotherSuffix);
        accesslog.Logged.Add(partitionsWrite);
        accesslog.Logged.AddRange(Timestamps(new DateTime(2026, 9, 21, 8, 40, 0, DateTimeKind.Utc), 50), UnderAnotherSuffix);

        var captured = await CaptureAsync(Source(accesslog.Executor()), LoggedWatermark(), PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Returned, Is.EqualTo(new[] { new[] { Watermark, partitionsWrite } }),
                "one search, answered with the partition's writes alone and none of the hundred under the other suffix");
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo(partitionsWrite));
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_SizeLimitExceededWithinThePartition_WalksForwardWithinItFromThePreviousWatermark()
    {
        // A size limit can still cut the partition's own writes short; each search carries on from the latest reqStart
        // the one before it reached, still within the partition, starting from the previous watermark rather than the
        // first entry in the log.
        const string first = "20260921090000.000001Z", second = "20260921090100.000002Z", third = "20260921090200.000003Z";
        var accesslog = new ScriptedAccesslog
        {
            SizeLimit = 2,
            Logged = { EarlierWrite, "20260921073000.000002Z", Watermark, first, { "20260921090030.000004Z", UnderAnotherSuffix }, second, third, { "20260921090300.000005Z", UnderAnotherSuffix } }
        };

        var captured = await CaptureAsync(Source(accesslog.Executor()), LoggedWatermark(), PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { WalkWithinThePartitionFrom(Watermark), WalkWithinThePartitionFrom(first), WalkWithinThePartitionFrom(second) }),
                "each search starts from the latest reqStart the one before it reached");
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo(third));
            Assert.That(captured?.AccesslogTimestampIsLogged, Is.True);
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_SeveralPartitionsHoldingOneWatermark_WalksTheWritesWithinAnyOfThemOnce()
    {
        const string secondPartitionsLatest = "20260921090100.000002Z";
        var accesslog = new ScriptedAccesslog
        {
            Logged =
            {
                Watermark,
                "20260921090000.000001Z",
                { secondPartitionsLatest, "uid=carol," + SecondPeopleDn },
                { "20260921090500.000003Z", UnderAnotherSuffix }
            }
        };

        var captured = await CaptureEachAsync(Source(accesslog.Executor()), (PartitionDn, LoggedWatermark()), (SecondPartitionDn, LoggedWatermark()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[]
            {
                $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={Watermark})(|(reqDN:dnSubtreeMatch:={PartitionDn})(reqDN:dnSubtreeMatch:={SecondPartitionDn})))"
            }), "one clause per partition, any of them matching, in one search for the run");
            Assert.That(captured.Values.Select(w => w?.AccesslogTimestamp), Is.EqualTo(new[] { secondPartitionsLatest, secondPartitionsLatest }),
                "the latest write within either partition, after both watermarks");
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_PartitionsWithDifferentWatermarks_WalkFromTheOldestAndGiveTheLatestWriteToEachItIsNewerFor()
    {
        // corp.local last read to 08:00 and partners.local to 09:00. Every write either has yet to read is at or after
        // 08:00, so one walk from there finds the latest write within the run's partitions, 09:30, and both take it:
        // each partition's change read in this run starts from its own watermark, so it reads up to that point.
        const string partnersWatermark = "20260921090000.000001Z", corpsLatest = "20260921093000.000001Z";
        var accesslog = new ScriptedAccesslog { Logged = { Watermark, { partnersWatermark, "uid=carol," + SecondPeopleDn }, corpsLatest } };

        var captured = await CaptureEachAsync(Source(accesslog.Executor()),
            (PartitionDn, LoggedWatermark()), (SecondPartitionDn, LoggedWatermark(partnersWatermark)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn, SecondPartitionDn) }));
            Assert.That(captured.Values.Select(w => w?.AccesslogTimestamp), Is.EqualTo(new[] { corpsLatest, corpsLatest }));
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_PartitionWithNothingNewerThanItsOwnWatermark_TakesTheLatestWriteAnywhereFromItsOwn()
    {
        // partners.local's last import took 09:30, another suffix's write, while corp.local's latest write is 09:10:
        // older than partners.local's watermark, so no watermark for it. It takes the latest write anywhere from its own
        // watermark instead, as a quiet partition always did, and keeps advancing with the log.
        const string partnersWatermark = "20260921093000.000001Z", corpsLatest = "20260921091000.000001Z", otherSuffixesLatest = "20260921094000.000001Z";
        var accesslog = new ScriptedAccesslog
        {
            Logged = { Watermark, corpsLatest, { partnersWatermark, UnderAnotherSuffix }, { otherSuffixesLatest, UnderAnotherSuffix } }
        };

        var captured = await CaptureEachAsync(Source(accesslog.Executor()),
            (PartitionDn, LoggedWatermark()), (SecondPartitionDn, LoggedWatermark(partnersWatermark)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn, SecondPartitionDn), WalkFrom(partnersWatermark) }));
            Assert.That(captured[1]?.AccesslogTimestamp, Is.EqualTo(corpsLatest));
            Assert.That(captured[2]?.AccesslogTimestamp, Is.EqualTo(otherSuffixesLatest), "never back to corp.local's older write");
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_SeveralQuietPartitions_WalkEveryWriteOnceFromTheOldestOfTheirWatermarks()
    {
        // Nothing new within either partition; one walk from the older of their two watermarks finds the latest write
        // anywhere after both, so the quiet partitions cost one search between them, not one each.
        const string partnersWatermark = "20260921090000.000001Z", otherSuffixesLatest = "20260921094000.000001Z";
        var accesslog = new ScriptedAccesslog
        {
            Logged = { { Watermark, UnderAnotherSuffix }, { partnersWatermark, UnderAnotherSuffix }, { otherSuffixesLatest, UnderAnotherSuffix } }
        };

        var captured = await CaptureEachAsync(Source(accesslog.Executor()),
            (PartitionDn, LoggedWatermark()), (SecondPartitionDn, LoggedWatermark(partnersWatermark)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn, SecondPartitionDn), WalkFrom(Watermark) }));
            Assert.That(captured.Values.Select(w => w?.AccesslogTimestamp), Is.EqualTo(new[] { otherSuffixesLatest, otherSuffixesLatest }));
        }
    }

    [TestCase(WithinThePartition, Description = "the last watermark was the partition's own write, which the search finds again and nothing newer")]
    [TestCase(UnderAnotherSuffix, Description = "the last watermark was another suffix's write, so the search finds nothing")]
    public async Task CaptureWatermarksAsync_NothingNewInThePartitionButWritesElsewhere_TakesTheLatestWriteFromThePreviousWatermarkSoTheWatermarkStillAdvances(string watermarkWrittenTo)
    {
        // A watermark that stood still while its partition was quiet would be purged once the partition had been quiet
        // for longer than the accesslog keeps entries (olcAccessLogPurge), and every Delta Import after that would be
        // refused for a Full Import. Taking the latest write anywhere instead keeps it advancing with the log, as before.
        const string otherSuffixesLatest = "20260921090500.000002Z";
        var accesslog = new ScriptedAccesslog { Logged = { EarlierWrite, { Watermark, watermarkWrittenTo }, { otherSuffixesLatest, UnderAnotherSuffix } } };

        var captured = await CaptureAsync(Source(accesslog.Executor()), LoggedWatermark(), PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { WalkWithinThePartitionFrom(Watermark), WalkFrom(Watermark) }),
                "nothing newer within the partition, so every write from the previous watermark is read, as before");
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo(otherSuffixesLatest), "the latest write in the log, as before");
            Assert.That(captured?.AccesslogTimestampIsLogged, Is.True);
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_PartitionWithoutADistinguishedName_WalksEveryWriteFromThePreviousWatermarkOnceAsBefore()
    {
        // A partition with no distinguished name gives the walk nothing to confine itself to, so its one search is every
        // write from the previous watermark, and that search already answers what a quiet partition would ask.
        var accesslog = new ScriptedAccesslog { Logged = { EarlierWrite, Watermark, "20260921090000.000001Z", { "20260921090500.000002Z", UnderAnotherSuffix } } };

        var captured = await CaptureAsync(Source(accesslog.Executor()), LoggedWatermark(), string.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { WalkFrom(Watermark) }));
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo("20260921090500.000002Z"));
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_PartitionWithoutADistinguishedNameAndNothingWrittenSince_TakesItsWatermarkAgainFromOneSearch()
    {
        var accesslog = new ScriptedAccesslog { Logged = { EarlierWrite, Watermark } };

        var captured = await CaptureAsync(Source(accesslog.Executor()), LoggedWatermark(), string.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { WalkFrom(Watermark) }), "the one unconfined search answers both questions");
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo(Watermark));
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_DirectoryRestoredToBeforeThePreviousWatermark_FallsBackToTheWholeLogAsBefore()
    {
        // A restore from a backup or snapshot leaves only writes older than the watermark. Nothing at or after it within
        // the partition or anywhere else, so the whole log is read and its latest write taken, as before; the
        // continuity check that follows reads the watermark's own entry, finds it gone, and refuses the Delta Import.
        const string latestBeforeTheRestore = "20260921073000.000002Z";
        var accesslog = new ScriptedAccesslog { Logged = { EarlierWrite, latestBeforeTheRestore } };

        var captured = await CaptureAsync(Source(accesslog.Executor()), LoggedWatermark(), PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { WalkWithinThePartitionFrom(Watermark), WalkFrom(Watermark), AccesslogFilter }));
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo(latestBeforeTheRestore), "the latest write the restored log holds, older than the previous watermark");
            Assert.That(captured?.AccesslogTimestampIsLogged, Is.True);
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_PreviousWatermarkPurgedWithNothingLoggedSince_FallsBackToTheWholeLogAsBefore()
    {
        // The purge (olcAccessLogPurge) removes the oldest entries first, so a watermark purged with nothing written
        // since leaves the log empty. The walks from the watermark find nothing, and the whole log is read instead, so
        // the outcome is what it always was: a readable, empty accesslog gets a generated timestamp naming no entry.
        var accesslog = new ScriptedAccesslog();

        var captured = await CaptureAsync(Source(accesslog.Executor()), LoggedWatermark(), PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accesslog.Walk, Is.EqualTo(new[] { WalkWithinThePartitionFrom(Watermark), WalkFrom(Watermark), AccesslogFilter }));
            Assert.That(captured?.AccesslogTimestamp, Does.Match(@"^\d{14}\.\d{6}Z$"), "a readable, empty accesslog still gets a generated timestamp");
            Assert.That(captured?.AccesslogTimestampIsLogged, Is.False);
        }
    }

    [Test]
    public async Task CaptureWatermarksAsync_PreviousWatermarkAndSortSupported_TakesTheSortedSearchAloneAsBefore()
    {
        // A directory that sorts answers with the log's single latest entry in one search, and that search is left as
        // it was: confined to a partition, it would return a quiet partition's last write however old, which is the
        // watermark the purge would then remove.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSortedSearch(r)), Timeout))
            .Returns(LdapTestResponses.SearchResponseWith("reqStart=20260921090000.000001Z,cn=accesslog", ("reqStart", "20260921090000.000001Z")));

        var captured = await CaptureAsync(Source(executor), LoggedWatermark(), PartitionDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured?.AccesslogTimestamp, Is.EqualTo("20260921090000.000001Z"));
            executor.Verify(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSortedSearch(r) && ((SearchRequest)r).Filter.ToString() == AccesslogFilter), Timeout), Times.Once);
            executor.VerifyNoOtherCalls();
        }
    }

    #endregion

    #region Continuity, readiness and baseline

    [TestCase(null, Description = "a record from before JIM kept whether the watermark names a logged entry")]
    [TestCase(false, Description = "a timestamp generated because the accesslog was empty, which names no entry")]
    public void VerifyContinuity_WatermarkNotKnownToNameALoggedEntry_ReadsNothing(bool? isLogged)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        var previous = new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Accesslog, AccesslogTimestamp = Watermark, AccesslogTimestampIsLogged = isLogged };

        Assert.That(() => Source(executor).VerifyContinuity([Partition()], previous, new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }), Throws.Nothing);
        executor.VerifyNoOtherCalls();
    }

    [Test]
    public void VerifyContinuity_WatermarkEntryStillLogged_ReadsItByItsOwnNameAndPasses()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        SearchRequest? sent = null;
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsBaseReadOf(r, WatermarkEntryDn))))
            .Callback<DirectoryRequest>(r => sent = (SearchRequest)r)
            .Returns(LdapTestResponses.SearchResponseWith(WatermarkEntryDn));

        Assert.That(() => Source(executor).VerifyContinuity([Partition()], LoggedWatermark(), new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }), Throws.Nothing);
        Assert.That(sent!.Attributes.Cast<string>(), Is.EqualTo(new[] { "1.1" }), "existence is all that is asked");
    }

    [Test]
    public void VerifyContinuity_WatermarkEntryPurged_ThrowsNamingTheWatermarkThePurgeTheRestoreAndTheRemedy()
    {
        // slapo-accesslog purged the entry the last import ended at (olcAccessLogPurge), or the directory was
        // restored to before it; either way the changes after it may be gone too (#2008).
        var executor = ExecutorWhereTheWatermarkEntryIsMissingButTheAccesslogIsThere(
            new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.NoSuchObject), "no such object"));

        var ex = Assert.Throws<CannotPerformDeltaImportException>(() =>
            Source(executor).VerifyContinuity([Partition()], LoggedWatermark(), new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain(Watermark), "the change the last import ended at");
            Assert.That(ex.Message, Does.Contain("olcAccessLogPurge"), "the usual cause, by the setting that controls it");
            Assert.That(ex.Message, Does.Contain("restored from a backup or snapshot"), "the other cause");
            Assert.That(ex.Message, Does.Contain("silently miss"), "why the run must not carry on");
            Assert.That(ex.Message, Does.Contain("Run a Full Import"), "what to do");
        }
    }

    [Test]
    public void VerifyContinuity_WatermarkEntryMissingAsLdapException32_Throws()
    {
        var executor = ExecutorWhereTheWatermarkEntryIsMissingButTheAccesslogIsThere(new LdapException(32, "No Such Object"));

        Assert.That(() => Source(executor).VerifyContinuity([Partition()], LoggedWatermark(), new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }),
            Throws.TypeOf<CannotPerformDeltaImportException>());
    }

    [Test]
    public void VerifyContinuity_WatermarkEntryReadAnswersNoEntry_Throws()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsBaseReadOf(r, WatermarkEntryDn)))).Returns(LdapTestResponses.EmptySearchResponse());
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsBaseReadOf(r, "cn=accesslog")))).Returns(LdapTestResponses.SearchResponseWith("cn=accesslog"));

        Assert.That(() => Source(executor).VerifyContinuity([Partition()], LoggedWatermark(), new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }),
            Throws.TypeOf<CannotPerformDeltaImportException>());
    }

    [Test]
    public void VerifyContinuity_WatermarkEntryMissingBecauseTheAccesslogIsGone_LeavesItToTheReadinessCheck()
    {
        // With cn=accesslog itself missing the watermark's entry is bound to be too; the readiness check that runs
        // next names the real problem (the overlay, or the account's access) rather than a purge.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.NoSuchObject), "no such object"));

        Assert.That(() => Source(executor).VerifyContinuity([Partition()], LoggedWatermark(), new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }), Throws.Nothing);
    }

    [Test]
    public void VerifyContinuity_WatermarkEntryReadRefused_LeavesItToTheReadinessCheck()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsBaseReadOf(r, WatermarkEntryDn))))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access"));

        Assert.That(() => Source(executor).VerifyContinuity([Partition()], LoggedWatermark(), new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }), Throws.Nothing);
    }

    [Test]
    public void VerifyContinuity_ConnectionFailure_Propagates()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>())).Throws(new LdapException(81, "Server down"));

        Assert.That(() => Source(executor).VerifyContinuity([Partition()], LoggedWatermark(), new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }),
            Throws.TypeOf<LdapException>(), "a failed connection is an error, not evidence about the accesslog");
    }

    private const string WatermarkEntryDn = $"reqStart={Watermark},cn=accesslog";

    private static LdapPartitionWatermark LoggedWatermark(string reqStart = Watermark) =>
        new() { Kind = LdapDeltaSourceKind.Accesslog, AccesslogTimestamp = reqStart, AccesslogTimestampIsLogged = true };

    private static Mock<ILdapOperationExecutor> ExecutorWhereTheWatermarkEntryIsMissingButTheAccesslogIsThere(Exception missing)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsBaseReadOf(r, WatermarkEntryDn)))).Throws(missing);
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsBaseReadOf(r, "cn=accesslog")))).Returns(LdapTestResponses.SearchResponseWith("cn=accesslog"));
        return executor;
    }

    private static bool IsBaseReadOf(DirectoryRequest request, string distinguishedName) =>
        request is SearchRequest { Scope: SearchScope.Base } search && string.Equals(search.DistinguishedName, distinguishedName, StringComparison.OrdinalIgnoreCase);

    [Test]
    public async Task VerifyReadinessAsync_AccesslogEntryReadable_ReportsAvailableAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        SearchRequest? sent = null;
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>()))
            .Callback<DirectoryRequest>(request => sent = (SearchRequest)request)
            .Returns(LdapTestResponses.SearchResponseWith("cn=accesslog"));

        var findings = await Source(executor).VerifyReadinessAsync(new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }, [PartitionDn], CancellationToken.None);

        var finding = findings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Subject, Is.EqualTo("cn=accesslog"));
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Available));
            Assert.That(finding.DeltaImportText, Is.Null, "an availability has nothing to say");
            Assert.That(finding.SchemaDiscoveryText, Is.Null);
            Assert.That(sent!.DistinguishedName, Is.EqualTo("cn=accesslog"));
            Assert.That(sent.Scope, Is.EqualTo(SearchScope.Base), "the entry itself is read, not the log it heads");
            Assert.That(sent.Filter, Is.EqualTo("(objectClass=*)"));
            Assert.That(sent.Attributes.Cast<string>(), Is.EqualTo(new[] { "1.1" }), "no attributes: the question is whether the entry is visible at all");
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_NoSuchObject_ReportsUnavailableNamingTheOverlayAndTheAclRemedyAsync()
    {
        var executor = ExecutorThrowingOnRead(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.NoSuchObject), "no such object"));

        var findings = await Source(executor).VerifyReadinessAsync(new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }, [PartitionDn], CancellationToken.None);

        var finding = findings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Subject, Is.EqualTo("cn=accesslog"));
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Unavailable));
            Assert.That(finding.DeltaImportText, Does.StartWith("Changes cannot be detected: the directory provides no accesslog at cn=accesslog, or none the account JIM connects as may read"),
                "some directories answer noSuchObject for a base the account may not read, so absence is never claimed outright");
            Assert.That(finding.DeltaImportText, Does.Contain("additions, updates and deletions since the last import would go unnoticed"));
            Assert.That(finding.DeltaImportText, Does.Contain("Run a Full Import, which also detects deletions by absence"));
            Assert.That(finding.DeltaImportText, Does.Contain("enable the accesslog overlay and grant the account read access to cn=accesslog"));
            Assert.That(finding.DeltaImportText, Does.Contain("Service Account Permissions"));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_NoSuchObjectAsLdapException32_ReportsUnavailableAsync()
    {
        var executor = ExecutorThrowingOnRead(new LdapException(32, "no such object"));

        var findings = await Source(executor).VerifyReadinessAsync(new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }, [PartitionDn], CancellationToken.None);

        var finding = findings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Unavailable));
            Assert.That(finding.DeltaImportText, Does.Contain("provides no accesslog at cn=accesslog"));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_SuccessWithNoEntry_ReportsUnavailableAsync()
    {
        // A directory that hides what the account may not see answers a base-scope read with success and no entry.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>())).Returns(LdapTestResponses.EmptySearchResponse());

        var findings = await Source(executor).VerifyReadinessAsync(new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }, [PartitionDn], CancellationToken.None);

        var finding = findings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Unavailable));
            Assert.That(finding.DeltaImportText, Does.Contain("provides no accesslog at cn=accesslog, or none the account JIM connects as may read"));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_Refused_ReportsUnavailableWithTheDirectorysReasonAsync()
    {
        var executor = ExecutorThrowingOnRead(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access rights"));

        var findings = await Source(executor).VerifyReadinessAsync(new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }, [PartitionDn], CancellationToken.None);

        var finding = findings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Unavailable), "a refusal is read in full; it is not an unknown");
            Assert.That(finding.DeltaImportText, Does.StartWith("Changes cannot be detected: the directory refused to read cn=accesslog (insufficient access rights)"));
            Assert.That(finding.DeltaImportText, Does.Contain("Run a Full Import, which also detects deletions by absence"));
            Assert.That(finding.SchemaDiscoveryText, Does.Contain("refused to read cn=accesslog (insufficient access rights)"));
            Assert.That(finding.SchemaDiscoveryText, Does.Contain("Delta Import is not available"));
            Assert.That(finding.Detail, Does.Contain("insufficient access rights"));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_ConnectionFailure_ReportsCouldNotDetermineAsync()
    {
        var executor = ExecutorThrowingOnRead(new LdapException(81, "server down"));

        var findings = await Source(executor).VerifyReadinessAsync(new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }, [PartitionDn], CancellationToken.None);

        var finding = findings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.CouldNotDetermine), "a fault on the way to the directory says nothing about the accesslog");
            Assert.That(finding.DeltaImportText, Does.StartWith("JIM could not confirm that the account it connects as can read cn=accesslog: "));
            Assert.That(finding.DeltaImportText, Does.Contain("server down"));
            Assert.That(finding.DeltaImportText, Does.EndWith("If it cannot, Delta Imports from this directory detect no changes."));
            Assert.That(finding.SchemaDiscoveryText, Is.EqualTo(finding.DeltaImportText), "one text for an unknown, in both places");
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_Unavailable_SchemaDiscoveryTextSaysDeltaImportIsNotAvailableAndFullImportDetectsDeletionsAsync()
    {
        var executor = ExecutorThrowingOnRead(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.NoSuchObject), "no such object"));

        var findings = await Source(executor).VerifyReadinessAsync(new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }, [], CancellationToken.None);

        var text = findings.Single().SchemaDiscoveryText;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.StartWith("This directory publishes no accesslog at cn=accesslog that the account JIM connects as may read, so Delta Import is not available"));
            Assert.That(text, Does.Contain("Full Import works as normal and also detects deletions by absence"));
            Assert.That(text, Does.Contain("enable the accesslog overlay and grant the account read access to cn=accesslog"));
            Assert.That(text, Does.Contain("Service Account Permissions"));
        }
    }

    [Test]
    public void VerifyReadinessAsync_CancellationRequested_ThrowsBeforeReading()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => Source(executor).VerifyReadinessAsync(new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }, [PartitionDn], cancellation.Token),
                Throws.InstanceOf<OperationCanceledException>());
            executor.VerifyNoOtherCalls();
        }
    }

    [Test]
    public void HasBaseline_NoTimestamp_IsFalse()
    {
        var source = Source(new Mock<ILdapOperationExecutor>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.HasBaseline(null), Is.False);
            Assert.That(source.HasBaseline(new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Accesslog, AccesslogTimestamp = null }), Is.False);
            Assert.That(source.HasBaseline(new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Accesslog, AccesslogTimestamp = string.Empty }), Is.False);
        }
    }

    [Test]
    public void HasBaseline_TimestampPresent_IsTrue()
    {
        Assert.That(Source(new Mock<ILdapOperationExecutor>()).HasBaseline(LoggedWatermark()), Is.True);
    }

    #endregion

    #region Reading changes: request shape and phase

    [Test]
    public async Task ReadChangesAsync_Always_QueriesTheWriteOperationsWithinEachTargetPartitionAtOrAfterItsWatermarkOneLevelUnderCnAccesslog()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out var sent);
        var host = new Mock<ILdapDeltaImportHost>();

        await Source(executor).ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Value!.DistinguishedName, Is.EqualTo("cn=accesslog"));
            Assert.That(sent.Value.Scope, Is.EqualTo(SearchScope.OneLevel));
            Assert.That(sent.Value.Filter, Is.EqualTo(ChangeRead((PartitionDn, Watermark))),
                "cn=accesslog is shared by every database on the server, so the other databases' writes are left on the server rather than read and discarded");
        }
    }

    [Test]
    public async Task ReadChangesAsync_TwoPartitionsWithDifferentWatermarks_GivesEachItsOwnClause()
    {
        // corp.local's last import read to 09:00 and partners.local's to 08:00 (#2715). One clause each, so the
        // directory returns neither partition's writes that its own last import read.
        const string corpsWatermark = "20260921090000.000001Z";
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out var sent);

        await Source(executor).ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>(), (PartitionDn, corpsWatermark), (SecondPartitionDn, Watermark)),
            new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(sent.Value!.Filter, Is.EqualTo(ChangeRead((PartitionDn, corpsWatermark), (SecondPartitionDn, Watermark))));
    }

    [Test]
    public async Task ReadChangesAsync_TwoPartitionsWithDifferentWatermarks_EntersTheQueryChangesPhaseSayingEachReadsFromItsOwn()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out _);
        var host = new Mock<ILdapDeltaImportHost>();

        await Source(executor).ReadChangesAsync(Context(host, (PartitionDn, "20260921090000.000001Z"), (SecondPartitionDn, Watermark)),
            new ConnectedSystemImportResult(), CancellationToken.None);

        host.Verify(h => h.EnterPhaseAsync(LdapConnectorPhases.QueryChanges, "Querying changes in 2 partitions, each since its own watermark..."), Times.Once);
    }

    [Test]
    public async Task ReadChangesAsync_Always_RequestsNoReqOld()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out var sent);

        await Source(executor).ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>()), new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(sent.Value!.Attributes.Cast<string>(), Is.EquivalentTo(new[] { "reqStart", "reqType", "reqDN", "reqEntryUUID" }),
            "reqOld holds a modified attribute's previous values, a large group's whole member list on every membership change, and only a deletion needs it");
    }

    [Test]
    public async Task ReadChangesAsync_PartitionDnHoldingFilterSpecialCharacters_IsEscapedInTheFilter()
    {
        const string partitionDn = "o=R&D (Lab*),dc=corp,dc=local";
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out var sent);

        await Source(executor).ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>(), partitionDn: partitionDn), new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(sent.Value!.Filter, Does.EndWith($@"(|(&(reqDN:dnSubtreeMatch:=o=R&D \28Lab\2a\29,dc=corp,dc=local)(reqStart>={Watermark}))))"));
    }

    [Test]
    public async Task ReadChangesAsync_Always_EntersTheQueryChangesPhase()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out _);
        var host = new Mock<ILdapDeltaImportHost>();

        await Source(executor).ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        host.Verify(h => h.EnterPhaseAsync(LdapConnectorPhases.QueryChanges, $"Querying changes since {Watermark}..."), Times.Once);
    }

    #endregion

    #region Reading changes: what is skipped

    [Test]
    public async Task ReadChangesAsync_EntryAtExactlyTheWatermark_IsSkipped()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            ModifyEntry(Watermark, "uid=alice," + PeopleDn)), out _);
        var host = HostReturningObjects();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty, "the watermark entry was imported by the run that recorded it; LDAP has no > so >= is narrowed here");
            host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Never);
        }
    }

    [Test]
    public async Task ReadChangesAsync_EntryAtExactlyItsOwnPartitionsWatermark_IsSkippedThoughAnotherPartitionsIsOlder()
    {
        // The skip is each partition's own (#2715): the entry corp.local's last import ended at was imported by that
        // import, however much older partners.local's watermark is.
        const string corpsWatermark = "20260921090000.000001Z";
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            ModifyEntry(corpsWatermark, "uid=alice," + PeopleDn)), out _);
        var host = HostReturningObjects();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host, (PartitionDn, corpsWatermark), (SecondPartitionDn, Watermark)), result, CancellationToken.None);

        Assert.That(result.ImportObjects, Is.Empty);
    }

    [Test]
    public async Task ReadChangesAsync_EntryBeforeItsOwnPartitionsWatermark_IsSkippedWhateverTheDirectoryReturned()
    {
        // The directory confines each partition's writes to those after its own watermark; a write it returns from
        // before that point anyway (an enclosing partition's clause returns a nested one's) was read by the run that
        // took the watermark, so it is judged here by the watermark of the partition it lies in.
        const string corpsWatermark = "20260921090000.000001Z";
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            ModifyEntry("20260921083000.000001Z", "uid=alice," + PeopleDn),
            ModifyEntry("20260921083000.000002Z", "uid=pat," + SecondPeopleDn)), out _);
        var host = HostReturningObjects();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host, (PartitionDn, corpsWatermark), (SecondPartitionDn, Watermark)), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            host.Verify(h => h.GetObjectByDn("uid=alice," + PeopleDn, It.IsAny<ObjectChangeType>()), Times.Never, "corp.local's last import read it");
            host.Verify(h => h.GetObjectByDn("uid=pat," + SecondPeopleDn, ObjectChangeType.Updated), Times.Once, "partners.local's last import had not");
        }
    }

    [Test]
    public async Task ReadChangesAsync_EntryWhoseDnEndsWithThePartitionsTextButNotItsRdns_IsSkipped()
    {
        // adc=corp,dc=local ends with the text dc=corp,dc=local but is not within it; a raw suffix comparison would
        // have taken its write for corp.local's. No Container states an opinion, so the partition alone decides.
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            ModifyEntry("20260921090000.000001Z", "uid=bob,ou=people,adc=corp,dc=local")), out _);
        var host = HostReturningObjects();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(ContextOf(host, [(PartitionDn, Watermark)], containersDecideScope: false), result, CancellationToken.None);

        host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Never);
    }

    [Test]
    public async Task ReadChangesAsync_PartitionWithoutADistinguishedName_ReadsEveryWriteAfterItsWatermarkAsBefore()
    {
        // A partition with no distinguished name cannot be named in the filter, so its clause is a bound alone, as the
        // read always was when no partition could be named, and it takes every write it returns.
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            ModifyEntry("20260921090000.000001Z", "uid=alice," + PeopleDn)), out var sent);
        var host = HostReturningObjects();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host, (string.Empty, Watermark)), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Value!.Filter, Is.EqualTo($"(&(objectClass=auditWriteObject)(reqResult=0)(|(reqStart>={Watermark})))"));
            host.Verify(h => h.GetObjectByDn("uid=alice," + PeopleDn, ObjectChangeType.Updated), Times.Once);
        }
    }

    [Test]
    public async Task ReadChangesAsync_EntryOutsideTheTargetPartitions_IsSkipped()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            ModifyEntry("20260921090000.000001Z", "uid=bob,ou=people,dc=other,dc=local"),
            ModifyEntry("20260921090000.000002Z", "uid=alice," + PeopleDn)), out _);
        var host = HostReturningObjects();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Has.Count.EqualTo(1), "cn=accesslog is shared by every database on the server");
            host.Verify(h => h.GetObjectByDn("uid=bob,ou=people,dc=other,dc=local", It.IsAny<ObjectChangeType>()), Times.Never);
            host.Verify(h => h.GetObjectByDn("uid=alice," + PeopleDn, ObjectChangeType.Updated), Times.Once);
        }
    }

    [Test]
    public async Task ReadChangesAsync_EntryOutsideTheSelectedContainers_IsSkipped()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            ModifyEntry("20260921090000.000001Z", "cn=admins,ou=groups," + PartitionDn),
            ModifyEntry("20260921090000.000002Z", "uid=alice," + PeopleDn)), out _);
        var host = HostReturningObjects();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Has.Count.EqualTo(1), "within a partition only the selected Containers are imported from, as a Full Import would");
            host.Verify(h => h.GetObjectByDn("cn=admins,ou=groups," + PartitionDn, It.IsAny<ObjectChangeType>()), Times.Never);
        }
    }

    [Test]
    public async Task ReadChangesAsync_SeveralModifiesOfOneDn_FetchTheObjectOnce()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            ModifyEntry("20260921090000.000001Z", "cn=staff," + PeopleDn),
            ModifyEntry("20260921090000.000002Z", "cn=staff," + PeopleDn),
            ModifyEntry("20260921090000.000003Z", "cn=staff," + PeopleDn)), out _);
        var host = HostReturningObjects();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Has.Count.EqualTo(1), "the current state is fetched, so one read covers every change to the object");
            host.Verify(h => h.GetObjectByDn("cn=staff," + PeopleDn, ObjectChangeType.Updated), Times.Once);
        }
    }

    [Test]
    public async Task ReadChangesAsync_AddThenDeleteOfOneDn_StillYieldsTheDelete()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            AccesslogEntry("20260921090000.000001Z", ("reqType", "add"), ("reqDN", "uid=carol," + PeopleDn)),
            LoggedDelete("20260921090000.000002Z", "uid=carol," + PeopleDn, "3c1f6f0e-4d1a-4b2b-9c3e-1e2f3a4b5c6d")), out _);
        var host = HostReturningObjects();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        Assert.That(result.ImportObjects.Select(o => o.ChangeType), Is.EqualTo(new[] { ObjectChangeType.Added, ObjectChangeType.Deleted }),
            "the DN was already seen, and the deletion is still the final state");
    }

    #endregion

    #region Reading changes: deletions

    [Test]
    public async Task ReadChangesAsync_Delete_YieldsAnUntypedDeletionCarryingTheEntryUuidAndDn()
    {
        // The accesslog records a deletion's entryUUID (reqEntryUUID) whatever logold says, but its object classes only
        // in reqOld, which logold costs a large group's whole member list on every membership change (#1066). The
        // deletion is reported without an Object Type, and JIM types it from the object it holds.
        const string uuid = "3c1f6f0e-4d1a-4b2b-9c3e-1e2f3a4b5c6d";
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            LoggedDelete("20260921090000.000001Z", "uid=carol," + PeopleDn, uuid)), out _);
        var host = new Mock<ILdapDeltaImportHost>();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        var deleted = result.ImportObjects.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted.ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            Assert.That(deleted.ObjectType, Is.Null);
            Assert.That(deleted.Attributes.Single(a => a.Name == "entryUUID").StringValues, Is.EqualTo(new[] { uuid }), "the external id the import matches the deletion on");
            Assert.That(deleted.Attributes.Single(a => a.Name == "distinguishedName").StringValues, Is.EqualTo(new[] { "uid=carol," + PeopleDn }));
            host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Never, "a deleted object cannot be fetched");
        }
    }

    [Test]
    public async Task ReadChangesAsync_Delete_ReadsNothingMoreFromTheLog()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            LoggedDelete("20260921090000.000001Z", "uid=carol," + PeopleDn, "3c1f6f0e-4d1a-4b2b-9c3e-1e2f3a4b5c6d")), out _);

        await Source(executor).ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>()), new ConnectedSystemImportResult(), CancellationToken.None);

        executor.Verify(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout), Times.Once, "the change read carries everything a deletion needs");
    }

    [Test]
    public async Task ReadChangesAsync_DeleteWithoutReqEntryUuid_IsNotStagedAndIsNotedOnTheRun()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            AccesslogEntry("20260921090000.000001Z", ("reqType", "delete"), ("reqDN", "uid=carol," + PeopleDn))), out _);
        var notes = new LdapDeltaSourceNotes();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>(), notes: notes), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty, "a deletion that names no object would be discarded by the import anyway");
            Assert.That(notes.Warning, Does.Contain("uid=carol," + PeopleDn).And.Contain("reqEntryUUID").And.Contain("Full Import"));
        }
    }

    #endregion

    #region Reading changes: paging

    [Test]
    public async Task ReadChangesAsync_FirstPage_AsksForOnePageOfTheRunProfilesPageSize()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out var sent);

        await Source(executor).ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>()), new ConnectedSystemImportResult(), CancellationToken.None);

        var paging = sent.Value!.Controls.OfType<PageResultRequestControl>().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(paging.PageSize, Is.EqualTo(500));
            Assert.That(paging.Cookie, Is.Empty, "the first page starts the search");
        }
    }

    [Test]
    public async Task ReadChangesAsync_DirectoryHasMore_LeavesAPaginationTokenThatResumesFromTheDirectorysCookie()
    {
        byte[] cookie = [1, 2, 3];
        var firstPage = ExecutorReturning(LdapTestResponses.SearchResponseWithPagingCookie(cookie,
            ModifyEntry("20260921090000.000001Z", "uid=alice," + PeopleDn)), out _);
        var firstResult = new ConnectedSystemImportResult();
        await Source(firstPage).ReadChangesAsync(Context(HostReturningObjects()), firstResult, CancellationToken.None);

        var secondPage = ExecutorReturning(LdapTestResponses.SearchResponseWithPagingCookie([],
            ModifyEntry("20260921090000.000002Z", "uid=bob," + PeopleDn)), out var sent);
        var secondResult = new ConnectedSystemImportResult();
        await Source(secondPage).ReadChangesAsync(Context(HostReturningObjects(), paginationTokens: firstResult.PaginationTokens), secondResult, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.PaginationTokens, Has.Count.EqualTo(1), "the import calls again while a token is left");
            Assert.That(sent.Value!.Controls.OfType<PageResultRequestControl>().Single().Cookie, Is.EqualTo(cookie),
                "the directory's own cursor carries the read on, so no entry can fall between two pages");
            Assert.That(sent.Value.Filter, Is.EqualTo(ChangeRead((PartitionDn, Watermark))),
                "a paged search is continued with the question it started with");
            Assert.That(secondResult.PaginationTokens, Is.Empty, "an empty cookie is the directory saying that page was the last");
            Assert.That(secondResult.ImportObjects, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task ReadChangesAsync_EntryAtExactlyTheWatermarkOnALaterPage_IsStillSkipped()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithPagingCookie([],
            ModifyEntry(Watermark, "uid=alice," + PeopleDn)), out _);
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(HostReturningObjects(), paginationTokens: [new ConnectedSystemPaginationToken("accesslog", [9])]), result, CancellationToken.None);

        Assert.That(result.ImportObjects, Is.Empty);
    }

    [Test]
    public async Task ReadChangesAsync_Always_ReportsThePagesObjectsRead()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithPagingCookie([7],
            ModifyEntry("20260921090000.000001Z", "uid=alice," + PeopleDn),
            ModifyEntry("20260921090000.000002Z", "uid=bob," + PeopleDn)), out _);
        var host = HostReturningObjects();

        await Source(executor).ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        host.Verify(h => h.ReportObjectsReadAsync(2), Times.Once);
    }

    [Test]
    public async Task ReadChangesAsync_SizeLimitExceeded_ThrowsNamingTheLimitAndTheRemedyAsync()
    {
        // The read used to carry on from the newest reqStart it had seen. The log is written in the order operations
        // finish, not the order they start, so an operation that started earlier and finished later could fall behind
        // that point and be missed without a word. Only an exempt account reads the whole log.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout))
            .Throws(new DirectoryOperationException(PartialResponse(
                ModifyEntry("20260921090000.000001Z", "uid=alice," + PeopleDn)), "size limit exceeded"));
        var result = new ConnectedSystemImportResult();

        await Assert.ThatAsync(() => Source(executor).ReadChangesAsync(Context(HostReturningObjects()), result, CancellationToken.None),
            Throws.TypeOf<CannotPerformDeltaImportException>()
                .With.Message.StartsWith("Changes cannot be detected: the directory stopped reading cn=accesslog at its size limit")
                .And.Message.Contains("olcLimits")
                .And.Message.Contains("run a Full Import"));
        Assert.That(result.ImportObjects, Is.Empty);
    }

    #endregion

    #region Reading changes: failure and cancellation

    [Test]
    public async Task ReadChangesAsync_ObjectNoLongerAtTheDn_SkipsTheEntry()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            ModifyEntry("20260921090000.000001Z", "uid=alice," + PeopleDn),
            ModifyEntry("20260921090000.000002Z", "uid=bob," + PeopleDn)), out _);
        var host = HostReturningObjects();
        host.Setup(h => h.GetObjectByDn("uid=alice," + PeopleDn, It.IsAny<ObjectChangeType>())).Returns((ConnectedSystemImportObject?)null);
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        Assert.That(result.ImportObjects.Select(o => o.Attributes.Single(a => a.Name == "distinguishedName").StringValues.Single()),
            Is.EqualTo(new[] { "uid=bob," + PeopleDn }), "an object deleted or moved since the log entry was written is skipped, and the rest are read");
    }

    [Test]
    public async Task ReadChangesAsync_SearchRefused_ThrowsCannotPerformDeltaImportExceptionAsync()
    {
        // A refused accesslog search used to be logged and the page imported nothing, which the run reported as
        // "no changes". A Delta Import that cannot see its changes says so and stops instead.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access rights"));
        var host = HostReturningObjects();
        var result = new ConnectedSystemImportResult();

        await Assert.ThatAsync(() => Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None),
            Throws.TypeOf<CannotPerformDeltaImportException>()
                .With.Message.StartsWith("Changes cannot be detected: the directory refused to read cn=accesslog (insufficient access rights)")
                .And.Message.Contains("Run a Full Import, which also detects deletions by absence"));
        Assert.That(result.ImportObjects, Is.Empty);
    }

    [Test]
    public async Task ReadChangesAsync_AccesslogNotFound_ThrowsCannotPerformDeltaImportExceptionAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.NoSuchObject), "no such object"));
        var result = new ConnectedSystemImportResult();

        await Assert.ThatAsync(() => Source(executor).ReadChangesAsync(Context(HostReturningObjects()), result, CancellationToken.None),
            Throws.TypeOf<CannotPerformDeltaImportException>()
                .With.Message.StartsWith("Changes cannot be detected: the directory provides no accesslog at cn=accesslog, or none the account JIM connects as may read")
                .And.Message.Contains("enable the accesslog overlay"));
        Assert.That(result.ImportObjects, Is.Empty);
    }

    [Test]
    public async Task ReadChangesAsync_AccesslogNotFoundAsLdapException32_ThrowsCannotPerformDeltaImportExceptionAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout)).Throws(new LdapException(32, "no such object"));

        await Assert.ThatAsync(() => Source(executor).ReadChangesAsync(Context(HostReturningObjects()), new ConnectedSystemImportResult(), CancellationToken.None),
            Throws.TypeOf<CannotPerformDeltaImportException>().With.Message.Contains("provides no accesslog at cn=accesslog"));
    }

    [Test]
    public async Task ReadChangesAsync_ConnectionFailure_PropagatesAsync()
    {
        // A fault on the way to the directory is not a verdict on the accesslog; it is the run's failure, in the
        // directory's own words, and not dressed up as a missing or refused accesslog.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout)).Throws(new LdapException(81, "server down"));

        await Assert.ThatAsync(() => Source(executor).ReadChangesAsync(Context(HostReturningObjects()), new ConnectedSystemImportResult(), CancellationToken.None),
            Throws.TypeOf<LdapException>().With.Message.Contains("server down"));
    }

    [Test]
    public async Task ReadChangesAsync_CancellationRequested_StopsBetweenEntries()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            ModifyEntry("20260921090000.000001Z", "uid=alice," + PeopleDn),
            ModifyEntry("20260921090000.000002Z", "uid=bob," + PeopleDn),
            ModifyEntry("20260921090000.000003Z", "uid=carol," + PeopleDn)), out _);
        using var cancellation = new CancellationTokenSource();
        var host = HostReturningObjects();
        host.Setup(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()))
            .Callback(() => cancellation.Cancel())
            .Returns((string dn, ObjectChangeType changeType) => ImportObjectFor(dn, changeType));
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Has.Count.EqualTo(1), "the entry in hand completes; the next is not started");
            host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Once);
        }
    }

    #endregion

    #region Helpers

    /// <summary>The partition a continuity check is told holds the watermark it checks.</summary>
    private static ConnectedSystemPartition Partition() => new() { Id = 1, ExternalId = PartitionDn, Name = PartitionDn, Selected = true };

    private static LdapAccesslogDeltaSource Source(Mock<ILdapOperationExecutor> executor, CapturingSink? sink = null) =>
        new(executor.Object, sink == null ? Log.Logger : new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger());

    private static LdapDeltaReadContext Context(Mock<ILdapDeltaImportHost> host, string previousTimestamp = Watermark, string partitionDn = PartitionDn,
        IReadOnlyList<ConnectedSystemPaginationToken>? paginationTokens = null, LdapDeltaSourceNotes? notes = null)
    {
        var partition = new ConnectedSystemPartition { Id = 1, ExternalId = partitionDn, Name = partitionDn, Selected = true };
        var container = new ConnectedSystemContainer { Id = 1, ExternalId = partitionDn == PartitionDn ? PeopleDn : partitionDn, Name = "people", Selected = true, Scope = ConnectedSystemContainerScope.Subtree };
        return new LdapDeltaReadContext
        {
            PreviousRootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP },
            Watermarks = new Dictionary<int, LdapPartitionWatermark>
            {
                [partition.Id] = new() { Kind = LdapDeltaSourceKind.Accesslog, AccesslogTimestamp = previousTimestamp, AccesslogTimestampIsLogged = true }
            },
            CurrentRootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP },
            TargetPartitions = [partition],
            ScopeDecidingContainers = [container],
            ObjectTypes = [InetOrgPerson()],
            PaginationTokens = paginationTokens ?? [],
            PageSize = 500,
            SearchTimeout = Timeout,
            Notes = notes ?? new LdapDeltaSourceNotes(),
            Host = host.Object
        };
    }

    /// <summary>
    /// A change read of the given partitions, by distinguished name, each with its own watermark and a selected people
    /// Container beneath it (Containers decide scope only for a partition with a DN).
    /// </summary>
    private static LdapDeltaReadContext Context(Mock<ILdapDeltaImportHost> host, params (string Dn, string Watermark)[] partitions) =>
        ContextOf(host, partitions, containersDecideScope: true);

    /// <param name="containersDecideScope">False for a run in which no Container states an opinion about scope, so
    /// that the partitions alone decide what is read.</param>
    private static LdapDeltaReadContext ContextOf(Mock<ILdapDeltaImportHost> host, (string Dn, string Watermark)[] partitions, bool containersDecideScope)
    {
        var targets = partitions.Select((partition, index) => new ConnectedSystemPartition { Id = index + 1, ExternalId = partition.Dn, Name = partition.Dn, Selected = true }).ToList();
        return new LdapDeltaReadContext
        {
            PreviousRootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP },
            Watermarks = targets.ToDictionary(partition => partition.Id, partition => new LdapPartitionWatermark
            {
                Kind = LdapDeltaSourceKind.Accesslog,
                AccesslogTimestamp = partitions[partition.Id - 1].Watermark,
                AccesslogTimestampIsLogged = true
            }),
            CurrentRootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP },
            TargetPartitions = targets,
            ScopeDecidingContainers = targets.Where(partition => containersDecideScope && partition.ExternalId.Length > 0)
                .Select(partition => new ConnectedSystemContainer { Id = partition.Id, ExternalId = "ou=people," + partition.ExternalId, Name = "people", Selected = true, Scope = ConnectedSystemContainerScope.Subtree })
                .ToList(),
            ObjectTypes = [InetOrgPerson()],
            PaginationTokens = [],
            PageSize = 500,
            SearchTimeout = Timeout,
            Notes = new LdapDeltaSourceNotes(),
            Host = host.Object
        };
    }

    /// <summary>The change read's filter: one clause per partition, by its DN, carrying that partition's own watermark.</summary>
    private static string ChangeRead(params (string PartitionDn, string Watermark)[] partitions) =>
        $"(&(objectClass=auditWriteObject)(reqResult=0)(|{string.Concat(partitions.Select(p => $"(&(reqDN:dnSubtreeMatch:={p.PartitionDn})(reqStart>={p.Watermark}))"))}))";

    private static ConnectedSystemObjectType InetOrgPerson()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "inetOrgPerson", Selected = true };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "entryUUID", Type = AttributeDataType.Text, Selected = true, IsExternalId = true });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "distinguishedName", Type = AttributeDataType.Text, Selected = true });
        return objectType;
    }

    /// <summary>A host whose object reads answer with a minimal import object carrying the DN asked for.</summary>
    private static Mock<ILdapDeltaImportHost> HostReturningObjects()
    {
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()))
            .Returns((string dn, ObjectChangeType changeType) => ImportObjectFor(dn, changeType));
        return host;
    }

    private static ConnectedSystemImportObject ImportObjectFor(string dn, ObjectChangeType changeType) => new()
    {
        ObjectType = "inetOrgPerson",
        ChangeType = changeType,
        Attributes = [new ConnectedSystemImportObjectAttribute { Name = "distinguishedName", Type = AttributeDataType.Text, StringValues = [dn] }]
    };

    private static Mock<ILdapOperationExecutor> ExecutorReturning(SearchResponse response, out StrongBox<SearchRequest?> sent)
    {
        var captured = new StrongBox<SearchRequest?>(null);
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout))
            .Callback<DirectoryRequest, TimeSpan>((request, _) => captured.Value = (SearchRequest)request)
            .Returns(response);
        sent = captured;
        return executor;
    }

    /// <summary>An executor whose untimed read, the one the readiness check makes, fails with the given exception.</summary>
    private static Mock<ILdapOperationExecutor> ExecutorThrowingOnRead(Exception exception)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>())).Throws(exception);
        return executor;
    }

    private static bool IsSearch(DirectoryRequest request) => request is SearchRequest;

    private static bool IsSortedSearch(DirectoryRequest request) =>
        request is SearchRequest search && search.Controls.OfType<SortRequestControl>().Any();

    private static bool IsSearchWithFilter(DirectoryRequest request, string filter) =>
        request is SearchRequest search && !search.Controls.OfType<SortRequestControl>().Any() && search.Filter.ToString() == filter;

    /// <summary>What a directory returns alongside SizeLimitExceeded: the entries it managed to send before the cap.</summary>
    private static SearchResponse PartialResponse(params SearchResultEntry[] entries)
    {
        var response = LdapTestResponses.Create<SearchResponse>(ResultCode.SizeLimitExceeded);
        var entryCollection = (SearchResultEntryCollection)Activator.CreateInstance(typeof(SearchResultEntryCollection), nonPublic: true)!;
        var add = typeof(SearchResultEntryCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(SearchResultEntry)])!;
        foreach (var entry in entries)
            add.Invoke(entryCollection, [entry]);

        typeof(SearchResponse).GetMethod("set_Entries", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(response, [entryCollection]);
        return response;
    }

    private static SearchResultEntry RootDseEntry() => LdapTestResponses.Entry(string.Empty, ("vendorName", "OpenLDAP"));

    /// <summary>
    /// The watermarks a run of the given partitions takes, by partition id (1, 2, ... in the order given), each given
    /// with its distinguished name and the watermark JIM holds for it, or none.
    /// </summary>
    private static Task<IReadOnlyDictionary<int, LdapPartitionWatermark?>> CaptureEachAsync(LdapAccesslogDeltaSource source, params (string Dn, LdapPartitionWatermark? Previous)[] partitions)
    {
        var targets = partitions.Select((partition, index) => new ConnectedSystemPartition { Id = index + 1, ExternalId = partition.Dn, Name = partition.Dn, Selected = true }).ToList();
        return source.CaptureWatermarksAsync(RootDseEntry(), new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP },
            targets.ToDictionary(partition => partition.Id, partition => partitions[partition.Id - 1].Previous), targets, Timeout);
    }

    /// <summary>The watermark a run of the given partitions, each holding <paramref name="previous"/>, takes for the first.</summary>
    private static async Task<LdapPartitionWatermark?> CaptureAsync(LdapAccesslogDeltaSource source, LdapPartitionWatermark? previous, params string[] partitionDns)
    {
        var captured = await CaptureEachAsync(source, partitionDns.Select(dn => (dn, previous)).ToArray());
        Assert.That(captured.Keys, Is.EquivalentTo(Enumerable.Range(1, partitionDns.Length)), "a watermark, or none, for every target partition and no other");
        return captured[1];
    }

    /// <summary>The walk's search for the write operations at or after a timestamp to entries within any of the given partitions.</summary>
    private static string WalkWithinFrom(string reqStart, params string[] partitionDns) =>
        $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={reqStart})(|{string.Concat(partitionDns.Select(dn => $"(reqDN:dnSubtreeMatch:={dn})"))}))";

    /// <summary>The walk's search for the write operations at or after a timestamp.</summary>
    private static string WalkFrom(string reqStart) => $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={reqStart}))";

    /// <summary>The walk's search for the write operations at or after a timestamp to entries within the partition.</summary>
    private static string WalkWithinThePartitionFrom(string reqStart) =>
        $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={reqStart})(|(reqDN:dnSubtreeMatch:={PartitionDn})))";

    /// <summary>Timestamps a second apart from <paramref name="first"/>, in generalised time, as the accesslog writes reqStart.</summary>
    private static List<string> Timestamps(DateTime first, int count) =>
        Enumerable.Range(0, count)
            .Select(i => first.AddSeconds(i).ToString("yyyyMMddHHmmss.ffffffZ", CultureInfo.InvariantCulture))
            .ToList();

    /// <summary>
    /// cn=accesslog on a directory without the sssvlv overlay, as the watermark walk sees it: the sorted search is
    /// refused, and each of the walk's searches is answered with the logged writes it matches (see
    /// <see cref="ScriptedAccesslogFilter"/>), in the order they were logged, cut short at the size limit as OpenLDAP
    /// does. Every search's filter is recorded, in order, and so is what the directory returned to it.
    /// </summary>
    private sealed class ScriptedAccesslog
    {
        /// <summary>Each logged write, in the order the directory logged them.</summary>
        internal LoggedWrites Logged { get; } = new();

        internal int? SizeLimit { get; init; }

        /// <summary>The filters of the walk's searches, in the order they were sent.</summary>
        internal List<string> Walk { get; } = [];

        /// <summary>The reqStart of every write the directory returned to each of the walk's searches, one list per search.</summary>
        internal List<List<string>> Returned { get; } = [];

        internal Mock<ILdapOperationExecutor> Executor()
        {
            var executor = new Mock<ILdapOperationExecutor>();
            executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSortedSearch(r)), Timeout))
                .Throws(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.UnavailableCriticalExtension), "critical extension is unavailable"));
            executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearch(r) && !IsSortedSearch(r)), Timeout))
                .Returns((DirectoryRequest request, TimeSpan _) => Answer((SearchRequest)request));
            return executor;
        }

        private SearchResponse Answer(SearchRequest request)
        {
            var filter = request.Filter.ToString()!;
            Walk.Add(filter);

            var matching = Logged
                .Where(write => ScriptedAccesslogFilter.Matches(filter, write.ReqStart, write.ReqDn))
                .Select(write => write.ReqStart)
                .ToList();
            var returned = SizeLimit is { } sizeLimit && matching.Count > sizeLimit ? matching.Take(sizeLimit).ToList() : matching;
            Returned.Add(returned);

            var entries = returned.Select(reqStart => AccesslogEntry(reqStart)).ToArray();
            return returned.Count < matching.Count
                ? throw new DirectoryOperationException(PartialResponse(entries), "size limit exceeded")
                : LdapTestResponses.SearchResponseWithEntries(entries);
        }
    }

    /// <summary>
    /// The writes a scripted accesslog holds, in the order the directory logged them: each one's reqStart, and the
    /// entry it was made to, which is one within the partition unless another is named (<c>{ reqStart, dn }</c>).
    /// </summary>
    private sealed class LoggedWrites : IEnumerable<(string ReqStart, string ReqDn)>
    {
        private readonly List<(string ReqStart, string ReqDn)> _writes = [];

        internal void Add(string reqStart) => Add(reqStart, WithinThePartition);

        internal void Add(string reqStart, string reqDn) => _writes.Add((reqStart, reqDn));

        internal void AddRange(IEnumerable<string> reqStarts, string reqDn = WithinThePartition) =>
            _writes.AddRange(reqStarts.Select(reqStart => (reqStart, reqDn)));

        public IEnumerator<(string ReqStart, string ReqDn)> GetEnumerator() => _writes.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static SearchResultEntry AccesslogEntry(string reqStart, params (string Name, string Value)[] attributes) =>
        LdapTestResponses.Entry($"reqStart={reqStart},cn=accesslog", [("reqStart", reqStart), .. attributes]);

    private static SearchResultEntry ModifyEntry(string reqStart, string reqDn) =>
        AccesslogEntry(reqStart, ("reqType", "modify"), ("reqDN", reqDn));

    /// <summary>An auditDelete entry as the change read returns it: its DN and entryUUID, and no reqOld.</summary>
    private static SearchResultEntry LoggedDelete(string reqStart, string reqDn, string entryUuid) =>
        AccesslogEntry(reqStart, ("reqType", "delete"), ("reqDN", reqDn), ("reqEntryUUID", entryUuid));

    private static DateTime ParseGeneralisedTime(string value)
    {
        var match = Regex.Match(value, @"^(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})\.(\d{6})Z$");
        Assert.That(match.Success, Is.True, $"'{value}' is not a generalised time");
        var groups = match.Groups.Cast<Group>().Skip(1).Select(g => int.Parse(g.Value)).ToArray();
        return new DateTime(groups[0], groups[1], groups[2], groups[3], groups[4], groups[5], DateTimeKind.Utc).AddTicks(groups[6] * 10);
    }

    /// <summary>A mutable cell, so a request captured inside a Moq callback can be read back through an out parameter.</summary>
    private sealed class StrongBox<T>(T value)
    {
        internal T Value { get; set; } = value;
    }

    /// <summary>Collects what the source logs, so a test can assert on the warning it raises.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        internal List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    #endregion
}
