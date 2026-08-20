// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the offset/count Metaverse Object attribute value range read (<c>GetAttributeValuesRangeAsync</c>)
/// that backs a virtualised (infinite-scroll) multi-valued attribute on the Metaverse Object detail page:
/// window correctness at absolute offsets, the skip-the-count contract (a null total, never zero, when the
/// caller already holds the count), the window-size cap, the search semantics, and that only the named
/// object's values for the named attribute are ever listed.
/// </summary>
[TestFixture]
public class MetaverseObjectAttributeValueRangeTests
{
    private const string ProxyAddressesAttributeName = "Proxy Addresses";

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
        var mvoId = await SeedValuesAsync(10);

        var result = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ProxyAddressesAttributeName, offset: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(av => av.StringValue),
                Is.EqualTo(new[] { "smtp:user001@example.com", "smtp:user002@example.com", "smtp:user003@example.com" }));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        var mvoId = await SeedValuesAsync(10);

        var result = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ProxyAddressesAttributeName, offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(av => av.StringValue),
                Is.EqualTo(new[] { "smtp:user004@example.com", "smtp:user005@example.com", "smtp:user006@example.com" }));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_WindowStraddlingEnd_ReturnsRemainderAsync()
    {
        var mvoId = await SeedValuesAsync(10);

        var result = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ProxyAddressesAttributeName, offset: 9, count: 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(av => av.StringValue),
                Is.EqualTo(new[] { "smtp:user010@example.com" }));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        var mvoId = await SeedValuesAsync(10);

        var result = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ProxyAddressesAttributeName, offset: 100, count: 10);

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
        var mvoId = await SeedValuesAsync(10);

        var result = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ProxyAddressesAttributeName, offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches",
            // which is the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(av => av.StringValue),
                Is.EqualTo(new[] { "smtp:user004@example.com", "smtp:user005@example.com", "smtp:user006@example.com" }));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        var mvoId = await SeedValuesAsync(10);

        // Skipping the count must change what the caller is told about the total and nothing else; the window
        // itself comes from the same filtered, ordered query either way.
        var counted = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ProxyAddressesAttributeName, offset: 5, count: 4);
        var uncounted = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ProxyAddressesAttributeName, offset: 5, count: 4, includeTotalCount: false);

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
        var mvoId = await SeedValuesAsync(505);

        var result = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ProxyAddressesAttributeName, offset: 0, count: 1000);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every value. The cap is
            // 500 rather than the paged reader's 100 because nothing here is a person choosing a page size:
            // the virtualiser asks for as many rows as the viewport needs, and a clamp it can reach silently
            // renders the shortfall as blank rows. See MaxHeaderWindowSize in MetaverseRepository.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public void GetAttributeValuesRangeAsync_CountBelowOne_Throws()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _repository.Metaverse.GetAttributeValuesRangeAsync(
                Guid.NewGuid(), ProxyAddressesAttributeName, offset: 0, count: 0));
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_NegativeOffset_IsTreatedAsTheTopOfTheListAsync()
    {
        var mvoId = await SeedValuesAsync(5);

        var result = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ProxyAddressesAttributeName, offset: -10, count: 2);

        Assert.That(result.Results.Select(av => av.StringValue),
            Is.EqualTo(new[] { "smtp:user001@example.com", "smtp:user002@example.com" }));
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_AnotherAttributesValues_AreNeverIncludedAsync()
    {
        var mvoId = await SeedValuesAsync(3);
        var mvo = _dbContext.MetaverseObjects.Single(m => m.Id == mvoId);
        var otherAttribute = NewAttribute("Job Title");
        _dbContext.MetaverseAttributes.Add(otherAttribute);
        _dbContext.Add(NewValue(mvo, otherAttribute, "Engineer", ordinal: 900));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ProxyAddressesAttributeName, offset: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(3));
            Assert.That(result.Results.Select(av => av.Attribute.Name),
                Is.All.EqualTo(ProxyAddressesAttributeName));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_AnotherObjectsValues_AreNeverIncludedAsync()
    {
        var mine = await SeedValuesAsync(2);
        await SeedValuesAsync(3, valuePrefix: "smtp:other", ordinalBase: 1000);

        var result = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mine, ProxyAddressesAttributeName, offset: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(av => av.StringValue),
                Is.EqualTo(new[] { "smtp:user001@example.com", "smtp:user002@example.com" }));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_Search_IsCaseInsensitiveAndRestrictsTotalAsync()
    {
        var mvoId = await SeedValuesAsync(10);

        var result = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ProxyAddressesAttributeName, offset: 0, count: 10, searchText: "USER004@EXAMPLE");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(av => av.StringValue),
                Is.EqualTo(new[] { "smtp:user004@example.com" }));
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_FullWindow_MatchesPagedReaderAsync()
    {
        var mvoId = await SeedValuesAsync(10);

        var range = await _repository.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ProxyAddressesAttributeName, offset: 0, count: 10);
        var paged = await _repository.Metaverse.GetAttributeValuesPagedAsync(
            mvoId, ProxyAddressesAttributeName, page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(av => av.Id), Is.EqualTo(paged.Results.Select(av => av.Id)));
        }
    }

    /// <summary>
    /// Seeds a Metaverse Object with <paramref name="count"/> values of a multi-valued "Proxy Addresses"
    /// attribute, with ids assigned in seeding order so the read's id order yields numeric value order.
    /// <paramref name="ordinalBase"/> shifts those ids so a test can seed a second object without colliding
    /// with the first's. Returns the object's id.
    /// </summary>
    private async Task<Guid> SeedValuesAsync(int count, string valuePrefix = "smtp:user", int ordinalBase = 0)
    {
        var type = new MetaverseObjectType { Name = "User", PluralName = "Users" };
        var attribute = NewAttribute(ProxyAddressesAttributeName);
        _dbContext.MetaverseObjectTypes.Add(type);
        _dbContext.MetaverseAttributes.Add(attribute);
        await _dbContext.SaveChangesAsync();

        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = type,
            Origin = MetaverseObjectOrigin.Projected
        };
        _dbContext.MetaverseObjects.Add(mvo);

        for (var i = 1; i <= count; i++)
            _dbContext.Add(NewValue(mvo, attribute, $"{valuePrefix}{i:D3}@example.com", ordinalBase + i));

        await _dbContext.SaveChangesAsync();
        return mvo.Id;
    }

    private static MetaverseAttribute NewAttribute(string name) => new()
    {
        Name = name,
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.MultiValued
    };

    /// <summary>
    /// Builds an attribute value whose id is derived from <paramref name="ordinal"/>, so the read's id ordering
    /// is the seeding order rather than an arbitrary one. The ordinal varies only the last group of the GUID,
    /// which .NET and PostgreSQL order identically.
    /// </summary>
    private static MetaverseObjectAttributeValue NewValue(
        MetaverseObject mvo,
        MetaverseAttribute attribute,
        string value,
        int ordinal) => new()
    {
        Id = new Guid($"00000000-0000-0000-0000-{ordinal:D12}"),
        MetaverseObject = mvo,
        Attribute = attribute,
        StringValue = value
    };
}
