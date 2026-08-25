// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// The outbound staging verdict, extracted from <c>ExportEvaluationServer</c>'s three
/// <c>CreateOrUpdatePendingExport*</c> entry points into the pure engine as the third slice of the #288
/// unbraiding (plan Phase 1b). The verdict answers "what kind of export, if any, does this Metaverse Object
/// change stage against this export Synchronisation Rule's target?": nothing (a reported Object Type conflict,
/// provisioning declined, a recall against no exportable presence, changes irrelevant to a pending
/// provisioning), a Create (provision new, or reuse the pending provisioning CSO), or an Update. Export
/// matching, CSO creation and persistence stay with the orchestrator; these tests pin the verdict to the
/// braided implementation's behaviour so the swap is provably behaviour-preserving.
/// </summary>
[TestFixture]
public class SyncEngineOutboundStagingTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    [Test]
    public void DecideOutboundStaging_ExistingCsoOfAnotherObjectType_ReportsTheConflictAndStagesNothing()
    {
        // #1331: a Metaverse Object holds one CSO per Connected System, so a rule targeting a different
        // Object Type has nowhere to put its export; writing onto the other type's object would be worse.
        var rule = ExportRule(provisionToConnectedSystem: true, csoTypeId: 5);
        var cso = Cso(ConnectedSystemObjectStatus.Normal, typeId: 6, joinedToMvo: true);

        var decision = _engine.DecideOutboundStaging(Mvo(), rule, cso, changedAttributes: [], recallSemantics: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Outcome, Is.EqualTo(OutboundStagingOutcome.ObjectTypeConflict));
            Assert.That(decision.Conflict, Is.Not.Null);
            Assert.That(decision.Conflict!.ExistingConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(decision.ChangeType, Is.Null);
        }
    }

    [Test]
    public void DecideOutboundStaging_NoCsoAndProvisioningDisabled_Declines()
    {
        var rule = ExportRule(provisionToConnectedSystem: false);

        var decision = _engine.DecideOutboundStaging(Mvo(), rule, existingCso: null, changedAttributes: [], recallSemantics: false);

        Assert.That(decision.Outcome, Is.EqualTo(OutboundStagingOutcome.ProvisioningDeclined));
    }

    [Test]
    public void DecideOutboundStaging_PendingProvisioningCsoAndProvisioningSinceDisabled_Declines()
    {
        // The braided code treated a PendingProvisioning CSO exactly like no CSO for the provisioning gate:
        // the object does not exist in the target yet, and a rule that no longer provisions must not stage.
        var rule = ExportRule(provisionToConnectedSystem: false);
        var cso = Cso(ConnectedSystemObjectStatus.PendingProvisioning, typeId: 5);

        var decision = _engine.DecideOutboundStaging(Mvo(), rule, cso, changedAttributes: [], recallSemantics: false);

        Assert.That(decision.Outcome, Is.EqualTo(OutboundStagingOutcome.ProvisioningDeclined));
    }

    [Test]
    public void DecideOutboundStaging_NoCsoAndProvisioningEnabled_ProvisionsACreate()
    {
        // The orchestrator interposes export matching before acting on this outcome (a matched CSO becomes an
        // Update instead); the verdict itself is "this rule wants a presence created".
        var rule = ExportRule(provisionToConnectedSystem: true);

        var decision = _engine.DecideOutboundStaging(Mvo(), rule, existingCso: null, changedAttributes: [], recallSemantics: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Outcome, Is.EqualTo(OutboundStagingOutcome.ProvisionNewCso));
            Assert.That(decision.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
        }
    }

    [Test]
    public void DecideOutboundStaging_PendingProvisioningCsoWithRelevantChanges_ReusesItForACreate()
    {
        var relevantAttributeId = 77;
        var rule = ExportRule(provisionToConnectedSystem: true, directSourceAttributeId: relevantAttributeId);
        var cso = Cso(ConnectedSystemObjectStatus.PendingProvisioning, typeId: 5);

        var decision = _engine.DecideOutboundStaging(
            Mvo(), rule, cso, changedAttributes: [ChangedAttribute(relevantAttributeId)], recallSemantics: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Outcome, Is.EqualTo(OutboundStagingOutcome.ReusePendingProvisioningCso));
            Assert.That(decision.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
        }
    }

    [Test]
    public void DecideOutboundStaging_PendingProvisioningCsoWithIrrelevantChanges_StagesNothing()
    {
        // Replacing the existing Create Pending Export with an identical one would misattribute it to this
        // synchronisation in the causality tree; the braided code skipped, and so must this.
        var rule = ExportRule(provisionToConnectedSystem: true, directSourceAttributeId: 77);
        var cso = Cso(ConnectedSystemObjectStatus.PendingProvisioning, typeId: 5);

        var decision = _engine.DecideOutboundStaging(
            Mvo(), rule, cso, changedAttributes: [ChangedAttribute(attributeId: 999)], recallSemantics: false);

        Assert.That(decision.Outcome, Is.EqualTo(OutboundStagingOutcome.PendingProvisioningChangesIrrelevant));
    }

    [Test]
    public void DecideOutboundStaging_PendingProvisioningCsoAndAnExpressionMapping_AnyChangeIsRelevant()
    {
        // An expression may depend on any Metaverse attribute, so relevance is decided conservatively.
        var rule = ExportRule(provisionToConnectedSystem: true, expression: "Upper(mv[\"DisplayName\"])");
        var cso = Cso(ConnectedSystemObjectStatus.PendingProvisioning, typeId: 5);

        var decision = _engine.DecideOutboundStaging(
            Mvo(), rule, cso, changedAttributes: [ChangedAttribute(attributeId: 999)], recallSemantics: false);

        Assert.That(decision.Outcome, Is.EqualTo(OutboundStagingOutcome.ReusePendingProvisioningCso));
    }

    [Test]
    public void DecideOutboundStaging_NormalJoinedCso_Updates()
    {
        var rule = ExportRule(provisionToConnectedSystem: true);
        var cso = Cso(ConnectedSystemObjectStatus.Normal, typeId: 5, joinedToMvo: true);

        var decision = _engine.DecideOutboundStaging(Mvo(), rule, cso, changedAttributes: [], recallSemantics: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Outcome, Is.EqualTo(OutboundStagingOutcome.UpdateExistingCso));
            Assert.That(decision.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
        }
    }

    [Test]
    public void DecideOutboundStaging_RecallAgainstNoTargetPresence_SkipsWithoutProvisioning()
    {
        // #1003: a referencing object with no presence in the target has nothing to remove a member from,
        // and provisioning one on a recall would be inventing an account to delete a membership from.
        var rule = ExportRule(provisionToConnectedSystem: true);

        var decision = _engine.DecideOutboundStaging(Mvo(), rule, existingCso: null, changedAttributes: [], recallSemantics: true);

        Assert.That(decision.Outcome, Is.EqualTo(OutboundStagingOutcome.RecallSkippedNoTargetPresence));
    }

    [Test]
    public void DecideOutboundStaging_RecallAgainstAPendingProvisioningCso_AlsoSkips()
    {
        // The recall guard also protects a pending Create export: merging a recall into it and then
        // filtering to Updates would silently lose the provisioning export (the pre-#1003 defect).
        var rule = ExportRule(provisionToConnectedSystem: true);
        var cso = Cso(ConnectedSystemObjectStatus.PendingProvisioning, typeId: 5);

        var decision = _engine.DecideOutboundStaging(Mvo(), rule, cso, changedAttributes: [], recallSemantics: true);

        Assert.That(decision.Outcome, Is.EqualTo(OutboundStagingOutcome.RecallSkippedNoTargetPresence));
    }

    [Test]
    public void DecideOutboundStaging_RecallAgainstANormalCso_UpdatesAsUsual()
    {
        var rule = ExportRule(provisionToConnectedSystem: true);
        var cso = Cso(ConnectedSystemObjectStatus.Normal, typeId: 5, joinedToMvo: true);

        var decision = _engine.DecideOutboundStaging(Mvo(), rule, cso, changedAttributes: [], recallSemantics: true);

        Assert.That(decision.Outcome, Is.EqualTo(OutboundStagingOutcome.UpdateExistingCso));
    }

    private static MetaverseObject Mvo() => new()
    {
        Id = Guid.NewGuid(),
        Type = new MetaverseObjectType { Id = 100, Name = "User" }
    };

    private static ConnectedSystemObject Cso(ConnectedSystemObjectStatus status, int typeId, bool joinedToMvo = false)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            TypeId = typeId,
            Status = status
        };

        if (joinedToMvo || status == ConnectedSystemObjectStatus.PendingProvisioning)
            cso.MetaverseObjectId = Guid.NewGuid();

        return cso;
    }

    private static SyncRule ExportRule(
        bool provisionToConnectedSystem, int csoTypeId = 5, int? directSourceAttributeId = null, string? expression = null)
    {
        var rule = new SyncRule
        {
            Name = "Export rule",
            ConnectedSystemId = 1,
            ConnectedSystemObjectTypeId = csoTypeId,
            MetaverseObjectTypeId = 100,
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ProvisionToConnectedSystem = provisionToConnectedSystem
        };

        if (directSourceAttributeId.HasValue || expression != null)
        {
            var mapping = new SyncRuleMapping
            {
                TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "target" }
            };
            mapping.Sources.Add(new SyncRuleMappingSource
            {
                Expression = expression,
                MetaverseAttribute = directSourceAttributeId.HasValue
                    ? new MetaverseAttribute { Id = directSourceAttributeId.Value, Name = $"attr{directSourceAttributeId.Value}" }
                    : null
            });
            rule.AttributeFlowRules.Add(mapping);
        }

        return rule;
    }

    private static MetaverseObjectAttributeValue ChangedAttribute(int attributeId) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attributeId
    };
}
