// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Integration tests for SecurityRepository.GetRoleHeadersAsync(). Verifies that the static
/// member count is projected directly in SQL rather than relying on a loaded navigation
/// property; the original RoleDto path silently returned 0 because GetRolesAsync() did
/// not Include StaticMembers, and the count was computed in memory.
/// </summary>
[TestFixture]
public class SecurityRepositoryRoleHeadersIntegrationTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseHostname, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseName, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseUsername, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabasePassword, "dummy");

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
        _dbContext?.Dispose();
    }

    [Test]
    public async Task GetRoleHeadersAsync_WithMembers_ReturnsCorrectStaticMemberCountAsync()
    {
        // Arrange: create a Person object type, two MetaverseObjects, and an Administrator
        // role with both objects assigned as static members.
        var personType = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" };
        _dbContext.MetaverseObjectTypes.Add(personType);

        var member1 = new MetaverseObject { Id = Guid.NewGuid(), Type = personType };
        var member2 = new MetaverseObject { Id = Guid.NewGuid(), Type = personType };
        _dbContext.MetaverseObjects.Add(member1);
        _dbContext.MetaverseObjects.Add(member2);

        var adminRole = new Role
        {
            Id = 1,
            Name = "Administrator",
            BuiltIn = true,
            Created = DateTime.UtcNow,
            StaticMembers = new List<MetaverseObject> { member1, member2 }
        };
        var emptyRole = new Role
        {
            Id = 2,
            Name = "Reader",
            BuiltIn = true,
            Created = DateTime.UtcNow,
            StaticMembers = new List<MetaverseObject>()
        };
        _dbContext.Roles.Add(adminRole);
        _dbContext.Roles.Add(emptyRole);
        await _dbContext.SaveChangesAsync();

        // Act
        var headers = await _repository.Security.GetRoleHeadersAsync();

        // Assert
        Assert.That(headers, Has.Count.EqualTo(2));
        var admin = headers.Single(h => h.Name == "Administrator");
        Assert.That(admin.StaticMemberCount, Is.EqualTo(2), "Administrator role should report two static members");
        Assert.That(admin.Id, Is.EqualTo(1));
        Assert.That(admin.BuiltIn, Is.True);

        var reader = headers.Single(h => h.Name == "Reader");
        Assert.That(reader.StaticMemberCount, Is.EqualTo(0));
    }

    [Test]
    public async Task GetRoleHeadersAsync_NoRoles_ReturnsEmptyAsync()
    {
        var headers = await _repository.Security.GetRoleHeadersAsync();
        Assert.That(headers, Is.Empty);
    }
}
