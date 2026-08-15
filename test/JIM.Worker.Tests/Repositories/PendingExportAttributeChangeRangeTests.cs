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
/// Tests the offset/count single-attribute Pending Export change range read
/// (<c>GetPendingExportAttributeChangesRangeAsync</c>) that backs a virtualised (infinite-scroll) multi-valued
/// attribute on a Pending Export: window correctness at absolute offsets, the skip-the-count contract (a null
/// total, never zero, when the caller already holds the count), the window-size cap, and that only the named
/// Pending Export's changes for the named attribute are ever listed.
/// </summary>
/// <remarks>
/// The search predicate uses <c>EF.Functions.ILike</c>, which the in-memory provider cannot execute, so it is
/// covered against a real database by <c>PendingExportAttributeChangeRangeDatabaseTests</c>.
/// </remarks>
[TestFixture]
public class PendingExportAttributeChangeRangeTests
{
    private const string MemberAttributeName = "member";

    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;

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
    }

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
        _dbContext?.Dispose();
    }

    [Test]
    public async Task GetPendingExportAttributeChangesRangeAsync_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        var result = await _repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(c => c.StringValue),
                Is.EqualTo(new[] { "CN=Member 001", "CN=Member 002", "CN=Member 003" }));
        }
    }

    [Test]
    public async Task GetPendingExportAttributeChangesRangeAsync_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        var result = await _repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(c => c.StringValue),
                Is.EqualTo(new[] { "CN=Member 004", "CN=Member 005", "CN=Member 006" }));
        }
    }

    [Test]
    public async Task GetPendingExportAttributeChangesRangeAsync_WindowStraddlingEnd_ReturnsRemainderAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        var result = await _repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 9, count: 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(c => c.StringValue), Is.EqualTo(new[] { "CN=Member 010" }));
        }
    }

    [Test]
    public async Task GetPendingExportAttributeChangesRangeAsync_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        var result = await _repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 100, count: 10);

        using (Assert.EnterMultipleScope())
        {
            // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results, Is.Empty);
        }
    }

    [Test]
    public async Task GetPendingExportAttributeChangesRangeAsync_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        var result = await _repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches",
            // which is the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(c => c.StringValue),
                Is.EqualTo(new[] { "CN=Member 004", "CN=Member 005", "CN=Member 006" }));
        }
    }

    [Test]
    public async Task GetPendingExportAttributeChangesRangeAsync_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        // Skipping the count must change what the caller is told about the total and nothing else; the window
        // itself comes from the same filtered, ordered query either way.
        var counted = await _repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 5, count: 4);
        var uncounted = await _repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 5, count: 4, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(c => c.Id), Is.EqualTo(counted.Results.Select(c => c.Id)));
        }
    }

    [Test]
    public async Task GetPendingExportAttributeChangesRangeAsync_CountAboveCap_ClampsToFiveHundredAsync()
    {
        var pendingExportId = await SeedChangesAsync(505);

        var result = await _repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 0, count: 1000);

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
    public void GetPendingExportAttributeChangesRangeAsync_CountBelowOne_Throws()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
                Guid.NewGuid(), MemberAttributeName, offset: 0, count: 0));
    }

    [Test]
    public async Task GetPendingExportAttributeChangesRangeAsync_NegativeOffset_IsTreatedAsTheTopOfTheListAsync()
    {
        var pendingExportId = await SeedChangesAsync(5);

        var result = await _repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: -10, count: 2);

        Assert.That(result.Results.Select(c => c.StringValue),
            Is.EqualTo(new[] { "CN=Member 001", "CN=Member 002" }));
    }

    [Test]
    public async Task GetPendingExportAttributeChangesRangeAsync_AnotherAttributesChanges_AreNeverIncludedAsync()
    {
        var pendingExportId = await SeedChangesAsync(3);
        var objectType = _dbContext.ConnectedSystemObjectTypes.Single();
        var otherAttribute = NewAttribute(objectType, "description");
        _dbContext.Add(otherAttribute);
        _dbContext.Add(NewChange(pendingExportId, otherAttribute, "A description", ordinal: 900));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(3));
            Assert.That(result.Results.Select(c => c.Attribute.Name), Is.All.EqualTo(MemberAttributeName));
        }
    }

    [Test]
    public async Task GetPendingExportAttributeChangesRangeAsync_FullWindow_MatchesPagedReaderAsync()
    {
        var pendingExportId = await SeedChangesAsync(10);

        var range = await _repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 0, count: 10);
        var paged = await _repository.ConnectedSystems.GetPendingExportAttributeChangesPagedAsync(
            pendingExportId, MemberAttributeName, page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(c => c.Id), Is.EqualTo(paged.Results.Select(c => c.Id)));
        }
    }

    /// <summary>
    /// Seeds a Pending Export with <paramref name="count"/> changes to one multi-valued "member" attribute,
    /// named "CN=Member 001", "CN=Member 002", ... with ids assigned in the same order so the read's id order
    /// yields numeric value order. Returns the Pending Export's id.
    /// </summary>
    private async Task<Guid> SeedChangesAsync(int count)
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = connectedSystem, Selected = true };
        var memberAttribute = NewAttribute(objectType, MemberAttributeName);
        objectType.Attributes.Add(memberAttribute);
        _dbContext.AddRange(connectorDefinition, connectedSystem, objectType);
        await _dbContext.SaveChangesAsync();

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ChangeType = PendingExportChangeType.Update
        };
        _dbContext.PendingExports.Add(pendingExport);

        for (var i = 1; i <= count; i++)
            _dbContext.Add(NewChange(pendingExport.Id, memberAttribute, $"CN=Member {i:D3}", i));

        await _dbContext.SaveChangesAsync();
        return pendingExport.Id;
    }

    private static ConnectedSystemObjectTypeAttribute NewAttribute(ConnectedSystemObjectType objectType, string name) => new()
    {
        Name = name,
        ConnectedSystemObjectType = objectType,
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.MultiValued,
        Selected = true
    };

    /// <summary>
    /// Builds a change whose id is derived from <paramref name="ordinal"/>, so the read's id ordering is the
    /// seeding order rather than an arbitrary one. The ordinal varies only the last group of the GUID, which
    /// .NET and PostgreSQL order identically.
    /// </summary>
    private static PendingExportAttributeValueChange NewChange(
        Guid pendingExportId,
        ConnectedSystemObjectTypeAttribute attribute,
        string value,
        int ordinal) => new()
    {
        Id = new Guid($"00000000-0000-0000-0000-{ordinal:D12}"),
        PendingExportId = pendingExportId,
        Attribute = attribute,
        StringValue = value,
        ChangeType = PendingExportAttributeChangeType.Add
    };
}
