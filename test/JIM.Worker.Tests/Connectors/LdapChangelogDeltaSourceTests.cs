// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The changelog change source (<see cref="LdapChangelogDeltaSource"/>), as extracted verbatim from the import
/// shell. Several cases characterise behaviour a later layer deliberately changes (a failed search importing
/// nothing rather than failing, a failed watermark query recording zero); they pin what the extraction preserved,
/// not what the source ought to do.
/// </summary>
[TestFixture]
public class LdapChangelogDeltaSourceTests
{
    private const string ContainerDn = "ou=People,dc=example,dc=com";
    private const string InScopeDn = "uid=jsmith,ou=People,dc=example,dc=com";
    private const string OutOfScopeDn = "uid=jsmith,ou=Elsewhere,dc=example,dc=com";
    private const int PreviousChangeNumber = 1200;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    #region HasBaseline / VerifyContinuity / VerifyReadinessAsync

    [Test]
    public void HasBaseline_NoChangeNumber_IsFalse()
    {
        var source = Source(new Mock<ILdapOperationExecutor>());

        Assert.That(source.HasBaseline(new LdapConnectorRootDse()), Is.False);
    }

    [Test]
    public void HasBaseline_ChangeNumberPresent_IsTrue()
    {
        var source = Source(new Mock<ILdapOperationExecutor>());

        Assert.That(source.HasBaseline(new LdapConnectorRootDse { LastChangeNumber = 0 }), Is.True);
    }

    [Test]
    public void VerifyContinuity_Always_DoesNothing()
    {
        var executor = new Mock<ILdapOperationExecutor>(MockBehavior.Strict);
        var source = Source(executor);

        Assert.DoesNotThrow(() => source.VerifyContinuity(
            new LdapConnectorRootDse { LastChangeNumber = 5 },
            new LdapConnectorRootDse { LastChangeNumber = 1 }));
        executor.VerifyNoOtherCalls();
    }

    [Test]
    public async Task VerifyReadinessAsync_Always_ReportsNothingInThisLayer()
    {
        var executor = new Mock<ILdapOperationExecutor>(MockBehavior.Strict);
        var source = Source(executor);

        var findings = await source.VerifyReadinessAsync(new LdapConnectorRootDse(), ["dc=example,dc=com"], CancellationToken.None);

        Assert.That(findings, Is.Empty);
        executor.VerifyNoOtherCalls();
    }

    #endregion

    #region CaptureWatermarkAsync

    [Test]
    public async Task CaptureWatermarkAsync_ChangelogHasEntries_RecordsTheLastEntrysChangeNumber()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns(LdapTestResponses.SearchResponseWithEntries(
                LdapTestResponses.Entry("changeNumber=7,cn=changelog", ("changeNumber", "7")),
                LdapTestResponses.Entry("changeNumber=9,cn=changelog", ("changeNumber", "9"))));
        var rootDse = new LdapConnectorRootDse();

        await Source(executor).CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        Assert.That(rootDse.LastChangeNumber, Is.EqualTo(9));
    }

    [Test]
    public async Task CaptureWatermarkAsync_ChangelogSearchFails_RecordsZero()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>()))
            .Throws(new DirectoryOperationException("insufficient access"));
        var rootDse = new LdapConnectorRootDse();

        await Source(executor).CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        // TODO (#1725): layer 1 characterisation; a later layer stops a failed watermark query reading as change number zero.
        Assert.That(rootDse.LastChangeNumber, Is.EqualTo(0));
    }

    #endregion

    #region ReadChangesAsync

    [Test]
    public async Task ReadChangesAsync_Always_EntersTheQueryChangesPhase()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse());
        var host = new Mock<ILdapDeltaImportHost>();

        await Source(executor).ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        host.Verify(h => h.EnterPhaseAsync(LdapConnectorPhases.QueryChanges, $"Querying changelog since change number {PreviousChangeNumber:N0}..."), Times.Once);
    }

    [Test]
    public async Task ReadChangesAsync_TargetOutsideSelectedContainers_IsSkipped()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(ChangeEntry("modify", OutOfScopeDn)));
        var host = new Mock<ILdapDeltaImportHost>();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty);
            host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Never);
        }
    }

    [TestCase("add", ObjectChangeType.Added)]
    [TestCase("modify", ObjectChangeType.Updated)]
    [TestCase("modrdn", ObjectChangeType.Updated)]
    [TestCase("moddn", ObjectChangeType.Updated)]
    public async Task ReadChangesAsync_AddModifyModrdnModdn_FetchTheCurrentObjectWithTheMappedChangeType(string changeType, ObjectChangeType expected)
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(ChangeEntry(changeType, InScopeDn)));
        var fetched = new ConnectedSystemImportObject { ChangeType = expected };
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(InScopeDn, expected)).Returns(fetched);
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            host.Verify(h => h.GetObjectByDn(InScopeDn, expected), Times.Once);
            Assert.That(result.ImportObjects, Is.EqualTo(new[] { fetched }));
        }
    }

    [Test]
    public async Task ReadChangesAsync_Delete_YieldsADeletedImportObject()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(ChangeEntry("delete", InScopeDn)));
        var host = new Mock<ILdapDeltaImportHost>();
        var result = new ConnectedSystemImportResult();

        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Has.Count.EqualTo(1));
            Assert.That(result.ImportObjects[0].ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Never);
        }
    }

    [Test]
    public async Task ReadChangesAsync_UnknownChangeType_FetchesTheObjectAsNotSet()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(ChangeEntry("something-new", InScopeDn)));
        var host = new Mock<ILdapDeltaImportHost>();

        await Source(executor).ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        host.Verify(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.NotSet), Times.Once);
    }

    [Test]
    public async Task ReadChangesAsync_Always_ReportsObjectsRead()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(
            ChangeEntry("add", InScopeDn),
            ChangeEntry("delete", InScopeDn),
            ChangeEntry("modify", OutOfScopeDn)));
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Added)).Returns(new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Added });

        await Source(executor).ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        host.Verify(h => h.ReportObjectsReadAsync(2), Times.Once);
    }

    [Test]
    public async Task ReadChangesAsync_SearchFails_ImportsNothingWithoutFailing()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Throws(new DirectoryOperationException("insufficient access"));
        var host = new Mock<ILdapDeltaImportHost>();
        var result = new ConnectedSystemImportResult();

        // TODO (#1725): layer 1 characterisation; a later layer makes a failed changelog search fail the Delta Import.
        await Source(executor).ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty);
            host.Verify(h => h.ReportObjectsReadAsync(0), Times.Once);
        }
    }

    #endregion

    private static LdapChangelogDeltaSource Source(Mock<ILdapOperationExecutor> executor) => new(executor.Object, Log.Logger);

    private static Mock<ILdapOperationExecutor> ExecutorReturning(SearchResponse response)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout)).Returns(response);
        return executor;
    }

    private static SearchResultEntry ChangeEntry(string changeType, string targetDn) =>
        LdapTestResponses.Entry($"changeNumber={PreviousChangeNumber + 1},cn=changelog",
            ("changeNumber", (PreviousChangeNumber + 1).ToString()),
            ("changeType", changeType),
            ("targetDN", targetDn));

    private static SearchResultEntry RootDseEntry() => LdapTestResponses.Entry("", ("vendorName", "389 Project"));

    private static LdapDeltaReadContext Context(Mock<ILdapDeltaImportHost> host) => new()
    {
        PreviousRootDse = new LdapConnectorRootDse { LastChangeNumber = PreviousChangeNumber },
        CurrentRootDse = new LdapConnectorRootDse { LastChangeNumber = PreviousChangeNumber + 10 },
        TargetPartitions = [],
        ScopeDecidingContainers = [new ConnectedSystemContainer { ExternalId = ContainerDn, Name = "People", Selected = true }],
        ObjectTypes = [new ConnectedSystemObjectType { Name = "inetOrgPerson", Selected = true }],
        PaginationTokens = [],
        PageSize = 500,
        SearchTimeout = Timeout,
        Notes = new LdapDeltaSourceNotes(),
        Host = host.Object
    };
}
