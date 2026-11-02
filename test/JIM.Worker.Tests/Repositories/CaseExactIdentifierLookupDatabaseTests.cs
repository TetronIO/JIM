// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// The repository lookups an import uses to find "the same object" by its External ID follow the anchor attribute's
/// case-exact declaration (#2051): ignoring case unless the attribute is case-exact, with the database folding both
/// sides. Also compares .NET's folding (<see cref="IdentifierKey.For"/>) with PostgreSQL's <c>lower()</c> on this
/// database, which the in-memory keys and the database lookups agree through.
/// </summary>
/// <remarks>
/// Runs against real PostgreSQL: the folding under test is the database's own, which the in-memory suite cannot see.
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class CaseExactIdentifierLookupDatabaseTests
{
    private string _connectionString = null!;

    // Accented Latin, Turkish dotted capital I (which .NET's invariant lowering leaves alone and PostgreSQL does not),
    // German capital sharp s, Greek with a final sigma, and a titlecase digraph.
    private static readonly string[] NonAsciiValues = ["ÉMILE", "İSTANBUL", "STRAẞE", "ΣΊΣΥΦΟΣ", "ǅURO"];

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL case-exact identifier tests.");

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

    [TestCaseSource(nameof(NonAsciiValues))]
    public async Task IdentifierKeyFor_IgnoringCase_FoldsAsPostgresLowerDoesAsync(string value)
    {
        await using var ctx = NewContext();
        var lowered = await ctx.Database.SqlQueryRaw<string>("SELECT lower({0}) AS \"Value\"", value).SingleAsync();

        Assert.That(IdentifierKey.For(value, caseExact: false), Is.EqualTo(lowered),
            "On a Unicode-aware database, .NET's fold and lower() agree, so in-memory keys and database lookups give one answer. " +
            "Lookups do not rely on it (the database folds both sides), but a disagreement here would show where they would part.");
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_AnchorIgnoringCase_FindsTheObjectInAnotherCaseAsync()
    {
        var seeded = await SeedAsync(caseExact: false, "JBloggs");

        await using var ctx = NewContext();
        var found = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetConnectedSystemObjectByAttributeAsync(seeded.SystemId, seeded.AnchorAttributeId, "jbloggs");

        Assert.That(found?.Id, Is.EqualTo(seeded.CsoIds[0]));
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_CaseExactAnchor_FindsOnlyTheObjectInThatCaseAsync()
    {
        var seeded = await SeedAsync(caseExact: true, "JBloggs", "jbloggs");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).ConnectedSystems;
        var upper = await repository.GetConnectedSystemObjectByAttributeAsync(seeded.SystemId, seeded.AnchorAttributeId, "JBloggs");
        var lower = await repository.GetConnectedSystemObjectByAttributeAsync(seeded.SystemId, seeded.AnchorAttributeId, "jbloggs");
        var neither = await repository.GetConnectedSystemObjectByAttributeAsync(seeded.SystemId, seeded.AnchorAttributeId, "JBLOGGS");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(upper?.Id, Is.EqualTo(seeded.CsoIds[0]));
            Assert.That(lower?.Id, Is.EqualTo(seeded.CsoIds[1]));
            Assert.That(neither, Is.Null);
        }
    }

    [TestCaseSource(nameof(NonAsciiValues))]
    public async Task GetConnectedSystemObjectByAttributeAsync_NonAsciiAnchorIgnoringCase_FindsTheObjectByItsOwnValueAsync(string value)
    {
        var seeded = await SeedAsync(caseExact: false, value);

        await using var ctx = NewContext();
        var found = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetConnectedSystemObjectByAttributeAsync(seeded.SystemId, seeded.AnchorAttributeId, value);

        Assert.That(found?.Id, Is.EqualTo(seeded.CsoIds[0]));
    }

    [Test]
    public async Task GetAllCsoImportStateLookupAsync_CaseExactAnchor_KeysEachObjectInItsOwnCaseAsync()
    {
        var seeded = await SeedAsync(caseExact: true, "JBloggs", "jbloggs");

        await using var ctx = NewContext();
        var lookup = await new PostgresDataRepository(ctx).ConnectedSystems.GetAllCsoImportStateLookupAsync(seeded.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lookup[CsoLookupKey.For(seeded.SystemId, seeded.AnchorAttributeId, "JBloggs")].CsoId, Is.EqualTo(seeded.CsoIds[0]));
            Assert.That(lookup[CsoLookupKey.For(seeded.SystemId, seeded.AnchorAttributeId, "jbloggs")].CsoId, Is.EqualTo(seeded.CsoIds[1]));
        }
    }

    [Test]
    public async Task GetAllCsoImportStateLookupAsync_AnchorIgnoringCase_KeysTheFoldedValueAsync()
    {
        var seeded = await SeedAsync(caseExact: false, "JBloggs");

        await using var ctx = NewContext();
        var lookup = await new PostgresDataRepository(ctx).ConnectedSystems.GetAllCsoImportStateLookupAsync(seeded.SystemId);

        Assert.That(lookup.ContainsKey(CsoLookupKey.For(seeded.SystemId, seeded.AnchorAttributeId, "jbloggs")), Is.True);
    }

    [Test]
    public async Task GetConnectedSystemObjectsByAttributeValuesAsync_CaseExactAnchor_ResolvesOnlyTheValueInItsOwnCaseAsync()
    {
        var seeded = await SeedAsync(caseExact: true, "Abc", "abc");

        await using var ctx = NewContext();
        var result = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetConnectedSystemObjectsByAttributeValuesAsync(seeded.SystemId, seeded.AnchorAttributeId, ["Abc", "ABC"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Keys, Is.EquivalentTo(new[] { "Abc" }), "only the value in its own case resolves, keyed as it was asked for");
            Assert.That(result["Abc"].Id, Is.EqualTo(seeded.CsoIds[0]));
            Assert.That(result.ContainsKey("ABC"), Is.False, "a probe in another case must not find it either");
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectsByAttributeValuesAsync_AnchorIgnoringCase_ResolvesTheValueAskedForInAnotherCaseAsync()
    {
        var seeded = await SeedAsync(caseExact: false, "Abc");

        await using var ctx = NewContext();
        var result = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetConnectedSystemObjectsByAttributeValuesAsync(seeded.SystemId, seeded.AnchorAttributeId, ["ABC"]);

        Assert.That(result.TryGetValue("ABC", out var cso) ? cso.Id : (Guid?)null, Is.EqualTo(seeded.CsoIds[0]));
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync_ValueInAnotherCase_FollowsTheDeclarationAsync(bool caseExact, bool expectFound)
    {
        var seeded = await SeedAsync(caseExact: false, secondaryCaseExact: caseExact, secondaryValue: "CN=Joe,DC=PANOPLY", anchorValues: "id-1");

        await using var ctx = NewContext();
        var result = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(seeded.SystemId, ["cn=joe,dc=panoply"]);

        Assert.That(result.TryGetValue("cn=joe,dc=panoply", out var cso) ? cso.Id : (Guid?)null,
            expectFound ? Is.EqualTo(seeded.CsoIds[0]) : Is.Null);
    }

    /// <summary>
    /// Where the database and .NET fold a value differently, the database's match stands: the object it finds is filed
    /// under the value asked for, never dropped as no match (which would leave a reference unresolved, or worse, read as
    /// a different object). U+A7CB is a Unicode 16 capital that PostgreSQL lowers to U+0264 and .NET leaves alone.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsByAttributeValuesAsync_DatabaseFoldsWhereDotNetDoesNot_FilesTheDatabasesMatchAsync()
    {
        var seeded = await SeedAsync(caseExact: false, "ref-\uA7CB");

        await using var ctx = NewContext();
        var result = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetConnectedSystemObjectsByAttributeValuesAsync(seeded.SystemId, seeded.AnchorAttributeId, ["ref-\u0264"]);

        Assert.That(result.TryGetValue("ref-\u0264", out var cso) ? cso.Id : (Guid?)null, Is.EqualTo(seeded.CsoIds[0]));
    }

    /// <summary>
    /// As above, for the any-type Secondary External ID lookup.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync_DatabaseFoldsWhereDotNetDoesNot_FilesTheDatabasesMatchAsync()
    {
        var seeded = await SeedAsync(caseExact: false, secondaryCaseExact: false, secondaryValue: "CN=\uA7CB", anchorValues: "id-1");

        await using var ctx = NewContext();
        var result = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(seeded.SystemId, ["cn=\u0264"]);

        Assert.That(result.TryGetValue("cn=\u0264", out var cso) ? cso.Id : (Guid?)null, Is.EqualTo(seeded.CsoIds[0]));
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task FixupCrossBatchReferenceIdsAsync_ReferenceInAnotherCase_FollowsTheTargetsDeclarationAsync(bool caseExact, bool expectResolved)
    {
        var seeded = await SeedReferenceAsync(caseExact);

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.FixupCrossBatchReferenceIdsAsync(seeded.SystemId);

        await using var read = NewContext();
        var referenceValueId = await read.ConnectedSystemObjectAttributeValues
            .Where(av => av.Id == seeded.ReferenceValueId)
            .Select(av => av.ReferenceValueId)
            .SingleAsync();
        Assert.That(referenceValueId, Is.EqualTo(expectResolved ? seeded.TargetCsoId : null));
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_ReferenceInAnotherCase_FollowsTheTargetsDeclarationAsync(bool caseExact, bool expectResolved)
    {
        var seeded = await SeedReferenceAsync(caseExact);

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.FixupCrossBatchChangeRecordReferenceIdsAsync(seeded.SystemId);

        await using var read = NewContext();
        var referenceValueId = await read.ConnectedSystemObjectChangeAttributeValues
            .Where(v => v.Id == seeded.ChangeValueId)
            .Select(v => EF.Property<Guid?>(v, "ReferenceValueId"))
            .SingleAsync();
        Assert.That(referenceValueId, Is.EqualTo(expectResolved ? seeded.TargetCsoId : null));
    }

    #region seeding

    private sealed record SeededReference(int SystemId, Guid TargetCsoId, Guid ReferenceValueId, Guid ChangeValueId);

    /// <summary>
    /// A target object whose Secondary External ID (a DN, with the given declaration) is stored in upper case, an owner
    /// object whose unresolved reference names it in lower case, and a change record doing the same, marked unresolved
    /// as the change record writer leaves it.
    /// </summary>
    private async Task<SeededReference> SeedReferenceAsync(bool secondaryCaseExact)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Panoply", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = connectedSystem, Selected = true };
        var anchor = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var dn = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true, CaseExact = secondaryCaseExact
        };
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        objectType.Attributes.AddRange([anchor, dn, member]);
        seed.AddRange(connectorDefinition, connectedSystem, objectType);
        await seed.SaveChangesAsync();

        var target = new ConnectedSystemObject
        {
            ConnectedSystemId = connectedSystem.Id, TypeId = objectType.Id, ExternalIdAttributeId = anchor.Id,
            SecondaryExternalIdAttributeId = dn.Id, Status = ConnectedSystemObjectStatus.Normal
        };
        target.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = target, AttributeId = anchor.Id, GuidValue = Guid.NewGuid() });
        target.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = target, AttributeId = dn.Id, StringValue = "CN=Joe,DC=PANOPLY" });

        var owner = new ConnectedSystemObject
        {
            ConnectedSystemId = connectedSystem.Id, TypeId = objectType.Id, ExternalIdAttributeId = anchor.Id,
            SecondaryExternalIdAttributeId = dn.Id, Status = ConnectedSystemObjectStatus.Normal
        };
        owner.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = owner, AttributeId = anchor.Id, GuidValue = Guid.NewGuid() });
        var reference = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = owner, AttributeId = member.Id, UnresolvedReferenceValue = "cn=joe,dc=panoply" };
        owner.AttributeValues.Add(reference);
        seed.AddRange(target, owner);

        var change = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Exported,
            InitiatedByType = ActivityInitiatorType.System
        };
        var memberChange = new ConnectedSystemObjectChangeAttribute
        {
            Id = Guid.NewGuid(), ConnectedSystemChange = change, AttributeName = "member", AttributeType = AttributeDataType.Reference
        };
        var changeValue = new ConnectedSystemObjectChangeAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObjectChangeAttribute = memberChange, ValueChangeType = ValueChangeType.Add, StringValue = "cn=joe,dc=panoply",
            IsUnresolvedReference = true
        };
        memberChange.ValueChanges.Add(changeValue);
        change.AttributeChanges.Add(memberChange);
        seed.Add(change);
        await seed.SaveChangesAsync();

        return new SeededReference(connectedSystem.Id, target.Id, reference.Id, changeValue.Id);
    }



    private sealed record Seeded(int SystemId, int ObjectTypeId, int AnchorAttributeId, List<Guid> CsoIds);

    /// <summary>
    /// One Connected System, one Object Type whose Text anchor carries the given declaration, and one Connected System
    /// Object per anchor value, in the order given.
    /// </summary>
    private Task<Seeded> SeedAsync(bool caseExact, params string[] anchorValues) =>
        SeedAsync(caseExact, secondaryCaseExact: false, secondaryValue: null, anchorValues);

    /// <summary>
    /// As above, optionally with a Text Secondary External ID carrying its own declaration, given the same value on
    /// every object.
    /// </summary>
    private async Task<Seeded> SeedAsync(bool caseExact, bool secondaryCaseExact, string? secondaryValue, params string[] anchorValues)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = connectedSystem, Selected = true };
        var anchorAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "accountId",
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true,
            IsExternalId = true,
            CaseExact = caseExact
        };
        objectType.Attributes.Add(anchorAttribute);
        var secondaryAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName",
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true,
            IsSecondaryExternalId = true,
            CaseExact = secondaryCaseExact
        };
        objectType.Attributes.Add(secondaryAttribute);
        seed.AddRange(connectorDefinition, connectedSystem, objectType);
        await seed.SaveChangesAsync();

        var csos = anchorValues.Select(value =>
        {
            var cso = new ConnectedSystemObject
            {
                ConnectedSystemId = connectedSystem.Id,
                TypeId = objectType.Id,
                ExternalIdAttributeId = anchorAttribute.Id,
                SecondaryExternalIdAttributeId = secondaryValue != null ? secondaryAttribute.Id : null,
                Status = ConnectedSystemObjectStatus.Normal
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                ConnectedSystemObject = cso,
                AttributeId = anchorAttribute.Id,
                StringValue = value
            });
            if (secondaryValue != null)
            {
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    ConnectedSystemObject = cso,
                    AttributeId = secondaryAttribute.Id,
                    StringValue = secondaryValue
                });
            }
            return cso;
        }).ToList();
        seed.AddRange(csos);
        await seed.SaveChangesAsync();

        return new Seeded(connectedSystem.Id, objectType.Id, anchorAttribute.Id, csos.Select(c => c.Id).ToList());
    }

    #endregion
}
