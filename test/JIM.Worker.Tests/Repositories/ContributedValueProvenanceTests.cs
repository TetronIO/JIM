// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the provenance queries behind the attribute value recall choice (#1537): the contributed-values
/// summary that quantifies a Synchronisation Rule's sole-contributor footprint before deletion (count
/// queries only, per-attribute and distinct-object), and provenance severing, the "keep the values"
/// mechanism that permanently exempts values from orphan recall by clearing their Synchronisation Rule
/// link while retaining the denormalised contributing-system record.
/// </summary>
[TestFixture]
public class ContributedValueProvenanceTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;

    private MetaverseAttribute _displayName = null!;
    private MetaverseAttribute _mobile = null!;
    private SyncRule _hrRule = null!;
    private SyncRule _adRule = null!;
    private MetaverseObject _mvo1 = null!;
    private MetaverseObject _mvo2 = null!;
    private MetaverseObject _mvo3 = null!;

    private const int HrSystemId = 11;
    private const int AdSystemId = 22;

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

        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        _dbContext.MetaverseObjectTypes.Add(mvoType);

        _displayName = new MetaverseAttribute
        {
            Name = "Display Name",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _mobile = new MetaverseAttribute
        {
            Name = "Mobile",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _dbContext.MetaverseAttributes.AddRange(_displayName, _mobile);

        _hrRule = new SyncRule { Name = "HR Users Inbound", Direction = SyncRuleDirection.Import };
        _adRule = new SyncRule { Name = "AD Users Inbound", Direction = SyncRuleDirection.Import };
        _dbContext.SyncRules.AddRange(_hrRule, _adRule);

        _mvo1 = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        _mvo2 = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        _mvo3 = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        _dbContext.MetaverseObjects.AddRange(_mvo1, _mvo2, _mvo3);

        await _dbContext.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
        _dbContext?.Dispose();
    }

    /// <summary>
    /// The standard estate the tests query against:
    /// HR rule contributes Display Name on MVO 1 and 2, and Mobile on MVO 1 (three values, two objects);
    /// AD rule contributes Display Name on MVO 3; MVO 2 also carries a null-provenance Mobile value
    /// (pre-provenance data, which no summary or severing may touch).
    /// </summary>
    private async Task SeedContributionsAsync()
    {
        _dbContext.MetaverseObjectAttributeValues.AddRange(
            NewValue(_mvo1, _displayName, "Alice", _hrRule.Id, HrSystemId),
            NewValue(_mvo2, _displayName, "Bob", _hrRule.Id, HrSystemId),
            NewValue(_mvo1, _mobile, "0700 000001", _hrRule.Id, HrSystemId),
            NewValue(_mvo3, _displayName, "Carol", _adRule.Id, AdSystemId),
            NewValue(_mvo2, _mobile, "0700 000002", contributedBySyncRuleId: null, contributedBySystemId: null));
        await _dbContext.SaveChangesAsync();
    }

    private static MetaverseObjectAttributeValue NewValue(
        MetaverseObject mvo,
        MetaverseAttribute attribute,
        string value,
        int? contributedBySyncRuleId,
        int? contributedBySystemId,
        bool nullValue = false)
    {
        return new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = attribute,
            AttributeId = attribute.Id,
            StringValue = nullValue ? null : value,
            NullValue = nullValue,
            ContributedBySyncRuleId = contributedBySyncRuleId,
            ContributedBySystemId = contributedBySystemId
        };
    }

    [Test]
    public async Task GetContributedValuesSummary_MixedContributions_CountsPerAttributeAndDistinctObjectsAsync()
    {
        await SeedContributionsAsync();

        var summary = await _repository.Metaverse.GetContributedValuesSummaryAsync(_hrRule.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.TotalValues, Is.EqualTo(3));
            Assert.That(summary.TotalObjects, Is.EqualTo(2), "MVO 1 carries two of the rule's values; distinct objects must not double-count it");
            Assert.That(summary.Attributes, Has.Count.EqualTo(2));

            var displayName = summary.Attributes.Single(a => a.AttributeId == _displayName.Id);
            Assert.That(displayName.AttributeName, Is.EqualTo("Display Name"));
            Assert.That(displayName.ValueCount, Is.EqualTo(2));
            Assert.That(displayName.ObjectCount, Is.EqualTo(2));

            var mobile = summary.Attributes.Single(a => a.AttributeId == _mobile.Id);
            Assert.That(mobile.ValueCount, Is.EqualTo(1), "the null-provenance Mobile value on MVO 2 must not be attributed to the rule");
            Assert.That(mobile.ObjectCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GetContributedValuesSummary_AttributeFilter_LimitsToThatAttributeAsync()
    {
        await SeedContributionsAsync();

        var summary = await _repository.Metaverse.GetContributedValuesSummaryAsync(_hrRule.Id, _mobile.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Attributes, Has.Count.EqualTo(1));
            Assert.That(summary.Attributes[0].AttributeId, Is.EqualTo(_mobile.Id));
            Assert.That(summary.TotalValues, Is.EqualTo(1));
            Assert.That(summary.TotalObjects, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GetContributedValuesSummary_NoContributions_ReturnsEmptySummaryAsync()
    {
        // No contributions seeded at all.
        var summary = await _repository.Metaverse.GetContributedValuesSummaryAsync(_hrRule.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Attributes, Is.Empty);
            Assert.That(summary.TotalValues, Is.Zero);
            Assert.That(summary.TotalObjects, Is.Zero);
        }
    }

    [Test]
    public async Task SeverContributedValueProvenance_OneAttribute_ClearsRuleLinkRetainsSystemAsync()
    {
        await SeedContributionsAsync();

        var severed = await _repository.Metaverse.SeverContributedValueProvenanceAsync(_hrRule.Id, _displayName.Id);

        var values = await _dbContext.MetaverseObjectAttributeValues.ToListAsync();
        var severedValues = values.Where(v => v.AttributeId == _displayName.Id && v.StringValue is "Alice" or "Bob").ToList();
        var hrMobile = values.Single(v => v.StringValue == "0700 000001");
        var adDisplayName = values.Single(v => v.StringValue == "Carol");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(severed, Is.EqualTo(2));
            Assert.That(severedValues.Select(v => v.ContributedBySyncRuleId), Is.All.Null);
            Assert.That(severedValues.Select(v => v.ContributedBySystemId), Is.All.EqualTo(HrSystemId),
                "the denormalised contributing-system record must survive severing, matching rule-deletion FK behaviour");
            Assert.That(hrMobile.ContributedBySyncRuleId, Is.EqualTo(_hrRule.Id), "the rule's other attribute must be untouched");
            Assert.That(adDisplayName.ContributedBySyncRuleId, Is.EqualTo(_adRule.Id), "another rule's values must be untouched");
        }
    }

    [Test]
    public async Task SeverContributedValueProvenance_AllAttributes_ClearsEveryValueOfTheRuleAsync()
    {
        await SeedContributionsAsync();

        var severed = await _repository.Metaverse.SeverContributedValueProvenanceAsync(_hrRule.Id, metaverseAttributeId: null);

        var values = await _dbContext.MetaverseObjectAttributeValues.ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(severed, Is.EqualTo(3));
            Assert.That(values.Where(v => v.ContributedBySyncRuleId == _hrRule.Id), Is.Empty);
            Assert.That(values.Single(v => v.StringValue == "Carol").ContributedBySyncRuleId, Is.EqualTo(_adRule.Id));
        }
    }

    [Test]
    public async Task SeverContributedValueProvenance_AssertedNullMarker_IsSeveredLikeAnyValueAsync()
    {
        // An asserted-null marker (#91) carries provenance exactly as a value row does; keeping it must
        // sever it too, or the orphan recall would later withdraw the assertion the administrator kept.
        _dbContext.MetaverseObjectAttributeValues.Add(
            NewValue(_mvo2, _displayName, string.Empty, _hrRule.Id, HrSystemId, nullValue: true));
        await _dbContext.SaveChangesAsync();

        var severed = await _repository.Metaverse.SeverContributedValueProvenanceAsync(_hrRule.Id, _displayName.Id);

        var marker = await _dbContext.MetaverseObjectAttributeValues.SingleAsync(v => v.NullValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(severed, Is.EqualTo(1));
            Assert.That(marker.ContributedBySyncRuleId, Is.Null);
            Assert.That(marker.ContributedBySystemId, Is.EqualTo(HrSystemId));
        }
    }
}
