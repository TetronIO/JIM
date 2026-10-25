// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the window reads over a change record's values (#2498): a change to a large group
/// carries one value change per member, so the portal and the REST API read its values a window at a time. The rows
/// are normalised in SQL exactly as the causality panel always showed them (a single-valued replacement is one Set
/// row carrying the value it replaced), so a window boundary can never split a replacement in two.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ChangeValueRowsDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL change value row tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    // ─── Windows ───

    [Test]
    public async Task GetCsoChangeValueRowsAsync_MidWindowWithCount_ReturnsTheSliceInSourceOrderAndTheTotalAsync()
    {
        var change = await SeedCsoChangeAsync(memberAdds: 30);

        var result = await NewJim().ConnectedSystems.GetCsoChangeValueRowsAsync(Query(change.ChangeId, offset: 10, count: 5, includeTotalCount: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(30));
            Assert.That(result.Results.Select(r => r.ValueChangeId), Is.EqualTo(Enumerable.Range(11, 5).Select(IdFor)),
                "rows run in the order the change recorded them");
            Assert.That(result.Results, Has.All.Matches<CsoChangeValueRow>(r => r.Operation == ChangeValueOperation.Add),
                "a multi-valued attribute gains values by Add");
        }
    }

    [Test]
    public async Task GetCsoChangeValueRowsAsync_TotalNotRequested_ReturnsANullTotalAsync()
    {
        var change = await SeedCsoChangeAsync(memberAdds: 3);

        var result = await NewJim().ConnectedSystems.GetCsoChangeValueRowsAsync(Query(change.ChangeId, offset: 0, count: 10));

        Assert.That(result.Results, Has.Count.EqualTo(3));
        Assert.That(result.TotalResults, Is.Null, "null means not counted, never zero");
    }

    [Test]
    public async Task GetCsoChangeValueRowsAsync_ConsecutiveWindows_PartitionTheRowsExactlyAsync()
    {
        var change = await SeedCsoChangeAsync(memberAdds: 30, descriptionReplacement: ("old", "new"));
        var jim = NewJim();

        var seen = new List<Guid>();
        for (var offset = 0; offset < 40; offset += 7)
            seen.AddRange((await jim.ConnectedSystems.GetCsoChangeValueRowsAsync(Query(change.ChangeId, offset, 7))).Results.Select(r => r.ValueChangeId));

        Assert.That(seen, Has.Count.EqualTo(31), "thirty member Adds and one description Set");
        Assert.That(seen, Is.Unique);
    }

    // ─── Normalising single-valued replacements ───

    [Test]
    public async Task GetCsoChangeValueRowsAsync_SingleValuedReplacement_ReadsOneSetRowCarryingTheValueItReplacedAsync()
    {
        var change = await SeedCsoChangeAsync(descriptionReplacement: ("Old description", "New description"));

        var rows = (await NewJim().ConnectedSystems.GetCsoChangeValueRowsAsync(Query(change.ChangeId, 0, 10))).Results;

        var row = rows.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Operation, Is.EqualTo(ChangeValueOperation.Set));
            Assert.That(row.Value.StringValue, Is.EqualTo("New description"));
            Assert.That(row.PreviousValue?.StringValue, Is.EqualTo("Old description"));
            Assert.That(row.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
        }
    }

    [Test]
    public async Task GetCsoChangeValueRowsAsync_NotNormalised_ReadsEachValueChangeAsItsOwnRowAsync()
    {
        var change = await SeedCsoChangeAsync(descriptionReplacement: ("Old description", "New description"));

        var rows = (await NewJim().ConnectedSystems.GetCsoChangeValueRowsAsync(Query(change.ChangeId, 0, 10) with { Normalise = false })).Results;

        Assert.That(rows.Select(r => (r.Operation, r.Value.StringValue)), Is.EquivalentTo(new[]
        {
            (ChangeValueOperation.Add, (string?)"New description"),
            (ChangeValueOperation.Remove, (string?)"Old description")
        }));
        Assert.That(rows, Has.All.Matches<CsoChangeValueRow>(r => r.PreviousValue == null));
    }

    [Test]
    public async Task GetCsoChangeValueRowsAsync_SingleValuedAddWithoutARemove_ReadsASetRowWithNoPreviousValueAsync()
    {
        var change = await SeedCsoChangeAsync(descriptionAdd: "First description");

        var row = (await NewJim().ConnectedSystems.GetCsoChangeValueRowsAsync(Query(change.ChangeId, 0, 10))).Results.Single();

        Assert.That(row.Operation, Is.EqualTo(ChangeValueOperation.Set));
        Assert.That(row.PreviousValue, Is.Null);
    }

    [Test]
    public async Task GetCsoChangeValueRowsAsync_ReplacementAfterAWindowBoundary_IsStillOneSetRowAsync()
    {
        // "description" sorts before "member", so ordering by attribute puts the replacement first; read in source
        // order the replacement is the 31st row, the first row of a window starting at 30.
        var change = await SeedCsoChangeAsync(memberAdds: 30, descriptionReplacement: ("old", "new"));
        var jim = NewJim();

        var lastWindow = await jim.ConnectedSystems.GetCsoChangeValueRowsAsync(
            Query(change.ChangeId, offset: 25, count: 10, includeTotalCount: true) with { Sort = ChangeValueRowSort.Attribute, SortDescending = true });

        Assert.That(lastWindow.TotalResults, Is.EqualTo(31), "the replacement counts as one row");
        var set = lastWindow.Results.Single(r => r.AttributeName == "description");
        Assert.That(set.Operation, Is.EqualTo(ChangeValueOperation.Set));
        Assert.That(set.PreviousValue?.StringValue, Is.EqualTo("old"));
    }

    // ─── Filters, search and sort ───

    [Test]
    public async Task GetCsoChangeValueRowsAsync_SearchText_MatchesAReferencedObjectsNameAsync()
    {
        var change = await SeedCsoChangeAsync(memberReferenceNames: ["Alice Smith", "Bob Jones", "Carol Price"]);

        var rows = (await NewJim().ConnectedSystems.GetCsoChangeValueRowsAsync(Query(change.ChangeId, 0, 10) with { SearchText = "bob" })).Results;

        var row = rows.Single();
        Assert.That(row.Value.ReferenceValue?.DisplayName, Is.EqualTo("Bob Jones"));
        Assert.That(row.Value.ReferenceValue?.TypeName, Is.EqualTo("group"));
    }

    [Test]
    public async Task GetCsoChangeValueRowsAsync_SearchText_MatchesAnAttributeNameAsync()
    {
        var change = await SeedCsoChangeAsync(memberAdds: 5, descriptionAdd: "Finance team");

        var rows = (await NewJim().ConnectedSystems.GetCsoChangeValueRowsAsync(Query(change.ChangeId, 0, 10) with { SearchText = "DESCRIP" })).Results;

        Assert.That(rows.Single().AttributeName, Is.EqualTo("description"));
    }

    [Test]
    public async Task GetCsoChangeValueRowsAsync_OperationFilter_ReadsOnlyThatOperationAsync()
    {
        var change = await SeedCsoChangeAsync(memberAdds: 4, memberRemoves: 2);

        var result = await NewJim().ConnectedSystems.GetCsoChangeValueRowsAsync(
            Query(change.ChangeId, 0, 10, includeTotalCount: true) with { Operation = ChangeValueOperation.Remove });

        Assert.That(result.TotalResults, Is.EqualTo(2));
        Assert.That(result.Results, Has.All.Matches<CsoChangeValueRow>(r => r.Operation == ChangeValueOperation.Remove));
    }

    [Test]
    public async Task GetCsoChangeValueRowsAsync_SortByValueDescending_OrdersByTheValueAsItReadsAsync()
    {
        var change = await SeedCsoChangeAsync(memberReferenceNames: ["Bob Jones", "Alice Smith", "Carol Price"]);

        var rows = (await NewJim().ConnectedSystems.GetCsoChangeValueRowsAsync(
            Query(change.ChangeId, 0, 10) with { Sort = ChangeValueRowSort.Value, SortDescending = true })).Results;

        Assert.That(rows.Select(r => r.Value.ReferenceValue?.DisplayName), Is.EqualTo(new[] { "Carol Price", "Bob Jones", "Alice Smith" }));
    }

    [Test]
    public async Task GetCsoChangeValueRowsAsync_AttributeName_ReadsOnlyThatAttributeCaseInsensitivelyAsync()
    {
        var change = await SeedCsoChangeAsync(memberAdds: 3, descriptionAdd: "Finance team");

        var rows = (await NewJim().ConnectedSystems.GetCsoChangeValueRowsAsync(Query(change.ChangeId, 0, 10) with { AttributeName = "MEMBER" })).Results;

        Assert.That(rows, Has.Count.EqualTo(3));
        Assert.That(rows, Has.All.Matches<CsoChangeValueRow>(r => r.AttributeName == "member"));
    }

    [Test]
    public async Task GetCsoChangeValueRowsAsync_TwoChanges_ReadsThemInTheOrderGivenAsync()
    {
        var first = await SeedCsoChangeAsync(descriptionAdd: "first", idBase: 100);
        var second = await SeedCsoChangeAsync(descriptionAdd: "second", idBase: 200, reuseSchemaFrom: first);

        var rows = (await NewJim().ConnectedSystems.GetCsoChangeValueRowsAsync(
            new ChangeValueRowQuery { ChangeIds = [second.ChangeId, first.ChangeId], Count = 10 })).Results;

        Assert.That(rows.Select(r => r.ChangeId), Is.EqualTo(new[] { second.ChangeId, first.ChangeId }));
    }

    // ─── Operation counts ───

    [Test]
    public async Task GetCsoChangeValueOperationCountsAsync_CountsNormalisedRowsPerOperationAsync()
    {
        var change = await SeedCsoChangeAsync(memberAdds: 4, memberRemoves: 2, descriptionReplacement: ("old", "new"));

        var counts = await NewJim().ConnectedSystems.GetCsoChangeValueOperationCountsAsync([change.ChangeId]);

        Assert.That(counts, Is.EquivalentTo(new[]
        {
            new ChangeValueOperationCount(change.ChangeId, ChangeValueOperation.Add, 4),
            new ChangeValueOperationCount(change.ChangeId, ChangeValueOperation.Remove, 2),
            new ChangeValueOperationCount(change.ChangeId, ChangeValueOperation.Set, 1)
        }));
    }

    // ─── Metaverse Object changes ───

    [Test]
    public async Task GetMvoChangeValueRowsAsync_ReferenceValues_AreNamedFromTheCachedDisplayNameAsync()
    {
        var change = await SeedMvoChangeAsync(proxyAddressAdds: 0, managerNames: ["Alice Smith", "Bob Jones"]);

        var result = await NewJim().Metaverse.GetMvoChangeValueRowsAsync(
            new ChangeValueRowQuery { ChangeIds = [change.ChangeId], Count = 10, IncludeTotalCount = true, SearchText = "alice" });

        var row = result.Results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Value.ReferenceValue?.DisplayName, Is.EqualTo("Alice Smith"));
            Assert.That(row.Value.ReferenceValue?.TypePluralName, Is.EqualTo("People"));
            Assert.That(row.MetaverseObjectId, Is.EqualTo(change.MetaverseObjectId));
            Assert.That(row.AttributeId, Is.Not.Null);
        }
    }

    [Test]
    public async Task GetMvoChangeValueRowsAsync_MidWindowWithCount_ReturnsTheSliceAndTheTotalAsync()
    {
        var change = await SeedMvoChangeAsync(proxyAddressAdds: 25);

        var result = await NewJim().Metaverse.GetMvoChangeValueRowsAsync(Query(change.ChangeId, offset: 20, count: 10, includeTotalCount: true));

        Assert.That(result.TotalResults, Is.EqualTo(25));
        Assert.That(result.Results.Select(r => r.ValueChangeId), Is.EqualTo(Enumerable.Range(21, 5).Select(IdFor)));
    }

    [Test]
    public async Task GetMvoChangeValueOperationCountsAsync_CountsNormalisedRowsPerOperationAsync()
    {
        var change = await SeedMvoChangeAsync(proxyAddressAdds: 3, managerNames: ["Alice Smith"]);

        var counts = await NewJim().Metaverse.GetMvoChangeValueOperationCountsAsync([change.ChangeId]);

        Assert.That(counts, Is.EquivalentTo(new[]
        {
            new ChangeValueOperationCount(change.ChangeId, ChangeValueOperation.Add, 3),
            new ChangeValueOperationCount(change.ChangeId, ChangeValueOperation.Set, 1)
        }));
    }

    // ─── Helpers ───

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    private static ChangeValueRowQuery Query(Guid changeId, int offset, int count, bool includeTotalCount = false) => new()
    {
        ChangeIds = [changeId],
        Offset = offset,
        Count = count,
        IncludeTotalCount = includeTotalCount
    };

    /// <summary>An id that sorts by <paramref name="ordinal"/>, so a test can say which rows a window holds.</summary>
    private static Guid IdFor(int ordinal) => new($"00000000-0000-0000-0000-{ordinal:D12}");

    private sealed record SeededChange(Guid ChangeId, Guid? MetaverseObjectId, CsoSchema? Schema);

    private sealed record CsoSchema(int ConnectedSystemId, int TypeId, int MemberAttributeId, int DescriptionAttributeId, int DisplayNameAttributeId, Guid OwnerId);

    /// <summary>
    /// Seeds one Connected System Object change on a group: member Adds and Removes (plain strings, or references to
    /// named groups), and a single-valued description Add or replacement. Value change ids follow
    /// <paramref name="idBase"/> + 1, 2, ... in that order, so source order is predictable.
    /// </summary>
    private async Task<SeededChange> SeedCsoChangeAsync(
        int memberAdds = 0,
        int memberRemoves = 0,
        IReadOnlyList<string>? memberReferenceNames = null,
        string? descriptionAdd = null,
        (string Old, string New)? descriptionReplacement = null,
        int idBase = 0,
        SeededChange? reuseSchemaFrom = null)
    {
        await using var ctx = NewContext();
        var schema = reuseSchemaFrom?.Schema ?? await SeedCsoSchemaAsync();

        var change = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = schema.ConnectedSystemId,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Updated,
            InitiatedByType = ActivityInitiatorType.System
        };
        ctx.Add(change);
        ctx.Entry(change).Property("ConnectedSystemObjectId").CurrentValue = schema.OwnerId;

        var ordinal = idBase;
        var member = new ConnectedSystemObjectChangeAttribute
        {
            AttributeName = "member", AttributeType = AttributeDataType.Reference, ConnectedSystemChange = change
        };
        ctx.Entry(member).Property("AttributeId").CurrentValue = schema.MemberAttributeId;
        for (var i = 0; i < memberAdds; i++)
            member.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(member, ValueChangeType.Add, $"CN=Member {i:D3}") { Id = IdFor(++ordinal) });
        for (var i = 0; i < memberRemoves; i++)
            member.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(member, ValueChangeType.Remove, $"CN=Former {i:D3}") { Id = IdFor(++ordinal) });
        foreach (var name in memberReferenceNames ?? [])
        {
            var referenced = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(), ConnectedSystemId = schema.ConnectedSystemId, TypeId = schema.TypeId, Created = DateTime.UtcNow
            };
            referenced.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                ConnectedSystemObject = referenced, AttributeId = schema.DisplayNameAttributeId, StringValue = name
            });
            ctx.Add(referenced);
            member.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(member, ValueChangeType.Add, referenced) { Id = IdFor(++ordinal) });
        }
        if (member.ValueChanges.Count > 0)
            change.AttributeChanges.Add(member);

        if (descriptionAdd != null || descriptionReplacement != null)
        {
            var description = new ConnectedSystemObjectChangeAttribute
            {
                AttributeName = "description", AttributeType = AttributeDataType.Text, ConnectedSystemChange = change
            };
            ctx.Entry(description).Property("AttributeId").CurrentValue = schema.DescriptionAttributeId;
            if (descriptionAdd != null)
                description.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(description, ValueChangeType.Add, descriptionAdd) { Id = IdFor(++ordinal) });
            if (descriptionReplacement is { } replacement)
            {
                description.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(description, ValueChangeType.Add, replacement.New) { Id = IdFor(++ordinal) });
                description.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(description, ValueChangeType.Remove, replacement.Old) { Id = IdFor(++ordinal) });
            }
            change.AttributeChanges.Add(description);
        }

        await ctx.SaveChangesAsync();
        return new SeededChange(change.Id, null, schema);
    }

    private async Task<CsoSchema> SeedCsoSchemaAsync()
    {
        await using var ctx = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
        ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType dataType, AttributePlurality plurality) => new()
        {
            Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = plurality, Selected = true
        };
        var member = Attribute("member", AttributeDataType.Reference, AttributePlurality.MultiValued);
        var description = Attribute("description", AttributeDataType.Text, AttributePlurality.SingleValued);
        var displayName = Attribute("displayName", AttributeDataType.Text, AttributePlurality.SingleValued);
        type.Attributes.AddRange([member, description, displayName]);
        ctx.AddRange(connectorDefinition, system, type);
        await ctx.SaveChangesAsync();

        var owner = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = system.Id, TypeId = type.Id, Created = DateTime.UtcNow };
        ctx.Add(owner);
        await ctx.SaveChangesAsync();

        return new CsoSchema(system.Id, type.Id, member.Id, description.Id, displayName.Id, owner.Id);
    }

    /// <summary>
    /// Seeds one Metaverse Object change on a Person: proxy address Adds, and a single-valued manager reference Add
    /// for the first of <paramref name="managerNames"/> (each further name is a person the test can search past).
    /// Value change ids follow 1, 2, ... in that order.
    /// </summary>
    private async Task<SeededChange> SeedMvoChangeAsync(int proxyAddressAdds, IReadOnlyList<string>? managerNames = null)
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var proxyAddresses = new MetaverseAttribute { Name = "proxyAddresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var manager = new MetaverseAttribute { Name = "manager", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued };
        ctx.MetaverseObjectTypes.Add(type);
        ctx.MetaverseAttributes.AddRange(proxyAddresses, manager);
        var subject = new MetaverseObject { Id = Guid.NewGuid(), Type = type, Origin = MetaverseObjectOrigin.Projected, CachedDisplayName = "Dan Ward" };
        ctx.MetaverseObjects.Add(subject);

        var change = new MetaverseObjectChange
        {
            MetaverseObject = subject,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Updated,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule
        };
        var ordinal = 0;
        var proxies = new MetaverseObjectChangeAttribute
        {
            Attribute = proxyAddresses, AttributeName = proxyAddresses.Name, AttributeType = AttributeDataType.Text, MetaverseObjectChange = change
        };
        for (var i = 0; i < proxyAddressAdds; i++)
            proxies.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(proxies, ValueChangeType.Add, $"smtp:dan{i:D2}@example.com") { Id = IdFor(++ordinal) });
        if (proxies.ValueChanges.Count > 0)
            change.AttributeChanges.Add(proxies);

        var people = (managerNames ?? []).Select(name => new MetaverseObject
        {
            Id = Guid.NewGuid(), Type = type, Origin = MetaverseObjectOrigin.Projected, CachedDisplayName = name
        }).ToList();
        ctx.MetaverseObjects.AddRange(people);
        if (people.Count > 0)
        {
            var managerChange = new MetaverseObjectChangeAttribute
            {
                Attribute = manager, AttributeName = manager.Name, AttributeType = AttributeDataType.Reference, MetaverseObjectChange = change
            };
            managerChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(managerChange, ValueChangeType.Add, people[0]) { Id = IdFor(++ordinal) });
            change.AttributeChanges.Add(managerChange);
        }

        ctx.MetaverseObjectChanges.Add(change);
        await ctx.SaveChangesAsync();
        return new SeededChange(change.Id, subject.Id, null);
    }
}
