// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Scheduling;
using JIM.Models.Search;
using JIM.Models.Security;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests that JIM's built-in configuration converges on every startup rather than being created once, at first
/// launch, and never again (issue #916).
/// <para>
/// SeedAsync used to stop the moment ServiceSettings existed, so everything it owned (the built-in Metaverse Object
/// Types, Predefined Searches and Example Data Sets) was a first-launch-only creation: add one in a later release
/// and no existing deployment ever received it. A built-in Metaverse Object Type was worse than silent, because the
/// schema sync that runs afterwards throws when the catalogue names an Object Type it cannot find, so adding one
/// would have crashed worker startup everywhere it had not been seeded.
/// </para>
/// <para>
/// Two passes that already ran on every startup had the same fault in miniature: each checked for its single
/// hardcoded built-in and returned, so a second built-in Schedule or Role would never have reached an existing
/// deployment either.
/// </para>
/// </summary>
[TestFixture]
public class BuiltInConfigurationConvergenceTests
{
    private SeedingTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new SeedingTestHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    #region SeedAsync converges against an already-seeded database
    [Test]
    public async Task SeedAsync_PredefinedSearchMissingFromASeededDatabase_CreatesItAsync()
    {
        // A Predefined Search added to the built-in set in a later release. The deployment is fully seeded, which
        // is precisely the state in which the old short-circuit refused to do anything at all.
        _harness.PersistBuiltInConfiguration();
        _harness.PredefinedSearches.Remove("distribution-groups");

        await _harness.Jim.Seeding.SeedAsync();

        Assert.That(_harness.PredefinedSearches.ContainsKey("distribution-groups"), Is.True,
            "a built-in Predefined Search absent from a seeded database must be created, not skipped for the life of the deployment");
    }

    [Test]
    public async Task SeedAsync_ExampleDataSetMissingFromASeededDatabase_CreatesItAsync()
    {
        _harness.PersistBuiltInConfiguration();
        _harness.ExampleDataSets.Remove(Constants.BuiltInExampleDataSets.JobTitles);

        await _harness.Jim.Seeding.SeedAsync();

        Assert.That(_harness.ExampleDataSets.ContainsKey(Constants.BuiltInExampleDataSets.JobTitles), Is.True,
            "a built-in Example Data Set absent from a seeded database must be created");
        Assert.That(_harness.ExampleDataSets[Constants.BuiltInExampleDataSets.JobTitles].Values, Is.Not.Empty,
            "the created set must carry its values");
    }

    [Test]
    public async Task SeedAsync_MetaverseObjectTypeMissingFromASeededDatabase_CreatesItAsync()
    {
        // The worst of the three: SyncBuiltInMetaverseSchemaAsync throws when a catalogue Object Type is missing,
        // so without this convergence a built-in Object Type added in a later release crashes worker startup on
        // every existing deployment rather than merely never arriving.
        _harness.PersistBuiltInConfiguration();
        _harness.ObjectTypes.Remove(Constants.BuiltInObjectTypes.Group);

        await _harness.Jim.Seeding.SeedAsync();

        Assert.That(_harness.ObjectTypes.ContainsKey(Constants.BuiltInObjectTypes.Group), Is.True,
            "a built-in Metaverse Object Type absent from a seeded database must be created");
    }

    [Test]
    public async Task SeedAsync_SeededDatabaseWithNothingMissing_CreatesNothingAsync()
    {
        // Convergence must stay free on the common path: the overwhelming majority of startups have nothing to do,
        // and a pass that re-records Create Activities for objects it did not create would corrupt change history.
        _harness.PersistBuiltInConfiguration();

        await _harness.Jim.Seeding.SeedAsync();

        Assert.That(_harness.LastSeedBatch, Is.Not.Null);
        Assert.That(_harness.LastSeedBatch!.MetaverseAttributes, Is.Empty);
        Assert.That(_harness.LastSeedBatch.MetaverseObjectTypes, Is.Empty);
        Assert.That(_harness.LastSeedBatch.PredefinedSearches, Is.Empty);
        Assert.That(_harness.LastSeedBatch.ExampleDataSets, Is.Empty);
        Assert.That(_harness.LastSeedBatch.ExampleDataTemplates, Is.Empty);
        Assert.That(_harness.CreatedActivities, Is.Empty,
            "a converged pass must not record a System Initialisation Activity");
    }
    #endregion

    #region built-in Schedules are catalogue-driven
    [Test]
    public async Task SeedBuiltInSchedulesAsync_NoneExist_CreatesEveryCatalogueEntryAsync()
    {
        await _harness.Jim.Seeding.SeedBuiltInSchedulesAsync();

        var expected = SeedingServer.BuiltInSchedules().Select(s => s.Name).ToList();
        Assert.That(_harness.CreatedSchedules.Select(s => s.Name), Is.EquivalentTo(expected),
            "every built-in Schedule the catalogue declares must be created");
        Assert.That(_harness.CreatedSchedules.All(s => s.BuiltIn && s.IsEnabled), Is.True);
        Assert.That(_harness.CreatedSchedules.All(s => s.CreatedByType == ActivityInitiatorType.System), Is.True,
            "built-in Schedules must be created through the audited path, attributed to System");
    }

    [Test]
    public async Task SeedBuiltInSchedulesAsync_ADifferentBuiltInScheduleSharesAStepType_StillCreatesTheCatalogueEntryAsync()
    {
        // The old check asked "does any built-in Schedule carry a Temporal Scope Reconciliation step?" and returned
        // if one did. A second built-in Schedule sharing that step type therefore suppressed the first, which is the
        // single-item failure this catalogue exists to remove. Identity is the catalogue entry's name.
        _harness.Schedules.Add(new Schedule
        {
            Id = Guid.NewGuid(),
            Name = "Some Other Built-In Schedule",
            BuiltIn = true,
            Steps = new List<ScheduleStep>
            {
                new() { StepIndex = 0, Name = "Reconcile", StepType = ScheduleStepType.TemporalScopeReconciliation }
            }
        });

        await _harness.Jim.Seeding.SeedBuiltInSchedulesAsync();

        var expected = SeedingServer.BuiltInSchedules().Select(s => s.Name).ToList();
        Assert.That(_harness.CreatedSchedules.Select(s => s.Name), Is.EquivalentTo(expected),
            "each catalogue entry must be matched by its own name, not by whether some other built-in happens to share a step type");
    }

    [Test]
    public async Task SeedBuiltInSchedulesAsync_EveryCatalogueEntryPresent_CreatesNothingAsync()
    {
        foreach (var schedule in SeedingServer.BuiltInSchedules())
        {
            schedule.Id = Guid.NewGuid();
            _harness.Schedules.Add(schedule);
        }

        await _harness.Jim.Seeding.SeedBuiltInSchedulesAsync();

        Assert.That(_harness.CreatedSchedules, Is.Empty, "an existing built-in Schedule must never be re-created");
        Assert.That(_harness.CreatedActivities, Is.Empty, "a converged pass must record no Activities at all");
    }
    #endregion

    #region built-in Roles are catalogue-driven
    [Test]
    public async Task SeedBuiltInRolesAsync_NoneExist_CreatesEveryCatalogueEntryAsync()
    {
        await _harness.Jim.Seeding.SeedBuiltInRolesAsync();

        Assert.That(_harness.CreatedRoles.Select(r => r.Name), Is.EquivalentTo(SeedingServer.BuiltInRoleNames()),
            "every built-in Role the catalogue declares must be created");
        Assert.That(_harness.CreatedRoles.All(r => r.BuiltIn), Is.True);
    }

    [Test]
    public async Task SeedBuiltInRolesAsync_OneCatalogueEntryMissing_CreatesOnlyThatOneAsync()
    {
        // With a single-entry catalogue this and the test above overlap; both are written against the catalogue
        // rather than against "Administrator" so they keep their meaning the day a second built-in Role is added.
        var names = SeedingServer.BuiltInRoleNames().ToList();
        foreach (var name in names.Skip(1))
            _harness.Roles[name] = new Role { Id = 1, Name = name, BuiltIn = true };

        await _harness.Jim.Seeding.SeedBuiltInRolesAsync();

        Assert.That(_harness.CreatedRoles.Select(r => r.Name), Is.EquivalentTo(names.Take(1)));
    }

    [Test]
    public async Task SeedBuiltInRolesAsync_EveryCatalogueEntryPresent_CreatesNothingAsync()
    {
        var nextId = 1;
        foreach (var name in SeedingServer.BuiltInRoleNames())
            _harness.Roles[name] = new Role { Id = nextId++, Name = name, BuiltIn = true };

        await _harness.Jim.Seeding.SeedBuiltInRolesAsync();

        Assert.That(_harness.CreatedRoles, Is.Empty, "an existing built-in Role must never be re-created");
        Assert.That(_harness.CreatedActivities, Is.Empty);
    }
    #endregion

    #region the shared pipeline
    [Test]
    public async Task ApplyBuiltInConfigurationAsync_EmptyDatabase_CreatesTheFullBuiltInConfigurationAsync()
    {
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();

        Assert.That(_harness.ObjectTypes, Is.Not.Empty);
        Assert.That(_harness.Attributes, Is.Not.Empty);
        Assert.That(_harness.PredefinedSearches, Is.Not.Empty);
        Assert.That(_harness.ExampleDataSets, Is.Not.Empty);
        Assert.That(_harness.ExampleDataTemplate, Is.Not.Null);
        Assert.That(_harness.ConnectorDefinitions, Is.Not.Empty);
        Assert.That(_harness.Schedules, Is.Not.Empty);
        Assert.That(_harness.Roles, Is.Not.Empty);
        Assert.That(_harness.ServiceSettings, Is.Not.Null);

        var parent = _harness.UpdatedActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        Assert.That(parent, Is.Not.Null, "the pipeline owns the System Initialisation parent Activity");
        Assert.That(parent!.Status, Is.EqualTo(ActivityStatus.Complete),
            "leaving the parent InProgress would misreport the pass as unfinished and block a later factory reset");
    }

    [Test]
    public async Task ApplyBuiltInConfigurationAsync_RunTwice_TheSecondPassCreatesNothingAsync()
    {
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();
        // Activities are not cleared: they are the harness's configuration change history, which the second pass
        // reads to decide that nothing has changed. Count the delta instead.
        var activitiesAfterFirstPass = _harness.CreatedActivities.Count;
        _harness.CreatedSchedules.Clear();
        _harness.CreatedRoles.Clear();
        _harness.CreatedConnectorDefinitions.Clear();

        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();

        Assert.That(_harness.LastSeedBatch!.MetaverseAttributes, Is.Empty);
        Assert.That(_harness.LastSeedBatch.MetaverseObjectTypes, Is.Empty);
        Assert.That(_harness.LastSeedBatch.PredefinedSearches, Is.Empty);
        Assert.That(_harness.LastSeedBatch.ExampleDataSets, Is.Empty);
        Assert.That(_harness.CreatedSchedules, Is.Empty);
        Assert.That(_harness.CreatedRoles, Is.Empty);
        Assert.That(_harness.CreatedConnectorDefinitions, Is.Empty);
        Assert.That(_harness.CreatedActivities, Has.Count.EqualTo(activitiesAfterFirstPass),
            "running the pipeline against a converged database must record no further Activities; every startup runs it");
    }

    [Test]
    public async Task ApplyBuiltInConfigurationAsync_BuiltInsMissingFromASeededDatabase_RestoresEveryOneAsync()
    {
        // One built-in removed from each category the pipeline owns, which is what a future release's additions
        // look like from an existing deployment's point of view (and what truncate collateral looks like after a
        // factory reset).
        _harness.PersistBuiltInConfiguration();
        _harness.PredefinedSearches.Remove("people");
        _harness.ExampleDataSets.Remove(Constants.BuiltInExampleDataSets.Departments);
        _harness.ObjectTypes.Remove(Constants.BuiltInObjectTypes.Group);
        _harness.Attributes.Remove(Constants.BuiltInAttributes.DisplayName);
        _harness.ExampleDataTemplate = null;
        _harness.Schedules.Clear();
        _harness.Roles.Clear();
        _harness.ConnectorDefinitions.Clear();

        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();

        Assert.That(_harness.PredefinedSearches.ContainsKey("people"), Is.True);
        Assert.That(_harness.ExampleDataSets.ContainsKey(Constants.BuiltInExampleDataSets.Departments), Is.True);
        Assert.That(_harness.ObjectTypes.ContainsKey(Constants.BuiltInObjectTypes.Group), Is.True);
        Assert.That(_harness.Attributes.ContainsKey(Constants.BuiltInAttributes.DisplayName), Is.True);
        Assert.That(_harness.ExampleDataTemplate, Is.Not.Null);
        Assert.That(_harness.Schedules, Is.Not.Empty);
        Assert.That(_harness.Roles, Is.Not.Empty);
        Assert.That(_harness.ConnectorDefinitions, Is.Not.Empty);
    }
    #endregion

    #region convergence-path guard
    [Test]
    public void BuiltInConvergencePaths_EveryEntityTypeCarryingBuiltIn_DeclaresHowItConverges()
    {
        // The guard against a fourth instance of this bug. Any table that can hold a BuiltIn row is configuration
        // JIM ships, so something has to keep it converged on upgrade and restore it after a factory reset. This
        // fails the moment a new one appears without that decision having been made; declaring the entity type in
        // SeedingServer.BuiltInConvergencePaths is how the decision is recorded.
        using var context = NewModelOnlyContext();

        var builtInEntityTypes = context.Model.GetEntityTypes()
            .Select(entityType => entityType.ClrType)
            .Where(clrType => clrType.GetProperty("BuiltIn") != null)
            .Distinct()
            .ToList();

        Assert.That(builtInEntityTypes, Is.Not.Empty, "the model must carry built-in configuration entity types");

        var undeclared = builtInEntityTypes
            .Where(clrType => !SeedingServer.BuiltInConvergencePaths.ContainsKey(clrType))
            .Select(clrType => clrType.Name)
            .OrderBy(name => name)
            .ToList();

        Assert.That(undeclared, Is.Empty,
            $"every entity type carrying BuiltIn must declare a convergence path in SeedingServer.BuiltInConvergencePaths. " +
            $"Undeclared: {string.Join(", ", undeclared)}");

        var stale = SeedingServer.BuiltInConvergencePaths.Keys
            .Where(clrType => builtInEntityTypes.All(t => t != clrType))
            .Select(clrType => clrType.Name)
            .OrderBy(name => name)
            .ToList();

        Assert.That(stale, Is.Empty,
            $"BuiltInConvergencePaths must not name entity types the model no longer has. Stale: {string.Join(", ", stale)}");
    }

    /// <summary>
    /// A context built purely to read the EF model. It never opens a connection, so no database is needed; the
    /// pending-model-changes warning is suppressed for the same reason JimDbContext.OnConfiguring suppresses it
    /// (the production model carries deliberate snapshot drift).
    /// </summary>
    private static JimDbContext NewModelOnlyContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql("Host=localhost;Database=jim_model_only")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }
    #endregion
}
