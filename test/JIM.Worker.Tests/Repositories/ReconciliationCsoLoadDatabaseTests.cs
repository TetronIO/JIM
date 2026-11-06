// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the Connected System Object load Pending Export reconciliation reads each page with
/// (#2669). Reconciliation compares a reference by its id and its string, never by the object it points at, so the
/// load must not bring every referenced member's Connected System Object along with a group: at Scale500k25kGroups
/// that cost the Target Delta Import 26 s. Runs on a <c>NoTracking</c> context, matching the
/// <c>AsNoTracking</c> load, and opt-in via the <c>JIM_TEST_RESET_*</c> environment variables like the other
/// <c>RequiresPostgres</c> fixtures.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ReconciliationCsoLoadDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL reconciliation load tests.");

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

    private sealed record SeededGroup(
        int ConnectedSystemId,
        Guid GroupCsoId,
        int MemberAttributeId,
        Dictionary<string, Guid> MemberCsoIds,
        Dictionary<string, string> MemberDns,
        Dictionary<Guid, Guid?> MemberRowReferenceIds);

    /// <summary>
    /// A group whose target object holds Alice and Dave as resolved references (each keeping its DN, as resolution
    /// leaves it) and Erin as an unresolved one in a different case; Bob and Carol exist but are not members.
    /// </summary>
    private async Task<SeededGroup> SeedGroupAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband LDAP", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        var dnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, IsSecondaryExternalId = true, Selected = true
        };
        csType.Attributes.Add(memberAttr);
        csType.Attributes.Add(dnAttr);
        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();

        var dns = new Dictionary<string, string>();
        var members = new Dictionary<string, ConnectedSystemObject>();
        foreach (var (name, dn) in new[] { "alice", "bob", "carol", "dave", "erin" }
                     .Select(n => (n, $"uid={n},ou=People,dc=glitterband,dc=local")))
        {
            dns[name] = dn;
            var cso = new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = dnAttr, StringValue = dn
            });
            members[name] = cso;
        }

        var groupCso = new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = groupCso, Attribute = dnAttr,
            StringValue = "cn=Team Alpha,ou=Groups,dc=glitterband,dc=local"
        });
        foreach (var name in new[] { "alice", "dave" })
        {
            groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = groupCso, Attribute = memberAttr,
                ReferenceValue = members[name], UnresolvedReferenceValue = dns[name]
            });
        }
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = groupCso, Attribute = memberAttr,
            UnresolvedReferenceValue = dns["erin"].ToUpperInvariant()
        });

        seed.AddRange(members.Values);
        seed.Add(groupCso);
        await seed.SaveChangesAsync();

        return new SeededGroup(
            system.Id,
            groupCso.Id,
            memberAttr.Id,
            members.ToDictionary(m => m.Key, m => m.Value.Id),
            dns,
            groupCso.AttributeValues.Where(av => av.AttributeId == memberAttr.Id)
                .ToDictionary(av => av.Id, av => av.ReferenceValue?.Id));
    }

    /// <summary>
    /// The reconciliation load returns each object's attribute values with their ids, attribute definitions and the
    /// referenced object's id, but not the referenced objects themselves.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsCoreByIdsNoTrackingAsync_Group_ReturnsReferenceIdsWithoutReferencedObjectsAsync()
    {
        var seeded = await SeedGroupAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var csos = await repository.Sync.GetConnectedSystemObjectsCoreByIdsNoTrackingAsync(seeded.ConnectedSystemId, [seeded.GroupCsoId]);

        Assert.That(csos, Has.Count.EqualTo(1));
        var group = csos[0];
        var memberValues = group.AttributeValues.Where(av => av.AttributeId == seeded.MemberAttributeId).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.AttributeValues, Has.Count.EqualTo(4), "The DN and the three member values");
            Assert.That(group.AttributeValues.Select(av => av.Id), Is.All.Not.EqualTo(Guid.Empty));
            Assert.That(group.AttributeValues.Select(av => av.Attribute), Is.All.Not.Null,
                "Reconciliation compares by the attribute's type and case rules, so its definition must be loaded");
            Assert.That(memberValues.ToDictionary(av => av.Id, av => av.ReferenceValueId),
                Is.EquivalentTo(seeded.MemberRowReferenceIds), "Each reference keeps the id of the object it names");
            Assert.That(memberValues.Select(av => av.ReferenceValue), Is.All.Null,
                "The referenced members' Connected System Objects must not be loaded");
            Assert.That(memberValues.Select(av => av.UnresolvedReferenceValue),
                Is.EquivalentTo(new[] { seeded.MemberDns["alice"], seeded.MemberDns["dave"], seeded.MemberDns["erin"].ToUpperInvariant() }));
            Assert.That(ctx.ChangeTracker.Entries().Count(), Is.Zero, "The load must not track what it reads");
        }
    }

    /// <summary>
    /// Reconciliation over the lighter load reaches the same verdicts as over the full one, for a group with members
    /// added and removed: an added member present and a removed member absent are confirmed (by the referenced
    /// object's id, and an unresolved one by its DN ignoring case); an added member absent and a removed member still
    /// present are not confirmed, and retried.
    /// </summary>
    [Test]
    public async Task ReconcileCsoAgainstPendingExport_OverCoreLoad_SameVerdictsAsFullLoadAsync()
    {
        var seeded = await SeedGroupAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var memberAttribute = await ctx.ConnectedSystemAttributes.SingleAsync(a => a.Id == seeded.MemberAttributeId);

        PendingExportAttributeValueChange Change(string name, PendingExportAttributeChangeType changeType, bool resolved) => new()
        {
            Id = Guid.NewGuid(),
            Attribute = memberAttribute,
            AttributeId = memberAttribute.Id,
            ChangeType = changeType,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ResolvedReferenceCsoId = resolved ? seeded.MemberCsoIds[name] : null,
            StringValue = resolved ? seeded.MemberDns[name] : null,
            UnresolvedReferenceValue = resolved ? null : seeded.MemberDns[name]
        };

        // One set of change ids for both runs, so the verdicts compare change by change.
        var template = new Dictionary<string, PendingExportAttributeValueChange>
        {
            ["add alice"] = Change("alice", PendingExportAttributeChangeType.Add, resolved: true),
            ["add bob"] = Change("bob", PendingExportAttributeChangeType.Add, resolved: true),
            ["remove carol"] = Change("carol", PendingExportAttributeChangeType.Remove, resolved: true),
            ["remove dave"] = Change("dave", PendingExportAttributeChangeType.Remove, resolved: true),
            ["add erin"] = Change("erin", PendingExportAttributeChangeType.Add, resolved: false)
        };

        PendingExport NewPendingExport() => new()
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = seeded.ConnectedSystemId,
            ConnectedSystemObjectId = seeded.GroupCsoId,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Exported,
            AttributeValueChanges = new(template.Values.Select(c => new PendingExportAttributeValueChange
            {
                Id = c.Id, Attribute = c.Attribute, AttributeId = c.AttributeId, ChangeType = c.ChangeType, Status = c.Status,
                ResolvedReferenceCsoId = c.ResolvedReferenceCsoId, StringValue = c.StringValue,
                UnresolvedReferenceValue = c.UnresolvedReferenceValue
            }).ToList())
        };

        var engine = new SyncEngine();
        PendingExportReconciliationResult Reconcile(ConnectedSystemObject cso)
        {
            var result = new PendingExportReconciliationResult();
            engine.ReconcileCsoAgainstPendingExport(cso, NewPendingExport(), result);
            return result;
        }

        var fullLoad = (await repository.Sync.GetConnectedSystemObjectsByIdsNoTrackingAsync(seeded.ConnectedSystemId, [seeded.GroupCsoId])).Single();
        var coreLoad = (await repository.Sync.GetConnectedSystemObjectsCoreByIdsNoTrackingAsync(seeded.ConnectedSystemId, [seeded.GroupCsoId])).Single();

        var overFull = Reconcile(fullLoad);
        var overCore = Reconcile(coreLoad);

        var expectedConfirmed = new[] { template["add alice"].Id, template["remove carol"].Id, template["add erin"].Id };
        var expectedRetried = new[] { template["add bob"].Id, template["remove dave"].Id };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(overCore.ConfirmedChanges.Select(c => c.Id), Is.EquivalentTo(expectedConfirmed), "Confirmed over the Core load");
            Assert.That(overCore.RetryChanges.Select(c => c.Id), Is.EquivalentTo(expectedRetried), "Retried over the Core load");
            Assert.That(overCore.RetryChanges.Select(c => c.Status), Is.All.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));
            Assert.That(overCore.RetryChanges.ToDictionary(c => c.Id, c => c.LastImportedValue),
                Is.EquivalentTo(overFull.RetryChanges.ToDictionary(c => c.Id, c => c.LastImportedValue)),
                "What the import showed is recorded the same way");
            Assert.That(overCore.ConfirmedChanges.Select(c => c.Id), Is.EquivalentTo(overFull.ConfirmedChanges.Select(c => c.Id)));
            Assert.That(overCore.RetryChanges.Select(c => c.Id), Is.EquivalentTo(overFull.RetryChanges.Select(c => c.Id)));
            Assert.That(overCore.PendingExportToDelete, Is.Null);
            Assert.That(overCore.PendingExportToUpdate?.Status, Is.EqualTo(overFull.PendingExportToUpdate?.Status));
            Assert.That(overCore.PendingExportToUpdate?.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
        }
    }
}
