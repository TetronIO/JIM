// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Logic;
using JIM.Models.Scheduling;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for <see cref="ConfigurationSnapshotService"/>: snapshot scope (what is and is not captured), secret redaction
/// (no secret material is ever serialised), and keyed-hash determinism and change detection.
/// </summary>
[TestFixture]
public class ConfigurationSnapshotServiceTests
{
    private JimApplication _jim = null!;
    private ConfigurationSnapshotService _service = null!;
    private RoundTripCredentialProtection _protection = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _protection = new RoundTripCredentialProtection();
        _jim = new JimApplication(new Mock<IRepository>().Object) { CredentialProtection = _protection };
        _service = _jim.ConfigurationSnapshots;
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- Synchronisation Rule scope ------------------------------------------------------------------------------------

    [Test]
    public void CreateSnapshot_SyncRule_CapturesScalarsAndChildCollectionsWithStableItemIds()
    {
        var mapping = new SyncRuleMapping { Id = 100, TargetMetaverseAttributeId = 5 };
        mapping.Sources.Add(new SyncRuleMappingSource { Id = 200, Order = 0, ConnectedSystemAttributeId = 9 });

        var rule = new SyncRule
        {
            Id = 42,
            Name = "HR Inbound",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemId = 3,
            ConnectedSystemObjectTypeId = 7,
            MetaverseObjectTypeId = 1
        };
        rule.AttributeFlowRules.Add(mapping);

        var snapshot = _service.CreateSnapshot(rule, HashKey);

        Assert.That(snapshot.ObjectType, Is.EqualTo(ConfigurationSnapshotService.SyncRuleObjectType));
        Assert.That(snapshot.ObjectId, Is.EqualTo(42));
        Assert.That(snapshot.ObjectName, Is.EqualTo("HR Inbound"));
        Assert.That(snapshot.Root.NodeType, Is.EqualTo(ConfigurationSnapshotNodeType.Object));

        Assert.That(Child(snapshot.Root, "name")!.Value, Is.EqualTo("HR Inbound"));
        Assert.That(Child(snapshot.Root, "direction")!.Value, Is.EqualTo("Import"));
        Assert.That(Child(snapshot.Root, "enabled")!.Value, Is.EqualTo("true"));
        Assert.That(Child(snapshot.Root, "connectedSystemId")!.Value, Is.EqualTo("3"));

        var flows = Child(snapshot.Root, "attributeFlowRules")!;
        Assert.That(flows.NodeType, Is.EqualTo(ConfigurationSnapshotNodeType.Collection));
        Assert.That(flows.Children, Has.Count.EqualTo(1));

        var flow = flows.Children![0];
        Assert.That(flow.ItemId, Is.EqualTo(100), "Collection items must carry the stable DB id for diff matching.");
        Assert.That(Child(flow, "targetMetaverseAttributeId")!.Value, Is.EqualTo("5"));

        var sources = Child(flow, "sources")!;
        Assert.That(sources.Children, Has.Count.EqualTo(1));
        Assert.That(sources.Children![0].ItemId, Is.EqualTo(200));
        Assert.That(Child(sources.Children[0], "connectedSystemAttributeId")!.Value, Is.EqualTo("9"));
    }

    [Test]
    public void CreateSnapshot_SyncRule_WithDescription_CapturesDescription()
    {
        // The description is administrator-facing configuration, so it must be snapshotted; without it a
        // description edit would diff as "no change".
        var rule = new SyncRule
        {
            Id = 42,
            Name = "HR Inbound",
            Description = "Flows joiner data from HR into the Metaverse.",
            Direction = SyncRuleDirection.Import
        };

        var snapshot = _service.CreateSnapshot(rule, HashKey);

        var description = Child(snapshot.Root, "description");
        Assert.That(description, Is.Not.Null, "the Synchronisation Rule description must be snapshotted");
        Assert.That(description!.Value, Is.EqualTo("Flows joiner data from HR into the Metaverse."));
        Assert.That(description.Label, Is.EqualTo("Description"));
    }

    [Test]
    public void CreateSnapshot_SyncRule_WithoutDescription_OmitsDescriptionNode()
    {
        // Matching Add()'s skip-empty behaviour: an unset description records nothing rather than an empty node.
        var rule = new SyncRule { Id = 42, Name = "HR Inbound", Direction = SyncRuleDirection.Import };

        var snapshot = _service.CreateSnapshot(rule, HashKey);

        Assert.That(Child(snapshot.Root, "description"), Is.Null);
    }

    [Test]
    public void CreateSnapshot_SyncRule_CapturesMappingPriorityAndNullIsValue()
    {
        // Priority and "Null is a value" determine which contributor wins a multi-source Metaverse attribute, so they
        // are configuration and must be snapshotted; without them a priority reorder diffs as "no change".
        var mapping = new SyncRuleMapping { Id = 100, TargetMetaverseAttributeId = 5, Priority = 2, NullIsValue = true };
        var rule = new SyncRule { Id = 42, Name = "HR Inbound", Direction = SyncRuleDirection.Import };
        rule.AttributeFlowRules.Add(mapping);

        var snapshot = _service.CreateSnapshot(rule, HashKey);

        var flow = Child(snapshot.Root, "attributeFlowRules")!.Children![0];
        Assert.That(Child(flow, "priority")!.Value, Is.EqualTo("2"));
        Assert.That(Child(flow, "nullIsValue")!.Value, Is.EqualTo("true"));
    }

    [Test]
    public void CreateSnapshot_SyncRule_OmitsSentinelPriority()
    {
        // int.MaxValue is the "sole contributor / no explicit priority" sentinel, not a real priority; rendering it
        // would show a meaningless 2147483647 in the field history.
        var mapping = new SyncRuleMapping { Id = 100, TargetMetaverseAttributeId = 5 };
        var rule = new SyncRule { Id = 42, Name = "HR Inbound", Direction = SyncRuleDirection.Import };
        rule.AttributeFlowRules.Add(mapping);

        var snapshot = _service.CreateSnapshot(rule, HashKey);

        var flow = Child(snapshot.Root, "attributeFlowRules")!.Children![0];
        Assert.That(Child(flow, "priority"), Is.Null, "the sentinel priority must not be snapshotted");
        Assert.That(Child(flow, "nullIsValue")!.Value, Is.EqualTo("false"));
    }

    [Test]
    public void CreateSnapshot_SyncRule_EnrichesForeignKeysAndEnumsWithDisplayValues()
    {
        var mapping = new SyncRuleMapping
        {
            Id = 100,
            TargetMetaverseAttributeId = 5,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 5, Name = "Display Name" },
            InboundValueProcessing = InboundValueProcessing.TreatWhitespaceAsNoValue
        };

        var rule = new SyncRule
        {
            Id = 42,
            Name = "HR Inbound",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 3,
            ConnectedSystem = new ConnectedSystem { Id = 3, Name = "AD" },
            ConnectedSystemObjectTypeId = 7,
            MetaverseObjectTypeId = 1
        };
        rule.AttributeFlowRules.Add(mapping);

        var snapshot = _service.CreateSnapshot(rule, HashKey);

        // Foreign keys keep the raw id for stable diffing, with the resolved name as the human-friendly display value.
        var connectedSystem = Child(snapshot.Root, "connectedSystemId")!;
        Assert.That(connectedSystem.Value, Is.EqualTo("3"), "the raw FK id is kept for diffing");
        Assert.That(connectedSystem.DisplayValue, Is.EqualTo("AD"));

        var flow = Child(snapshot.Root, "attributeFlowRules")!.Children![0];
        var targetAttribute = Child(flow, "targetMetaverseAttributeId")!;
        Assert.That(targetAttribute.Value, Is.EqualTo("5"));
        Assert.That(targetAttribute.DisplayValue, Is.EqualTo("Display Name"));

        // Enums keep the raw name for diffing, with the name spaced into words for display.
        var processing = Child(flow, "inboundValueProcessing")!;
        Assert.That(processing.Value, Is.EqualTo("TreatWhitespaceAsNoValue"));
        Assert.That(processing.DisplayValue, Is.EqualTo("Treat Whitespace As No Value"));

        // A reference with no loaded navigation falls back to id-only (no display value), never crashes.
        var objectType = Child(snapshot.Root, "connectedSystemObjectTypeId")!;
        Assert.That(objectType.Value, Is.EqualTo("7"));
        Assert.That(objectType.DisplayValue, Is.Null);
    }

    [Test]
    public void CreateSnapshot_SyncRule_DoesNotSerialiseBacklinksOrParentNavigation()
    {
        var rule = new SyncRule
        {
            Id = 1,
            Name = "Rule",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = 2,
            // Activities backlink is left as the EF default (null!) on purpose; it must never be walked.
            ConnectedSystem = new ConnectedSystem { Id = 2, Name = "AD" }
        };

        var json = ConfigurationSnapshotService.Serialise(_service.CreateSnapshot(rule, HashKey));

        // The parent ConnectedSystem navigation and the Activities backlink are excluded by design; capture is by FK id.
        Assert.That(json, Does.Not.Contain("\"activities\""));
        Assert.That(json, Does.Not.Contain("\"connectedSystem\":{"));
        // The FK id is captured.
        Assert.That(json, Does.Contain("\"connectedSystemId\""));
    }

    // -- Connected System redaction ------------------------------------------------------------------------------------

    [Test]
    public void CreateSnapshot_ConnectedSystem_CapturesObjectTypeMatchingRules()
    {
        // Simple Mode Object Matching Rules attach to a Connected System Object Type; they are the system's matching
        // configuration and must be snapshotted, or a rule change diffs as "no change".
        var connectedSystem = new ConnectedSystem
        {
            Id = 3,
            Name = "AD",
            ConnectorDefinitionId = 4,
            ObjectTypes =
            [
                new ConnectedSystemObjectType
                {
                    Id = 7, Name = "user", Selected = true,
                    ObjectMatchingRules = [new ObjectMatchingRule { Id = 9, Order = 1, TargetMetaverseAttributeId = 5 }]
                }
            ]
        };

        var snapshot = _service.CreateSnapshot(connectedSystem, HashKey);

        var objectType = Child(snapshot.Root, "objectTypes")!.Children![0];
        var matchingRules = Child(objectType, "objectMatchingRules");
        Assert.That(matchingRules, Is.Not.Null, "the object type's matching rules must be part of the system's configuration snapshot");
        Assert.That(matchingRules!.Children, Has.Count.EqualTo(1));
        Assert.That(matchingRules.Children![0].ItemId, Is.EqualTo(9), "collection items must carry the stable DB id for diff matching");
        Assert.That(Child(matchingRules.Children[0], "targetMetaverseAttributeId")!.Value, Is.EqualTo("5"));
    }

    [Test]
    public void CreateSnapshot_ConnectedSystem_ExcludesRuntimeStatus()
    {
        // Status (Active/Deleting) is runtime state, not configuration; snapshotting it would record phantom
        // configuration changes around deletion attempts.
        var connectedSystem = new ConnectedSystem { Id = 3, Name = "AD", ConnectorDefinitionId = 4, Status = ConnectedSystemStatus.Deleting };

        var snapshot = _service.CreateSnapshot(connectedSystem, HashKey);

        Assert.That(Child(snapshot.Root, "status"), Is.Null, "runtime status must not be part of the configuration snapshot");
    }

    [Test]
    public void CreateSnapshot_ConnectedSystem_RedactsEncryptedSettingAndKeepsPlainSetting()
    {
        const string secret = "super-secret-pw";
        var cs = BuildConnectedSystemWithSecret(secret, out var ciphertext);

        var snapshot = _service.CreateSnapshot(cs, HashKey);
        var json = ConfigurationSnapshotService.Serialise(snapshot);

        // Hard requirement: neither the plaintext nor the ciphertext may ever appear in the snapshot.
        Assert.That(json, Does.Not.Contain(secret), "Plaintext secret must never be serialised.");
        Assert.That(json, Does.Not.Contain(ciphertext), "Encrypted secret value must never be serialised.");

        var settings = Child(snapshot.Root, "settingValues")!;
        var bindPassword = settings.Children!.Single(c => c.Label == "Bind password");
        Assert.That(bindPassword.IsSecret, Is.True);
        Assert.That(bindPassword.Value, Is.EqualTo(ExpectedHash(secret)), "Secret is represented by a keyed hash of the plaintext.");

        var server = settings.Children!.Single(c => c.Label == "Server");
        Assert.That(server.IsSecret, Is.False);
        Assert.That(server.Value, Is.EqualTo("ldaps://dc1"));
    }

    [Test]
    public void CreateSnapshot_ConnectedSystem_RedactsWhenSettingNavigationIsNotLoaded()
    {
        // Robust detection: a populated StringEncryptedValue is redacted even if the Setting navigation is null,
        // because StringEncryptedValue is only ever populated for encrypted settings.
        var cs = new ConnectedSystem { Id = 9, Name = "AD", ConnectorDefinitionId = 4 };
        cs.SettingValues.Add(new ConnectedSystemSettingValue { Id = 11, StringEncryptedValue = _protection.Protect("pw") });

        var snapshot = _service.CreateSnapshot(cs, HashKey);
        var json = ConfigurationSnapshotService.Serialise(snapshot);

        Assert.That(json, Does.Not.Contain("pw\""));
        var node = Child(snapshot.Root, "settingValues")!.Children!.Single();
        Assert.That(node.IsSecret, Is.True);
        Assert.That(node.Value, Is.EqualTo(ExpectedHash("pw")));
    }

    [Test]
    public void CreateSnapshot_ConnectedSystem_SecretHashIsDeterministicAndDetectsChange()
    {
        var unchangedA = HashOfSecretInSnapshot("same-value");
        var unchangedB = HashOfSecretInSnapshot("same-value");
        var changed = HashOfSecretInSnapshot("different-value");

        Assert.That(unchangedA, Is.EqualTo(unchangedB), "Same secret and key must hash identically (so 'unchanged' is detectable).");
        Assert.That(changed, Is.Not.EqualTo(unchangedA), "A changed secret must hash differently (so 'changed' is detectable).");
    }

    [Test]
    public void CreateSnapshot_ConnectedSystem_ExcludesOperationalCollections()
    {
        var cs = new ConnectedSystem { Id = 9, Name = "AD", ConnectorDefinitionId = 4, PersistedConnectorData = "watermark-12345" };
        cs.SettingValues.Add(new ConnectedSystemSettingValue { Id = 1, StringValue = "value" });

        var json = ConfigurationSnapshotService.Serialise(_service.CreateSnapshot(cs, HashKey));

        Assert.That(json, Does.Not.Contain("watermark-12345"), "PersistedConnectorData is opaque operational data and must be excluded.");
        Assert.That(json, Does.Not.Contain("\"objects\""));
        Assert.That(json, Does.Not.Contain("\"pendingExports\""));
    }

    [Test]
    public void CreateSnapshot_ConnectedSystem_ExcludesInternalSettingValuesValidFlag()
    {
        // SettingValuesValid is internal UI-flow state (whether the connector has validated the settings), not
        // configuration, so it must never appear in a configuration change history.
        var cs = new ConnectedSystem { Id = 9, Name = "AD", ConnectorDefinitionId = 4, SettingValuesValid = true };

        var snapshot = _service.CreateSnapshot(cs, HashKey);

        Assert.That(Child(snapshot.Root, "settingValuesValid"), Is.Null, "internal validation state is not configuration");
    }

    [Test]
    public void CreateSnapshot_ConnectedSystem_SkipsUnconfiguredSettingValues()
    {
        // An unset setting (no value) is not configuration; capturing it produces empty "+ File Path:" noise at creation
        // and misleading empty-to-value modifications later. Only populated settings are captured, matching how the
        // top-level scalars skip empties.
        var cs = new ConnectedSystem { Id = 9, Name = "AD", ConnectorDefinitionId = 4 };
        cs.SettingValues.Add(new ConnectedSystemSettingValue { Id = 1, Setting = new ConnectorDefinitionSetting { Id = 1, Name = "File Path", Type = ConnectedSystemSettingType.String } });
        cs.SettingValues.Add(new ConnectedSystemSettingValue { Id = 2, Setting = new ConnectorDefinitionSetting { Id = 2, Name = "Delimiter", Type = ConnectedSystemSettingType.String }, StringValue = "," });

        var settings = Child(_service.CreateSnapshot(cs, HashKey).Root, "settingValues")!;

        Assert.That(settings.Children, Has.Count.EqualTo(1), "the empty File Path setting must be skipped");
        Assert.That(settings.Children!.Single().Label, Is.EqualTo("Delimiter"));
    }

    // -- Schedule (Guid-keyed configuration object) --------------------------------------------------------------------

    [Test]
    public void CreateSnapshot_Schedule_CapturesConfigFieldsAndStepsWithGuidItemIds()
    {
        var stepAId = Guid.NewGuid();
        var stepBId = Guid.NewGuid();
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = "Nightly Sync",
            Description = "runs overnight",
            IsEnabled = true,
            TriggerType = ScheduleTriggerType.Cron,
            PatternType = SchedulePatternType.Interval,
            IntervalValue = 2,
            IntervalUnit = ScheduleIntervalUnit.Hours,
            IntervalWindowStart = "06:00",
            IntervalWindowEnd = "18:00",
            DaysOfWeek = "1,2,3,4,5",
            RunTimes = "09:00,12:00",
            CronExpression = "0 6 * * 1-5"
        };
        // Added out of StepIndex order to prove the snapshot orders by StepIndex.
        schedule.Steps.Add(new ScheduleStep { Id = stepBId, StepIndex = 1, StepType = ScheduleStepType.PowerShell, ScriptPath = "/x.ps1", ExecutionMode = StepExecutionMode.ParallelWithPrevious });
        schedule.Steps.Add(new ScheduleStep { Id = stepAId, StepIndex = 0, StepType = ScheduleStepType.RunProfile, ConnectedSystemId = 3, RunProfileId = 7 });

        var snapshot = _service.CreateSnapshot(schedule, HashKey);

        Assert.That(snapshot.ObjectType, Is.EqualTo(ConfigurationSnapshotService.ScheduleObjectType));
        Assert.That(snapshot.ObjectGuidId, Is.EqualTo(schedule.Id), "a Guid-keyed object carries its id in ObjectGuidId");
        Assert.That(snapshot.ObjectId, Is.EqualTo(0), "a Guid-keyed object does not use the integer ObjectId");
        Assert.That(snapshot.ObjectName, Is.EqualTo("Nightly Sync"));

        Assert.That(Child(snapshot.Root, "name")!.Value, Is.EqualTo("Nightly Sync"));
        Assert.That(Child(snapshot.Root, "enabled")!.Value, Is.EqualTo("true"));
        Assert.That(Child(snapshot.Root, "triggerType")!.Value, Is.EqualTo("Cron"));
        Assert.That(Child(snapshot.Root, "intervalUnit")!.Value, Is.EqualTo("Hours"), "a nullable enum is captured when set");
        Assert.That(Child(snapshot.Root, "cronExpression")!.Value, Is.EqualTo("0 6 * * 1-5"));

        var steps = Child(snapshot.Root, "steps")!;
        Assert.That(steps.NodeType, Is.EqualTo(ConfigurationSnapshotNodeType.Collection));
        Assert.That(steps.Children, Has.Count.EqualTo(2));

        var firstStep = steps.Children![0];
        Assert.That(firstStep.ItemGuidId, Is.EqualTo(stepAId), "steps are ordered by StepIndex and carry their Guid id for diff matching");
        Assert.That(firstStep.ItemId, Is.Null, "a Guid-keyed item does not use the integer ItemId");
        Assert.That(Child(firstStep, "connectedSystemId")!.Value, Is.EqualTo("3"));
        Assert.That(steps.Children![1].ItemGuidId, Is.EqualTo(stepBId));
    }

    [Test]
    public void CreateSnapshot_Schedule_RedactsSqlConnectionStringSecret()
    {
        const string connectionString = "Server=db;Database=jim;User Id=sa;Password=super-secret-pw;";
        var schedule = new Schedule { Id = Guid.NewGuid(), Name = "SQL Job" };
        schedule.Steps.Add(new ScheduleStep { Id = Guid.NewGuid(), StepIndex = 0, StepType = ScheduleStepType.SqlScript, SqlScriptPath = "/job.sql", SqlConnectionString = connectionString });

        var snapshot = _service.CreateSnapshot(schedule, HashKey);
        var json = ConfigurationSnapshotService.Serialise(snapshot);

        // Hard requirement: the connection string (which can contain a credential) must never be serialised.
        Assert.That(json, Does.Not.Contain("super-secret-pw"), "the connection string secret must never be serialised");
        Assert.That(json, Does.Not.Contain(connectionString));

        var step = Child(snapshot.Root, "steps")!.Children!.Single();
        var connection = Child(step, "sqlConnectionString")!;
        Assert.That(connection.IsSecret, Is.True);
        // SqlConnectionString is stored in plaintext, so the keyed hash is taken over the plaintext directly.
        Assert.That(connection.Value, Is.EqualTo(ExpectedHash(connectionString)), "the connection string is represented by a keyed hash of its plaintext");

        // A non-sensitive step field is captured in the clear.
        Assert.That(Child(step, "sqlScriptPath")!.Value, Is.EqualTo("/job.sql"));
    }

    [Test]
    public void CreateSnapshot_Schedule_ExcludesRuntimeAndAuditState()
    {
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = "Sched",
            LastRunTime = DateTime.UtcNow,
            NextRunTime = DateTime.UtcNow.AddHours(1),
            CreatedByType = ActivityInitiatorType.User,
            CreatedByName = "admin-user",
            LastUpdatedByName = "editor-user"
        };

        var json = ConfigurationSnapshotService.Serialise(_service.CreateSnapshot(schedule, HashKey));

        // Runtime and audit state is not configuration and must never appear in a configuration change history.
        Assert.That(json, Does.Not.Contain("nextRunTime"));
        Assert.That(json, Does.Not.Contain("lastRunTime"));
        Assert.That(json, Does.Not.Contain("admin-user"));
        Assert.That(json, Does.Not.Contain("editor-user"));
        Assert.That(json, Does.Not.Contain("createdBy"));
    }

    // -- Connector Definition scope ------------------------------------------------------------------------------------

    [Test]
    public void CreateSnapshot_ConnectorDefinition_CapturesMetadataCapabilitiesSettingsAndFiles()
    {
        var definition = new ConnectorDefinition
        {
            Id = 7,
            Name = "LDAP",
            Description = "Directory connector",
            Url = "https://example.test/ldap",
            BuiltIn = true,
            SupportsFullImport = true,
            SupportsExport = true,
            SupportsPaging = true,
            Settings =
            {
                new ConnectorDefinitionSetting
                {
                    Id = 3,
                    Name = "Server",
                    Category = ConnectedSystemSettingCategory.Connectivity,
                    Type = ConnectedSystemSettingType.String,
                    Required = true,
                    DefaultStringValue = "ldaps://dc1"
                }
            }
        };
        definition.Files.Add(new ConnectorDefinitionFile { Id = 5, Filename = "Ldap.dll", FileSizeBytes = 2048, Version = "1.2.3", File = [1, 2, 3, 4] });

        var snapshot = _service.CreateSnapshot(definition, HashKey);

        Assert.That(snapshot.ObjectType, Is.EqualTo("ConnectorDefinition"));
        Assert.That(snapshot.ObjectId, Is.EqualTo(7));
        Assert.That(snapshot.ObjectName, Is.EqualTo("LDAP"));

        Assert.That(Child(snapshot.Root, "name")!.Value, Is.EqualTo("LDAP"));
        Assert.That(Child(snapshot.Root, "description")!.Value, Is.EqualTo("Directory connector"));
        Assert.That(Child(snapshot.Root, "builtIn")!.Value, Is.EqualTo("true"));

        var capabilities = Child(snapshot.Root, "capabilities")!;
        Assert.That(Child(capabilities, "supportsFullImport")!.Value, Is.EqualTo("true"));
        Assert.That(Child(capabilities, "supportsDeltaImport")!.Value, Is.EqualTo("false"));
        Assert.That(Child(capabilities, "supportsPaging")!.Value, Is.EqualTo("true"));

        var settings = Child(snapshot.Root, "settings")!;
        var setting = settings.Children!.Single();
        Assert.That(setting.ItemId, Is.EqualTo(3));
        Assert.That(Child(setting, "name")!.Value, Is.EqualTo("Server"));
        Assert.That(Child(setting, "required")!.Value, Is.EqualTo("true"));
        Assert.That(Child(setting, "defaultStringValue")!.Value, Is.EqualTo("ldaps://dc1"));

        var files = Child(snapshot.Root, "files")!;
        var file = files.Children!.Single();
        Assert.That(file.ItemId, Is.EqualTo(5));
        Assert.That(Child(file, "filename")!.Value, Is.EqualTo("Ldap.dll"));
        Assert.That(Child(file, "fileSizeBytes")!.Value, Is.EqualTo("2048"));
        Assert.That(Child(file, "version")!.Value, Is.EqualTo("1.2.3"));
        Assert.That(Child(file, "sha256")!.Value, Is.EqualTo(Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])).ToLowerInvariant()));
    }

    [Test]
    public void CreateSnapshot_ConnectorDefinition_NeverSerialisesFileBinaryContent()
    {
        var definition = new ConnectorDefinition { Id = 7, Name = "File" };
        // A recognisable byte sequence whose Base64 would appear in the JSON if the binary were serialised.
        definition.Files.Add(new ConnectorDefinitionFile { Id = 5, Filename = "File.dll", FileSizeBytes = 4, Version = "1.0.0", File = Encoding.UTF8.GetBytes("SECRETBINARY") });

        var json = ConfigurationSnapshotService.Serialise(_service.CreateSnapshot(definition, HashKey));

        Assert.That(json, Does.Not.Contain(Convert.ToBase64String(Encoding.UTF8.GetBytes("SECRETBINARY"))));
        Assert.That(json, Does.Not.Contain("SECRETBINARY"));
        // The file's metadata and content hash are captured; only the raw bytes are excluded.
        Assert.That(json, Does.Contain("sha256"));
    }

    [Test]
    public void CreateSnapshot_ConnectorDefinition_FileHashChangesWhenBytesChange()
    {
        string HashFor(byte[] bytes)
        {
            var definition = new ConnectorDefinition { Id = 7, Name = "File" };
            definition.Files.Add(new ConnectorDefinitionFile { Id = 5, Filename = "File.dll", FileSizeBytes = bytes.Length, Version = "1.0.0", File = bytes });
            return Child(Child(_service.CreateSnapshot(definition, HashKey).Root, "files")!.Children!.Single(), "sha256")!.Value!;
        }

        Assert.That(HashFor([1, 2, 3]), Is.Not.EqualTo(HashFor([1, 2, 4])));
    }

    // -- Example Data Set scope ----------------------------------------------------------------------------------------

    [Test]
    public void CreateSnapshot_ExampleDataSet_CapturesMetadataAndValueCountNotIndividualValues()
    {
        var dataSet = new ExampleDataSet
        {
            Id = 12,
            Name = "Job Titles",
            Culture = "en",
            BuiltIn = true,
            Values =
            {
                new ExampleDataSetValue { Id = 1, StringValue = "UNIQUEJOBTITLEONE" },
                new ExampleDataSetValue { Id = 2, StringValue = "UNIQUEJOBTITLETWO" }
            }
        };

        var snapshot = _service.CreateSnapshot(dataSet, HashKey);
        var json = ConfigurationSnapshotService.Serialise(snapshot);

        Assert.That(snapshot.ObjectType, Is.EqualTo("ExampleDataSet"));
        Assert.That(snapshot.ObjectId, Is.EqualTo(12));
        Assert.That(Child(snapshot.Root, "name")!.Value, Is.EqualTo("Job Titles"));
        Assert.That(Child(snapshot.Root, "culture")!.Value, Is.EqualTo("en"));
        Assert.That(Child(snapshot.Root, "builtIn")!.Value, Is.EqualTo("true"));
        Assert.That(Child(snapshot.Root, "valueCount")!.Value, Is.EqualTo("2"));
        // The value collection can hold thousands of strings; only its size is captured, never the values themselves.
        Assert.That(json, Does.Not.Contain("UNIQUEJOBTITLEONE"));
        Assert.That(json, Does.Not.Contain("UNIQUEJOBTITLETWO"));
    }

    // -- Example Data Template scope -----------------------------------------------------------------------------------

    [Test]
    public void CreateSnapshot_ExampleDataTemplate_CapturesObjectTypesAttributesAndReferencedSets()
    {
        var userType = new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" };
        var emailAttribute = new MetaverseAttribute { Id = 5, Name = "Email" };
        var firstnamesSet = new ExampleDataSet { Id = 7, Name = "Firstnames" };

        var template = new ExampleDataTemplate { Id = 3, Name = "Users and Groups", BuiltIn = true };
        var objectType = new ExampleDataObjectType { Id = 9, MetaverseObjectType = userType, ObjectsToCreate = 10000 };
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 20,
            MetaverseAttribute = emailAttribute,
            PopulatedValuesPercentage = 100,
            Pattern = "{First Name}@example.io",
            ExampleDataSetInstances = { new ExampleDataSetInstance { Id = 30, ExampleDataSet = firstnamesSet, Order = 0 } }
        });
        template.ObjectTypes.Add(objectType);

        var snapshot = _service.CreateSnapshot(template, HashKey);

        Assert.That(snapshot.ObjectType, Is.EqualTo("ExampleDataTemplate"));
        Assert.That(snapshot.ObjectId, Is.EqualTo(3));
        Assert.That(Child(snapshot.Root, "name")!.Value, Is.EqualTo("Users and Groups"));

        var objectTypes = Child(snapshot.Root, "objectTypes")!;
        var objectTypeNode = objectTypes.Children!.Single();
        Assert.That(objectTypeNode.ItemId, Is.EqualTo(9));
        Assert.That(Child(objectTypeNode, "metaverseObjectTypeId")!.Value, Is.EqualTo("1"));
        Assert.That(Child(objectTypeNode, "objectsToCreate")!.Value, Is.EqualTo("10000"));

        var attributeNode = Child(objectTypeNode, "attributes")!.Children!.Single();
        Assert.That(attributeNode.ItemId, Is.EqualTo(20));
        Assert.That(Child(attributeNode, "metaverseAttributeId")!.Value, Is.EqualTo("5"));
        Assert.That(Child(attributeNode, "pattern")!.Value, Is.EqualTo("{First Name}@example.io"));
        Assert.That(Child(attributeNode, "populatedValuesPercentage")!.Value, Is.EqualTo("100"));

        var referencedSet = Child(attributeNode, "referencedDataSets")!.Children!.Single();
        Assert.That(referencedSet.ItemId, Is.EqualTo(30));
        Assert.That(referencedSet.Value, Is.EqualTo("7"), "a referenced data set is captured by its stable id");
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static ConfigurationSnapshotNode? Child(ConfigurationSnapshotNode node, string key) =>
        node.Children?.FirstOrDefault(c => c.Key == key);

    private static string ExpectedHash(string plaintext)
    {
        using var hmac = new HMACSHA256(HashKey);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(plaintext)));
    }

    private ConnectedSystem BuildConnectedSystemWithSecret(string secret, out string ciphertext)
    {
        var secretSetting = new ConnectorDefinitionSetting { Id = 1, Name = "Bind password", Type = ConnectedSystemSettingType.StringEncrypted };
        var serverSetting = new ConnectorDefinitionSetting { Id = 2, Name = "Server", Type = ConnectedSystemSettingType.String };
        ciphertext = _protection.Protect(secret)!;

        var cs = new ConnectedSystem { Id = 9, Name = "Active Directory", ConnectorDefinitionId = 4 };
        cs.SettingValues.Add(new ConnectedSystemSettingValue { Id = 11, Setting = secretSetting, StringEncryptedValue = ciphertext });
        cs.SettingValues.Add(new ConnectedSystemSettingValue { Id = 12, Setting = serverSetting, StringValue = "ldaps://dc1" });
        return cs;
    }

    private string? HashOfSecretInSnapshot(string secret)
    {
        var cs = new ConnectedSystem { Id = 9, Name = "AD", ConnectorDefinitionId = 4 };
        cs.SettingValues.Add(new ConnectedSystemSettingValue
        {
            Id = 11,
            Setting = new ConnectorDefinitionSetting { Id = 1, Name = "Password", Type = ConnectedSystemSettingType.StringEncrypted },
            StringEncryptedValue = _protection.Protect(secret)
        });
        var snapshot = _service.CreateSnapshot(cs, HashKey);
        return Child(snapshot.Root, "settingValues")!.Children!.Single().Value;
    }

    /// <summary>
    /// A test double for credential protection that round-trips via a recognisable prefix and base64 so that the
    /// ciphertext does not literally contain the plaintext (letting tests assert both are absent from a snapshot).
    /// </summary>
    private sealed class RoundTripCredentialProtection : ICredentialProtectionService
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
