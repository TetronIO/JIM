// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Sign-in finds the person by a Guid-typed Metaverse Attribute, such as the <c>Object GUID</c> AD FS issues as
/// <c>oid</c> (#2438), comparing the stored GUID rather than any text.
/// </summary>
/// <remarks>
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SsoGuidIdentifierLookupDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SSO Guid identifier lookup tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public async Task GetMetaverseObjectByTypeAndAttributeAsync_Guid_FindsTheObjectHoldingThatGuidAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Metaverse;
        var userType = await ctx.MetaverseObjectTypes.SingleAsync(t => t.Id == seeded.UserTypeId);
        var objectGuid = await ctx.MetaverseAttributes.SingleAsync(a => a.Id == seeded.ObjectGuidId);

        var alice = await repository.GetMetaverseObjectByTypeAndAttributeAsync(userType, objectGuid, seeded.AliceGuid);
        var nobody = await repository.GetMetaverseObjectByTypeAndAttributeAsync(userType, objectGuid, Guid.NewGuid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(alice?.Id, Is.EqualTo(seeded.AliceId));
            Assert.That(alice?.AttributeValues.Any(av => av.Attribute.Id == seeded.ObjectGuidId), Is.True, "attribute values are loaded");
            Assert.That(nobody, Is.Null);
        }
    }

    [Test]
    public async Task GetMetaverseObjectByTypeAndAttributeAsync_GuidHeldByAnotherType_FindsNothingAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Metaverse;
        var userType = await ctx.MetaverseObjectTypes.SingleAsync(t => t.Id == seeded.UserTypeId);
        var objectGuid = await ctx.MetaverseAttributes.SingleAsync(a => a.Id == seeded.ObjectGuidId);

        var found = await repository.GetMetaverseObjectByTypeAndAttributeAsync(userType, objectGuid, seeded.GroupGuid);

        Assert.That(found, Is.Null);
    }

    private sealed record Seeded(int UserTypeId, int ObjectGuidId, Guid AliceId, Guid AliceGuid, Guid GroupGuid);

    private async Task<Seeded> SeedAsync()
    {
        await using var ctx = NewContext();

        var userType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.User, PluralName = "Users", BuiltIn = true };
        var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        var objectGuid = new MetaverseAttribute
        {
            Name = Constants.BuiltInAttributes.ObjectGuid,
            Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = true
        };
        ctx.MetaverseObjectTypes.AddRange(userType, groupType);
        ctx.MetaverseAttributes.Add(objectGuid);
        await ctx.SaveChangesAsync();

        var aliceGuid = Guid.NewGuid();
        var bobGuid = Guid.NewGuid();
        var groupGuid = Guid.NewGuid();
        var alice = WithGuid(new MetaverseObject { Id = Guid.NewGuid(), Type = userType, CachedDisplayName = "Alice" }, objectGuid, aliceGuid);
        var bob = WithGuid(new MetaverseObject { Id = Guid.NewGuid(), Type = userType, CachedDisplayName = "Bob" }, objectGuid, bobGuid);
        var group = WithGuid(new MetaverseObject { Id = Guid.NewGuid(), Type = groupType, CachedDisplayName = "Engineers" }, objectGuid, groupGuid);
        ctx.MetaverseObjects.AddRange(alice, bob, group);
        await ctx.SaveChangesAsync();

        return new Seeded(userType.Id, objectGuid.Id, alice.Id, aliceGuid, groupGuid);
    }

    private static MetaverseObject WithGuid(MetaverseObject metaverseObject, MetaverseAttribute attribute, Guid value)
    {
        metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = metaverseObject,
            Attribute = attribute,
            GuidValue = value
        });
        return metaverseObject;
    }
}
