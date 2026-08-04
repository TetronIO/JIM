// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using Serilog;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// An export makes two passes. The second covers only what the first could not write for want of
/// an object that did not exist yet, so it is a fraction of the export's work and takes its own
/// time. Reported against the export's totals it read as finished from the moment it started, which
/// is what the Activity's progress bar and time remaining are rendered from.
/// </summary>
/// <remarks>
/// Pinned here rather than observed on a real run: the pass takes tens of milliseconds at
/// integration-test scale (28ms to 153ms measured on Scenario 8), so no amount of polling a running
/// system reliably catches it mid-flight.
/// </remarks>
public class DeferredExportProgressTests
{
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    private ConnectedSystem TargetSystem { get; set; } = null!;
    private ConnectedSystemObjectType TargetUserType { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute DisplayNameAttr { get; set; } = null!;

    [TearDown]
    public void TearDown() => Jim?.Dispose();

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        PendingExportsData = [];

        MockJimDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(ConnectedSystemsData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(ConnectedSystemObjectTypesData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(ConnectedSystemObjectsData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(PendingExportsData.BuildMockDbSet().Object);

        SyncRepo = TestUtilities.CreateSyncRepository();
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        TargetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        TargetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        DisplayNameAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
    }

    private PendingExport SeedExport(bool hasUnresolvedReferences)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            Type = TargetUserType,
            TypeId = TargetUserType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            AttributeValues = []
        };
        ConnectedSystemObjectsData.Add(cso);

        var export = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObjectId = cso.Id,
            ConnectedSystemObject = cso,
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            HasUnresolvedReferences = hasUnresolvedReferences,
            AttributeValueChanges = []
        };
        export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "Someone",
            UnresolvedReferenceValue = hasUnresolvedReferences ? Guid.NewGuid().ToString() : null,
            Status = PendingExportAttributeChangeStatus.Pending
        });

        PendingExportsData.Add(export);
        SyncRepo.SeedPendingExport(export);
        return export;
    }

    private static Mock<IConnector> ExportingConnector()
    {
        var connector = new Mock<IConnector>();
        connector.As<IConnectorExportUsingCalls>()
            .Setup(c => c.ExportAsync(It.IsAny<List<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((List<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList());
        connector.Setup(c => c.Name).Returns("Test Connector");
        return connector;
    }

    [Test]
    public async Task ExecuteExportsAsync_DeferredPass_ReportsItsOwnWorkRatherThanTheExportsTotalsAsync()
    {
        // Arrange: three exports go straight out, one has to wait for a reference that does not
        // resolve, so the second pass covers one export out of four.
        for (var i = 0; i < 3; i++)
            SeedExport(hasUnresolvedReferences: false);
        SeedExport(hasUnresolvedReferences: true);

        var reports = new List<ExportProgressInfo>();

        // Act
        await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            ExportingConnector().Object,
            SyncRunMode.PreviewAndSync,
            new ExportExecutionOptions { BatchSize = 100, MaxParallelism = 1 },
            CancellationToken.None,
            progressInfo =>
            {
                reports.Add(progressInfo);
                return Task.CompletedTask;
            });

        // Assert
        var deferredPassReports = reports.Where(r => r.PassTotal.HasValue).ToList();
        Assert.That(deferredPassReports, Is.Not.Empty,
            "The deferred pass reported nothing about its own work, so the Activity's counters would still describe the whole export.");
        Assert.That(deferredPassReports.Select(r => r.CountingWindow.Total).Distinct(), Is.EqualTo(new[] { 1 }),
            "Every report from the second pass should count against the one export it actually covers.");
        Assert.That(deferredPassReports.Select(r => r.CountingWindow.Processed).Max(), Is.EqualTo(1),
            "Confirming an export still cannot be written finishes with it, so the pass should end complete rather than short.");
    }

    [Test]
    public async Task ExecuteExportsAsync_FirstPass_KeepsReportingAgainstTheWholeExportAsync()
    {
        // The window only narrows for the pass that has its own work; the first pass is the export.
        for (var i = 0; i < 3; i++)
            SeedExport(hasUnresolvedReferences: false);

        var reports = new List<ExportProgressInfo>();

        await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            ExportingConnector().Object,
            SyncRunMode.PreviewAndSync,
            new ExportExecutionOptions { BatchSize = 100, MaxParallelism = 1 },
            CancellationToken.None,
            progressInfo =>
            {
                reports.Add(progressInfo);
                return Task.CompletedTask;
            });

        Assert.That(reports.Any(r => r.PassTotal.HasValue), Is.False,
            "Nothing was deferred, so no report should claim a window of its own.");
        Assert.That(reports.Select(r => r.CountingWindow.Total).Distinct(), Is.EqualTo(new[] { 3 }));
    }
}
