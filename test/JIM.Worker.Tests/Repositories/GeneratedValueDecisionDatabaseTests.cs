// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for the generated value decision reads and actions (Unique Value Generation, #242, release 4,
/// Phase 9): the header projection's joins (the in-memory provider cannot run them, and a missing join silently empties a
/// name), the status and filter predicates, the ordering, the grouped counts, the round trip of the two columns the phase
/// adds, and the actions through the facade on a <c>NoTracking</c> context, as JIM.Web runs them (src/CLAUDE.md,
/// "Mutating Repository Methods Must Assert They Got a Tracked Entity").
/// <para>
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables, as every <c>RequiresPostgres</c> fixture is. Each test
/// seeds its own estate and reads it back by id or by its own flows, so tests never see each other's rows.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class GeneratedValueDecisionDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL generated value decision tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    private static SyncRepository NewSyncRepository(JimDbContext ctx) => new(new PostgresDataRepository(ctx));

    // ---- Projection, status and ordering ----

    [Test]
    public async Task GetGeneratedValueDecisionHeadersAsync_ImportModeHeldValue_NamesEveryJoinAsync()
    {
        var estate = await SeedEstateAsync();
        var since = DateTime.UtcNow.AddHours(-2);
        var held = await AddImportAssignmentAsync(estate, estate.RitaId, "r.okafor", a =>
            Hold(a, since, estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.AnchoredElsewhere, estate.AnchoringSystemId));

        var row = (await ReadAsync(new GeneratedValueDecisionQuery { Ids = [held] })).Results.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Status, Is.EqualTo(GeneratedValueDecisionStatus.NeedsDecision));
            Assert.That(row.MetaverseObjectId, Is.EqualTo(estate.RitaId));
            Assert.That(row.MetaverseObjectDisplayName, Is.EqualTo("Rita Okafor"));
            Assert.That(row.MetaverseObjectTypeName, Is.EqualTo(estate.TypeName));
            Assert.That(row.MetaverseObjectTypePluralName, Is.EqualTo(estate.TypePluralName));
            Assert.That(row.ConnectedSystemObjectId, Is.Null);
            Assert.That(row.ConnectedSystemObjectConnectedSystemId, Is.Null);
            Assert.That(row.AttributeName, Is.EqualTo(estate.MvAttributeName));
            Assert.That(row.Value, Is.EqualTo("r.okafor"));
            Assert.That(row.Reason, Is.EqualTo(GeneratedValueNeedsDecisionReason.AnchoredElsewhere));
            Assert.That(row.RemediationCount, Is.EqualTo(0));
            Assert.That(row.RejectedByConnectedSystemId, Is.EqualTo(estate.RejectingSystemId));
            Assert.That(row.RejectedByConnectedSystemName, Is.EqualTo(estate.RejectingSystemName));
            Assert.That(row.AnchoredByConnectedSystemId, Is.EqualTo(estate.AnchoringSystemId));
            Assert.That(row.AnchoredByConnectedSystemName, Is.EqualTo(estate.AnchoringSystemName));
            Assert.That(row.Since, Is.EqualTo(since).Within(TimeSpan.FromSeconds(1)));
            Assert.That(row.RenameAllowedAt, Is.Null);
            Assert.That(row.RenameAllowedBy, Is.Null);
            Assert.That(row.SyncRuleId, Is.EqualTo(estate.ImportRuleId));
            Assert.That(row.SyncRuleName, Is.EqualTo(estate.ImportRuleName));
            Assert.That(row.SyncRuleMappingId, Is.EqualTo(estate.ImportMappingId));
        }
    }

    [Test]
    public async Task GetGeneratedValueDecisionHeadersAsync_ExportModeHeldValue_NamesTheAccountAndItsJoinedObjectAsync()
    {
        var estate = await SeedEstateAsync();
        var held = await AddExportAssignmentAsync(estate, "plee", a =>
            Hold(a, DateTime.UtcNow, estate.ExportSystemId, GeneratedValueNeedsDecisionReason.NoValueAvailable, anchoredBy: null));

        var row = (await ReadAsync(new GeneratedValueDecisionQuery { Ids = [held] })).Results.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.ConnectedSystemObjectId, Is.EqualTo(estate.CsoId));
            Assert.That(row.ConnectedSystemObjectConnectedSystemId, Is.EqualTo(estate.ExportSystemId));
            Assert.That(row.MetaverseObjectId, Is.EqualTo(estate.PatId));
            Assert.That(row.MetaverseObjectDisplayName, Is.EqualTo("Pat Lee"));
            Assert.That(row.MetaverseObjectTypePluralName, Is.EqualTo(estate.TypePluralName));
            Assert.That(row.AttributeName, Is.EqualTo("loginName"));
            Assert.That(row.Reason, Is.EqualTo(GeneratedValueNeedsDecisionReason.NoValueAvailable));
            Assert.That(row.AnchoredByConnectedSystemName, Is.Null);
            Assert.That(row.SyncRuleId, Is.EqualTo(estate.ExportRuleId));
        }
    }

    [Test]
    public async Task GetGeneratedValueDecisionHeadersAsync_DeletedRejectingSystem_KeepsTheIdAndLosesOnlyTheNameAsync()
    {
        var estate = await SeedEstateAsync();
        var held = await AddImportAssignmentAsync(estate, estate.RitaId, "r.okafor", a =>
            Hold(a, DateTime.UtcNow, rejectedBy: 987654321, GeneratedValueNeedsDecisionReason.RemediationLimitReached, anchoredBy: null));

        var row = (await ReadAsync(new GeneratedValueDecisionQuery { Ids = [held] })).Results.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.RejectedByConnectedSystemId, Is.EqualTo(987654321));
            Assert.That(row.RejectedByConnectedSystemName, Is.Null);
        }
    }

    [Test]
    public async Task GetGeneratedValueDecisionHeadersAsync_StatusesAndOrdering_MatchTheAssignmentsStateAsync()
    {
        var estate = await SeedEstateAsync();
        var now = DateTime.UtcNow;
        var older = await AddImportAssignmentAsync(estate, estate.RitaId, "older", a => Hold(a, now.AddDays(-3), estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.CannotTell, estate.AnchoringSystemId));
        var newer = await AddImportAssignmentAsync(estate, estate.SamId, "newer", a => Hold(a, now.AddHours(-1), estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.AnchoredElsewhere, estate.AnchoringSystemId));
        var allowed = await AddImportAssignmentAsync(estate, estate.LeeId, "allowed", a =>
        {
            a.State = GeneratedValueAssignmentState.Committed;
            a.NeedsDecisionEnteredAt = now.AddDays(-4);
            a.RenameAuthorised = true;
            a.RenameAuthorisedAt = now.AddMinutes(-10);
            a.RenameAuthorisedByName = "Jay";
        });
        var released = await AddImportAssignmentAsync(estate, estate.PatId, "released", _ => { });
        var generations = new[] { estate.ImportGenerationId };

        var listed = await ReadAsync(new GeneratedValueDecisionQuery { GenerationIds = generations });
        var needsDecision = await ReadAsync(new GeneratedValueDecisionQuery { GenerationIds = generations, Status = GeneratedValueDecisionStatus.NeedsDecision });
        var renameAllowed = await ReadAsync(new GeneratedValueDecisionQuery { GenerationIds = generations, Status = GeneratedValueDecisionStatus.RenameAllowed });
        var byIdReleased = await ReadAsync(new GeneratedValueDecisionQuery { Ids = [released], IncludeReleased = true });
        var byIdDefault = await ReadAsync(new GeneratedValueDecisionQuery { Ids = [released] });
        var window = await ReadAsync(new GeneratedValueDecisionQuery { GenerationIds = generations }, offset: 1, count: 1, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(listed.Results.Select(r => r.AssignmentId), Is.EqualTo(new[] { newer, older, allowed }), "newest wait first");
            Assert.That(listed.TotalResults, Is.EqualTo(3));
            Assert.That(needsDecision.Results.Select(r => r.AssignmentId), Is.EqualTo(new[] { newer, older }));
            Assert.That(renameAllowed.Results.Single().AssignmentId, Is.EqualTo(allowed));
            Assert.That(renameAllowed.Results.Single().RenameAllowedBy, Is.EqualTo("Jay"));
            Assert.That(renameAllowed.Results.Single().RenameAllowedAt, Is.Not.Null);
            Assert.That(byIdReleased.Results.Single().Status, Is.EqualTo(GeneratedValueDecisionStatus.Released));
            Assert.That(byIdDefault.Results, Is.Empty, "a list never shows a value nothing is waiting on");
            Assert.That(window.Results.Select(r => r.AssignmentId), Is.EqualTo(new[] { older }));
            Assert.That(window.TotalResults, Is.Null);
        }
    }

    // ---- Filters ----

    [Test]
    public async Task GetGeneratedValueDecisionHeadersAsync_ConnectedSystem_MatchesRejectingAnchoringOrParticipatingAsync()
    {
        var estate = await SeedEstateAsync();
        var rejected = await AddImportAssignmentAsync(estate, estate.RitaId, "rejected", a => Hold(a, DateTime.UtcNow, estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.RemediationLimitReached, null));
        var anchored = await AddImportAssignmentAsync(estate, estate.SamId, "anchored", a => Hold(a, DateTime.UtcNow, estate.ExportSystemId, GeneratedValueNeedsDecisionReason.AnchoredElsewhere, estate.RejectingSystemId));
        var neither = await AddImportAssignmentAsync(estate, estate.LeeId, "neither", a => Hold(a, DateTime.UtcNow, estate.ExportSystemId, GeneratedValueNeedsDecisionReason.RemediationLimitReached, null));

        var withoutParticipation = await ReadAsync(new GeneratedValueDecisionQuery { GenerationIds = [estate.ImportGenerationId], ConnectedSystemId = estate.RejectingSystemId });
        var withParticipation = await ReadAsync(new GeneratedValueDecisionQuery
        {
            GenerationIds = [estate.ImportGenerationId],
            ConnectedSystemId = estate.RejectingSystemId,
            GenerationIdsParticipatingInConnectedSystem = [estate.ImportGenerationId]
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withoutParticipation.Results.Select(r => r.AssignmentId), Is.EquivalentTo(new[] { rejected, anchored }));
            Assert.That(withParticipation.Results.Select(r => r.AssignmentId), Is.EquivalentTo(new[] { rejected, anchored, neither }));
        }
    }

    [Test]
    public async Task GetGeneratedValueDecisionHeadersAsync_MetaverseObject_MatchesItsValuesAndItsJoinedAccountsValuesAsync()
    {
        var estate = await SeedEstateAsync();
        var own = await AddImportAssignmentAsync(estate, estate.PatId, "pat.lee", a => Hold(a, DateTime.UtcNow, estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.CannotTell, estate.AnchoringSystemId));
        var account = await AddExportAssignmentAsync(estate, "plee", a => Hold(a, DateTime.UtcNow.AddMinutes(-1), estate.ExportSystemId, GeneratedValueNeedsDecisionReason.NoValueAvailable, null));
        await AddImportAssignmentAsync(estate, estate.RitaId, "someone.else", a => Hold(a, DateTime.UtcNow, estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.CannotTell, null));

        var pat = await ReadAsync(new GeneratedValueDecisionQuery { MetaverseObjectId = estate.PatId });

        Assert.That(pat.Results.Select(r => r.AssignmentId), Is.EqualTo(new[] { own, account }));
    }

    [Test]
    public async Task GetGeneratedValueDecisionIdsAsync_ReturnsEveryMatchInListOrderAsync()
    {
        var estate = await SeedEstateAsync();
        var older = await AddImportAssignmentAsync(estate, estate.RitaId, "a", a => Hold(a, DateTime.UtcNow.AddDays(-1), estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.CannotTell, null));
        var newer = await AddImportAssignmentAsync(estate, estate.SamId, "b", a => Hold(a, DateTime.UtcNow, estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.CannotTell, null));

        await using var ctx = NewContext();
        var ids = await NewSyncRepository(ctx).GetGeneratedValueDecisionIdsAsync(new GeneratedValueDecisionQuery { GenerationIds = [estate.ImportGenerationId] });

        Assert.That(ids, Is.EqualTo(new[] { newer, older }));
    }

    // ---- Counts ----

    [Test]
    public async Task GetGeneratedValueDecisionCountsAsync_GroupsByFlowAndSystemsAndCountsEachKindAsync()
    {
        var estate = await SeedEstateAsync();
        var now = DateTime.UtcNow;
        await AddImportAssignmentAsync(estate, estate.RitaId, "held1", a => Hold(a, now, estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.AnchoredElsewhere, estate.AnchoringSystemId));
        await AddImportAssignmentAsync(estate, estate.SamId, "held2", a => Hold(a, now, estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.AnchoredElsewhere, estate.AnchoringSystemId));
        await AddImportAssignmentAsync(estate, estate.LeeId, "allowed", a =>
        {
            a.RejectedByConnectedSystemId = estate.RejectingSystemId;
            a.RenameAuthorised = true;
        });
        await AddImportAssignmentAsync(estate, estate.PatId, "corrected", a =>
        {
            a.State = GeneratedValueAssignmentState.Remediated;
            a.RejectedByConnectedSystemId = estate.RejectingSystemId;
            a.RemediatedAt = now.AddDays(-2);
        });
        await AddExportAssignmentAsync(estate, "long.ago", a =>
        {
            a.State = GeneratedValueAssignmentState.Remediated;
            a.RejectedByConnectedSystemId = estate.ExportSystemId;
            a.RemediatedAt = now.AddDays(-30);
        });

        await using var ctx = NewContext();
        var counts = (await NewSyncRepository(ctx).GetGeneratedValueDecisionCountsAsync(now.AddDays(-7)))
            .Where(c => c.GenerationId == estate.ImportGenerationId || c.GenerationId == estate.ExportGenerationId)
            .ToList();

        var anchoredGroup = counts.Single(c => c.AnchoredByConnectedSystemId == estate.AnchoringSystemId);
        var rejectedOnlyGroup = counts.Single(c => c.GenerationId == estate.ImportGenerationId && c.AnchoredByConnectedSystemId == null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts, Has.Count.EqualTo(2), "a correction outside the window is not read at all");
            Assert.That(anchoredGroup.NeedsDecisionCount, Is.EqualTo(2));
            Assert.That(anchoredGroup.RejectedByConnectedSystemId, Is.EqualTo(estate.RejectingSystemId));
            Assert.That(rejectedOnlyGroup.RenameAllowedCount, Is.EqualTo(1));
            Assert.That(rejectedOnlyGroup.CorrectedCount, Is.EqualTo(1));
            Assert.That(rejectedOnlyGroup.NeedsDecisionCount, Is.Zero);
        }
    }

    // ---- Round trip and the actions on a NoTracking context ----

    [Test]
    public async Task UpdateGeneratedValueAssignmentAsync_DecisionDetail_RoundTripsAsync()
    {
        var estate = await SeedEstateAsync();
        var id = await AddImportAssignmentAsync(estate, estate.RitaId, "r.okafor", _ => { });
        var remediatedAt = DateTime.UtcNow.AddDays(-1);

        await using (var ctx = NewContext())
        {
            var assignment = (await NewSyncRepository(ctx).GetGeneratedValueAssignmentByIdAsync(id))!;
            assignment.NeedsDecisionReason = GeneratedValueNeedsDecisionReason.CannotTell;
            assignment.RemediatedAt = remediatedAt;
            await NewSyncRepository(ctx).UpdateGeneratedValueAssignmentAsync(assignment);
        }

        await using var check = NewContext();
        var stored = await check.GeneratedValueAssignments.SingleAsync(a => a.Id == id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.NeedsDecisionReason, Is.EqualTo(GeneratedValueNeedsDecisionReason.CannotTell));
            Assert.That(stored.RemediatedAt, Is.EqualTo(remediatedAt).Within(TimeSpan.FromSeconds(1)));
        }
    }

    [Test]
    public async Task AllowRenameAsync_OnANoTrackingContext_AuthorisesReleasesTheParkedExportAndRecordsTheActivityAsync()
    {
        var estate = await SeedEstateAsync();
        var id = await AddImportAssignmentAsync(estate, estate.RitaId, "r.okafor", a =>
            Hold(a, DateTime.UtcNow, estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.AnchoredElsewhere, estate.AnchoringSystemId));
        var parkedExportId = await AddParkedExportAsync(estate.RejectingSystemId, estate.RitaId);
        var apiKey = await AddApiKeyAsync();

        GeneratedValueDecisionActionOutcome outcome;
        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx), syncRepository: NewSyncRepository(ctx));
            outcome = await jim.GeneratedValueDecisions.AllowRenameAsync(id, null, apiKey);
        }

        await using var check = NewContext();
        var stored = await check.GeneratedValueAssignments.SingleAsync(a => a.Id == id);
        var export = await check.PendingExports.SingleAsync(pe => pe.Id == parkedExportId);
        var activity = await check.Activities.SingleAsync(a => a.MetaverseObjectId == estate.RitaId && a.TargetOperationType == ActivityTargetOperationType.AllowGeneratedValueRename);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome, Is.EqualTo(GeneratedValueDecisionActionOutcome.Done));
            Assert.That(stored.State, Is.EqualTo(GeneratedValueAssignmentState.Committed));
            Assert.That(stored.RenameAuthorised, Is.True);
            Assert.That(stored.RenameAuthorisedByName, Is.EqualTo(apiKey.Name));
            Assert.That(stored.NeedsDecisionReason, Is.EqualTo(GeneratedValueNeedsDecisionReason.AnchoredElsewhere), "the allowed rename still says what was decided about");
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Pending));
            Assert.That(activity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(activity.InitiatedById, Is.EqualTo(apiKey.Id));
            Assert.That(activity.SyncRuleId, Is.EqualTo(estate.ImportRuleId));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
        }
    }

    [Test]
    public async Task TryAgainAsync_FilterOnANoTrackingContext_ReleasesEveryHeldValueOfTheRuleAsync()
    {
        var estate = await SeedEstateAsync();
        var first = await AddImportAssignmentAsync(estate, estate.RitaId, "one", a => Hold(a, DateTime.UtcNow, estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.CannotTell, estate.AnchoringSystemId));
        var second = await AddImportAssignmentAsync(estate, estate.SamId, "two", a => Hold(a, DateTime.UtcNow, estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.CannotTell, estate.AnchoringSystemId));
        var apiKey = await AddApiKeyAsync();

        int released;
        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx), syncRepository: NewSyncRepository(ctx));
            released = await jim.GeneratedValueDecisions.TryAgainAsync(new GeneratedValueDecisionFilter { SyncRuleId = estate.ImportRuleId }, null, apiKey);
        }

        await using var check = NewContext();
        var states = await check.GeneratedValueAssignments.Where(a => a.Id == first || a.Id == second).Select(a => new { a.State, a.NeedsDecisionReason }).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.EqualTo(2));
            Assert.That(states.Select(s => s.State), Is.All.EqualTo(GeneratedValueAssignmentState.Committed));
            Assert.That(states.Select(s => s.NeedsDecisionReason), Is.All.Null);
        }
    }

    [Test]
    public async Task ClearRenameAuthorisationsAfterSuccessfulExportAsync_ClearsOnlyTheRejectingSystemsAllowancesForTheExportedObjectsAsync()
    {
        var estate = await SeedEstateAsync();
        void Allowed(GeneratedValueAssignment a, int rejectedBy)
        {
            Hold(a, DateTime.UtcNow.AddHours(-1), rejectedBy, GeneratedValueNeedsDecisionReason.AnchoredElsewhere, estate.AnchoringSystemId);
            a.State = GeneratedValueAssignmentState.Committed;
            a.RenameAuthorised = true;
            a.RenameAuthorisedAt = DateTime.UtcNow;
            a.RenameAuthorisedByName = "Ada Admin";
        }
        var rita = await AddImportAssignmentAsync(estate, estate.RitaId, "r.okafor", a => Allowed(a, estate.RejectingSystemId));
        var otherSystem = await AddImportAssignmentAsync(estate, estate.SamId, "s.adeyemi", a => Allowed(a, estate.AnchoringSystemId));
        var notExported = await AddImportAssignmentAsync(estate, estate.LeeId, "l.chen", a => Allowed(a, estate.RejectingSystemId));
        var exportMode = await AddExportAssignmentAsync(estate, "plee", a => Allowed(a, estate.ExportSystemId));
        var stillHeld = await AddImportAssignmentAsync(estate, estate.PatId, "p.lee", a =>
            Hold(a, DateTime.UtcNow, estate.RejectingSystemId, GeneratedValueNeedsDecisionReason.CannotTell, estate.AnchoringSystemId));

        int clearedImport, clearedExport;
        await using (var ctx = NewContext())
        {
            var repo = NewSyncRepository(ctx);
            clearedImport = await repo.ClearRenameAuthorisationsAfterSuccessfulExportAsync(estate.RejectingSystemId, [estate.RitaId, estate.SamId, estate.PatId], []);
            clearedExport = await repo.ClearRenameAuthorisationsAfterSuccessfulExportAsync(estate.ExportSystemId, [], [estate.CsoId]);
        }

        await using var check = NewContext();
        var stored = await check.GeneratedValueAssignments.Where(a => new[] { rita, otherSystem, notExported, exportMode, stillHeld }.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clearedImport, Is.EqualTo(1));
            Assert.That(clearedExport, Is.EqualTo(1));
            Assert.That(stored[rita].RenameAuthorised, Is.False);
            Assert.That(stored[rita].RenameAuthorisedAt, Is.Null);
            Assert.That(stored[rita].RenameAuthorisedByName, Is.Null);
            Assert.That(stored[rita].State, Is.EqualTo(GeneratedValueAssignmentState.Committed));
            Assert.That(stored[rita].NeedsDecisionReason, Is.Null);
            Assert.That(stored[rita].NeedsDecisionEnteredAt, Is.Null);
            Assert.That(stored[rita].AnchoredByConnectedSystemId, Is.Null);
            Assert.That(stored[otherSystem].RenameAuthorised, Is.True, "a different system's rejection is not answered by this export");
            Assert.That(stored[notExported].RenameAuthorised, Is.True, "an object this batch did not export keeps its allowance");
            Assert.That(stored[exportMode].RenameAuthorised, Is.False, "an export-mode allowance clears on its own account's export");
            Assert.That(stored[stillHeld].State, Is.EqualTo(GeneratedValueAssignmentState.NeedsDecision), "a held value is left for its decision");
        }
    }

    // ---- Estate ----

    private sealed record Estate(
        string TypeName, string TypePluralName, string MvAttributeName,
        int RejectingSystemId, string RejectingSystemName, int AnchoringSystemId, string AnchoringSystemName, int ExportSystemId,
        int ImportRuleId, string ImportRuleName, int ImportMappingId, int ImportGenerationId, int MvAttributeId,
        int ExportRuleId, int ExportGenerationId, int CsAttributeId, Guid CsoId,
        Guid RitaId, Guid SamId, Guid LeeId, Guid PatId);

    private async Task<Estate> SeedEstateAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = NewContext();

        var mvoType = new MetaverseObjectType { Name = $"Person-{suffix}", PluralName = $"People-{suffix}" };
        var accountName = new MetaverseAttribute { Name = $"Account Name-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        ctx.MetaverseObjectTypes.Add(mvoType);
        ctx.MetaverseAttributes.Add(accountName);

        var definition = new ConnectorDefinition { Name = $"def-{suffix}" };
        var source = new ConnectedSystem { Name = $"HR-{suffix}", ConnectorDefinition = definition };
        var rejecting = new ConnectedSystem { Name = $"Contractor LDAP-{suffix}", ConnectorDefinition = definition };
        var anchoring = new ConnectedSystem { Name = $"Corporate AD-{suffix}", ConnectorDefinition = definition };
        var exportSystem = new ConnectedSystem { Name = $"Ticketing-{suffix}", ConnectorDefinition = definition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = exportSystem, Selected = true };
        var loginName = new ConnectedSystemObjectTypeAttribute
        {
            Name = "loginName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(loginName);
        var sourceType = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = source, Selected = true };
        ctx.AddRange(definition, source, rejecting, anchoring, exportSystem, csType, sourceType);
        await ctx.SaveChangesAsync();

        MetaverseObject Person(string name) => new() { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow, CachedDisplayName = name };
        var rita = Person("Rita Okafor");
        var sam = Person("Sam Adeyemi");
        var lee = Person("Lee Chen");
        var pat = Person("Pat Lee");
        ctx.MetaverseObjects.AddRange(rita, sam, lee, pat);
        var cso = new ConnectedSystemObject
        {
            Type = csType, ConnectedSystem = exportSystem, Status = ConnectedSystemObjectStatus.Normal, MetaverseObjectId = pat.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned, DateJoined = DateTime.UtcNow, ExternalIdAttributeId = loginName.Id
        };
        ctx.ConnectedSystemObjects.Add(cso);
        await ctx.SaveChangesAsync();

        var importRule = new SyncRule
        {
            Name = $"HR Import-{suffix}", Direction = SyncRuleDirection.Import, ConnectedSystemId = source.Id,
            ConnectedSystemObjectTypeId = sourceType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        var exportRule = new SyncRule
        {
            Name = $"Ticketing Export-{suffix}", Direction = SyncRuleDirection.Export, ConnectedSystemId = exportSystem.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        ctx.SyncRules.AddRange(importRule, exportRule);
        await ctx.SaveChangesAsync();

        var importMapping = new SyncRuleMapping { SyncRuleId = importRule.Id, TargetMetaverseAttributeId = accountName.Id };
        var exportMapping = new SyncRuleMapping { SyncRuleId = exportRule.Id, TargetConnectedSystemAttributeId = loginName.Id };
        ctx.SyncRuleMappings.AddRange(importMapping, exportMapping);
        await ctx.SaveChangesAsync();

        var importGeneration = new SyncRuleMappingGeneration { SyncRuleMappingId = importMapping.Id };
        var exportGeneration = new SyncRuleMappingGeneration { SyncRuleMappingId = exportMapping.Id };
        ctx.SyncRuleMappingGenerations.AddRange(importGeneration, exportGeneration);
        await ctx.SaveChangesAsync();

        return new Estate(mvoType.Name, mvoType.PluralName, accountName.Name,
            rejecting.Id, rejecting.Name, anchoring.Id, anchoring.Name, exportSystem.Id,
            importRule.Id, importRule.Name, importMapping.Id, importGeneration.Id, accountName.Id,
            exportRule.Id, exportGeneration.Id, loginName.Id, cso.Id,
            rita.Id, sam.Id, lee.Id, pat.Id);
    }

    private async Task<Guid> AddImportAssignmentAsync(Estate estate, Guid metaverseObjectId, string value, Action<GeneratedValueAssignment> shape)
    {
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = metaverseObjectId, MetaverseAttributeId = estate.MvAttributeId, Value = value,
            NormalisedValue = value.ToLowerInvariant(), SyncRuleMappingGenerationId = estate.ImportGenerationId,
            State = GeneratedValueAssignmentState.Committed, CommittedAt = DateTime.UtcNow
        };
        shape(assignment);
        await using var ctx = NewContext();
        ctx.GeneratedValueAssignments.Add(assignment);
        await ctx.SaveChangesAsync();
        return assignment.Id;
    }

    private async Task<Guid> AddExportAssignmentAsync(Estate estate, string value, Action<GeneratedValueAssignment> shape)
    {
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), ConnectedSystemObjectId = estate.CsoId, ConnectedSystemObjectTypeAttributeId = estate.CsAttributeId, Value = value,
            NormalisedValue = value.ToLowerInvariant(), SyncRuleMappingGenerationId = estate.ExportGenerationId,
            State = GeneratedValueAssignmentState.Committed, CommittedAt = DateTime.UtcNow
        };
        shape(assignment);
        await using var ctx = NewContext();
        ctx.GeneratedValueAssignments.Add(assignment);
        await ctx.SaveChangesAsync();
        return assignment.Id;
    }

    private async Task<Guid> AddParkedExportAsync(int connectedSystemId, Guid metaverseObjectId)
    {
        await using var ctx = NewContext();
        var csType = new ConnectedSystemObjectType { Name = $"account-{Guid.NewGuid():N}", ConnectedSystemId = connectedSystemId, Selected = true };
        ctx.ConnectedSystemObjectTypes.Add(csType);
        await ctx.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            TypeId = csType.Id, ConnectedSystemId = connectedSystemId, Status = ConnectedSystemObjectStatus.Normal, MetaverseObjectId = metaverseObjectId,
            JoinType = ConnectedSystemObjectJoinType.Provisioned, DateJoined = DateTime.UtcNow
        };
        ctx.ConnectedSystemObjects.Add(cso);
        await ctx.SaveChangesAsync();

        var export = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = connectedSystemId, ConnectedSystemObjectId = cso.Id, Status = PendingExportStatus.Parked,
            ChangeType = PendingExportChangeType.Update, CreatedAt = DateTime.UtcNow
        };
        ctx.PendingExports.Add(export);
        await ctx.SaveChangesAsync();
        return export.Id;
    }

    private async Task<ApiKey> AddApiKeyAsync()
    {
        await using var ctx = NewContext();
        var key = new ApiKey { Name = $"automation-{Guid.NewGuid():N}"[..20], KeyHash = Guid.NewGuid().ToString("N"), KeyPrefix = "jim_ak_t", Created = DateTime.UtcNow };
        ctx.ApiKeys.Add(key);
        await ctx.SaveChangesAsync();
        return key;
    }

    private async Task<RangeResultSetView> ReadAsync(GeneratedValueDecisionQuery query, int offset = 0, int count = 100, bool includeTotalCount = true)
    {
        await using var ctx = NewContext();
        var result = await NewSyncRepository(ctx).GetGeneratedValueDecisionHeadersAsync(query, offset, count, includeTotalCount);
        return new RangeResultSetView(result.Results, result.TotalResults);
    }

    private sealed record RangeResultSetView(List<GeneratedValueDecisionHeader> Results, int? TotalResults);

    private static void Hold(GeneratedValueAssignment assignment, DateTime since, int rejectedBy, GeneratedValueNeedsDecisionReason reason, int? anchoredBy)
    {
        assignment.State = GeneratedValueAssignmentState.NeedsDecision;
        assignment.NeedsDecisionEnteredAt = since;
        assignment.RejectedByConnectedSystemId = rejectedBy;
        assignment.AnchoredByConnectedSystemId = anchoredBy;
        assignment.NeedsDecisionReason = reason;
    }
}
