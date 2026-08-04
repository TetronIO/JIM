// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests for the Pending Deletions listing queries in MetaverseRepository. The page these queries back
/// must show every MVO housekeeping will delete, so the rule filter must match the housekeeping
/// eligibility rule set: WhenLastConnectorDisconnected AND WhenAuthoritativeSourceDisconnected (#119).
/// Before #119 the listing filtered to WhenLastConnectorDisconnected only, silently hiding scheduled
/// deletions triggered by an authoritative source disconnection.
/// </summary>
[TestFixture]
public class MetaverseRepositoryPendingDeletionListTests
{
    private Mock<JimDbContext> _mockDbContext = null!;
    private List<MetaverseObject> _metaverseObjectsData = null!;
    private Mock<DbSet<MetaverseObject>> _mockDbSetMetaverseObjects = null!;
    private PostgresDataRepository _repository = null!;

    private const int HrSystemId = 1;

    private MetaverseObjectType _personTypeLastConnector = null!;
    private MetaverseObjectType _personTypeAuthoritativeSource = null!;
    private MetaverseObjectType _personTypeManual = null!;

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _personTypeLastConnector = new MetaverseObjectType
        {
            Id = 1,
            Name = "PersonLastConnector",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };

        _personTypeAuthoritativeSource = new MetaverseObjectType
        {
            Id = 2,
            Name = "PersonAuthoritativeSource",
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            DeletionTriggerConnectedSystemIds = new List<int> { HrSystemId },
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };

        _personTypeManual = new MetaverseObjectType
        {
            Id = 3,
            Name = "ServiceAccount",
            DeletionRule = MetaverseObjectDeletionRule.Manual
        };

        _metaverseObjectsData = new List<MetaverseObject>();
    }

    private void SetupMockDbContext()
    {
        _mockDbSetMetaverseObjects = _metaverseObjectsData.BuildMockDbSet();
        _mockDbContext = new Mock<JimDbContext>();
        _mockDbContext.Setup(m => m.MetaverseObjects).Returns(_mockDbSetMetaverseObjects.Object);
        _repository = new PostgresDataRepository(_mockDbContext.Object);
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionAsync_AuthoritativeSourceScheduledMvo_IsListedAsync()
    {
        // An MVO scheduled for deletion by an authoritative source disconnection may retain target
        // connectors during its grace period; it must still appear on the Pending Deletions page.
        var mvo = CreateMarkedMvo(_personTypeAuthoritativeSource);
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        var results = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionAsync(1, 10);

        Assert.That(results.Results, Has.Count.EqualTo(1));
        Assert.That(results.Results[0].Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionAsync_LastConnectorScheduledMvo_IsListedAsync()
    {
        var mvo = CreateMarkedMvo(_personTypeLastConnector);
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        var results = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionAsync(1, 10);

        Assert.That(results.Results, Has.Count.EqualTo(1));
        Assert.That(results.Results[0].Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionAsync_ManualRuleMvoWithMarker_IsNotListedAsync()
    {
        // Manual-rule MVOs are never automatically deleted, so a stray marker must not list them.
        var mvo = CreateMarkedMvo(_personTypeManual);
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        var results = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionAsync(1, 10);

        Assert.That(results.Results, Is.Empty);
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionCountAsync_AuthoritativeSourceScheduledMvo_IsCountedAsync()
    {
        var mvo = CreateMarkedMvo(_personTypeAuthoritativeSource);
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        var count = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionCountAsync();

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionCountAsync_MixedRules_CountAgreesWithListAsync()
    {
        _metaverseObjectsData.Add(CreateMarkedMvo(_personTypeLastConnector));
        _metaverseObjectsData.Add(CreateMarkedMvo(_personTypeAuthoritativeSource));
        _metaverseObjectsData.Add(CreateMarkedMvo(_personTypeManual));
        SetupMockDbContext();

        var results = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionAsync(1, 10);
        var count = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionCountAsync();

        Assert.That(results.Results, Has.Count.EqualTo(2));
        Assert.That(count, Is.EqualTo(2));
    }

    private static MetaverseObject CreateMarkedMvo(MetaverseObjectType type)
    {
        return new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Origin = MetaverseObjectOrigin.Projected,
            Type = type,
            LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1),
            ConnectedSystemObjects = new List<ConnectedSystemObject>(),
            AttributeValues = new List<MetaverseObjectAttributeValue>()
        };
    }
}
