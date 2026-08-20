// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Models.Interfaces;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests that seeding built-in configuration is idempotent (issue #1287). SeedAsync documents that a failure part
/// way through is safe because the next restart retries seeding from scratch; that only holds if every seed step
/// genuinely checks-then-creates. Two faults broke it: already-persisted Example Data Sets were returned into the
/// create batch (a duplicate-key crash on every subsequent start), and the built-in Example Data Template was
/// built from the objects created during that pass alone, so a retry (where nothing needs creating) could not
/// resolve its attributes and Example Data Sets at all. A third fault sat the other side of the same
/// short-circuit: a built-in Connector added in a later release only ever reached brand-new deployments, because
/// creation lived in SeedAsync, which never runs again once ServiceSettings exist.
/// </summary>
[TestFixture]
public class SeedingIdempotencyTests
{
    private SeedingTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new SeedingTestHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    #region example data value normalisation
    [Test]
    public void NormaliseExampleDataSetValues_CrlfLineEndings_ReturnsTrimmedValues()
    {
        // the embedded resources are checked in as LF but a Windows checkout converts them to CRLF, so the string
        // baked into the assembly depends on the build host. Splitting on Environment.NewLine (LF in a Linux
        // container) left a trailing carriage return on every value, which never matched the trimmed value stored
        // in the database, so every already-persisted set was returned into the create batch on a retry.
        var values = SeedingServer.NormaliseExampleDataSetValues("Alpha\r\nBravo\r\nCharlie");

        Assert.That(values, Is.EqualTo(new[] { "Alpha", "Bravo", "Charlie" }),
            "values must be free of line-ending characters whichever host built the resources");
    }

    [Test]
    public void NormaliseExampleDataSetValues_LfLineEndings_ReturnsTrimmedValues()
    {
        var values = SeedingServer.NormaliseExampleDataSetValues("Alpha\nBravo\nCharlie");

        Assert.That(values, Is.EqualTo(new[] { "Alpha", "Bravo", "Charlie" }));
    }

    [Test]
    public void NormaliseExampleDataSetValues_BlankAndTrailingLines_AreDropped()
    {
        // most of the resource files end with a newline, which previously seeded an empty value into the set.
        var values = SeedingServer.NormaliseExampleDataSetValues("Alpha\r\n\r\n  \r\nBravo\r\n");

        Assert.That(values, Is.EqualTo(new[] { "Alpha", "Bravo" }),
            "blank lines must never become Example Data Set values");
    }

    [Test]
    public void NormaliseExampleDataSetValues_LeadingByteOrderMark_IsStripped()
    {
        // The resource files are read with byte-order-mark detection on, so a mark never reaches here today. It is
        // stripped anyway because the alternative is silent: a mark that did survive would prefix the set's first
        // value with an invisible character, and every later comparison against the stored value would match it.
        var values = SeedingServer.NormaliseExampleDataSetValues("\uFEFFActive\nLeaver");

        Assert.That(values, Is.EqualTo(new[] { "Active", "Leaver" }));
    }

    [Test]
    public void NormaliseExampleDataSetValues_DuplicateLines_AreCollapsed()
    {
        var values = SeedingServer.NormaliseExampleDataSetValues("Alpha\nBravo\nAlpha");

        Assert.That(values, Is.EqualTo(new[] { "Alpha", "Bravo" }),
            "a value repeated in a resource must be seeded once, so the same comparison holds on a retry");
    }
    #endregion

    #region SeedAsync idempotency
    [Test]
    public async Task SeedAsync_ExampleDataSetsAlreadyPersisted_DoesNotReturnThemIntoTheCreateBatchAsync()
    {
        // the reported failure: 23505 duplicate key value violates unique constraint "PK_ExampleDataSets", because
        // an already-persisted set was handed to the create batch. Values are deliberately absent from the
        // persisted sets so the "needs topping up" path is the one exercised.
        _harness.PersistBuiltInConfiguration(withExampleDataSetValues: false, withServiceSettings: false);

        await _harness.Jim.Seeding.SeedAsync();

        Assert.That(_harness.LastSeedBatch, Is.Not.Null, "the seed batch must still be submitted");
        Assert.That(_harness.LastSeedBatch!.ExampleDataSets.Where(s => s.Id != 0), Is.Empty,
            "an already-persisted Example Data Set must never be submitted for creation");
    }

    [Test]
    public async Task SeedAsync_ExampleDataSetMissingValues_TopsUpThePersistedSetInPlaceAsync()
    {
        _harness.PersistBuiltInConfiguration(withExampleDataSetValues: false, withServiceSettings: false);
        var companies = _harness.ExampleDataSets[Constants.BuiltInExampleDataSets.Companies];

        await _harness.Jim.Seeding.SeedAsync();

        Assert.That(companies.Values, Is.Not.Empty,
            "a persisted set missing its values must be topped up rather than re-created");
        Assert.That(companies.Values.Select(v => v.StringValue), Is.Unique);
        Assert.That(companies.Values.Any(v => v.StringValue.Length == 0), Is.False,
            "topping up must not introduce empty values");
    }

    [Test]
    public async Task SeedAsync_RetryWithOnlyTheTemplateMissing_BuildsItFromThePersistedObjectsAsync()
    {
        // the second reported failure: "Sequence contains no matching element", because the template was built
        // from the objects created during this pass, and on a retry that list is empty.
        _harness.PersistBuiltInConfiguration(withServiceSettings: false);
        _harness.ExampleDataTemplate = null;

        await _harness.Jim.Seeding.SeedAsync();

        Assert.That(_harness.LastSeedBatch, Is.Not.Null);
        var template = _harness.LastSeedBatch!.ExampleDataTemplates.SingleOrDefault();
        Assert.That(template, Is.Not.Null, "the missing built-in template must still be prepared on a retry");
        Assert.That(template!.ObjectTypes, Has.Count.EqualTo(2), "the template covers Users and Groups");
        Assert.That(template.ObjectTypes.All(ot => ot.TemplateAttributes.Count > 0), Is.True,
            "the template's attributes must resolve against the persisted Metaverse Attributes");
        Assert.That(template.ObjectTypes.SelectMany(ot => ot.TemplateAttributes)
                .SelectMany(a => a.ExampleDataSetInstances)
                .All(i => i.ExampleDataSet != null && i.ExampleDataSet.Id != 0), Is.True,
            "the template must bind to the persisted Example Data Sets, not to unsaved copies");
    }

    [Test]
    public async Task SeedAsync_EverythingAlreadyPersisted_CreatesNothingAsync()
    {
        _harness.PersistBuiltInConfiguration(withServiceSettings: false);

        await _harness.Jim.Seeding.SeedAsync();

        Assert.That(_harness.LastSeedBatch, Is.Not.Null);
        Assert.That(_harness.LastSeedBatch!.MetaverseAttributes, Is.Empty);
        Assert.That(_harness.LastSeedBatch.MetaverseObjectTypes, Is.Empty);
        Assert.That(_harness.LastSeedBatch.PredefinedSearches, Is.Empty);
        Assert.That(_harness.LastSeedBatch.ExampleDataSets, Is.Empty);
        Assert.That(_harness.LastSeedBatch.ExampleDataTemplates, Is.Empty);
        Assert.That(_harness.CreatedActivities, Is.Empty,
            "a seeding pass that creates nothing must not record a System Initialisation Activity");
    }

    [Test]
    public async Task SeedAsync_PredefinedSearchesAlreadyPersisted_DoesNotReCreateThemAsync()
    {
        _harness.PersistBuiltInConfiguration(withServiceSettings: false);

        await _harness.Jim.Seeding.SeedAsync();

        Assert.That(_harness.LastSeedBatch, Is.Not.Null);
        Assert.That(_harness.LastSeedBatch!.PredefinedSearches.Select(s => s.Uri), Is.Empty,
            "every built-in Predefined Search must be found by the Uri it is stored under, not a different one");
    }

    [Test]
    public async Task SeedAsync_NothingPersisted_CreatesTheFullBuiltInConfigurationAsync()
    {
        await _harness.Jim.Seeding.SeedAsync();

        Assert.That(_harness.LastSeedBatch, Is.Not.Null);
        Assert.That(_harness.LastSeedBatch!.MetaverseAttributes, Is.Not.Empty);
        Assert.That(_harness.LastSeedBatch.MetaverseObjectTypes, Has.Count.EqualTo(2));
        Assert.That(_harness.LastSeedBatch.PredefinedSearches.Select(s => s.Uri), Is.EquivalentTo(SeedingTestHarness.BuiltInPredefinedSearchUris),
            "the Uri a Predefined Search is created with is the Uri the next pass looks it up by");
        Assert.That(_harness.LastSeedBatch.ExampleDataSets, Has.Count.EqualTo(13));
        Assert.That(_harness.LastSeedBatch.ExampleDataTemplates, Has.Count.EqualTo(1));
        Assert.That(_harness.LastSeedBatch.ExampleDataSets.SelectMany(s => s.Values).Any(v => v.StringValue.Length == 0), Is.False,
            "a resource's trailing newline must not seed an empty Example Data Set value");
    }
    #endregion

    #region built-in Connector Definitions
    [Test]
    public async Task SyncBuiltInConnectorDefinitionsAsync_DefinitionMissing_CreatesItWithASeededBaselineAsync()
    {
        // SeedAsync short-circuits once ServiceSettings exists, so a Connector added to BuiltInConnectors() in a
        // later release only reaches an existing deployment if the startup sync creates what is missing.
        await _harness.Jim.Seeding.SyncBuiltInConnectorDefinitionsAsync();

        var expectedNames = SeedingServer.BuiltInConnectors().Select(c => c.Name).ToList();
        Assert.That(_harness.CreatedConnectorDefinitions.Select(d => d.Name), Is.EquivalentTo(expectedNames),
            "every built-in Connector missing a definition must be created");
        Assert.That(_harness.CreatedConnectorDefinitions.All(d => d.BuiltIn), Is.True);
        Assert.That(_harness.CreatedConnectorDefinitions.All(d => d.Settings.Count > 0), Is.True,
            "a created definition must carry the Connector's declared settings");

        var parent = _harness.CreatedActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        Assert.That(parent, Is.Not.Null, "the creations must be grouped under one System Initialisation Activity");
        var connectorActivities = _harness.CreatedActivities.Where(a => a.TargetType == ActivityTargetType.ConnectorDefinition).ToList();
        Assert.That(connectorActivities, Has.Count.EqualTo(expectedNames.Count));
        Assert.That(connectorActivities.All(a => a.TargetOperationType == ActivityTargetOperationType.Create), Is.True);
        Assert.That(connectorActivities.All(a => a.ParentActivityId == parent!.Id), Is.True,
            "each Connector Definition's Create Activity must be a child of the seeding parent");
        Assert.That(connectorActivities.All(a => a.InitiatedByType == ActivityInitiatorType.System), Is.True);
    }

    [Test]
    public async Task SyncBuiltInConnectorDefinitionsAsync_DefinitionsAlreadyExist_CreatesNothingAsync()
    {
        foreach (var connector in SeedingServer.BuiltInConnectors())
        {
            var definition = new ConnectorDefinition { Id = _harness.ConnectorDefinitions.Count + 1, Name = connector.Name, BuiltIn = true };
            SeedingServer.ApplyConnectorDeclarations((IConnectorCapabilities)connector, definition);
            _harness.Jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition((IConnectorSettings)connector, definition);
            _harness.ConnectorDefinitions[connector.Name] = definition;
        }

        await _harness.Jim.Seeding.SyncBuiltInConnectorDefinitionsAsync();

        Assert.That(_harness.CreatedConnectorDefinitions, Is.Empty, "an existing definition must never be re-created");
        Assert.That(_harness.CreatedActivities, Is.Empty, "a converged sync must record no Activities at all");
    }

    [Test]
    public async Task SeedAsync_DoesNotCreateConnectorDefinitions_LeavingThemToTheStartupSyncAsync()
    {
        await _harness.Jim.Seeding.SeedAsync();

        Assert.That(_harness.CreatedConnectorDefinitions, Is.Empty,
            "built-in Connector Definitions are owned by the startup sync, which runs on every start");
    }
    #endregion

}
