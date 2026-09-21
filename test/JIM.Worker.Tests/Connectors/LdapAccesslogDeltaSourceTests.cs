// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.DirectoryServices.Protocols;
using System.Reflection;
using System.Text.RegularExpressions;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The accesslog change source an OpenLDAP Delta Import reads through, as extracted from the import: the watermark
/// it captures from cn=accesslog (by server-side sort when the directory allows it, by walking the size limit
/// forward when it does not), and the reading of write operations at or after that watermark into import objects.
/// These characterise the behaviour as it stood before the extraction; where a later layer changes it, the test
/// says so.
/// </summary>
[TestFixture]
public class LdapAccesslogDeltaSourceTests
{
    private const string PartitionDn = "dc=corp,dc=local";
    private const string PeopleDn = "ou=people,dc=corp,dc=local";
    private const string Watermark = "20260921080000.000000Z";
    private const string AccesslogFilter = "(&(objectClass=auditWriteObject)(reqResult=0))";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    #region Watermark capture

    [Test]
    public async Task CaptureWatermarkAsync_ServerSideSortSupported_TakesTheLatestReqStartFromASingleEntry()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSortedSearch(r)), Timeout))
            .Returns(LdapTestResponses.SearchResponseWith("reqStart=20260921090000.000001Z,cn=accesslog", ("reqStart", "20260921090000.000001Z")));
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };

        await Source(executor).CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootDse.LastAccesslogTimestamp, Is.EqualTo("20260921090000.000001Z"));
            executor.Verify(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSortedSearch(r) && ((SearchRequest)r).SizeLimit == 1), Timeout), Times.Once,
                "the sort asks for the single latest entry rather than the whole log");
            executor.VerifyNoOtherCalls();
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_SortRefused_FallsBackToTheSizeLimitWalk()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSortedSearch(r)), Timeout))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.UnavailableCriticalExtension), "critical extension is unavailable"));
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearchWithFilter(r, AccesslogFilter)), Timeout))
            .Returns(LdapTestResponses.SearchResponseWithEntries(
                AccesslogEntry("20260921090000.000001Z"),
                AccesslogEntry("20260921090500.000002Z"),
                AccesslogEntry("20260921090200.000003Z")));
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };

        await Source(executor).CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        Assert.That(rootDse.LastAccesslogTimestamp, Is.EqualTo("20260921090500.000002Z"), "the latest reqStart, whatever order the directory returned them in");
    }

    [Test]
    public async Task CaptureWatermarkAsync_SizeLimitExceeded_WalksForwardFromTheLatestTimestampSeen()
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
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };

        await Source(executor).CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootDse.LastAccesslogTimestamp, Is.EqualTo("20260921090200.000003Z"));
            executor.Verify(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearch(r)), Timeout), Times.Exactly(3), "the sort attempt, the capped first batch and the narrowed second");
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_AccesslogEmpty_RecordsANowTimestamp()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout)).Returns(LdapTestResponses.EmptySearchResponse());
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        var before = DateTime.UtcNow.AddSeconds(-1);

        await Source(executor).CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootDse.LastAccesslogTimestamp, Does.Match(@"^\d{14}\.\d{6}Z$"), "a generalised-time watermark so the next Delta Import has a baseline");
            Assert.That(ParseGeneralisedTime(rootDse.LastAccesslogTimestamp!), Is.InRange(before, DateTime.UtcNow.AddSeconds(1)));
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_AccesslogUnreadable_RecordsANowTimestamp()
    {
        // Layer 1 characterisation: a refusal from the accesslog search is logged and a generated timestamp stands
        // in for the watermark, exactly as an empty accesslog does. A later layer tells the two apart.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access rights"));
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        var log = new CapturingSink();

        await Source(executor, log).CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootDse.LastAccesslogTimestamp, Does.Match(@"^\d{14}\.\d{6}Z$"));
            Assert.That(log.Events.Any(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("Failed to query accesslog")), Is.True);
        }
    }

    #endregion

    #region Continuity, readiness and baseline

    [Test]
    public void VerifyContinuity_Always_DoesNothing()
    {
        var previous = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP, DnsHostName = "ldap-a.corp.local", LastAccesslogTimestamp = Watermark };
        var current = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP, DnsHostName = "ldap-b.corp.local", LastAccesslogTimestamp = "20260921070000.000000Z" };

        Assert.DoesNotThrow(() => Source(new Mock<ILdapOperationExecutor>()).VerifyContinuity(previous, current),
            "a timestamp carries no server identity to verify, so nothing can invalidate it here");
    }

    [Test]
    public async Task VerifyReadinessAsync_Always_ReportsNothingInThisLayer()
    {
        var executor = new Mock<ILdapOperationExecutor>();

        var findings = await Source(executor).VerifyReadinessAsync(new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP }, [PartitionDn], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings, Is.Empty);
            executor.VerifyNoOtherCalls();
        }
    }

    [Test]
    public void HasBaseline_NoTimestamp_IsFalse()
    {
        var source = Source(new Mock<ILdapOperationExecutor>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.HasBaseline(new LdapConnectorRootDse { LastAccesslogTimestamp = null }), Is.False);
            Assert.That(source.HasBaseline(new LdapConnectorRootDse { LastAccesslogTimestamp = string.Empty }), Is.False);
        }
    }

    [Test]
    public void HasBaseline_TimestampPresent_IsTrue()
    {
        Assert.That(Source(new Mock<ILdapOperationExecutor>()).HasBaseline(new LdapConnectorRootDse { LastAccesslogTimestamp = Watermark }), Is.True);
    }

    #endregion

    #region Reading changes: request shape and phase

    [Test]
    public async Task ReadChangesAsync_Always_QueriesWriteOperationsAtOrAfterTheWatermarkOneLevelUnderCnAccesslog()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out var sent);
        var host = new Mock<ILdapDeltaImportHost>();

        await Source(executor).ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Value!.DistinguishedName, Is.EqualTo("cn=accesslog"));
            Assert.That(sent.Value.Scope, Is.EqualTo(SearchScope.OneLevel));
            Assert.That(sent.Value.Filter, Is.EqualTo($"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={Watermark}))"));
            Assert.That(sent.Value.Attributes.Cast<string>(), Is.EquivalentTo(new[] { "reqStart", "reqType", "reqDN", "reqOld", "reqEntryUUID" }));
        }
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
            DeleteEntry("20260921090000.000002Z", "uid=carol," + PeopleDn, "3c1f6f0e-4d1a-4b2b-9c3e-1e2f3a4b5c6d")), out _);
        var host = HostReturningObjects();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        Assert.That(result.ImportObjects.Select(o => o.ChangeType), Is.EqualTo(new[] { ObjectChangeType.Added, ObjectChangeType.Deleted }),
            "the DN was already seen, and the deletion is still the final state");
    }

    #endregion

    #region Reading changes: deletions

    [Test]
    public async Task ReadChangesAsync_DeleteWithReqOldClassAndEntryUuid_YieldsADeletedImportObjectWithExternalIdAndDn()
    {
        const string uuid = "3c1f6f0e-4d1a-4b2b-9c3e-1e2f3a4b5c6d";
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            DeleteEntry("20260921090000.000001Z", "uid=carol," + PeopleDn, uuid)), out _);
        var host = new Mock<ILdapDeltaImportHost>();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        var deleted = result.ImportObjects.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted.ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            Assert.That(deleted.ObjectType, Is.EqualTo("inetOrgPerson"), "resolved from the objectClass values in reqOld, as a live entry's would be");
            Assert.That(deleted.Attributes.Single(a => a.Name == "entryUUID").StringValues, Is.EqualTo(new[] { uuid }));
            Assert.That(deleted.Attributes.Single(a => a.Name == "distinguishedName").StringValues, Is.EqualTo(new[] { "uid=carol," + PeopleDn }));
            host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Never, "a deleted object cannot be fetched");
        }
    }

    [Test]
    public async Task ReadChangesAsync_DeleteWithoutReqOld_IsSkippedWithAWarning()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            AccesslogEntry("20260921090000.000001Z", ("reqType", "delete"), ("reqDN", "uid=carol," + PeopleDn), ("reqEntryUUID", "3c1f6f0e-4d1a-4b2b-9c3e-1e2f3a4b5c6d"))), out _);
        var log = new CapturingSink();
        var result = new ConnectedSystemImportResult();

        await Source(executor, log).ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>()), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty, "without reqOld there is no objectClass to resolve an Object Type from");
            Assert.That(log.Events.Any(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("Could not determine object type for deleted object")), Is.True);
        }
    }

    #endregion

    #region Reading changes: the size limit walk

    [Test]
    public async Task ReadChangesAsync_SizeLimitExceeded_NarrowsToTheLatestTimestampAndContinues()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearchWithFilter(r, $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={Watermark}))")), Timeout))
            .Throws(new DirectoryOperationException(PartialResponse(
                ModifyEntry("20260921090000.000001Z", "uid=alice," + PeopleDn),
                ModifyEntry("20260921090000.000002Z", "uid=bob," + PeopleDn)), "size limit exceeded"));
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearchWithFilter(r, "(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>=20260921090000.000002Z))")), Timeout))
            .Returns(LdapTestResponses.SearchResponseWithEntries(
                ModifyEntry("20260921090000.000002Z", "uid=bob," + PeopleDn),
                ModifyEntry("20260921090000.000003Z", "uid=carol," + PeopleDn)));
        var host = HostReturningObjects();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Has.Count.EqualTo(3), "every distinct object across both batches, the overlap at the boundary read once");
            host.Verify(h => h.GetObjectByDn("uid=bob," + PeopleDn, ObjectChangeType.Updated), Times.Once);
            executor.Verify(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearch(r)), Timeout), Times.Exactly(2));
        }
    }

    [Test]
    public async Task ReadChangesAsync_SizeLimitExceededWithNoProgress_StopsWithAWarning()
    {
        // Every entry the capped batch holds carries the same reqStart, so narrowing to it asks the same question
        // again: the walk stops rather than loops, and says that changes may have been missed.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearch(r)), Timeout))
            .Throws(new DirectoryOperationException(PartialResponse(
                ModifyEntry("20260921090000.000001Z", "uid=alice," + PeopleDn),
                ModifyEntry("20260921090000.000001Z", "uid=bob," + PeopleDn)), "size limit exceeded"));
        var host = HostReturningObjects();
        var log = new CapturingSink();
        var result = new ConnectedSystemImportResult();

        await Source(executor, log).ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Has.Count.EqualTo(2), "what the first batch held is still imported");
            executor.Verify(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearch(r)), Timeout), Times.Exactly(2), "the narrowed query is tried once and found to make no progress");
            Assert.That(log.Events.Any(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("Cannot narrow accesslog query further")), Is.True);
        }
    }

    [Test]
    public async Task ReadChangesAsync_Always_ReportsObjectsReadAtEachBatchBoundary()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearchWithFilter(r, $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={Watermark}))")), Timeout))
            .Throws(new DirectoryOperationException(PartialResponse(
                ModifyEntry("20260921090000.000001Z", "uid=alice," + PeopleDn),
                ModifyEntry("20260921090000.000002Z", "uid=bob," + PeopleDn)), "size limit exceeded"));
        executor.Setup(x => x.SendRequest(It.Is<DirectoryRequest>(r => IsSearchWithFilter(r, "(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>=20260921090000.000002Z))")), Timeout))
            .Returns(LdapTestResponses.SearchResponseWithEntries(
                ModifyEntry("20260921090000.000002Z", "uid=bob," + PeopleDn),
                ModifyEntry("20260921090000.000003Z", "uid=carol," + PeopleDn)));
        var host = HostReturningObjects();

        await Source(executor).ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            host.Verify(h => h.ReportObjectsReadAsync(2), Times.Once, "the first batch's two objects, reported before the second batch is fetched");
            host.Verify(h => h.ReportObjectsReadAsync(1), Times.Once, "the second batch's one new object");
            host.Verify(h => h.ReportObjectsReadAsync(It.IsAny<int>()), Times.Exactly(2));
        }
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
    public async Task ReadChangesAsync_SearchRefused_ImportsNothingWithoutFailing()
    {
        // Layer 1 characterisation: a refused accesslog search is logged and the page imports nothing, which reads
        // as "no changes". A later layer inverts this so that a Delta Import that cannot see its changes says so.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access rights"));
        var host = HostReturningObjects();
        var log = new CapturingSink();
        var result = new ConnectedSystemImportResult();

        await Source(executor, log).ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty);
            Assert.That(log.Events.Any(e => e.Level == LogEventLevel.Error && e.MessageTemplate.Text.Contains("Error querying accesslog")), Is.True);
        }
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

    private static LdapAccesslogDeltaSource Source(Mock<ILdapOperationExecutor> executor, CapturingSink? sink = null) =>
        new(executor.Object, sink == null ? Log.Logger : new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger());

    private static LdapDeltaReadContext Context(Mock<ILdapDeltaImportHost> host, string previousTimestamp = Watermark)
    {
        var partition = new ConnectedSystemPartition { Id = 1, ExternalId = PartitionDn, Name = PartitionDn, Selected = true };
        var container = new ConnectedSystemContainer { Id = 1, ExternalId = PeopleDn, Name = "people", Selected = true, Scope = ConnectedSystemContainerScope.Subtree };
        return new LdapDeltaReadContext
        {
            PreviousRootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP, LastAccesslogTimestamp = previousTimestamp },
            CurrentRootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP },
            TargetPartitions = [partition],
            ScopeDecidingContainers = [container],
            ObjectTypes = [InetOrgPerson()],
            PaginationTokens = [],
            PageSize = 500,
            SearchTimeout = Timeout,
            Notes = new LdapDeltaSourceNotes(),
            Host = host.Object
        };
    }

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

    private static SearchResultEntry AccesslogEntry(string reqStart, params (string Name, string Value)[] attributes) =>
        LdapTestResponses.Entry($"reqStart={reqStart},cn=accesslog", [("reqStart", reqStart), .. attributes]);

    private static SearchResultEntry ModifyEntry(string reqStart, string reqDn) =>
        AccesslogEntry(reqStart, ("reqType", "modify"), ("reqDN", reqDn));

    /// <summary>
    /// An auditDelete entry as slapo-accesslog writes it: reqOld holds the deleted entry's attributes as
    /// "name: value" lines, several of them, so it is built with a multi-valued attribute.
    /// </summary>
    private static SearchResultEntry DeleteEntry(string reqStart, string reqDn, string entryUuid)
    {
        const BindingFlags nonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        var attributeCollection = (SearchResultAttributeCollection)Activator.CreateInstance(typeof(SearchResultAttributeCollection), nonPublic: true)!;
        var add = typeof(SearchResultAttributeCollection).GetMethod("Add", nonPublicInstance, [typeof(string), typeof(DirectoryAttribute)])!;

        add.Invoke(attributeCollection, ["reqStart", new DirectoryAttribute("reqStart", reqStart)]);
        add.Invoke(attributeCollection, ["reqType", new DirectoryAttribute("reqType", "delete")]);
        add.Invoke(attributeCollection, ["reqDN", new DirectoryAttribute("reqDN", reqDn)]);
        add.Invoke(attributeCollection, ["reqEntryUUID", new DirectoryAttribute("reqEntryUUID", entryUuid)]);
        add.Invoke(attributeCollection, ["reqOld", new DirectoryAttribute("reqOld", "objectClass: top", "objectClass: person", "objectClass: inetOrgPerson", "uid: carol", $"entryUUID: {entryUuid}")]);

        return (SearchResultEntry)Activator.CreateInstance(typeof(SearchResultEntry), nonPublicInstance, binder: null,
            args: [$"reqStart={reqStart},cn=accesslog", attributeCollection], culture: null)!;
    }

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
