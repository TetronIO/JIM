// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the type/plurality/built-in/object-type filters on the offset/count Metaverse Attribute header range read
/// (<c>GetMetaverseAttributeHeadersRangeAsync</c>) that backs the Schema Attributes tab's filter row. These are
/// plain equality/Any() predicates, so unlike the read's ILIKE-based search (which needs a real Postgres
/// database - see <c>MetaverseAttributeHeaderRangeDatabaseTests</c>) they are translatable, and testable, against
/// the EF Core in-memory provider.
/// </summary>
[TestFixture]
public class MetaverseAttributeHeaderRangeFilterTests
{
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

    private async Task<MetaverseObjectType> SeedObjectTypeAsync(string name)
    {
        var type = new MetaverseObjectType { Name = name, PluralName = name + "s" };
        _dbContext.MetaverseObjectTypes.Add(type);
        await _dbContext.SaveChangesAsync();
        return type;
    }

    private MetaverseAttribute NewAttribute(
        string name,
        AttributeDataType type = AttributeDataType.Text,
        AttributePlurality plurality = AttributePlurality.SingleValued,
        bool builtIn = false,
        IEnumerable<MetaverseObjectType>? objectTypes = null)
    {
        return new MetaverseAttribute
        {
            Name = name,
            Type = type,
            AttributePlurality = plurality,
            BuiltIn = builtIn,
            MetaverseObjectTypes = objectTypes?.ToList() ?? []
        };
    }

    [Test]
    public async Task GetMetaverseAttributeHeadersRangeAsync_TypeFilter_RestrictsWindowAndTotalAsync()
    {
        _dbContext.MetaverseAttributes.Add(NewAttribute("Account Name", type: AttributeDataType.Text));
        _dbContext.MetaverseAttributes.Add(NewAttribute("Account Enabled", type: AttributeDataType.Boolean));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Metaverse.GetMetaverseAttributeHeadersRangeAsync(
            offset: 0, count: 10, typeFilter: AttributeDataType.Boolean);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(a => a.Name), Is.EqualTo(new[] { "Account Enabled" }));
        }
    }

    [Test]
    public async Task GetMetaverseAttributeHeadersRangeAsync_PluralityFilter_RestrictsWindowAndTotalAsync()
    {
        _dbContext.MetaverseAttributes.Add(NewAttribute("Account Name", plurality: AttributePlurality.SingleValued));
        _dbContext.MetaverseAttributes.Add(NewAttribute("Alt Security Identities", plurality: AttributePlurality.MultiValued));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Metaverse.GetMetaverseAttributeHeadersRangeAsync(
            offset: 0, count: 10, pluralityFilter: AttributePlurality.MultiValued);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(a => a.Name), Is.EqualTo(new[] { "Alt Security Identities" }));
        }
    }

    [Test]
    public async Task GetMetaverseAttributeHeadersRangeAsync_BuiltInFilter_RestrictsWindowAndTotalAsync()
    {
        _dbContext.MetaverseAttributes.Add(NewAttribute("Account Name", builtIn: true));
        _dbContext.MetaverseAttributes.Add(NewAttribute("Custom Attribute", builtIn: false));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Metaverse.GetMetaverseAttributeHeadersRangeAsync(
            offset: 0, count: 10, builtInFilter: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(a => a.Name), Is.EqualTo(new[] { "Custom Attribute" }));
        }
    }

    [Test]
    public async Task GetMetaverseAttributeHeadersRangeAsync_ObjectTypeFilter_RestrictsWindowAndTotalAsync()
    {
        var userType = await SeedObjectTypeAsync("User");
        var groupType = await SeedObjectTypeAsync("Group");
        _dbContext.MetaverseAttributes.Add(NewAttribute("Account Name", objectTypes: [userType]));
        _dbContext.MetaverseAttributes.Add(NewAttribute("Group Name", objectTypes: [groupType]));
        _dbContext.MetaverseAttributes.Add(NewAttribute("Shared Attribute", objectTypes: [userType, groupType]));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Metaverse.GetMetaverseAttributeHeadersRangeAsync(
            offset: 0, count: 10, objectTypeId: groupType.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(a => a.Name),
                Is.EquivalentTo(new[] { "Group Name", "Shared Attribute" }));
        }
    }

    [Test]
    public async Task GetMetaverseAttributeHeadersRangeAsync_NoFiltersApplied_ReturnsEveryAttributeAsync()
    {
        _dbContext.MetaverseAttributes.Add(NewAttribute("Account Name"));
        _dbContext.MetaverseAttributes.Add(NewAttribute("Account Enabled", type: AttributeDataType.Boolean));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Metaverse.GetMetaverseAttributeHeadersRangeAsync(offset: 0, count: 10);

        Assert.That(result.TotalResults, Is.EqualTo(2));
    }
}
