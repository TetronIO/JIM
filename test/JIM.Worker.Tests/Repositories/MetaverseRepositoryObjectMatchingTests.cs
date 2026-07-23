// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests FindMetaverseObjectUsingMatchingRuleAsync (the import-side CSO to MVO join) against the
/// real PostgresData implementation. Reproduces the gap where an Object Matching Rule keyed on a
/// LongNumber or Decimal attribute never joined: the null-value pre-check had no arm for either
/// type, so every value was silently treated as absent and the candidate was skipped. The same
/// silent pre-check also swallowed the designed NotSupportedException for matching rules keyed on
/// types Object Matching does not support (DateTime, Binary, Reference, Boolean).
/// </summary>
[TestFixture]
public class MetaverseRepositoryObjectMatchingTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;
    private MetaverseObjectType _mvoType = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        TestUtilities.SetEnvironmentVariables();

        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .EnableSensitiveDataLogging()
            .Options;

        _dbContext = new JimDbContext(options);
        _repository = new PostgresDataRepository(_dbContext);

        _mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        _dbContext.MetaverseObjectTypes.Add(_mvoType);
        await _dbContext.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
        _dbContext?.Dispose();
    }

    /// <summary>
    /// Persists a Metaverse Attribute of the given type and returns it with its database-assigned id.
    /// </summary>
    private async Task<MetaverseAttribute> CreateMetaverseAttributeAsync(string name, AttributeDataType type)
    {
        var attribute = new MetaverseAttribute { Name = name, Type = type };
        _dbContext.MetaverseAttributes.Add(attribute);
        await _dbContext.SaveChangesAsync();
        return attribute;
    }

    /// <summary>
    /// Persists an MVO carrying a single attribute value, configured by the caller.
    /// </summary>
    private async Task<MetaverseObject> CreateMvoAsync(MetaverseAttribute attribute, Action<MetaverseObjectAttributeValue> setValue)
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _mvoType,
            Created = DateTime.UtcNow
        };
        var attributeValue = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = attribute,
            AttributeId = attribute.Id
        };
        setValue(attributeValue);
        mvo.AttributeValues.Add(attributeValue);
        _dbContext.MetaverseObjects.Add(mvo);
        await _dbContext.SaveChangesAsync();
        return mvo;
    }

    /// <summary>
    /// Builds an unpersisted CSO carrying a single attribute value for the given Connected System
    /// attribute; the matching method only reads the in-memory graph on the CSO side.
    /// </summary>
    private static ConnectedSystemObject BuildCso(ConnectedSystemObjectTypeAttribute csAttribute, Action<ConnectedSystemObjectAttributeValue> setValue)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var attributeValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = csAttribute.Id,
            Attribute = csAttribute
        };
        setValue(attributeValue);
        cso.AttributeValues.Add(attributeValue);
        return cso;
    }

    private static ObjectMatchingRule BuildMatchingRule(ConnectedSystemObjectTypeAttribute csAttribute, MetaverseAttribute targetAttribute)
    {
        var rule = new ObjectMatchingRule
        {
            TargetMetaverseAttribute = targetAttribute,
            TargetMetaverseAttributeId = targetAttribute.Id
        };
        rule.Sources.Add(new ObjectMatchingRuleSource
        {
            ObjectMatchingRule = rule,
            ConnectedSystemAttribute = csAttribute,
            ConnectedSystemAttributeId = csAttribute.Id
        });
        return rule;
    }

    [Test]
    public async Task FindMetaverseObjectUsingMatchingRuleAsync_LongNumberMatch_ReturnsMatchingMvoAsync()
    {
        // Arrange: a value beyond int range proves no narrowing occurs anywhere on the join path
        const long matchValue = 9999999999L;
        var mvAttribute = await CreateMetaverseAttributeAsync("usnChanged", AttributeDataType.LongNumber);
        var expectedMvo = await CreateMvoAsync(mvAttribute, av => av.LongValue = matchValue);
        await CreateMvoAsync(mvAttribute, av => av.LongValue = matchValue + 1);

        var csAttribute = new ConnectedSystemObjectTypeAttribute { Id = 500, Name = "usnChanged", Type = AttributeDataType.LongNumber };
        var cso = BuildCso(csAttribute, av => av.LongValue = matchValue);
        var rule = BuildMatchingRule(csAttribute, mvAttribute);

        // Act
        var result = await _repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(cso, _mvoType, rule);

        // Assert
        Assert.That(result, Is.Not.Null, "A LongNumber matching rule must join when an MVO carries the same value.");
        Assert.That(result!.Id, Is.EqualTo(expectedMvo.Id));
    }

    [Test]
    public async Task FindMetaverseObjectUsingMatchingRuleAsync_LongNumberAmbiguousMatch_ThrowsMultipleMatchesExceptionAsync()
    {
        // Arrange
        const long matchValue = 8888888888L;
        var mvAttribute = await CreateMetaverseAttributeAsync("usnChanged", AttributeDataType.LongNumber);
        await CreateMvoAsync(mvAttribute, av => av.LongValue = matchValue);
        await CreateMvoAsync(mvAttribute, av => av.LongValue = matchValue);

        var csAttribute = new ConnectedSystemObjectTypeAttribute { Id = 500, Name = "usnChanged", Type = AttributeDataType.LongNumber };
        var cso = BuildCso(csAttribute, av => av.LongValue = matchValue);
        var rule = BuildMatchingRule(csAttribute, mvAttribute);

        // Act / Assert
        Assert.ThrowsAsync<MultipleMatchesException>(() =>
            _repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(cso, _mvoType, rule));
    }

    [Test]
    public async Task FindMetaverseObjectUsingMatchingRuleAsync_LongNumberCsoValueAbsent_ReturnsNullAsync()
    {
        // Arrange: the CSO carries no LongValue, so the rule has nothing to match on
        var mvAttribute = await CreateMetaverseAttributeAsync("usnChanged", AttributeDataType.LongNumber);
        await CreateMvoAsync(mvAttribute, av => av.LongValue = 1234L);

        var csAttribute = new ConnectedSystemObjectTypeAttribute { Id = 500, Name = "usnChanged", Type = AttributeDataType.LongNumber };
        var cso = BuildCso(csAttribute, _ => { });
        var rule = BuildMatchingRule(csAttribute, mvAttribute);

        // Act
        var result = await _repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(cso, _mvoType, rule);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task FindMetaverseObjectUsingMatchingRuleAsync_DecimalMatch_ReturnsMatchingMvoAsync()
    {
        // Arrange: differing scale (5.50 vs 5.5) must still join; decimal comparison is numeric
        var mvAttribute = await CreateMetaverseAttributeAsync("fte", AttributeDataType.Decimal);
        var expectedMvo = await CreateMvoAsync(mvAttribute, av => av.DecimalValue = 5.5m);
        await CreateMvoAsync(mvAttribute, av => av.DecimalValue = 7.25m);

        var csAttribute = new ConnectedSystemObjectTypeAttribute { Id = 501, Name = "fte", Type = AttributeDataType.Decimal };
        var cso = BuildCso(csAttribute, av => av.DecimalValue = 5.50m);
        var rule = BuildMatchingRule(csAttribute, mvAttribute);

        // Act
        var result = await _repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(cso, _mvoType, rule);

        // Assert
        Assert.That(result, Is.Not.Null, "A Decimal matching rule must join when an MVO carries the same numeric value.");
        Assert.That(result!.Id, Is.EqualTo(expectedMvo.Id));
    }

    [Test]
    public async Task FindMetaverseObjectUsingMatchingRuleAsync_BooleanMatchingAttribute_ThrowsNotSupportedExceptionAsync()
    {
        // Arrange: Boolean is not a supported Object Matching type; the rule must fail loudly,
        // not be silently skipped because the null-value pre-check has no Boolean arm
        var mvAttribute = await CreateMetaverseAttributeAsync("enabled", AttributeDataType.Boolean);
        var csAttribute = new ConnectedSystemObjectTypeAttribute { Id = 502, Name = "enabled", Type = AttributeDataType.Boolean };
        var cso = BuildCso(csAttribute, av => av.BoolValue = true);
        var rule = BuildMatchingRule(csAttribute, mvAttribute);

        // Act / Assert
        Assert.ThrowsAsync<NotSupportedException>(() =>
            _repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(cso, _mvoType, rule));
    }

    [Test]
    public async Task FindMetaverseObjectUsingMatchingRuleAsync_DateTimeMatchingAttribute_ThrowsNotSupportedExceptionAsync()
    {
        // Arrange
        var mvAttribute = await CreateMetaverseAttributeAsync("whenCreated", AttributeDataType.DateTime);
        var csAttribute = new ConnectedSystemObjectTypeAttribute { Id = 503, Name = "whenCreated", Type = AttributeDataType.DateTime };
        var cso = BuildCso(csAttribute, av => av.DateTimeValue = DateTime.UtcNow);
        var rule = BuildMatchingRule(csAttribute, mvAttribute);

        // Act / Assert
        Assert.ThrowsAsync<NotSupportedException>(() =>
            _repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(cso, _mvoType, rule));
    }
}
