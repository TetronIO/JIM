// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Application.Servers;
using JIM.Models.Exceptions;
using JIM.Models.Localisation;
using JIM.Models.Scheduling;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Sync;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// The text a stored Activity carries (#3039): each message says exactly what the English description it replaced says
/// (the REST API, logs and older readers keep reading that), and renders in French for the portal.
/// </summary>
[TestFixture]
public class ActivityMessageFamiliesTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    private static string InFrench(JimMessage message) => MessageRenderer.Render(message, French, French);

    private static ConnectedSystemRunProfile RunProfile(bool enabled, ConnectedSystemRunProfileDisabledReason? reason = null, string? partitionName = null) => new()
    {
        Id = 7,
        Name = "Full Import (Glitterband)",
        RunType = ConnectedSystemRunType.FullImport,
        Enabled = enabled,
        DisabledReason = reason,
        DisabledPartitionName = partitionName
    };

    [TestCase(nameof(ActivityMessages.TargetBuiltInConfiguration), null, "Built-in configuration")]
    [TestCase(nameof(ActivityMessages.TargetFactoryReset), null, "Factory reset")]
    [TestCase(nameof(ActivityMessages.TargetInteractiveSignInSucceeded), null, "Interactive sign-in succeeded")]
    [TestCase(nameof(ActivityMessages.TargetTemporalScopeReconciliation), null, "Temporal Scope Reconciliation")]
    [TestCase(nameof(ActivityMessages.TargetHistoryRetentionCleanup), null, "History Retention Cleanup")]
    [TestCase(nameof(ActivityMessages.TargetAttributePriorityOrder), "mail", "Attribute priority order for mail")]
    [TestCase(nameof(ActivityMessages.TargetRuleFor), "User", "Rule for User")]
    [TestCase(nameof(ActivityMessages.TargetRuleForUnknownType), null, "Rule for Object Type")]
    public void TargetName_EveryKind_ReadsAsTheTargetNameAlwaysHas_AndRendersInFrench(string key, string? name, string english)
    {
        var message = name == null ? ActivityMessage.Create(key) : ActivityMessage.Create(key, name);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo(english));
            Assert.That(InFrench(message), Is.Not.EqualTo(english));
        }
    }

    [Test]
    public void TargetName_MappingAndTestCase_StartWithThePrefixesThePortalRecognises()
    {
        // Helpers.cs recognises mapping and test case Activities by these prefixes on the stored English, so the
        // catalogue's wording and the constants must not drift apart.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ActivityMessage.Create(nameof(ActivityMessages.TargetMappingTo), "mail").ToEnglish(),
                Is.EqualTo($"{JIM.Models.Activities.Activity.SyncRuleMappingTargetNamePrefix}mail"));
            Assert.That(ActivityMessage.Create(nameof(ActivityMessages.TargetTestCase), "In scope").ToEnglish(),
                Is.EqualTo($"{JIM.Models.Activities.Activity.ConfigurationTestCaseTargetNamePrefix}'In scope'"));
        }
    }

    [TestCase(nameof(ActivityMessages.CreatedBuiltInServiceSetting), "Created built-in Service Setting 'Sync page size'")]
    [TestCase(nameof(ActivityMessages.CreatedBuiltInExampleDataSet), "Created built-in Example Data Set 'Sync page size'")]
    [TestCase(nameof(ActivityMessages.CreatedBuiltInExampleDataTemplate), "Created built-in Example Data Template 'Sync page size'")]
    [TestCase(nameof(ActivityMessages.CreatedBuiltInPredefinedSearch), "Created built-in Predefined Search 'Sync page size'")]
    [TestCase(nameof(ActivityMessages.CreatedBuiltInMetaverseObjectType), "Created built-in Metaverse Object Type 'Sync page size'")]
    [TestCase(nameof(ActivityMessages.CreatedBuiltInMetaverseAttribute), "Created built-in Metaverse Attribute 'Sync page size'")]
    [TestCase(nameof(ActivityMessages.CreatedBuiltInConnectorDefinition), "Created built-in Connector Definition 'Sync page size'")]
    [TestCase(nameof(ActivityMessages.CreatedBuiltInRole), "Created built-in Role 'Sync page size'")]
    public void CreatedBuiltIn_EveryKind_ReadsAsTheSeedingSummaryAlwaysHas_AndRendersInFrench(string key, string english)
    {
        var message = ActivityMessage.Create(key, "Sync page size");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo(english));
            Assert.That(InFrench(message), Does.Contain("« Sync page size »"));
        }
    }

    [Test]
    public void RunRefusal_DisabledByHand_SaysWhatTheModelDescriptionSays_AndRendersInFrench()
    {
        var runProfile = RunProfile(false, ConnectedSystemRunProfileDisabledReason.Manual);

        var message = ActivityMessage.RunRefusal(runProfile);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo(RunProfileEnablement.DescribeRunRefusal(runProfile)));
            Assert.That(InFrench(message), Does.StartWith("Le profil d’exécution « Full Import (Glitterband) » est désactivé"));
        }
    }

    [Test]
    public void RunRefusal_DisabledByARemovedPartition_NamesThePartitionAndTheRemedy()
    {
        var runProfile = RunProfile(false, ConnectedSystemRunProfileDisabledReason.PartitionRemoved, "DC=glitterband");

        var message = ActivityMessage.RunRefusal(runProfile);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo(RunProfileEnablement.DescribeRunRefusal(runProfile)));
            Assert.That(InFrench(message), Does.Contain("DC=glitterband").And.Contain("choisissant la partition"));
        }
    }

    [Test]
    public void ImportIncompleteRefusal_SaysWhatTheModelDescriptionSays()
    {
        var connectedSystem = new ConnectedSystem
        {
            Name = "Glitterband",
            ImportIncompleteSince = new DateTime(2026, 11, 16, 10, 3, 12, DateTimeKind.Utc),
            Partitions = [new ConnectedSystemPartition { Name = "DC=a", Selected = true }]
        };
        var runProfile = RunProfile(true);

        var message = ActivityMessage.ImportIncompleteRefusal(connectedSystem, runProfile);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo(ImportCompleteness.DescribeSynchronisationRefusal(connectedSystem, runProfile)));
            Assert.That(InFrench(message), Does.Contain("16 Nov 2026 10:03:12 UTC").And.Contain("toutes les partitions sélectionnées"));
        }
    }

    [Test]
    public void ImportStillIncomplete_SaysWhatTheModelDescriptionSays([Values] bool readOnlyChanges, [Values] bool onePartition)
    {
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ImportIncompleteSince = new DateTime(2026, 11, 16, 10, 3, 12, DateTimeKind.Utc) };

        var message = ActivityMessage.ImportStillIncomplete(connectedSystem, readOnlyChanges, onePartition);

        Assert.That(message.ToEnglish(), Is.EqualTo(ImportCompleteness.DescribeStillIncompleteAfterImport(connectedSystem, readOnlyChanges, onePartition)));
    }

    [TestCase(true, 0)]
    [TestCase(true, 1)]
    [TestCase(true, 12)]
    [TestCase(false, 3)]
    [TestCase(false, 0)]
    public void DeltaImportReset_SaysWhatTheModelDescriptionSays(bool systemWatermark, int partitions)
    {
        var result = new ClearConnectedSystemResult { ImportWatermarkRemoved = systemWatermark, PartitionWatermarksRemoved = partitions };

        var message = ActivityMessage.DeltaImportReset(result);

        Assert.That(message?.ToEnglish(), Is.EqualTo(result.DescribeDeltaImportReset()));
    }

    [Test]
    public void ScopeReconciliation_SaysWhatTheModelDescriptionSays_AndNothingWhenNothingIsUnusual()
    {
        var unusual = new ScopeReconciliationResult { FullPassRules = 2, FullPassObjectsEvaluated = 12_345, UndecidableObjects = 3 };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ActivityMessage.ScopeReconciliation(unusual)?.ToEnglish(), Is.EqualTo(unusual.Describe()));
            Assert.That(ActivityMessage.ScopeReconciliation(new ScopeReconciliationResult()), Is.Null);
        }
    }

    [Test]
    public void HierarchyRefreshSummary_SaysWhatTheModelSummarySays()
    {
        var result = new HierarchyRefreshResult();
        Assert.That(ActivityMessage.HierarchyRefreshSummary(result).ToEnglish(), Is.EqualTo(result.GetSummary()));

        result.AddedPartitions.Add(new HierarchyChangeItem());
        result.RemovedContainers.Add(new HierarchyChangeItem());
        result.RenamedPartitions.Add(new HierarchyRenameItem());
        result.PartitionsSelectedForMovedContainers.Add(new HierarchyPartitionSelectionItem());
        Assert.That(ActivityMessage.HierarchyRefreshSummary(result).ToEnglish(), Is.EqualTo(result.GetSummary()));
    }

    [Test]
    public void SynchronisedDeprovisioningAbandoned_SaysWhatTheConstantSays()
    {
        var message = ActivityMessage.Create(nameof(ActivityMessages.SynchronisedDeprovisioningAbandoned));

        Assert.That(message.ToEnglish(), Is.EqualTo(ConnectedSystemServer.SynchronisedDeprovisioningAbandonedMessage));
    }

    [Test]
    public void ConfigurationChangeNotRecorded_SaysWhatTheExceptionSays()
    {
        var exception = new ConfigurationChangeSynchronisationNotRecordedException("Glitterband", "User", new InvalidOperationException("db"));

        var message = ActivityMessage.Create(nameof(ActivityMessages.ConfigurationChangeNotRecorded), exception.ConnectedSystemName, exception.ObjectTypeName);

        Assert.That(message.ToEnglish(), Is.EqualTo(exception.Message));
    }

    [Test]
    public void StepNotRunReason_EveryReasonJimWrites_HasAMessageSayingExactlyIt()
    {
        string[] reasons =
        [
            ScheduleStepNotRunReasons.EarlierStepStoppedSchedule,
            ScheduleStepNotRunReasons.ScheduleCouldNotStart,
            ScheduleStepNotRunReasons.ExecutionCancelled
        ];

        using (Assert.EnterMultipleScope())
        {
            foreach (var reason in reasons)
                Assert.That(ActivityMessage.StepNotRunReason(reason)?.ToEnglish(), Is.EqualTo(reason));

            Assert.That(ActivityMessage.StepNotRunReason("Importing objects"), Is.Null, "Progress text is not a reason");
            Assert.That(InFrench(ActivityMessage.StepNotRunReason(ScheduleStepNotRunReasons.EarlierStepStoppedSchedule)!), Does.StartWith("Non exécutée"));
        }
    }

    [Test]
    public void AddWarning_OntoEnglishOnlyWarning_KeepsBothAndTheLineBreak()
    {
        var activity = new JIM.Models.Activities.Activity { WarningMessage = "An earlier warning." };

        ActivityMessage.AddWarning(activity, ActivityMessage.Create(nameof(ActivityMessages.RoleCreated), "Admins"));
        ActivityMessage.AddWarning(activity, ActivityMessage.Create(nameof(ActivityMessages.RoleCreated), "Users"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.WarningMessage, Is.EqualTo("An earlier warning.\nCreated Role 'Admins'\nCreated Role 'Users'"));
            Assert.That(InFrench(activity.WarningMessageDetail!), Is.EqualTo("An earlier warning.\nRôle « Admins » créé.\nRôle « Users » créé."));
        }
    }

    [Test]
    public void SetError_ExceptionCarryingAMessage_StoresBothAndAnEnglishOnlyOneStoresNoMessage()
    {
        var item = new JIM.Models.Activities.ActivityRunProfileExecutionItem();

        ActivityMessage.SetError(item, new JimMessageException(ActivityMessage.Create(nameof(ActivityMessages.CleanupFailed), "disk full")));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ErrorMessage, Is.EqualTo("Cleanup failed: disk full"));
            Assert.That(InFrench(item.ErrorMessageDetail!), Is.EqualTo("Échec du nettoyage : disk full"));
        }

        ActivityMessage.SetError(item, new InvalidOperationException("Framework text"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ErrorMessage, Is.EqualTo("Framework text"));
            Assert.That(item.ErrorMessageDetailJson, Is.Null);
        }
    }

    [Test]
    public void AuditSummaries_KeepTheirEnglish_AndRenderInFrench()
    {
        var created = ActivityMessage.Create(nameof(ActivityMessages.ApiKeyCreated), "Pipeline");
        var added = ActivityMessage.Create(nameof(ActivityMessages.CertificateAdded), "Root", "CN=Root");
        var unchanged = ActivityMessage.Create(nameof(ActivityMessages.CertificateUnchanged));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(created.ToEnglish(), Is.EqualTo("Created API key 'Pipeline'"));
            Assert.That(InFrench(created), Is.EqualTo("Clé d’API « Pipeline » créée."));
            Assert.That(added.ToEnglish(), Is.EqualTo("Added trusted certificate 'Root' (Subject: CN=Root)"));
            Assert.That(InFrench(added), Is.EqualTo("Certificat approuvé « Root » ajouté (objet : CN=Root)."));
            Assert.That(unchanged.ToEnglish(), Is.EqualTo("No changes made to trusted certificate"));
        }
    }

    [Test]
    public void ImportSummary_KeepsItsEnglish_AndRendersInFrench()
    {
        var message = ActivityMessage.ImportComplete(objects: 1, created: 1, updated: 0, unchanged: 0, connectorReportedDeletions: 0,
            detectedDeletions: 2, errors: 1, throughput: " in 3 min 12 sec (avg 312 obj/s)");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo("Import complete: 1 object (1 created, 0 updated, 1 error) in 3 min 12 sec (avg 312 obj/s); 2 no longer found and marked as deleted"));
            Assert.That(InFrench(message), Does.StartWith("Importation terminée : 1 objet (1 créé(s), 0 mis à jour, 1 erreur)"));
        }
    }

    [Test]
    public void ThroughputCompletion_RendersTheDurationInTheReadersLanguage()
    {
        var message = ActivityMessage.ThroughputCompletion(192, 60_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message?.ToEnglish(), Is.EqualTo(" in 3 min 12 sec (avg 313 obj/s)"));
            Assert.That(InFrench(message!), Is.EqualTo(" en 3 min 12 s (moyenne : 313 obj/s)"));
            Assert.That(ActivityMessage.ThroughputCompletion(0.5, 100), Is.Null);
        }
    }
}
