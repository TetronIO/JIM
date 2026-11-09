// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exchange;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Staging an Exchange change during export execution (#2021, PRD requirements 5, 7, 8, 12, 13 and 16, decision D9):
/// a successful Create or Update whose Pending Export changes a connector-defined Exchange attribute leaves the
/// account's declared recipient state on the Exchange queue (<see cref="PendingExchangeChange"/>), with a follow-up
/// Activity under the export's execution item. A declared type that does not fit the object, or that would change an
/// existing recipient's type, queues nothing and is reported on a follow-up Activity instead.
/// <para>
/// As with Initial Passwords, the directory write is the record: nothing about the Exchange step may alter the export.
/// </para>
/// </summary>
[TestFixture]
public class ExchangeChangeStagingTests
{
    private const int ProvisioningRuleId = 92021;
    private const int UpdatingRuleId = 92022;
    private const string DomainController = "dc01.corp.example";

    // groupType values as Active Directory stores them.
    private const int GlobalSecurityGroup = unchecked((int)0x80000002);
    private const int UniversalSecurityGroup = unchecked((int)0x80000008);

    #region accessors
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    private int _nextAttributeId = 92100;
    #endregion

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        var runProfiles = TestUtilities.GetConnectedSystemRunProfileData();
        var exportRunProfile = runProfiles.Single(rp => rp.Name == "Dummy Target System Export");
        var activities = TestUtilities.GetActivityData(exportRunProfile.RunType, exportRunProfile.Id);
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        var objectTypes = TestUtilities.GetConnectedSystemObjectTypeData();

        MockJimDbContext = new Mock<JimDbContext>();
        // Tests run as though the feature had shipped (test/CLAUDE.md).
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.Activities).Returns(activities.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemAttributes).Returns(objectTypes.SelectMany(t => t.Attributes).ToList().BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(objectTypes.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(new List<ConnectedSystemObject>().BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(TestUtilities.GetConnectedSystemPartitionData().BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(runProfiles.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(ConnectedSystemsData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(TestUtilities.GetMetaverseObjectTypeData().BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(TestUtilities.GetMetaverseObjectData().BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(new List<PendingExport>().BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(TestUtilities.GetSyncRuleData().BuildMockDbSet().Object);

        SyncRepo = TestUtilities.CreateSyncRepository(activity: activities.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
    }

    #region Staging

    /// <summary>
    /// A Create declaring a mailbox leaves the account's declared state on the queue, with everything the delivery
    /// lane needs: the account, its identity, the Synchronisation Rule, the declared values and the domain controller
    /// the directory write went to.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateDeclaringAMailbox_StagesTheDeclaredStateAsync()
    {
        var account = Arrange("user", PendingExportChangeType.Create, changes: (type, rule) =>
        [
            Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox, rule),
            Change(type, ExchangeRecipientAttributes.Alias, "ada.lovelace", rule),
            Change(type, ExchangeRecipientAttributes.Database, "DB01", rule)
        ]);

        var result = await ExportAsync();

        var staged = SyncRepo.PendingExchangeChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SuccessCount, Is.EqualTo(1));
            Assert.That(staged.ConnectedSystemId, Is.EqualTo(account.System.Id));
            Assert.That(staged.ConnectedSystemObjectId, Is.EqualTo(account.Cso.Id));
            Assert.That(staged.MetaverseObjectId, Is.EqualTo(account.Cso.MetaverseObjectId));
            Assert.That(staged.SyncRuleId, Is.EqualTo(ProvisioningRuleId));
            Assert.That(staged.RecipientType, Is.EqualTo(ExchangeRecipientTypes.Mailbox));
            Assert.That(staged.Alias, Is.EqualTo("ada.lovelace"));
            Assert.That(staged.Database, Is.EqualTo("DB01"));
            Assert.That(staged.PrimarySmtpAddress, Is.Null);
            Assert.That(staged.ExternalAddress, Is.Null);
            Assert.That(staged.DomainController, Is.EqualTo(DomainController));
            Assert.That(staged.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(staged.AttemptCount, Is.Zero);
            Assert.That(result.ExchangeChangesStagedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// The change's Activity is a follow-up of the export item (#2668): system-initiated, already complete, naming the
    /// Connected System, and parented by the item, which takes that id when it is written after the batch.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateDeclaringAMailbox_WritesAFollowUpActivityUnderTheExportItemAsync()
    {
        var account = Arrange("user", PendingExportChangeType.Create, changes: (type, rule) =>
            [Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox, rule)]);

        var result = await ExportAsync();

        var staged = SyncRepo.PendingExchangeChanges.Values.Single();
        var activity = SyncRepo.Activities[staged.ActivityId];
        var item = result.ProcessedExportItems.Single(i => i.PendingExportId == account.Export.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.ExchangeRecipient));
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.SetExchangeRecipient));
            Assert.That(activity.TargetName, Is.EqualTo(account.System.Name));
            Assert.That(activity.TargetContext, Is.EqualTo(ExchangeRecipientTypes.Mailbox));
            Assert.That(activity.ConnectedSystemId, Is.EqualTo(account.System.Id));
            Assert.That(activity.ConnectedSystemObjectId, Is.EqualTo(account.Cso.Id));
            Assert.That(activity.MetaverseObjectId, Is.EqualTo(account.Cso.MetaverseObjectId));
            Assert.That(activity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(activity.Message, Is.EqualTo($"Mailbox queued for Exchange on {account.System.Name}."));
            Assert.That(item.ExecutionItemId, Is.Not.Null, "the item is named before it exists, so the Activity can point at it");
            Assert.That(activity.ParentRunProfileExecutionItemId, Is.EqualTo(item.ExecutionItemId));
            Assert.That(activity.ParentActivityId, Is.Null, "an Activity has one parent");
        }
    }

    /// <summary>
    /// The declared state is the account's current values of the five attributes, with the export's changes laid over
    /// them: an attribute the export does not change keeps the value the last import read, and a removal clears one.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_UpdateChangingSomeAttributes_DeclaresTheImportedValuesOverlaidWithTheChangesAsync()
    {
        Arrange("user", PendingExportChangeType.Update,
            imported: type =>
            [
                Imported(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox),
                Imported(type, ExchangeRecipientAttributes.Alias, "ada"),
                Imported(type, ExchangeRecipientAttributes.Database, "DB01"),
                Imported(type, ExchangeRecipientAttributes.PrimarySmtpAddress, "ada@corp.example")
            ],
            changes: (type, rule) =>
            [
                Change(type, ExchangeRecipientAttributes.Alias, "ada.lovelace", rule),
                Change(type, ExchangeRecipientAttributes.PrimarySmtpAddress, "ada@corp.example", rule, PendingExportAttributeChangeType.Remove)
            ]);

        await ExportAsync();

        var staged = SyncRepo.PendingExchangeChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(staged.RecipientType, Is.EqualTo(ExchangeRecipientTypes.Mailbox), "kept from the import");
            Assert.That(staged.Alias, Is.EqualTo("ada.lovelace"), "the export's change");
            Assert.That(staged.Database, Is.EqualTo("DB01"), "kept from the import");
            Assert.That(staged.PrimarySmtpAddress, Is.Null, "removed by the export");
            Assert.That(staged.SyncRuleId, Is.EqualTo(UpdatingRuleId), "the rule whose Attribute Flow declared the change");
        }
    }

    [Test]
    public async Task ExecuteExportsAsync_UpdateToAnExistingRecipient_SaysTheUpdateIsQueuedAsync()
    {
        var account = Arrange("user", PendingExportChangeType.Update,
            imported: type => [Imported(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox)],
            changes: (type, rule) => [Change(type, ExchangeRecipientAttributes.Alias, "ada.lovelace", rule)]);

        await ExportAsync();

        var activity = SyncRepo.Activities[SyncRepo.PendingExchangeChanges.Values.Single().ActivityId];
        Assert.That(activity.Message, Is.EqualTo($"Update to the mailbox queued for Exchange on {account.System.Name}."));
    }

    /// <summary>
    /// Declared None on an object that is not a recipient: nothing to do, so nothing is written at all.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeclaredNoneOnAnObjectThatIsNotARecipient_WritesNothingAsync()
    {
        Arrange("user", PendingExportChangeType.Create, changes: (type, rule) =>
            [Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.None, rule)]);

        var result = await ExportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.PendingExchangeChanges, Is.Empty);
            Assert.That(ExchangeActivities(), Is.Empty);
            Assert.That(result.SuccessCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// An export that changes no connector-defined attribute has nothing to say to Exchange, even on a recipient.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_UpdateChangingNoExchangeAttribute_StagesNothingAsync()
    {
        Arrange("user", PendingExportChangeType.Update,
            imported: type => [Imported(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox)],
            changes: (type, rule) => [Change(type, "displayName", "Ada Lovelace", rule)]);

        await ExportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.PendingExchangeChanges, Is.Empty);
            Assert.That(ExchangeActivities(), Is.Empty);
        }
    }

    /// <summary>
    /// A failed export wrote nothing to the directory, so nothing follows it to Exchange (requirement 12).
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_FailedExport_StagesNothingAsync()
    {
        Arrange("user", PendingExportChangeType.Create, changes: (type, rule) =>
            [Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox, rule)]);

        var result = await ExportAsync(ConnectedSystemExportResult.Failed("The object already exists."));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailedCount, Is.EqualTo(1));
            Assert.That(SyncRepo.PendingExchangeChanges, Is.Empty);
            Assert.That(ExchangeActivities(), Is.Empty);
        }
    }

    #endregion

    #region Awaiting Exchange (the confirming import's hold)

    /// <summary>
    /// The directory write did not make the Exchange changes: they are marked as awaiting Exchange, so the confirming
    /// import holds them while the account's change is on the queue rather than reporting them unconfirmed. The
    /// export's directory changes are confirmed as they always were.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateDeclaringAMailbox_MarksOnlyItsExchangeChangesAwaitingExchangeAsync()
    {
        var account = Arrange("user", PendingExportChangeType.Create, changes: (type, rule) =>
        [
            Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox, rule),
            Change(type, ExchangeRecipientAttributes.Alias, "ada.lovelace", rule),
            Change(type, "displayName", "Ada Lovelace", rule)
        ]);

        await ExportAsync();

        var changes = SyncRepo.PendingExports[account.Export.Id].AttributeValueChanges;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Where(c => c.AwaitsExchange).Select(c => c.Attribute.Name),
                Is.EquivalentTo(new[] { ExchangeRecipientAttributes.RecipientType, ExchangeRecipientAttributes.Alias }));
            Assert.That(changes.Select(c => c.Status), Is.All.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        }
    }

    /// <summary>
    /// A refusal is held too: it is a row on the queue, and until the declared state changes there is nothing an
    /// import could confirm and nothing worth sending again.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_RefusedDeclaration_MarksItsExchangeChangeAwaitingExchangeAsync()
    {
        var account = Arrange("contact", PendingExportChangeType.Create, changes: (type, rule) =>
            [Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox, rule)]);

        await ExportAsync();

        Assert.That(SyncRepo.PendingExports[account.Export.Id].AttributeValueChanges.Single().AwaitsExchange, Is.True);
    }

    /// <summary>
    /// Declared None on an object that is not a recipient asks Exchange for nothing, so nothing waits on Exchange.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeclaredNoneOnAnObjectThatIsNotARecipient_MarksNothingAsync()
    {
        var account = Arrange("user", PendingExportChangeType.Create, changes: (type, rule) =>
            [Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.None, rule)]);

        await ExportAsync();

        Assert.That(SyncRepo.PendingExports[account.Export.Id].AttributeValueChanges.Single().AwaitsExchange, Is.False);
    }

    /// <summary>
    /// The declared values are not recorded on the Connected System Object at export: only an import shows what
    /// Exchange made (PRD requirement 12), and staging must keep reading the imported recipient type.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateDeclaringAMailbox_RecordsNoDeclaredValueOnTheObjectAsync()
    {
        var account = Arrange("user", PendingExportChangeType.Create, changes: (type, rule) =>
        [
            Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox, rule),
            Change(type, "displayName", "Ada Lovelace", rule)
        ]);

        await ExportAsync();

        Assert.That(account.Cso.AttributeValues.Select(v => v.Attribute?.Name ?? v.AttributeId.ToString()),
            Does.Not.Contain(ExchangeRecipientAttributes.RecipientType));
    }

    #endregion

    #region Refusals and type changes (decision D9)

    /// <summary>
    /// Scenario 6: a global group declared mail-enabled. No Exchange step is attempted, and the export item's follow-up
    /// says why, as an error.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_MailEnabledGroupThatIsNotUniversal_RefusesWithAFollowUpAndQueuesNothingAsync()
    {
        var account = Arrange("group", PendingExportChangeType.Update,
            imported: type => [ImportedNumber(type, "groupType", GlobalSecurityGroup)],
            changes: (type, rule) => [Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.MailEnabledGroup, rule)]);

        var result = await ExportAsync();

        var activity = ExchangeActivities().Single();
        var item = result.ProcessedExportItems.Single(i => i.PendingExportId == account.Export.Id);
        var row = SyncRepo.PendingExchangeChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Status, Is.EqualTo(PendingExchangeChangeStatus.Refused), "recorded as a terminal row, never claimed by the lane");
            Assert.That(row.FailureMessage, Is.EqualTo("Only universal groups can be mail-enabled; this group is not universal."));
            Assert.That(row.RecipientType, Is.EqualTo(ExchangeRecipientTypes.MailEnabledGroup));
            Assert.That(row.ActivityId, Is.EqualTo(activity.Id), "the row names the follow-up that reported it");
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.CompleteWithError));
            Assert.That(activity.Message, Is.EqualTo("Only universal groups can be mail-enabled; this group is not universal."));
            Assert.That(activity.ErrorMessage, Is.EqualTo(activity.Message));
            Assert.That(activity.ParentRunProfileExecutionItemId, Is.Not.Null.And.EqualTo(item.ExecutionItemId));
            Assert.That(activity.ConnectedSystemObjectId, Is.EqualTo(account.Cso.Id));
            Assert.That(result.SuccessCount, Is.EqualTo(1), "the directory write stands");
            Assert.That(result.ExchangeChangesRefusedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// The scope that decides fit is the one the export declares: a groupType changed to universal in the same export
    /// makes the group mail-enabled.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_GroupMadeUniversalInTheSameExport_StagesAsync()
    {
        Arrange("group", PendingExportChangeType.Update,
            imported: type => [ImportedNumber(type, "groupType", GlobalSecurityGroup)],
            changes: (type, rule) =>
            [
                ChangeNumber(type, "groupType", UniversalSecurityGroup, rule),
                Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.MailEnabledGroup, rule)
            ]);

        await ExportAsync();

        Assert.That(SyncRepo.PendingExchangeChanges.Values.Single().RecipientType, Is.EqualTo(ExchangeRecipientTypes.MailEnabledGroup));
    }

    [Test]
    public async Task ExecuteExportsAsync_MailboxDeclaredOnAContact_RefusesAsync()
    {
        Arrange("contact", PendingExportChangeType.Create, changes: (type, rule) =>
            [Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox, rule)]);

        await ExportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.PendingExchangeChanges.Values.Single().Status, Is.EqualTo(PendingExchangeChangeStatus.Refused));
            Assert.That(ExchangeActivities().Single().Message, Is.EqualTo("Only a user can have a mailbox; this object is a contact."));
        }
    }

    /// <summary>
    /// Scenario 8: a mailbox declared a shared mailbox. Nothing changes in Exchange, and the follow-up warns that a
    /// type change is not supported in this release.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_TypeChangeOnAnExistingRecipient_WarnsWithAFollowUpAndQueuesNothingAsync()
    {
        var account = Arrange("user", PendingExportChangeType.Update,
            imported: type => [Imported(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox)],
            changes: (type, rule) => [Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.SharedMailbox, rule)]);

        var result = await ExportAsync();

        var activity = ExchangeActivities().Single();
        var row = SyncRepo.PendingExchangeChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Status, Is.EqualTo(PendingExchangeChangeStatus.Refused), "a type change this release does not make is never delivered");
            Assert.That(row.FailureMessage, Is.EqualTo(activity.Message));
            Assert.That(row.RecipientType, Is.EqualTo(ExchangeRecipientTypes.SharedMailbox));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(activity.Message, Does.Contain("not supported in this release"));
            Assert.That(activity.Message, Does.Contain("a shared mailbox"));
            Assert.That(activity.ConnectedSystemObjectId, Is.EqualTo(account.Cso.Id));
            Assert.That(activity.ParentRunProfileExecutionItemId, Is.Not.Null);
            Assert.That(result.ExchangeTypeChangesNotMadeCount, Is.EqualTo(1));
        }
    }

    #endregion

    #region What the queue already holds

    /// <summary>
    /// A newer declared state for an account already on the queue supersedes its row: the new follow-up says it is
    /// queued, and the superseded change's own Activity is told it was replaced.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_AccountAlreadyQueuedWithADifferentState_SupersedesAndTellsTheEarlierChangeAsync()
    {
        var account = Arrange("user", PendingExportChangeType.Update,
            imported: type => [Imported(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox)],
            changes: (type, rule) => [Change(type, ExchangeRecipientAttributes.Alias, "ada.lovelace", rule)]);
        var earlier = await SeedQueuedAsync(account.Cso, alias: "ada", PendingExchangeChangeStatus.Parked);
        // Read now: superseding the row in place re-points its Activity.
        var (earlierId, earlierActivityId) = (earlier.Id, earlier.ActivityId);

        var result = await ExportAsync();

        var row = SyncRepo.PendingExchangeChanges.Values.Single();
        var replaced = ExchangeActivities().Single(a => a.ParentActivityId == earlierActivityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Id, Is.EqualTo(earlierId), "superseded in place");
            Assert.That(row.ActivityId, Is.Not.EqualTo(earlierActivityId), "the row names the Activity of the state it now carries");
            Assert.That(row.Alias, Is.EqualTo("ada.lovelace"));
            Assert.That(row.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(SyncRepo.Activities[row.ActivityId].Message, Does.StartWith("Update to the mailbox queued for Exchange"));
            Assert.That(replaced.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(replaced.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.ReplaceExchangeRecipient));
            Assert.That(SyncRepo.Activities[row.ActivityId].TargetOperationType, Is.EqualTo(ActivityTargetOperationType.SetExchangeRecipient),
                "the change that replaced it is an ordinary Exchange change");
            Assert.That(replaced.Message, Does.Contain("replaced by a newer declared state"));
            Assert.That(replaced.ParentRunProfileExecutionItemId, Is.Null, "an Activity has one parent");
            Assert.That(result.ExchangeChangesSupersededCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// Re-staging the change already queued is the same change, not a new one: its Activity already says what became of
    /// it, so the export writes no follow-up for it again.
    /// </summary>
    [TestCase(PendingExchangeChangeStatus.Pending)]
    [TestCase(PendingExchangeChangeStatus.Parked)]
    public async Task ExecuteExportsAsync_SameStateAsAQueuedChange_WritesNoNewActivityAsync(PendingExchangeChangeStatus status)
    {
        var account = Arrange("user", PendingExportChangeType.Update,
            imported: type => [Imported(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox)],
            changes: (type, rule) => [Change(type, ExchangeRecipientAttributes.Alias, "ada", rule)]);
        await SeedQueuedAsync(account.Cso, alias: "ada", status);

        var result = await ExportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExchangeActivities(), Is.Empty);
            Assert.That(SyncRepo.PendingExchangeChanges.Values.Single().Status, Is.EqualTo(status));
            Assert.That(result.ExchangeChangesUnchangedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A refusal is reported once: re-staging the same declared state finds the refused row already there and says
    /// nothing more, until the declared state changes.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_SameStateAsARefusedChange_IsNotReportedAgainAsync()
    {
        var account = Arrange("contact", PendingExportChangeType.Update,
            changes: (type, rule) => [Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox, rule)]);
        var refused = await SeedQueuedAsync(account.Cso, alias: null, PendingExchangeChangeStatus.Refused);

        var result = await ExportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExchangeActivities(), Is.Empty);
            Assert.That(SyncRepo.PendingExchangeChanges.Values.Single().ActivityId, Is.EqualTo(refused.ActivityId));
            Assert.That(result.ExchangeChangesRefusedCount, Is.Zero, "already reported");
            Assert.That(result.ExchangeChangesUnchangedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A refusal whose cause has gone with the declared state unchanged (the group made universal) is no longer a
    /// refusal: the refused row is superseded by the deliverable change, not left refused for ever.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_RefusedChangeNowDeliverableWithTheSameDeclaredState_SupersedesItAsync()
    {
        var account = Arrange("group", PendingExportChangeType.Update,
            imported: type => [ImportedNumber(type, "groupType", GlobalSecurityGroup)],
            changes: (type, rule) =>
            [
                ChangeNumber(type, "groupType", UniversalSecurityGroup, rule),
                Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.MailEnabledGroup, rule)
            ]);
        var refused = await SeedQueuedAsync(account.Cso, alias: null, PendingExchangeChangeStatus.Refused, ExchangeRecipientTypes.MailEnabledGroup);
        // Read now: superseding the row in place re-points its Activity.
        var refusedActivityId = refused.ActivityId;

        var result = await ExportAsync();

        var row = SyncRepo.PendingExchangeChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(row.FailureMessage, Is.Null);
            Assert.That(row.ActivityId, Is.Not.EqualTo(refusedActivityId));
            Assert.That(result.ExchangeChangesSupersededCount, Is.EqualTo(1));
            Assert.That(SyncRepo.Activities[row.ActivityId].Status, Is.EqualTo(ActivityStatus.Complete));
        }
    }

    /// <summary>
    /// A newer declared state that is refused supersedes a deliverable change still queued: the lane must not deliver
    /// the earlier state, and the row carries the refusal and its reason.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_QueuedChangeSupersededByARefusal_RecordsTheRefusalOnTheRowAsync()
    {
        var account = Arrange("user", PendingExportChangeType.Update,
            imported: type => [Imported(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox)],
            changes: (type, rule) => [Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.SharedMailbox, rule)]);
        var earlier = await SeedQueuedAsync(account.Cso, alias: null, PendingExchangeChangeStatus.Pending);
        var earlierActivityId = earlier.ActivityId;
        earlier.AttemptCount = 3;

        var result = await ExportAsync();

        var row = SyncRepo.PendingExchangeChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Status, Is.EqualTo(PendingExchangeChangeStatus.Refused));
            Assert.That(row.FailureMessage, Does.Contain("not supported in this release"));
            Assert.That(row.AttemptCount, Is.Zero);
            Assert.That(SyncRepo.Activities[row.ActivityId].Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(ExchangeActivities().Count(a => a.ParentActivityId == earlierActivityId), Is.EqualTo(1), "the earlier change is told it was replaced");
            Assert.That(result.ExchangeTypeChangesNotMadeCount, Is.EqualTo(1));
        }
    }

    #endregion

    #region Containment

    /// <summary>
    /// A queue that cannot be written to fails nothing that was exported: the account is in the directory and its export
    /// succeeded. Never silent either: the count reaches the result, for the export Activity to report.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_StagingFails_LeavesTheExportSuccessfulAndCountsTheAccountAsync()
    {
        Arrange("user", PendingExportChangeType.Create, changes: (type, rule) =>
            [Change(type, ExchangeRecipientAttributes.RecipientType, ExchangeRecipientTypes.Mailbox, rule)]);
        SyncRepo.FailExchangeChangeStagingWith = new InvalidOperationException("The queue is unavailable.");

        var result = await ExportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SuccessCount, Is.EqualTo(1));
            Assert.That(result.FailedCount, Is.Zero);
            Assert.That(result.ExchangeChangeStagingFailedCount, Is.EqualTo(1));
            Assert.That(SyncRepo.PendingExchangeChanges, Is.Empty);
        }
    }

    #endregion

    #region Helper Methods

    private sealed record Account(ConnectedSystem System, ConnectedSystemObject Cso, PendingExport Export);

    /// <summary>
    /// One export of an account of <paramref name="objectTypeName"/> (an Object Type carrying the five Exchange
    /// attributes and <c>groupType</c>), holding the values the last import read and the changes the export makes.
    /// </summary>
    private Account Arrange(
        string objectTypeName,
        PendingExportChangeType changeType,
        Func<ConnectedSystemObjectType, int, List<PendingExportAttributeValueChange>> changes,
        Func<ConnectedSystemObjectType, List<ConnectedSystemObjectAttributeValue>>? imported = null)
    {
        var system = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var type = ObjectType(objectTypeName);
        var ruleId = changeType == PendingExportChangeType.Create ? ProvisioningRuleId : UpdatingRuleId;
        // An Attribute Flow behind every attribute, so the export run's check that a queued Update change still has
        // something authorising it keeps them all.
        var rule = new SyncRule
        {
            Id = ruleId,
            Name = $"Exchange rule {ruleId}",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = system.Id,
            ProvisionToConnectedSystem = changeType == PendingExportChangeType.Create
        };
        foreach (var attribute in type.Attributes)
            rule.AttributeFlowRules.Add(new SyncRuleMapping { SyncRule = rule, SyncRuleId = ruleId, TargetConnectedSystemAttribute = attribute, TargetConnectedSystemAttributeId = attribute.Id });
        SyncRepo.SeedSyncRule(rule);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            Type = type,
            TypeId = type.Id,
            AttributeValues = new(imported?.Invoke(type) ?? []),
            MetaverseObjectId = Guid.NewGuid()
        };

        var export = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            ConnectedSystem = system,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = changeType,
            CreatedAt = DateTime.UtcNow,
            ProvisioningSyncRuleId = changeType == PendingExportChangeType.Create ? ruleId : null,
            AttributeValueChanges = new(changes(type, ruleId))
        };
        SyncRepo.SeedPendingExport(export);
        return new Account(system, cso, export);
    }

    private ConnectedSystemObjectType ObjectType(string name)
    {
        var type = new ConnectedSystemObjectType { Id = _nextAttributeId++, Name = name, Selected = true };
        foreach (var attributeName in ExchangeRecipientAttributes.All)
            type.Attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Id = _nextAttributeId++, Name = attributeName, Type = AttributeDataType.Text, Selected = true, IsConnectorDefined = true,
                ConnectedSystemObjectType = type
            });
        type.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Id = _nextAttributeId++, Name = "groupType", Type = AttributeDataType.Number, Selected = true, ConnectedSystemObjectType = type
        });
        type.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Id = _nextAttributeId++, Name = "displayName", Type = AttributeDataType.Text, Selected = true, ConnectedSystemObjectType = type
        });
        return type;
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name) =>
        type.Attributes.Single(a => a.Name == name);

    private static PendingExportAttributeValueChange Change(ConnectedSystemObjectType type, string name, string value, int syncRuleId,
        PendingExportAttributeChangeType changeType = PendingExportAttributeChangeType.Update)
    {
        var attribute = Attribute(type, name);
        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, StringValue = value, ChangeType = changeType,
            SyncRuleId = syncRuleId, Status = PendingExportAttributeChangeStatus.Pending
        };
    }

    private static PendingExportAttributeValueChange ChangeNumber(ConnectedSystemObjectType type, string name, int value, int syncRuleId)
    {
        var attribute = Attribute(type, name);
        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, IntValue = value, ChangeType = PendingExportAttributeChangeType.Update,
            SyncRuleId = syncRuleId, Status = PendingExportAttributeChangeStatus.Pending
        };
    }

    private static ConnectedSystemObjectAttributeValue Imported(ConnectedSystemObjectType type, string name, string value)
    {
        var attribute = Attribute(type, name);
        return new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, StringValue = value };
    }

    private static ConnectedSystemObjectAttributeValue ImportedNumber(ConnectedSystemObjectType type, string name, int value)
    {
        var attribute = Attribute(type, name);
        return new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, IntValue = value };
    }

    /// <summary>A change already on the queue for the account, declaring a mailbox with <paramref name="alias"/>.</summary>
    private async Task<PendingExchangeChange> SeedQueuedAsync(ConnectedSystemObject cso, string? alias, PendingExchangeChangeStatus status,
        string recipientType = ExchangeRecipientTypes.Mailbox)
    {
        var change = new PendingExchangeChange
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = cso.ConnectedSystemId,
            ConnectedSystemObjectId = cso.Id,
            MetaverseObjectId = cso.MetaverseObjectId,
            SyncRuleId = UpdatingRuleId,
            RecipientType = recipientType,
            Alias = alias,
            DomainController = DomainController,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            ActivityId = Guid.NewGuid()
        };
        await SyncRepo.StageExchangeChangesAsync([change]);
        SyncRepo.PendingExchangeChanges[change.Id].Status = status;
        return SyncRepo.PendingExchangeChanges[change.Id];
    }

    private List<Activity> ExchangeActivities() =>
        SyncRepo.Activities.Values.Where(a => a.TargetType == ActivityTargetType.ExchangeRecipient).ToList();

    /// <summary>Runs an export whose Connector answers every export with <paramref name="answer"/>, success on the pinned domain controller by default.</summary>
    private Task<ExportExecutionResult> ExportAsync(ConnectedSystemExportResult? answer = null)
    {
        var exportResult = answer ?? new ConnectedSystemExportResult { Success = true, DirectoryServer = DomainController };
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) => exports.Select(_ => exportResult).ToList());

        return Jim.ExportExecution.ExecuteExportsAsync(ConnectedSystemsData.Single(s => s.Name == "Dummy Target System"),
            mockConnector.Object, SyncRunMode.PreviewAndSync);
    }

    #endregion
}
