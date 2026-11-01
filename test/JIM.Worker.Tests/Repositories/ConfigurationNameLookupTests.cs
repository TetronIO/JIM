// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Repository lookups by a configuration name ignore case (#2051): an attribute name in a REST API filter or a
/// PowerShell parameter, a Predefined Search URI, a Connector name. An exact name still wins, so a Connected System
/// whose own names are case-exact keeps its two attributes apart.
/// </summary>
[TestFixture]
public class ConfigurationNameLookupTests
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

    private async Task<(MetaverseObjectType Type, MetaverseAttribute Attribute, MetaverseObject Mvo)> SeedMetaverseObjectAsync()
    {
        var type = new MetaverseObjectType { Name = "User", PluralName = "Users" };
        var attribute = new MetaverseAttribute { Name = "Employee Id", Type = AttributeDataType.Text };
        var mvo = new MetaverseObject
        {
            Type = type,
            AttributeValues = [new MetaverseObjectAttributeValue { Attribute = attribute, StringValue = "E123" }]
        };
        _dbContext.AddRange(type, attribute, mvo);
        await _dbContext.SaveChangesAsync();
        return (type, attribute, mvo);
    }

    #region Metaverse attribute names

    [Test]
    public async Task GetMetaverseObjectsCountAsync_FilterAttributeNameInDifferentCase_MatchesAsync()
    {
        await SeedMetaverseObjectAsync();

        var count = await _repository.Metaverse.GetMetaverseObjectsCountAsync(
            filterAttributeName: "employee id", filterAttributeValue: "E123");

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task GetMetaverseObjectsAsync_FilterAttributeNameInDifferentCase_MatchesAsync()
    {
        await SeedMetaverseObjectAsync();

        var page = await _repository.Metaverse.GetMetaverseObjectsAsync(
            1, 10, filterAttributeName: "EMPLOYEE ID", filterAttributeValue: "e123");

        Assert.That(page.Results, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetMetaverseObjectsAsync_RequestedAttributeInDifferentCase_IsReturnedAsync()
    {
        await SeedMetaverseObjectAsync();

        var page = await _repository.Metaverse.GetMetaverseObjectsAsync(1, 10, attributes: ["employee id"]);

        Assert.That(page.Results.Single().AttributeValues.Select(av => av.StringValue), Does.Contain("E123"));
    }

    [Test]
    public async Task GetAttributeValuesPagedAsync_MetaverseAttributeNameInDifferentCase_ReturnsTheValuesAsync()
    {
        var (_, _, mvo) = await SeedMetaverseObjectAsync();

        var page = await _repository.Metaverse.GetAttributeValuesPagedAsync(mvo.Id, "employee ID", 1, 10);

        Assert.That(page.Results.Select(av => av.StringValue), Is.EqualTo(new[] { "E123" }));
    }

    #endregion

    #region Connected System attribute names

    private async Task<(ConnectedSystemObject Cso, PendingExport PendingExport)> SeedConnectedSystemObjectAsync(params string[] attributeNames)
    {
        var connectedSystem = new ConnectedSystem { Name = "Directory" };
        var type = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = connectedSystem };
        var attributes = attributeNames
            .Select(name => new ConnectedSystemObjectTypeAttribute { Name = name, Type = AttributeDataType.Text, ConnectedSystemObjectType = type })
            .ToList();
        type.Attributes = attributes;

        var cso = new ConnectedSystemObject
        {
            ConnectedSystem = connectedSystem,
            Type = type,
            AttributeValues = new(attributes
                .Select(a => new ConnectedSystemObjectAttributeValue { Attribute = a, StringValue = $"{a.Name} value" }))
        };

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = connectedSystem,
            ConnectedSystemObject = cso,
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges = new(attributes
                .Select(a => new PendingExportAttributeValueChange { Attribute = a, StringValue = $"{a.Name} change" }))
        };

        _dbContext.AddRange(connectedSystem, cso, pendingExport);
        await _dbContext.SaveChangesAsync();
        return (cso, pendingExport);
    }

    [Test]
    public async Task GetAttributeValuesPagedAsync_ConnectedSystemAttributeNameInDifferentCase_ReturnsTheValuesAsync()
    {
        var (cso, _) = await SeedConnectedSystemObjectAsync("mail");

        var page = await _repository.ConnectedSystems.GetAttributeValuesPagedAsync(cso.Id, "Mail", 1, 10);

        Assert.That(page.Results.Select(av => av.StringValue), Is.EqualTo(new[] { "mail value" }));
    }

    [Test]
    public async Task GetAttributeValuesPagedAsync_TwoAttributesDifferingOnlyInCase_ReturnsOnlyTheExactOneAsync()
    {
        var (cso, _) = await SeedConnectedSystemObjectAsync("mail", "Mail");

        var page = await _repository.ConnectedSystems.GetAttributeValuesPagedAsync(cso.Id, "Mail", 1, 10);

        Assert.That(page.Results.Select(av => av.StringValue), Is.EqualTo(new[] { "Mail value" }));
    }

    [Test]
    public async Task GetPendingExportAttributeChangesPagedAsync_AttributeNameInDifferentCase_ReturnsTheChangesAsync()
    {
        var (_, pendingExport) = await SeedConnectedSystemObjectAsync("mail");

        var page = await _repository.ConnectedSystems.GetPendingExportAttributeChangesPagedAsync(pendingExport.Id, "MAIL", 1, 10);

        Assert.That(page.Results.Select(c => c.StringValue), Is.EqualTo(new[] { "mail change" }));
    }

    #endregion

    #region Predefined Search URI and Connector name

    [Test]
    public async Task GetPredefinedSearchAsync_UriInDifferentCase_FindsTheSearchAsync()
    {
        var type = new MetaverseObjectType { Name = "User", PluralName = "Users" };
        var search = new PredefinedSearch { Name = "Leavers", Uri = "leavers", MetaverseObjectType = type };
        _dbContext.AddRange(type, search);
        await _dbContext.SaveChangesAsync();

        var found = await _repository.Search.GetPredefinedSearchAsync("Leavers");

        Assert.That(found?.Id, Is.EqualTo(search.Id));
    }

    [Test]
    public async Task GetConnectorDefinitionAsync_NameInDifferentCase_FindsTheConnectorAsync()
    {
        var definition = new ConnectorDefinition { Name = "JIM LDAP Connector" };
        _dbContext.Add(definition);
        await _dbContext.SaveChangesAsync();

        var found = await _repository.ConnectedSystems.GetConnectorDefinitionAsync("jim ldap connector");

        Assert.That(found?.Id, Is.EqualTo(definition.Id));
    }

    #endregion
}
