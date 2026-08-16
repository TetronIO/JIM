// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the offset/count Connected System Object attribute value range read
/// (<c>GetAttributeValuesRangeAsync</c>) that backs a virtualised (infinite-scroll) multi-valued attribute on
/// the Connected System Object detail page: window correctness at absolute offsets, the skip-the-count contract
/// (a null total, never zero, when the caller already holds the count), the window-size cap, the search
/// semantics, and that only the named object's values for the named attribute are ever listed.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectAttributeValueRangeTests
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
    public async Task GetAttributeValuesRangeAsync_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        var csoId = await SeedValuesAsync(10);

        var result = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(av => av.StringValue),
                Is.EqualTo(new[] { "CN=Member 001", "CN=Member 002", "CN=Member 003" }));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        var csoId = await SeedValuesAsync(10);

        var result = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(av => av.StringValue),
                Is.EqualTo(new[] { "CN=Member 004", "CN=Member 005", "CN=Member 006" }));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_WindowStraddlingEnd_ReturnsRemainderAsync()
    {
        var csoId = await SeedValuesAsync(10);

        var result = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 9, count: 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(av => av.StringValue), Is.EqualTo(new[] { "CN=Member 010" }));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        var csoId = await SeedValuesAsync(10);

        var result = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 100, count: 10);

        using (Assert.EnterMultipleScope())
        {
            // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results, Is.Empty);
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        var csoId = await SeedValuesAsync(10);

        var result = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches",
            // which is the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(av => av.StringValue),
                Is.EqualTo(new[] { "CN=Member 004", "CN=Member 005", "CN=Member 006" }));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        var csoId = await SeedValuesAsync(10);

        // Skipping the count must change what the caller is told about the total and nothing else; the window
        // itself comes from the same filtered, ordered query either way.
        var counted = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 5, count: 4);
        var uncounted = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 5, count: 4, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(av => av.Id), Is.EqualTo(counted.Results.Select(av => av.Id)));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_CountAboveCap_ClampsToFiveHundredAsync()
    {
        var csoId = await SeedValuesAsync(505);

        var result = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 0, count: 1000);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every value. The cap is
            // 500 rather than the paged reader's 100 because nothing here is a person choosing a page size:
            // the virtualiser asks for as many rows as the viewport needs, and a clamp it can reach silently
            // renders the shortfall as blank rows. See MaxHeaderWindowSize in ConnectedSystemRepository.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public void GetAttributeValuesRangeAsync_CountBelowOne_Throws()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
                Guid.NewGuid(), MemberAttributeName, offset: 0, count: 0));
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_NegativeOffset_IsTreatedAsTheTopOfTheListAsync()
    {
        var csoId = await SeedValuesAsync(5);

        var result = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: -10, count: 2);

        Assert.That(result.Results.Select(av => av.StringValue),
            Is.EqualTo(new[] { "CN=Member 001", "CN=Member 002" }));
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_AnotherAttributesValues_AreNeverIncludedAsync()
    {
        var csoId = await SeedValuesAsync(3);
        var cso = _dbContext.ConnectedSystemObjects.Single(c => c.Id == csoId);
        var otherAttribute = NewAttribute(cso.Type, "description");
        _dbContext.Add(otherAttribute);
        _dbContext.Add(NewValue(cso, otherAttribute, "A description", ordinal: 900));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(3));
            Assert.That(result.Results.Select(av => av.Attribute.Name), Is.All.EqualTo(MemberAttributeName));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_AnotherObjectsValues_AreNeverIncludedAsync()
    {
        var mine = await SeedValuesAsync(2);
        await SeedValuesAsync(3, valuePrefix: "CN=Other", ordinalBase: 1000);

        var result = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            mine, MemberAttributeName, offset: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(av => av.StringValue),
                Is.EqualTo(new[] { "CN=Member 001", "CN=Member 002" }));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_Search_IsCaseInsensitiveAndRestrictsTotalAsync()
    {
        var csoId = await SeedValuesAsync(10);

        var result = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 0, count: 10, searchText: "cn=MEMBER 004");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(av => av.StringValue), Is.EqualTo(new[] { "CN=Member 004" }));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_FullWindow_MatchesPagedReaderAsync()
    {
        var csoId = await SeedValuesAsync(10);

        var range = await _repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 0, count: 10);
        var paged = await _repository.ConnectedSystems.GetAttributeValuesPagedAsync(
            csoId, MemberAttributeName, page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(av => av.Id), Is.EqualTo(paged.Results.Select(av => av.Id)));
        }
    }

    /// <summary>
    /// Seeds a Connected System Object with <paramref name="count"/> values of a multi-valued "member"
    /// attribute, named "CN=Member 001", "CN=Member 002", ... with ids assigned in the same order so the
    /// read's id order yields numeric value order. <paramref name="ordinalBase"/> shifts those ids so a test can
    /// seed a second object without colliding with the first's. Returns the object's id.
    /// </summary>
    private async Task<Guid> SeedValuesAsync(int count, string valuePrefix = "CN=Member", int ordinalBase = 0)
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = connectedSystem, Selected = true };
        var memberAttribute = NewAttribute(objectType, MemberAttributeName);
        objectType.Attributes.Add(memberAttribute);
        _dbContext.AddRange(connectorDefinition, connectedSystem, objectType);
        await _dbContext.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = connectedSystem,
            Type = objectType,
            Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.Add(cso);

        for (var i = 1; i <= count; i++)
            _dbContext.Add(NewValue(cso, memberAttribute, $"{valuePrefix} {i:D3}", ordinalBase + i));

        await _dbContext.SaveChangesAsync();
        return cso.Id;
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
    /// Builds an attribute value whose id is derived from <paramref name="ordinal"/>, so the read's id ordering
    /// is the seeding order rather than an arbitrary one. The ordinal varies only the last group of the GUID,
    /// which .NET and PostgreSQL order identically.
    /// </summary>
    private static ConnectedSystemObjectAttributeValue NewValue(
        ConnectedSystemObject cso,
        ConnectedSystemObjectTypeAttribute attribute,
        string value,
        int ordinal) => new()
    {
        Id = new Guid($"00000000-0000-0000-0000-{ordinal:D12}"),
        ConnectedSystemObject = cso,
        Attribute = attribute,
        StringValue = value
    };
}
