// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Scheduling;
using JIM.Models.Search;
using JIM.Models.Search.DTOs;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// An in-memory stand-in for the repositories the built-in configuration pipeline reads and writes, wired into a
/// real <see cref="JimApplication"/>. Every store is a plain dictionary or list, and every write lands in one, so a
/// pass can be run twice and the second pass genuinely sees what the first created. That is what makes convergence
/// assertions possible without a database.
/// <para>
/// Shared by every fixture that exercises the pipeline (seeding idempotency, built-in convergence, and the factory
/// reset paths). Before this existed each fixture carried its own ~90 lines of mock wiring, and the reset fixtures
/// only mocked the handful of repositories their bespoke repair calls happened to touch; running the whole pipeline
/// from a reset would have needed all of it duplicated a third and fourth time.
/// </para>
/// </summary>
internal sealed class SeedingTestHarness : IDisposable
{
    /// <summary>
    /// The Uri each built-in Predefined Search is stored under. Two of them were once looked up under a different
    /// Uri to the one they were created with ("security" against "security-groups"), so a retry re-created them
    /// (issue #1287).
    /// </summary>
    public static readonly string[] BuiltInPredefinedSearchUris =
        { "users", "people", "service-principals", "groups", "security-groups", "distribution-groups" };

    public const string BuiltInExampleDataTemplateName = "Users & Groups";

    private int _nextId = 1;

    #region stores
    public Dictionary<string, MetaverseAttribute> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MetaverseObjectType> ObjectTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keyed by Uri, which is how <c>SeedAsync</c> looks a Predefined Search up.</summary>
    public Dictionary<string, PredefinedSearch> PredefinedSearches { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ExampleDataSet> ExampleDataSets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ConnectorDefinition> ConnectorDefinitions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ServiceSetting> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Role> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Schedule> Schedules { get; } = new();
    public ExampleDataTemplate? ExampleDataTemplate { get; set; }
    public ServiceSettings? ServiceSettings { get; set; }
    #endregion

    #region observations
    /// <summary>Every Activity created through the application, in creation order.</summary>
    public List<Activity> CreatedActivities { get; } = new();

    /// <summary>Every Activity handed to UpdateActivityAsync, which is how completion and failure are recorded.</summary>
    public List<Activity> UpdatedActivities { get; } = new();

    /// <summary>The lists handed to the last <c>SeedDataAsync</c> call, so a test can assert what was submitted.</summary>
    public SeedBatch? LastSeedBatch { get; private set; }

    /// <summary>Connector Definitions created through the audited path, in creation order.</summary>
    public List<ConnectorDefinition> CreatedConnectorDefinitions { get; } = new();

    /// <summary>Schedules created through the audited path, in creation order.</summary>
    public List<Schedule> CreatedSchedules { get; } = new();

    /// <summary>Roles created through the audited path, in creation order.</summary>
    public List<Role> CreatedRoles { get; } = new();
    #endregion

    #region mocks
    public Mock<IRepository> Repository { get; } = new();
    public Mock<IActivityRepository> ActivityRepository { get; } = new();
    public Mock<IServiceSettingsRepository> ServiceSettingsRepository { get; } = new();
    public Mock<IMetaverseRepository> MetaverseRepository { get; } = new();
    public Mock<ISearchRepository> SearchRepository { get; } = new();
    public Mock<IExampleDataRepository> ExampleDataRepository { get; } = new();
    public Mock<IConnectedSystemRepository> ConnectedSystemRepository { get; } = new();
    public Mock<ISeedingRepository> SeedingRepository { get; } = new();
    public Mock<ISchedulingRepository> SchedulingRepository { get; } = new();
    public Mock<ISecurityRepository> SecurityRepository { get; } = new();
    public Mock<ISystemRepository> SystemRepository { get; } = new();
    #endregion

    public JimApplication Jim { get; }

    public SeedingTestHarness()
    {
        TestUtilities.SetEnvironmentVariables();

        Repository.Setup(r => r.Activity).Returns(ActivityRepository.Object);
        Repository.Setup(r => r.ServiceSettings).Returns(ServiceSettingsRepository.Object);
        Repository.Setup(r => r.Metaverse).Returns(MetaverseRepository.Object);
        Repository.Setup(r => r.Search).Returns(SearchRepository.Object);
        Repository.Setup(r => r.ExampleData).Returns(ExampleDataRepository.Object);
        Repository.Setup(r => r.ConnectedSystems).Returns(ConnectedSystemRepository.Object);
        Repository.Setup(r => r.Seeding).Returns(SeedingRepository.Object);
        Repository.Setup(r => r.Scheduling).Returns(SchedulingRepository.Object);
        Repository.Setup(r => r.Security).Returns(SecurityRepository.Object);
        Repository.Setup(r => r.System).Returns(SystemRepository.Object);

        SetupActivities();
        SetupServiceSettings();
        SetupMetaverse();
        SetupSearch();
        SetupExampleData();
        SetupConnectorDefinitions();
        SetupSeeding();
        SetupScheduling();
        SetupSecurity();
        SetupSystem();

        Jim = new JimApplication(Repository.Object) { CredentialProtection = Protection };

        SetConfigurationChangeTracking(enabled: true);
        SetupHashKeySetting();
    }

    public FakeProtection Protection { get; } = new();

    public void Dispose() => Jim.Dispose();

    #region setup
    private void SetupActivities()
    {
        ActivityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => CreatedActivities.Add(a))
            .Returns(Task.CompletedTask);
        ActivityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => UpdatedActivities.Add(a))
            .Returns(Task.CompletedTask);
        // Configuration change history, served from the Activities the harness has recorded. The capture path uses
        // it twice: to number the next version, and to decide whether a value actually changed since the last
        // snapshot. Returning a constant 0/null instead would make every pass look like a first write, so a
        // converged startup would appear to re-record baselines and updates that production would skip.
        ActivityRepository.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>()))
            .ReturnsAsync((ActivityTargetType targetType, int targetObjectId) =>
                MaxVersion(ConfigurationChangesFor(targetType, a => TargetIdOf(a, targetType) == targetObjectId)));
        ActivityRepository.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<Guid>()))
            .ReturnsAsync((ActivityTargetType targetType, Guid targetObjectId) =>
                MaxVersion(ConfigurationChangesFor(targetType, a => a.ScheduleId == targetObjectId)));
        ActivityRepository.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<string>()))
            .ReturnsAsync((ActivityTargetType targetType, string targetObjectKey) =>
                MaxVersion(ConfigurationChangesFor(targetType, a => a.ServiceSettingKey == targetObjectKey)));

        ActivityRepository.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>()))
            .ReturnsAsync((ActivityTargetType targetType, int targetObjectId) =>
                LatestSnapshot(ConfigurationChangesFor(targetType, a => TargetIdOf(a, targetType) == targetObjectId)));
        ActivityRepository.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(It.IsAny<ActivityTargetType>(), It.IsAny<Guid>()))
            .ReturnsAsync((ActivityTargetType targetType, Guid targetObjectId) =>
                LatestSnapshot(ConfigurationChangesFor(targetType, a => a.ScheduleId == targetObjectId)));
        ActivityRepository.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(It.IsAny<ActivityTargetType>(), It.IsAny<string>()))
            .ReturnsAsync((ActivityTargetType targetType, string targetObjectKey) =>
                LatestSnapshot(ConfigurationChangesFor(targetType, a => a.ServiceSettingKey == targetObjectKey)));

        // No activity in progress, so the factory reset's integrity guard passes.
        ActivityRepository.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<IEnumerable<ActivityTargetOperationType>?>(),
                It.IsAny<IEnumerable<ActivityOutcomeType>?>(), It.IsAny<IEnumerable<ActivityTargetType>?>(),
                It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>()))
            .ReturnsAsync(new PagedResultSet<Activity> { Results = new List<Activity>(), TotalResults = 0, PageSize = 1, CurrentPage = 1 });
    }

    private List<Activity> ConfigurationChangesFor(ActivityTargetType targetType, Func<Activity, bool> matchesTarget) =>
        CreatedActivities
            .Where(a => a.TargetType == targetType && a.ConfigurationChangeVersion.HasValue && matchesTarget(a))
            .OrderBy(a => a.ConfigurationChangeVersion)
            .ToList();

    private static int MaxVersion(List<Activity> changes) =>
        changes.Count == 0 ? 0 : changes.Max(a => a.ConfigurationChangeVersion!.Value);

    private static string? LatestSnapshot(List<Activity> changes) =>
        changes.LastOrDefault()?.ConfigurationChangeSnapshot;

    /// <summary>
    /// Reads back the integer-keyed target id the capture path wrote onto the Activity, per configuration type.
    /// Mirrors Activity.SetConfigurationTargetId, which is the forward direction of the same mapping.
    /// </summary>
    private static int? TargetIdOf(Activity activity, ActivityTargetType targetType) => targetType switch
    {
        ActivityTargetType.MetaverseAttribute => activity.MetaverseAttributeId,
        ActivityTargetType.MetaverseObjectType => activity.MetaverseObjectTypeId,
        ActivityTargetType.PredefinedSearch => activity.PredefinedSearchId,
        ActivityTargetType.Role => activity.RoleId,
        ActivityTargetType.ConnectorDefinition => activity.ConnectorDefinitionId,
        ActivityTargetType.ExampleDataTemplate => activity.ExampleDataTemplateId,
        ActivityTargetType.ExampleDataSet => activity.ExampleDataSetId,
        _ => null
    };

    private void SetupServiceSettings()
    {
        ServiceSettingsRepository.Setup(r => r.ServiceSettingsExistAsync()).ReturnsAsync(() => ServiceSettings != null);
        ServiceSettingsRepository.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync(() => ServiceSettings);
        ServiceSettingsRepository.Setup(r => r.UpdateServiceSettingsAsync(It.IsAny<ServiceSettings>())).Returns(Task.CompletedTask);

        ServiceSettingsRepository.Setup(r => r.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => Settings.GetValueOrDefault(key));
        ServiceSettingsRepository.Setup(r => r.GetAllSettingsAsync())
            .ReturnsAsync(() => Settings.Values.ToList());
        ServiceSettingsRepository.Setup(r => r.SettingExistsAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => Settings.ContainsKey(key));
        ServiceSettingsRepository.Setup(r => r.CreateSettingAsync(It.IsAny<ServiceSetting>()))
            .Callback<ServiceSetting>(s => Settings[s.Key] = s)
            .Returns(Task.CompletedTask);
        ServiceSettingsRepository.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()))
            .Callback<ServiceSetting>(s => Settings[s.Key] = s)
            .Returns(Task.CompletedTask);
        ServiceSettingsRepository.Setup(r => r.GetOrCreateSettingAsync(It.IsAny<ServiceSetting>()))
            .ReturnsAsync((ServiceSetting s) =>
            {
                if (Settings.TryGetValue(s.Key, out var existing))
                    return existing;
                Settings[s.Key] = s;
                return s;
            });
    }

    private void SetupMetaverse()
    {
        MetaverseRepository.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => Attributes.GetValueOrDefault(name));
        MetaverseRepository.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int id, bool _) => Attributes.Values.SingleOrDefault(a => a.Id == id));
        MetaverseRepository.Setup(r => r.GetMetaverseAttributesAsync(It.IsAny<bool>()))
            .ReturnsAsync(() => Attributes.Values.ToList());
        MetaverseRepository.Setup(r => r.GetMetaverseObjectTypeAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _, bool __) => ObjectTypes.GetValueOrDefault(name));
        MetaverseRepository.Setup(r => r.GetMetaverseObjectTypeAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int id, bool _) => ObjectTypes.Values.SingleOrDefault(t => t.Id == id));
        MetaverseRepository.Setup(r => r.GetMetaverseObjectTypesAsync(It.IsAny<bool>()))
            .ReturnsAsync(() => ObjectTypes.Values.ToList());
        MetaverseRepository.Setup(r => r.GetMetaverseAttributesForSchemaSyncAsync())
            .ReturnsAsync(() => Attributes.Values.ToList());
        MetaverseRepository.Setup(r => r.GetBuiltInMetaverseObjectTypesForSchemaSyncAsync())
            .ReturnsAsync(() => ObjectTypes.Values.Where(t => t.BuiltIn).ToList());
    }

    private void SetupSearch()
    {
        SearchRepository.Setup(r => r.GetPredefinedSearchAsync(It.IsAny<string>()))
            .ReturnsAsync((string uri) => PredefinedSearches.GetValueOrDefault(uri));
        SearchRepository.Setup(r => r.GetPredefinedSearchHeadersAsync())
            .ReturnsAsync(() => PredefinedSearches.Values
                .Select(s => new PredefinedSearchHeader { Id = s.Id, Name = s.Name, Uri = s.Uri, BuiltIn = s.BuiltIn })
                .ToList());
    }

    private void SetupExampleData()
    {
        ExampleDataRepository.Setup(r => r.GetExampleDataSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, string _, bool __) => ExampleDataSets.GetValueOrDefault(name));
        ExampleDataRepository.Setup(r => r.GetExampleDataSetsAsync(It.IsAny<bool>()))
            .ReturnsAsync(() => ExampleDataSets.Values.ToList());
        ExampleDataRepository.Setup(r => r.GetTemplateAsync(It.IsAny<string>()))
            .ReturnsAsync(() => ExampleDataTemplate);
        ExampleDataRepository.Setup(r => r.GetTemplatesAsync())
            .ReturnsAsync(() => ExampleDataTemplate == null ? new List<ExampleDataTemplate>() : new List<ExampleDataTemplate> { ExampleDataTemplate });
        ExampleDataRepository.Setup(r => r.DeleteTemplateAsync(It.IsAny<int>()))
            .Callback<int>(_ => ExampleDataTemplate = null)
            .Returns(Task.CompletedTask);
        ExampleDataRepository.Setup(r => r.CreateTemplateGraphAsync(It.IsAny<ExampleDataTemplate>()))
            .Callback<ExampleDataTemplate>(t =>
            {
                t.Id = _nextId++;
                ExampleDataTemplate = t;
            })
            .Returns(Task.CompletedTask);
    }

    private void SetupConnectorDefinitions()
    {
        ConnectedSystemRepository.Setup(r => r.GetConnectorDefinitionAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => ConnectorDefinitions.GetValueOrDefault(name));
        ConnectedSystemRepository.Setup(r => r.GetConnectorDefinitionAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int id, bool _) => ConnectorDefinitions.Values.SingleOrDefault(d => d.Id == id));
        ConnectedSystemRepository.Setup(r => r.CreateConnectorDefinitionAsync(It.IsAny<ConnectorDefinition>()))
            .Callback<ConnectorDefinition>(d =>
            {
                d.Id = _nextId++;
                ConnectorDefinitions[d.Name] = d;
                CreatedConnectorDefinitions.Add(d);
            })
            .Returns(Task.CompletedTask);
        ConnectedSystemRepository.Setup(r => r.GetConnectorDefinitionHeadersAsync())
            .ReturnsAsync(() => (IList<ConnectorDefinitionHeader>)ConnectorDefinitions.Values
                .Select(d => new ConnectorDefinitionHeader { Id = d.Id, Name = d.Name, BuiltIn = d.BuiltIn })
                .ToList());
    }

    private void SetupSeeding()
    {
        SeedingRepository.Setup(r => r.SeedDataAsync(
                It.IsAny<List<MetaverseAttribute>>(),
                It.IsAny<List<MetaverseObjectType>>(),
                It.IsAny<List<PredefinedSearch>>(),
                It.IsAny<List<ExampleDataSet>>(),
                It.IsAny<List<ExampleDataTemplate>>()))
            .Callback<List<MetaverseAttribute>, List<MetaverseObjectType>, List<PredefinedSearch>, List<ExampleDataSet>, List<ExampleDataTemplate>>(
                (attributes, objectTypes, searches, dataSets, templates) =>
                {
                    LastSeedBatch = new SeedBatch(attributes, objectTypes, searches, dataSets, templates);

                    // Mirror the repository: everything submitted is persisted with an id, so a second pass over the
                    // same harness finds it already there. Without this no convergence assertion means anything.
                    foreach (var attribute in attributes)
                    {
                        attribute.Id = _nextId++;
                        Attributes[attribute.Name] = attribute;
                    }

                    foreach (var objectType in objectTypes)
                    {
                        objectType.Id = _nextId++;
                        ObjectTypes[objectType.Name] = objectType;
                    }

                    foreach (var search in searches)
                    {
                        search.Id = _nextId++;
                        PredefinedSearches[search.Uri] = search;
                    }

                    foreach (var dataSet in dataSets)
                    {
                        dataSet.Id = _nextId++;
                        ExampleDataSets[dataSet.Name] = dataSet;
                    }

                    foreach (var template in templates)
                    {
                        template.Id = _nextId++;
                        ExampleDataTemplate = template;
                    }

                    // ServiceSettings is created last and in the same transaction, so the row exists from the first
                    // successful seed onwards. Guarded exactly as the repository guards it (issue #1287).
                    ServiceSettings ??= new ServiceSettings();
                })
            .Returns(Task.CompletedTask);

        SeedingRepository.Setup(r => r.SaveBuiltInSchemaChangesAsync(It.IsAny<List<MetaverseAttribute>>()))
            .Callback<List<MetaverseAttribute>>(attributes =>
            {
                foreach (var attribute in attributes)
                {
                    attribute.Id = _nextId++;
                    Attributes[attribute.Name] = attribute;
                }
            })
            .Returns(Task.CompletedTask);
    }

    private void SetupScheduling()
    {
        SchedulingRepository.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(() => Schedules.ToList());
        SchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => Schedules.SingleOrDefault(s => s.Id == id));
        SchedulingRepository.Setup(r => r.CreateScheduleAsync(It.IsAny<Schedule>()))
            .Callback<Schedule>(s =>
            {
                if (s.Id == Guid.Empty)
                    s.Id = Guid.NewGuid();
                Schedules.Add(s);
                CreatedSchedules.Add(s);
            })
            .Returns(Task.CompletedTask);
    }

    private void SetupSecurity()
    {
        SecurityRepository.Setup(r => r.GetRoleAsync(It.IsAny<string>()))
            .ReturnsAsync((string name) => Roles.GetValueOrDefault(name));
        SecurityRepository.Setup(r => r.GetRolesAsync()).ReturnsAsync(() => Roles.Values.ToList());
        SecurityRepository.Setup(r => r.CreateRoleAsync(It.IsAny<Role>()))
            .ReturnsAsync((Role role) =>
            {
                role.Id = _nextId++;
                Roles[role.Name] = role;
                CreatedRoles.Add(role);
                return role;
            });
    }

    private void SetupSystem() =>
        SystemRepository.Setup(r => r.ResetSystemAsync(It.IsAny<bool>())).ReturnsAsync(new SystemResetResult());
    #endregion

    #region state helpers
    /// <summary>
    /// Puts the harness in the state an already-seeded deployment is in: every built-in object persisted with a
    /// non-zero id, and ServiceSettings present. Also the state a crashed seeding pass leaves behind, which is why
    /// <paramref name="withExampleDataSetValues"/> exists: a set persisted without its values is the "needs topping
    /// up" case (issue #1287).
    /// </summary>
    public void PersistBuiltInConfiguration(bool withExampleDataSetValues = true, bool withServiceSettings = true)
    {
        foreach (var definition in BuiltInMetaverseSchema.Attributes)
            Attributes[definition.Name] = new MetaverseAttribute
            {
                Id = _nextId++,
                Name = definition.Name,
                Type = definition.Type,
                AttributePlurality = definition.Plurality,
                RenderingHint = definition.RenderingHint,
                BuiltIn = true
            };

        // BuiltInMetaverseSchema is the catalogue the startup schema sync converges towards; SeedAsync prepares a
        // handful of attributes that predate it, so add any it asks for that the catalogue does not carry.
        foreach (var name in SeedAsyncAttributeNames().Where(name => !Attributes.ContainsKey(name)))
            Attributes[name] = new MetaverseAttribute { Id = _nextId++, Name = name, BuiltIn = true };

        foreach (var name in new[] { Constants.BuiltInObjectTypes.User, Constants.BuiltInObjectTypes.Group })
            ObjectTypes[name] = new MetaverseObjectType { Id = _nextId++, Name = name, BuiltIn = true };

        foreach (var uri in BuiltInPredefinedSearchUris)
            PredefinedSearches[uri] = new PredefinedSearch { Id = _nextId++, Name = uri, Uri = uri, BuiltIn = true };

        foreach (var (name, culture, resource) in SeedingServer.BuiltInExampleDataSets())
        {
            var dataSet = new ExampleDataSet { Id = _nextId++, Name = name, Culture = culture, BuiltIn = true };
            if (withExampleDataSetValues)
                dataSet.Values.AddRange(SeedingServer.NormaliseExampleDataSetValues(resource)
                    .Select(v => new ExampleDataSetValue { StringValue = v }));
            ExampleDataSets[name] = dataSet;
        }

        ExampleDataTemplate = BuildIntactExampleDataTemplate();

        if (withServiceSettings)
            ServiceSettings ??= new ServiceSettings();
    }

    /// <summary>
    /// A template that satisfies the "present and complete" check in EnsureBuiltInExampleDataTemplateAsync, so its
    /// repair path stays a no-op for tests that are not about the template.
    /// </summary>
    private ExampleDataTemplate BuildIntactExampleDataTemplate()
    {
        var objectType = new ExampleDataObjectType
        {
            MetaverseObjectType = ObjectTypes.GetValueOrDefault(Constants.BuiltInObjectTypes.User)
                                  ?? new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.User }
        };
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute());

        var template = new ExampleDataTemplate { Id = _nextId++, Name = BuiltInExampleDataTemplateName, BuiltIn = true };
        template.ObjectTypes.Add(objectType);
        return template;
    }

    /// <summary>
    /// The built-in Metaverse Attribute names SeedAsync prepares, so a "fully seeded" harness really is fully seeded.
    /// </summary>
    public static IEnumerable<string> SeedAsyncAttributeNames() =>
        typeof(Constants.BuiltInAttributes).GetProperties()
            .Select(constant => constant.GetValue(null))
            .OfType<string>();

    public void SetConfigurationChangeTracking(bool enabled) =>
        Settings[Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled] = new ServiceSetting
        {
            Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
            DisplayName = "Track configuration changes",
            ValueType = ServiceSettingValueType.Boolean,
            Value = enabled ? "true" : "false"
        };

    private void SetupHashKeySetting() =>
        Settings[Constants.SettingKeys.ConfigurationChangeHashKey] = new ServiceSetting
        {
            Key = Constants.SettingKeys.ConfigurationChangeHashKey,
            DisplayName = "Configuration change hash key",
            ValueType = ServiceSettingValueType.StringEncrypted,
            Value = Protection.Protect(Convert.ToBase64String(new byte[32]))!
        };
    #endregion

    internal sealed record SeedBatch(
        List<MetaverseAttribute> MetaverseAttributes,
        List<MetaverseObjectType> MetaverseObjectTypes,
        List<PredefinedSearch> PredefinedSearches,
        List<ExampleDataSet> ExampleDataSets,
        List<ExampleDataTemplate> ExampleDataTemplates);

    internal sealed class FakeProtection : ICredentialProtectionService
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
