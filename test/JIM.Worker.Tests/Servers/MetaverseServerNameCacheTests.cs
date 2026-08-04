// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// <see cref="MetaverseObject.CachedDisplayName"/> is the denormalised sort key behind the Metaverse
/// list, its ORDER BY, and change-history reference labels; those paths read the column without
/// materialising attribute values. It must therefore cache whatever
/// <see cref="MetaverseObject.Name"/> would resolve, across every tier of
/// <see cref="ObjectNaming.MetaverseNameAttributes"/>. If it only tracked Display Name, a Group
/// carrying just a Common Name would read correctly on its detail page and blank on the list.
/// </summary>
[TestFixture]
public class MetaverseServerNameCacheTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private JimApplication _jim = null!;

    private MetaverseObjectType _groupType = null!;
    private MetaverseAttribute _displayNameAttr = null!;
    private MetaverseAttribute _commonNameAttr = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();

        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockServiceSettingsRepo
            .Setup(r => r.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((ServiceSetting?)null);

        _jim = new JimApplication(_mockRepository.Object);

        _groupType = new MetaverseObjectType { Id = 1, Name = Constants.BuiltInObjectTypes.Group };
        _displayNameAttr = new MetaverseAttribute
        {
            Id = 1,
            Name = Constants.BuiltInAttributes.DisplayName,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _commonNameAttr = new MetaverseAttribute
        {
            Id = 2,
            Name = Constants.BuiltInAttributes.CommonName,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    private static MetaverseObjectAttributeValue Value(MetaverseAttribute attribute, string? stringValue) =>
        new() { Id = Guid.NewGuid(), Attribute = attribute, StringValue = stringValue };

    [Test]
    public async Task UpdateMetaverseObjectAsync_CommonNameAddedWithNoDisplayName_RefreshesCacheAsync()
    {
        // The gate previously only fired for Display Name changes, so a Group named solely by its
        // Common Name kept a null sort key and rendered blank on the Metaverse list.
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _groupType };
        var addition = Value(_commonNameAttr, "Project-GlobalGateway");
        mvo.AttributeValues.Add(addition);

        await _jim.Metaverse.UpdateMetaverseObjectAsync(
            mvo,
            [addition],
            null,
            ActivityInitiatorType.System,
            null,
            null,
            MetaverseObjectChangeInitiatorType.System);

        Assert.That(mvo.CachedDisplayName, Is.EqualTo("Project-GlobalGateway"));
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_CommonNameRemoved_ClearsCacheAsync()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _groupType,
            CachedDisplayName = "Project-GlobalGateway"
        };
        var removal = Value(_commonNameAttr, "Project-GlobalGateway");

        await _jim.Metaverse.UpdateMetaverseObjectAsync(
            mvo,
            null,
            [removal],
            ActivityInitiatorType.System,
            null,
            null,
            MetaverseObjectChangeInitiatorType.System);

        Assert.That(mvo.CachedDisplayName, Is.Null);
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_DisplayNameAddedAlongsideCommonName_PrefersDisplayNameAsync()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _groupType };
        var commonName = Value(_commonNameAttr, "Project-GlobalGateway");
        var displayName = Value(_displayNameAttr, "Global Gateway Project");
        mvo.AttributeValues.Add(commonName);
        mvo.AttributeValues.Add(displayName);

        await _jim.Metaverse.UpdateMetaverseObjectAsync(
            mvo,
            [displayName],
            null,
            ActivityInitiatorType.System,
            null,
            null,
            MetaverseObjectChangeInitiatorType.System);

        Assert.That(mvo.CachedDisplayName, Is.EqualTo("Global Gateway Project"));
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_DisplayNameRemovedLeavingCommonName_FallsBackToCommonNameAsync()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _groupType,
            CachedDisplayName = "Global Gateway Project"
        };
        var commonName = Value(_commonNameAttr, "Project-GlobalGateway");
        mvo.AttributeValues.Add(commonName);
        var removal = Value(_displayNameAttr, "Global Gateway Project");

        await _jim.Metaverse.UpdateMetaverseObjectAsync(
            mvo,
            null,
            [removal],
            ActivityInitiatorType.System,
            null,
            null,
            MetaverseObjectChangeInitiatorType.System);

        Assert.That(mvo.CachedDisplayName, Is.EqualTo("Project-GlobalGateway"));
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_NonNameAttributeChanged_LeavesCacheUntouchedAsync()
    {
        var departmentAttr = new MetaverseAttribute
        {
            Id = 3,
            Name = Constants.BuiltInAttributes.Department,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _groupType,
            CachedDisplayName = "Project-GlobalGateway"
        };
        var addition = Value(departmentAttr, "Finance");
        mvo.AttributeValues.Add(addition);

        await _jim.Metaverse.UpdateMetaverseObjectAsync(
            mvo,
            [addition],
            null,
            ActivityInitiatorType.System,
            null,
            null,
            MetaverseObjectChangeInitiatorType.System);

        Assert.That(mvo.CachedDisplayName, Is.EqualTo("Project-GlobalGateway"));
    }

    [Test]
    public async Task CreateMetaverseObjectAsync_CommonNameOnly_PopulatesCacheAsync()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _groupType };
        mvo.AttributeValues.Add(Value(_commonNameAttr, "Project-GlobalGateway"));

        await _jim.Metaverse.CreateMetaverseObjectAsync(mvo);

        Assert.That(mvo.CachedDisplayName, Is.EqualTo("Project-GlobalGateway"));
    }
}
