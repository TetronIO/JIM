// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Models.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Search;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
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
    /// <summary>
    /// The Uri each built-in Predefined Search is stored under. Two of them were looked up under a different Uri to
    /// the one they were created with ("security" against "security-groups"), so a retry re-created them (#1287).
    /// </summary>
    private static readonly string[] BuiltInPredefinedSearchUris =
        { "users", "people", "service-principals", "groups", "security-groups", "distribution-groups" };

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<ISearchRepository> _searchRepo = null!;
    private Mock<IExampleDataRepository> _exampleDataRepo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<ISeedingRepository> _seedingRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;

    private List<Activity> _createdActivities = null!;
    private Dictionary<string, MetaverseAttribute> _persistedAttributes = null!;
    private Dictionary<string, MetaverseObjectType> _persistedObjectTypes = null!;
    private Dictionary<string, ExampleDataSet> _persistedDataSets = null!;
    private Dictionary<string, PredefinedSearch> _persistedSearches = null!;
    private Dictionary<string, ConnectorDefinition> _persistedConnectorDefinitions = null!;
    private List<ConnectorDefinition> _createdConnectorDefinitions = null!;
    private ExampleDataTemplate? _persistedTemplate;
    private SeedBatch? _seedBatch;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _searchRepo = new Mock<ISearchRepository>();
        _exampleDataRepo = new Mock<IExampleDataRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _seedingRepo = new Mock<ISeedingRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _repo.Setup(r => r.Search).Returns(_searchRepo.Object);
        _repo.Setup(r => r.ExampleData).Returns(_exampleDataRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repo.Setup(r => r.Seeding).Returns(_seedingRepo.Object);

        _createdActivities = new List<Activity>();
        _persistedAttributes = new Dictionary<string, MetaverseAttribute>(StringComparer.OrdinalIgnoreCase);
        _persistedObjectTypes = new Dictionary<string, MetaverseObjectType>(StringComparer.OrdinalIgnoreCase);
        _persistedDataSets = new Dictionary<string, ExampleDataSet>(StringComparer.OrdinalIgnoreCase);
        _persistedSearches = new Dictionary<string, PredefinedSearch>(StringComparer.OrdinalIgnoreCase);
        _persistedConnectorDefinitions = new Dictionary<string, ConnectorDefinition>(StringComparer.OrdinalIgnoreCase);
        _createdConnectorDefinitions = new List<ConnectorDefinition>();
        _persistedTemplate = null;
        _seedBatch = null;

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivities.Add(a))
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>()))
            .ReturnsAsync(0);

        // seeding runs only when ServiceSettings does not exist; every test here is a first seed or a retry of one.
        _settingsRepo.Setup(r => r.ServiceSettingsExistAsync()).ReturnsAsync(false);

        _metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => _persistedAttributes.GetValueOrDefault(name));
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => _persistedObjectTypes.GetValueOrDefault(name));
        _searchRepo.Setup(r => r.GetPredefinedSearchAsync(It.IsAny<string>()))
            .ReturnsAsync((string uri) => _persistedSearches.GetValueOrDefault(uri));
        _exampleDataRepo.Setup(r => r.GetExampleDataSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, string _, bool __) => _persistedDataSets.GetValueOrDefault(name));
        _exampleDataRepo.Setup(r => r.GetTemplateAsync(It.IsAny<string>()))
            .ReturnsAsync(() => _persistedTemplate);
        _connectedSystemRepo.Setup(r => r.GetConnectorDefinitionAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => _persistedConnectorDefinitions.GetValueOrDefault(name));
        _connectedSystemRepo.Setup(r => r.CreateConnectorDefinitionAsync(It.IsAny<ConnectorDefinition>()))
            .Callback<ConnectorDefinition>(d =>
            {
                d.Id = _createdConnectorDefinitions.Count + 1;
                _createdConnectorDefinitions.Add(d);
                _persistedConnectorDefinitions[d.Name] = d;
            })
            .Returns(Task.CompletedTask);
        _connectedSystemRepo.Setup(r => r.GetConnectorDefinitionAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int id, bool _) => _createdConnectorDefinitions.SingleOrDefault(d => d.Id == id));

        _seedingRepo.Setup(r => r.SeedDataAsync(
                It.IsAny<List<MetaverseAttribute>>(),
                It.IsAny<List<MetaverseObjectType>>(),
                It.IsAny<List<PredefinedSearch>>(),
                It.IsAny<List<ExampleDataSet>>(),
                It.IsAny<List<ExampleDataTemplate>>()))
            .Callback<List<MetaverseAttribute>, List<MetaverseObjectType>, List<PredefinedSearch>, List<ExampleDataSet>, List<ExampleDataTemplate>>(
                (attributes, objectTypes, searches, dataSets, templates) =>
                    _seedBatch = new SeedBatch(attributes, objectTypes, searches, dataSets, templates))
            .Returns(Task.CompletedTask);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

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
        PersistEverything(withDataSetValues: false);

        await _jim.Seeding.SeedAsync();

        Assert.That(_seedBatch, Is.Not.Null, "the seed batch must still be submitted");
        Assert.That(_seedBatch!.ExampleDataSets.Where(s => s.Id != 0), Is.Empty,
            "an already-persisted Example Data Set must never be submitted for creation");
    }

    [Test]
    public async Task SeedAsync_ExampleDataSetMissingValues_TopsUpThePersistedSetInPlaceAsync()
    {
        PersistEverything(withDataSetValues: false);
        var companies = _persistedDataSets[Constants.BuiltInExampleDataSets.Companies];

        await _jim.Seeding.SeedAsync();

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
        PersistEverything(withDataSetValues: true);
        _persistedTemplate = null;

        await _jim.Seeding.SeedAsync();

        Assert.That(_seedBatch, Is.Not.Null);
        var template = _seedBatch!.ExampleDataTemplates.SingleOrDefault();
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
        PersistEverything(withDataSetValues: true);

        await _jim.Seeding.SeedAsync();

        Assert.That(_seedBatch, Is.Not.Null);
        Assert.That(_seedBatch!.MetaverseAttributes, Is.Empty);
        Assert.That(_seedBatch.MetaverseObjectTypes, Is.Empty);
        Assert.That(_seedBatch.PredefinedSearches, Is.Empty);
        Assert.That(_seedBatch.ExampleDataSets, Is.Empty);
        Assert.That(_seedBatch.ExampleDataTemplates, Is.Empty);
        Assert.That(_createdActivities, Is.Empty,
            "a seeding pass that creates nothing must not record a System Initialisation Activity");
    }

    [Test]
    public async Task SeedAsync_PredefinedSearchesAlreadyPersisted_DoesNotReCreateThemAsync()
    {
        PersistEverything(withDataSetValues: true);

        await _jim.Seeding.SeedAsync();

        Assert.That(_seedBatch, Is.Not.Null);
        Assert.That(_seedBatch!.PredefinedSearches.Select(s => s.Uri), Is.Empty,
            "every built-in Predefined Search must be found by the Uri it is stored under, not a different one");
    }

    [Test]
    public async Task SeedAsync_NothingPersisted_CreatesTheFullBuiltInConfigurationAsync()
    {
        await _jim.Seeding.SeedAsync();

        Assert.That(_seedBatch, Is.Not.Null);
        Assert.That(_seedBatch!.MetaverseAttributes, Is.Not.Empty);
        Assert.That(_seedBatch.MetaverseObjectTypes, Has.Count.EqualTo(2));
        Assert.That(_seedBatch.PredefinedSearches.Select(s => s.Uri), Is.EquivalentTo(BuiltInPredefinedSearchUris),
            "the Uri a Predefined Search is created with is the Uri the next pass looks it up by");
        Assert.That(_seedBatch.ExampleDataSets, Has.Count.EqualTo(13));
        Assert.That(_seedBatch.ExampleDataTemplates, Has.Count.EqualTo(1));
        Assert.That(_seedBatch.ExampleDataSets.SelectMany(s => s.Values).Any(v => v.StringValue.Length == 0), Is.False,
            "a resource's trailing newline must not seed an empty Example Data Set value");
    }
    #endregion

    #region built-in Connector Definitions
    [Test]
    public async Task SyncBuiltInConnectorDefinitionsAsync_DefinitionMissing_CreatesItWithASeededBaselineAsync()
    {
        // SeedAsync short-circuits once ServiceSettings exists, so a Connector added to BuiltInConnectors() in a
        // later release only reaches an existing deployment if the startup sync creates what is missing.
        await _jim.Seeding.SyncBuiltInConnectorDefinitionsAsync();

        var expectedNames = SeedingServer.BuiltInConnectors().Select(c => c.Name).ToList();
        Assert.That(_createdConnectorDefinitions.Select(d => d.Name), Is.EquivalentTo(expectedNames),
            "every built-in Connector missing a definition must be created");
        Assert.That(_createdConnectorDefinitions.All(d => d.BuiltIn), Is.True);
        Assert.That(_createdConnectorDefinitions.All(d => d.Settings.Count > 0), Is.True,
            "a created definition must carry the Connector's declared settings");

        var parent = _createdActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        Assert.That(parent, Is.Not.Null, "the creations must be grouped under one System Initialisation Activity");
        var connectorActivities = _createdActivities.Where(a => a.TargetType == ActivityTargetType.ConnectorDefinition).ToList();
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
            var definition = new ConnectorDefinition { Id = _persistedConnectorDefinitions.Count + 1, Name = connector.Name, BuiltIn = true };
            SeedingServer.ApplyConnectorDeclarations((IConnectorCapabilities)connector, definition);
            _jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition((IConnectorSettings)connector, definition);
            _persistedConnectorDefinitions[connector.Name] = definition;
        }

        await _jim.Seeding.SyncBuiltInConnectorDefinitionsAsync();

        Assert.That(_createdConnectorDefinitions, Is.Empty, "an existing definition must never be re-created");
        Assert.That(_createdActivities, Is.Empty, "a converged sync must record no Activities at all");
    }

    [Test]
    public async Task SeedAsync_DoesNotCreateConnectorDefinitions_LeavingThemToTheStartupSyncAsync()
    {
        await _jim.Seeding.SeedAsync();

        Assert.That(_createdConnectorDefinitions, Is.Empty,
            "built-in Connector Definitions are owned by the startup sync, which runs on every start");
    }
    #endregion

    // -- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Puts the database in the state a crashed seeding pass leaves behind: every built-in object already
    /// persisted (with a non-zero id), which is also the state an already-seeded deployment is in.
    /// </summary>
    private void PersistEverything(bool withDataSetValues)
    {
        var nextId = 1;

        foreach (var definition in BuiltInMetaverseSchema.Attributes)
            _persistedAttributes[definition.Name] = new MetaverseAttribute
            {
                Id = nextId++,
                Name = definition.Name,
                Type = definition.Type,
                AttributePlurality = definition.Plurality,
                RenderingHint = definition.RenderingHint,
                BuiltIn = true
            };

        // BuiltInMetaverseSchema is the catalogue the startup schema sync converges towards; SeedAsync prepares a
        // handful of attributes that predate it, so add any it asks for that the catalogue does not carry.
        foreach (var name in SeedAsyncAttributeNames())
            if (!_persistedAttributes.ContainsKey(name))
                _persistedAttributes[name] = new MetaverseAttribute { Id = nextId++, Name = name, BuiltIn = true };

        foreach (var name in new[] { Constants.BuiltInObjectTypes.User, Constants.BuiltInObjectTypes.Group })
            _persistedObjectTypes[name] = new MetaverseObjectType { Id = nextId++, Name = name, BuiltIn = true };

        foreach (var uri in BuiltInPredefinedSearchUris)
            _persistedSearches[uri] = new PredefinedSearch { Id = nextId++, Name = uri, Uri = uri, BuiltIn = true };

        foreach (var (name, culture, resource) in SeedingServer.BuiltInExampleDataSets())
        {
            var dataSet = new ExampleDataSet { Id = nextId++, Name = name, Culture = culture, BuiltIn = true };
            if (withDataSetValues)
                dataSet.Values.AddRange(SeedingServer.NormaliseExampleDataSetValues(resource)
                    .Select(v => new ExampleDataSetValue { StringValue = v }));
            _persistedDataSets[name] = dataSet;
        }

        _persistedTemplate = new ExampleDataTemplate { Id = nextId, Name = "Users & Groups", BuiltIn = true };
    }

    /// <summary>
    /// The built-in Metaverse Attribute names SeedAsync prepares that the BuiltInMetaverseSchema catalogue does
    /// not carry, so a "fully seeded" database in these tests really is fully seeded.
    /// </summary>
    private static IEnumerable<string> SeedAsyncAttributeNames()
    {
        var constants = typeof(Constants.BuiltInAttributes).GetProperties();
        foreach (var constant in constants)
            if (constant.GetValue(null) is string name)
                yield return name;
    }

    private void SetupTrackingSetting(bool enabled) =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = enabled ? "true" : "false"
            });

    private void SetupHashKeySetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = _protection.Protect(Convert.ToBase64String(new byte[32]))
            });

    private sealed record SeedBatch(
        List<MetaverseAttribute> MetaverseAttributes,
        List<MetaverseObjectType> MetaverseObjectTypes,
        List<PredefinedSearch> PredefinedSearches,
        List<ExampleDataSet> ExampleDataSets,
        List<ExampleDataTemplate> ExampleDataTemplates);

    private sealed class FakeProtection : ICredentialProtectionService
    {
        private const string Prefix = "$JIM$v1$";

        public string? Protect(string? plainText) =>
            string.IsNullOrEmpty(plainText) ? plainText : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string? protectedData) =>
            string.IsNullOrEmpty(protectedData) || !IsProtected(protectedData)
                ? protectedData
                : Encoding.UTF8.GetString(Convert.FromBase64String(protectedData[Prefix.Length..]));

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
