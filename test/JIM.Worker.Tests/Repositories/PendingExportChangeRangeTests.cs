// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the offset/count Pending Export attribute-change range read
/// (<c>GetAllPendingExportChangesRangeAsync</c>) that backs the virtualised (infinite-scroll) Pending Export
/// grid on a Connected System Object: window correctness at absolute offsets, the skip-the-count contract
/// (a null total, never zero, when the caller already holds the count), the window-size cap, the
/// attribute-name order it shares with the paged read, and that only the named Pending Export's changes are
/// ever listed.
/// </summary>
/// <remarks>
/// The search predicate uses <c>EF.Functions.ILike</c>, which the in-memory provider cannot execute, so it is
/// covered against a real database by <c>PendingExportChangeRangeDatabaseTests</c>.
/// </remarks>
[TestFixture]
public class PendingExportChangeRangeTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;
    private ConnectedSystemObjectType? _objectType;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .EnableSensitiveDataLogging()
            .Options;

        _dbContext = new JimDbContext(options);
        _repository = new PostgresDataRepository(_dbContext);

        // NUnit reuses one fixture instance across the tests in the class, so the cached object type has to be
        // dropped alongside the context it was created on.
        _objectType = null;
    }

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
        _dbContext?.Dispose();
    }

    [Test]
    public async Task GetAllPendingExportChangesRangeAsync_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        var result = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(c => c.Attribute.Name),
                Is.EqualTo(new[] { "attribute001", "attribute002", "attribute003" }));
        }
    }

    [Test]
    public async Task GetAllPendingExportChangesRangeAsync_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        var result = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(c => c.Attribute.Name),
                Is.EqualTo(new[] { "attribute004", "attribute005", "attribute006" }));
        }
    }

    [Test]
    public async Task GetAllPendingExportChangesRangeAsync_WindowStraddlingEnd_ReturnsRemainderAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        var result = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 9, count: 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(c => c.Attribute.Name), Is.EqualTo(new[] { "attribute010" }));
        }
    }

    [Test]
    public async Task GetAllPendingExportChangesRangeAsync_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        var result = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 100, count: 10);

        using (Assert.EnterMultipleScope())
        {
            // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results, Is.Empty);
        }
    }

    [Test]
    public async Task GetAllPendingExportChangesRangeAsync_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        var result = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches",
            // which is the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(c => c.Attribute.Name),
                Is.EqualTo(new[] { "attribute004", "attribute005", "attribute006" }));
        }
    }

    [Test]
    public async Task GetAllPendingExportChangesRangeAsync_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        // Skipping the count must change what the caller is told about the total and nothing else; the window
        // itself comes from the same filtered, sorted query either way.
        var counted = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 5, count: 4);
        var uncounted = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 5, count: 4, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(c => c.Id), Is.EqualTo(counted.Results.Select(c => c.Id)));
        }
    }

    [Test]
    public async Task GetAllPendingExportChangesRangeAsync_CountAboveCap_ClampsToFiveHundredAsync()
    {
        var pendingExportId = await SeedChangesAsync(505);

        var result = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 0, count: 1000);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every change. The cap is
            // 500 rather than the paged reader's 100 because nothing here is a person choosing a page size:
            // the virtualiser asks for as many rows as the viewport needs, and a clamp it can reach silently
            // renders the shortfall as blank rows. See MaxHeaderWindowSize in ConnectedSystemRepository.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public void GetAllPendingExportChangesRangeAsync_CountBelowOne_Throws()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
                Guid.NewGuid(), offset: 0, count: 0));
    }

    [Test]
    public async Task GetAllPendingExportChangesRangeAsync_NegativeOffset_IsTreatedAsTheTopOfTheListAsync()
    {
        var pendingExportId = await SeedChangesAsync(5);

        var result = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: -10, count: 2);

        Assert.That(result.Results.Select(c => c.Attribute.Name),
            Is.EqualTo(new[] { "attribute001", "attribute002" }));
    }

    [Test]
    public async Task GetAllPendingExportChangesRangeAsync_ChangesOfAnotherPendingExport_AreNeverIncludedAsync()
    {
        var mine = await SeedChangesAsync(2);
        await SeedChangesAsync(3, attributeNamePrefix: "other");

        var result = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            mine, offset: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(c => c.Attribute.Name),
                Is.EqualTo(new[] { "attribute001", "attribute002" }));
        }
    }

    [Test]
    public async Task GetAllPendingExportChangesRangeAsync_TiedAttributeNames_ProduceNonOverlappingWindowsAsync()
    {
        // A multi-valued attribute contributes several changes under one attribute name, so the sort key
        // alone cannot order them. Without the id tie-break the two windows may repeat and skip rows.
        var pendingExport = await SeedPendingExportAsync();
        var attribute = NewAttribute("member");
        _dbContext.Add(attribute);
        for (var i = 0; i < 20; i++)
            _dbContext.Add(NewChange(pendingExport.Id, attribute, $"CN=Member {i:D3}"));
        await _dbContext.SaveChangesAsync();

        var first = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExport.Id, offset: 0, count: 10);
        var second = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExport.Id, offset: 10, count: 10);

        // Asserted as the exact id order rather than merely as "no duplicates": the windows are only
        // guaranteed to partition the changes if the tie is broken by a total order, and the id order is the
        // observable consequence of that.
        var expected = _dbContext.Set<PendingExportAttributeValueChange>()
            .Where(c => c.PendingExportId == pendingExport.Id)
            .Select(c => c.Id)
            .OrderBy(id => id)
            .ToList();
        var seen = first.Results.Select(c => c.Id).Concat(second.Results.Select(c => c.Id)).ToList();
        Assert.That(seen, Is.EqualTo(expected));
    }

    [Test]
    public async Task GetAllPendingExportChangesRangeAsync_FullWindow_MatchesPagedReaderAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        var range = await _repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 0, count: 10);
        var paged = await _repository.ConnectedSystems.GetAllPendingExportChangesPagedAsync(
            pendingExportId, page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(c => c.Id), Is.EqualTo(paged.Results.Select(c => c.Id)));
        }
    }

    /// <summary>
    /// Seeds a Pending Export with <paramref name="count"/> attribute changes, one per attribute named
    /// "attribute001", "attribute002", ... (zero-padded so lexical order matches numeric order under the
    /// attribute-name sort). Returns the Pending Export's id.
    /// </summary>
    private async Task<Guid> SeedChangesAsync(int count, string attributeNamePrefix = "attribute")
    {
        var pendingExport = await SeedPendingExportAsync();

        for (var i = 1; i <= count; i++)
        {
            var attribute = NewAttribute($"{attributeNamePrefix}{i:D3}");
            _dbContext.Add(attribute);
            _dbContext.Add(NewChange(pendingExport.Id, attribute, $"value {i:D3}"));
        }

        await _dbContext.SaveChangesAsync();
        return pendingExport.Id;
    }

    private async Task<PendingExport> SeedPendingExportAsync()
    {
        var objectType = await GetOrCreateObjectTypeAsync();

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = objectType.ConnectedSystem.Id,
            ChangeType = PendingExportChangeType.Update
        };
        _dbContext.PendingExports.Add(pendingExport);
        await _dbContext.SaveChangesAsync();
        return pendingExport;
    }

    /// <summary>
    /// The one Connected System Object Type every seeded attribute hangs off; created on first use so a test
    /// that seeds two Pending Exports still describes a single Connected System.
    /// </summary>
    private async Task<ConnectedSystemObjectType> GetOrCreateObjectTypeAsync()
    {
        if (_objectType != null)
            return _objectType;

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        _objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = connectedSystem, Selected = true };
        _dbContext.AddRange(connectorDefinition, connectedSystem, _objectType);
        await _dbContext.SaveChangesAsync();
        return _objectType;
    }

    private ConnectedSystemObjectTypeAttribute NewAttribute(string name) => new()
    {
        Name = name,
        ConnectedSystemObjectType = _objectType!,
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued,
        Selected = true
    };

    private static PendingExportAttributeValueChange NewChange(
        Guid pendingExportId,
        ConnectedSystemObjectTypeAttribute attribute,
        string value) => new()
    {
        Id = Guid.NewGuid(),
        PendingExportId = pendingExportId,
        Attribute = attribute,
        StringValue = value,
        ChangeType = PendingExportAttributeChangeType.Add
    };
}
