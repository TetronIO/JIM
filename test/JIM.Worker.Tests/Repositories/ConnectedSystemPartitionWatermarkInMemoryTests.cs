// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// The in-memory test provider's forms of the Partition watermark writes (#2715). It runs no SQL, so each write falls
/// back to EF Core with the same observable result; the workflow tests depend on that result being the real one. The
/// relational forms are covered against PostgreSQL by <see cref="ConnectedSystemPartitionWatermarkDatabaseTests"/>.
/// </summary>
[TestFixture]
public class ConnectedSystemPartitionWatermarkInMemoryTests
{
    private string _databaseName = null!;
    private JimDbContext _context = null!;
    private PostgresDataRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _databaseName = Guid.NewGuid().ToString();
        _context = new JimDbContext(new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options);
        _repository = new PostgresDataRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        (_repository as IDisposable)?.Dispose();
        _context.Dispose();
    }

    [Test]
    public async Task RecordImportWatermarksAsync_InMemory_RecordsClearsAndSweepsAsTheSqlDoesAsync()
    {
        var (system, corp, partners, branch) = await SeedAsync();
        var other = await SeedAsync("Other Directory");
        await _repository.ConnectedSystems.RecordImportWatermarksAsync(system.Id, null,
            new Dictionary<int, string> { [corp.Id] = "corp-1", [partners.Id] = "partners-1", [branch.Id] = "branch-1" }, [], DateTime.UtcNow);
        await SetSelectedAsync(branch, selected: false);

        var recordedAt = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        var result = await _repository.ConnectedSystems.RecordImportWatermarksAsync(system.Id, "connector-data",
            new Dictionary<int, string> { [corp.Id] = "corp-2", [other.Corp.Id] = "intruder" }, [partners.Id], recordedAt);

        var rows = await _repository.ConnectedSystems.GetPartitionWatermarksAsync(system.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows.Select(w => (w.PartitionId, w.Value, w.RecordedAt)), Is.EqualTo(new[] { (corp.Id, "corp-2", recordedAt) }));
            Assert.That(await _repository.ConnectedSystems.GetPartitionWatermarksAsync(other.System.Id), Is.Empty,
                "another Connected System's Partition is never written");
            Assert.That(result.PartitionWatermarksRecorded, Is.EqualTo(1));
            Assert.That(result.PartitionWatermarksCleared, Is.EqualTo(1));
            Assert.That(result.DeselectedPartitionWatermarksRemoved, Is.EqualTo(1));
            Assert.That((await ReadSystemAsync(system.Id)).PersistedConnectorData, Is.EqualTo("connector-data"));
        }
    }

    [Test]
    public async Task RecordImportWatermarksAsync_InMemoryContextHoldsUnsavedChanges_DoesNotSaveThemAsync()
    {
        // As on PostgreSQL, the write must not flush the run's tracked graph.
        var (system, corp, _, _) = await SeedAsync();
        var activity = new Activity { TargetName = "Delta Import", TargetType = ActivityTargetType.ConnectedSystemRunProfile, Message = "as staged" };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        activity.Message = "unsaved";

        await _repository.ConnectedSystems.RecordImportWatermarksAsync(system.Id, null, new Dictionary<int, string> { [corp.Id] = "corp-1" }, [], DateTime.UtcNow);

        Assert.That(_context.Entry(activity).State, Is.EqualTo(EntityState.Modified), "the shared context's change is still pending, so it was not saved");
    }

    [Test]
    public async Task DeleteWatermarksOfDeselectedPartitionsAsync_InMemory_RemovesOnlyTheDeselectedPartitionsRowsAsync()
    {
        var (system, corp, partners, _) = await SeedAsync();
        await _repository.ConnectedSystems.RecordImportWatermarksAsync(system.Id, null,
            new Dictionary<int, string> { [corp.Id] = "corp-1", [partners.Id] = "partners-1" }, [], DateTime.UtcNow);
        await SetSelectedAsync(partners, selected: false);

        var removed = await _repository.ConnectedSystems.DeleteWatermarksOfDeselectedPartitionsAsync(system.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That((await _repository.ConnectedSystems.GetPartitionWatermarksAsync(system.Id)).Select(w => w.PartitionId), Is.EqualTo(new[] { corp.Id }));
        }
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_InMemoryPartitionsHoldWatermarks_DeletesThemWithTheSystemAsync()
    {
        // The relational schema cascades the rows; the in-memory provider cascades only to tracked dependents, and
        // nothing tracks a watermark, so the tracked fallback has to remove them itself.
        var (system, corp, partners, _) = await SeedAsync();
        await _repository.ConnectedSystems.RecordImportWatermarksAsync(system.Id, null,
            new Dictionary<int, string> { [corp.Id] = "corp-1", [partners.Id] = "partners-1" }, [], DateTime.UtcNow);

        await _repository.ConnectedSystems.DeleteConnectedSystemAsync(system.Id);

        Assert.That(await _context.ConnectedSystemPartitionWatermarks.AnyAsync(), Is.False);
    }

    #region Helpers

    private async Task<(ConnectedSystem System, ConnectedSystemPartition Corp, ConnectedSystemPartition Partners, ConnectedSystemPartition Branch)> SeedAsync(
        string name = "Directory")
    {
        var connectorDefinition = new ConnectorDefinition { Name = $"{name} Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition };
        var corp = new ConnectedSystemPartition { ConnectedSystem = system, ExternalId = "dc=corp,dc=local", Name = "corp.local", Selected = true };
        var partners = new ConnectedSystemPartition { ConnectedSystem = system, ExternalId = "dc=partners,dc=local", Name = "partners.local", Selected = true };
        var branch = new ConnectedSystemPartition { ConnectedSystem = system, ExternalId = "dc=branch,dc=local", Name = "branch.local", Selected = true };
        _context.ConnectorDefinitions.Add(connectorDefinition);
        _context.ConnectedSystems.Add(system);
        _context.ConnectedSystemPartitions.AddRange(corp, partners, branch);
        await _context.SaveChangesAsync();
        return (system, corp, partners, branch);
    }

    private async Task SetSelectedAsync(ConnectedSystemPartition partition, bool selected)
    {
        partition.Selected = selected;
        await _context.SaveChangesAsync();
    }

    private async Task<ConnectedSystem> ReadSystemAsync(int systemId)
    {
        await using var read = new JimDbContext(new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options);
        return await read.ConnectedSystems.AsNoTracking().SingleAsync(cs => cs.Id == systemId);
    }

    #endregion
}
