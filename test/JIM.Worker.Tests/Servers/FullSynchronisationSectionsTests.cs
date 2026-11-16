// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Which changed sections of a configuration only a Full Synchronisation applies to the objects that already exist (#2973).
/// The drift indicator tells an administrator to run one, so a section a Full Synchronisation would do nothing about must
/// not count: an indicator that fires when there is nothing to do teaches administrators to ignore it.
/// <para>
/// Two kinds of change are consequential yet never applied by a Full Synchronisation. A Metaverse Object Type's deletion
/// settings are read when an object is disconnected and when housekeeping runs, never by a synchronisation of objects that
/// are already disconnected. An Initial Export Only Attribute Flow writes its value only when an object is provisioned, so
/// adding, changing or removing one changes nothing for the objects already provisioned.
/// </para>
/// </summary>
[TestFixture]
public class FullSynchronisationSectionsTests
{
    private JimApplication _jim = null!;
    private ConfigurationDiffService _diffs = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _jim = new JimApplication(new Mock<IRepository>().Object);
        _diffs = new ConfigurationDiffService();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region Metaverse Object Type

    [Test]
    public void SectionsNeedingFullSynchronisation_GracePeriodChange_IsNoneAlthoughDestructive()
    {
        var before = ObjectType();
        var after = ObjectType(type => type.DeletionGracePeriod = TimeSpan.Zero);

        var diff = Diff(before, after);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConfigurationChangeClassifier.Classify(diff), Is.EqualTo(ConfigurationChangeClass.Destructive), "precondition: the change is graded Destructive");
            Assert.That(ConfigurationChangeSynchronisationPolicy.SectionsNeedingFullSynchronisation(diff), Is.Empty);
        }
    }

    [Test]
    public void SectionsNeedingFullSynchronisation_DeletionRuleAndTriggerSystemsChange_IsNone()
    {
        var before = ObjectType();
        var after = ObjectType(type =>
        {
            type.DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected;
            type.DeletionTriggerConnectedSystemIds = [3];
        });

        Assert.That(ConfigurationChangeSynchronisationPolicy.SectionsNeedingFullSynchronisation(Diff(before, after)), Is.Empty);
    }

    [Test]
    public void SectionsNeedingFullSynchronisation_AttributeBoundToTheType_IsTheAttributesSection()
    {
        var before = ObjectType();
        var after = ObjectType(type => type.Attributes.Add(new MetaverseAttribute { Id = 31, Name = "Employee Type" }));

        Assert.That(ConfigurationChangeSynchronisationPolicy.SectionsNeedingFullSynchronisation(Diff(before, after)), Is.EqualTo(new[] { "attributes" }));
    }

    [Test]
    public void SectionsNeedingFullSynchronisation_GracePeriodAndAttributeBindingChange_IsOnlyTheAttributesSection()
    {
        var before = ObjectType();
        var after = ObjectType(type =>
        {
            type.DeletionGracePeriod = TimeSpan.Zero;
            type.Attributes.Add(new MetaverseAttribute { Id = 31, Name = "Employee Type" });
        });

        Assert.That(ConfigurationChangeSynchronisationPolicy.SectionsNeedingFullSynchronisation(Diff(before, after)), Is.EqualTo(new[] { "attributes" }));
    }

    #endregion

    #region Synchronisation Rule Attribute Flows

    [Test]
    public void SectionsNeedingFullSynchronisation_InitialExportOnlyFlowAdded_IsNone()
    {
        var before = ExportRule();
        var after = ExportRule(rule => rule.AttributeFlowRules.Add(Flow(101, initialExportOnly: true)));

        Assert.That(ConfigurationChangeSynchronisationPolicy.SectionsNeedingFullSynchronisation(Diff(before, after)), Is.Empty);
    }

    [Test]
    public void SectionsNeedingFullSynchronisation_InitialExportOnlyFlowRemoved_IsNone()
    {
        var before = ExportRule(rule => rule.AttributeFlowRules.Add(Flow(101, initialExportOnly: true)));
        var after = ExportRule();

        Assert.That(ConfigurationChangeSynchronisationPolicy.SectionsNeedingFullSynchronisation(Diff(before, after)), Is.Empty);
    }

    [Test]
    public void SectionsNeedingFullSynchronisation_InitialExportOnlyFlowEdited_IsNone()
    {
        var before = ExportRule(rule => rule.AttributeFlowRules.Add(Flow(101, initialExportOnly: true)));
        var after = ExportRule(rule => rule.AttributeFlowRules.Add(Flow(101, initialExportOnly: true, targetAttributeId: 43)));

        Assert.That(ConfigurationChangeSynchronisationPolicy.SectionsNeedingFullSynchronisation(Diff(before, after)), Is.Empty);
    }

    [Test]
    public void SectionsNeedingFullSynchronisation_FlowMadeInitialExportOnly_IsNone()
    {
        // From now on the flow writes nothing to objects already provisioned, so there is nothing for a Full
        // Synchronisation to correct on them.
        var before = ExportRule(rule => rule.AttributeFlowRules.Add(Flow(101, initialExportOnly: false)));
        var after = ExportRule(rule => rule.AttributeFlowRules.Add(Flow(101, initialExportOnly: true)));

        Assert.That(ConfigurationChangeSynchronisationPolicy.SectionsNeedingFullSynchronisation(Diff(before, after)), Is.Empty);
    }

    [Test]
    public void SectionsNeedingFullSynchronisation_FlowNoLongerInitialExportOnly_IsTheAttributeFlowsSection()
    {
        // The flow now manages the attribute on every object, including those provisioned before the change.
        var before = ExportRule(rule => rule.AttributeFlowRules.Add(Flow(101, initialExportOnly: true)));
        var after = ExportRule(rule => rule.AttributeFlowRules.Add(Flow(101, initialExportOnly: false)));

        Assert.That(ConfigurationChangeSynchronisationPolicy.SectionsNeedingFullSynchronisation(Diff(before, after)), Is.EqualTo(new[] { "attributeFlowRules" }));
    }

    [Test]
    public void SectionsNeedingFullSynchronisation_ManagedFlowAdded_IsTheAttributeFlowsSection()
    {
        var before = ExportRule();
        var after = ExportRule(rule => rule.AttributeFlowRules.Add(Flow(101, initialExportOnly: false)));

        Assert.That(ConfigurationChangeSynchronisationPolicy.SectionsNeedingFullSynchronisation(Diff(before, after)), Is.EqualTo(new[] { "attributeFlowRules" }));
    }

    [Test]
    public void SectionsNeedingFullSynchronisation_InitialExportOnlyAndManagedFlowsBothAdded_IsTheAttributeFlowsSection()
    {
        var before = ExportRule();
        var after = ExportRule(rule =>
        {
            rule.AttributeFlowRules.Add(Flow(101, initialExportOnly: true));
            rule.AttributeFlowRules.Add(Flow(102, initialExportOnly: false));
        });

        Assert.That(ConfigurationChangeSynchronisationPolicy.SectionsNeedingFullSynchronisation(Diff(before, after)), Is.EqualTo(new[] { "attributeFlowRules" }));
    }

    [Test]
    public void SectionsNeedingFullSynchronisation_RenameOnly_IsNone()
    {
        var before = ExportRule();
        var after = ExportRule(rule => rule.Name = "AD Export (EMEA)");

        Assert.That(ConfigurationChangeSynchronisationPolicy.SectionsNeedingFullSynchronisation(Diff(before, after)), Is.Empty);
    }

    #endregion

    #region What a save records

    [Test]
    public void NeedsFullSynchronisation_GracePeriodOnlySave_IsFalse()
    {
        var outcome = ConfigurationChangeCaptureService.OutcomeOf(
            Diff(ObjectType(), ObjectType(type => type.DeletionGracePeriod = TimeSpan.Zero)), objectKey: null, "test");

        Assert.That(ConfigurationChangeSynchronisationPolicy.NeedsFullSynchronisation(outcome, recordsAwaitingSynchronisation: false, flagsExportReview: false),
            Is.False);
    }

    [Test]
    public void NeedsFullSynchronisation_InitialExportOnlyFlowAddedToExportRule_IsFalse()
    {
        var outcome = ConfigurationChangeCaptureService.OutcomeOf(
            Diff(ExportRule(), ExportRule(rule => rule.AttributeFlowRules.Add(Flow(101, initialExportOnly: true)))), objectKey: null, "test");

        Assert.That(ConfigurationChangeSynchronisationPolicy.NeedsFullSynchronisation(outcome, recordsAwaitingSynchronisation: false, flagsExportReview: false),
            Is.False);
    }

    [Test]
    public void NeedsFullSynchronisation_ManagedFlowAddedToExportRule_IsTrue()
    {
        var outcome = ConfigurationChangeCaptureService.OutcomeOf(
            Diff(ExportRule(), ExportRule(rule => rule.AttributeFlowRules.Add(Flow(101, initialExportOnly: false)))), objectKey: null, "test");

        Assert.That(ConfigurationChangeSynchronisationPolicy.NeedsFullSynchronisation(outcome, recordsAwaitingSynchronisation: false, flagsExportReview: false),
            Is.True);
    }

    #endregion

    #region Helpers

    private ConfigurationDiff Diff(MetaverseObjectType before, MetaverseObjectType after) =>
        _diffs.Diff(_jim.ConfigurationSnapshots.CreateSnapshot(before, HashKey), _jim.ConfigurationSnapshots.CreateSnapshot(after, HashKey));

    private ConfigurationDiff Diff(SyncRule before, SyncRule after) =>
        _diffs.Diff(_jim.ConfigurationSnapshots.CreateSnapshot(before, HashKey), _jim.ConfigurationSnapshots.CreateSnapshot(after, HashKey));

    private static MetaverseObjectType ObjectType(Action<MetaverseObjectType>? edit = null)
    {
        var type = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(7),
            Attributes = [new MetaverseAttribute { Id = 30, Name = "Display Name" }]
        };
        edit?.Invoke(type);
        return type;
    }

    private static SyncRule ExportRule(Action<SyncRule>? edit = null)
    {
        var rule = new SyncRule
        {
            Id = 42,
            Name = "AD Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ProvisionToConnectedSystem = true,
            ConnectedSystemId = 3,
            ConnectedSystemObjectTypeId = 7,
            MetaverseObjectTypeId = 1
        };
        edit?.Invoke(rule);
        return rule;
    }

    private static SyncRuleMapping Flow(int id, bool initialExportOnly, int targetAttributeId = 42) => new()
    {
        Id = id,
        TargetConnectedSystemAttributeId = targetAttributeId,
        InitialExportOnly = initialExportOnly
    };

    #endregion
}
