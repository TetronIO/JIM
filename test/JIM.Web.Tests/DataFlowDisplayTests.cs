// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="DataFlowDisplay"/> (#1199): how a data flow reads on the system-wide Data Flow page.
/// <para>
/// The page's whole job is to let an administrator answer "where does this attribute's value come from?" at a
/// glance, and every judgement it makes is direction-dependent: priority is meaningless outbound, Enforce State is
/// meaningless inbound, and the target sits on the opposite side in each direction. Getting one of those backwards
/// produces a page that looks right and tells the reader the wrong thing, which is why the logic lives here as
/// plain functions rather than inline in the markup.
/// </para>
/// </summary>
[TestFixture]
public class DataFlowDisplayTests
{
    [Test]
    public void PriorityLabel_ImportFlowThatHasNeverBeenOrdered_ReadsAsUnranked()
    {
        // int.MaxValue is the safe-addition sentinel a new mapping is created with, not a position anybody chose.
        // Printing it as "2147483647" would be technically true and completely useless.
        var flow = BuildImportFlow(priority: int.MaxValue);

        Assert.That(DataFlowDisplay.PriorityLabel(flow), Is.EqualTo("Unranked"));
    }

    [Test]
    public void PriorityLabel_SoleContributor_ReadsAsThatNumberAlone()
    {
        // "1 of 1" is noise: there is no contest to state the size of.
        var flow = BuildImportFlow(priority: 1);
        flow.ContributorCount = 1;

        Assert.That(DataFlowDisplay.PriorityLabel(flow), Is.EqualTo("1"));
    }

    [Test]
    public void PriorityLabel_ContestedFlow_CarriesItsDenominator()
    {
        // A bare "2" is a position with nothing to be a position in, and the reader has to reconstruct the set by
        // eye from neighbouring rows, which any re-sort or page boundary breaks. The count travels with the number.
        var flow = BuildImportFlow(priority: 2);
        flow.ContributorCount = 3;

        Assert.That(DataFlowDisplay.PriorityLabel(flow), Is.EqualTo("2 of 3"));
    }

    [Test]
    public void PriorityLabel_ExportFlow_HasNoLabel()
    {
        // Priority orders contributions into the Metaverse. An Export flow has no competitors to be ordered
        // against, so a number here would invent a concept the engine does not have.
        var flow = BuildExportFlow();

        Assert.That(DataFlowDisplay.PriorityLabel(flow), Is.Null);
    }

    [Test]
    public void IsTopPriorityContender_ContestedRankOne_IsTheOneToEmphasise()
    {
        // Colour has to encode rank, not "is contested". Emphasising every contested row paints identical chips on
        // 1 and 2 alike, which is what made the column unreadable: the eye is told something is significant and
        // then given no way to tell the significant one from its competitor.
        var winner = BuildImportFlow(priority: 1);
        winner.ContributorCount = 2;
        var loser = BuildImportFlow(priority: 2);
        loser.ContributorCount = 2;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DataFlowDisplay.IsTopPriorityContender(winner), Is.True);
            Assert.That(DataFlowDisplay.IsTopPriorityContender(loser), Is.False);
        }
    }

    [Test]
    public void IsTopPriorityContender_SoleContributor_IsNotEmphasised()
    {
        // Nothing is competing, so there is no contest to be winning. Emphasis here would invite an administrator
        // to read significance into an ordering that decides nothing.
        var flow = BuildImportFlow(priority: 1);
        flow.ContributorCount = 1;

        Assert.That(DataFlowDisplay.IsTopPriorityContender(flow), Is.False);
    }

    [Test]
    public void IsTopPriorityContender_ExportFlow_IsNeverEmphasised()
    {
        Assert.That(DataFlowDisplay.IsTopPriorityContender(BuildExportFlow()), Is.False);
    }

    [Test]
    public void DirectionLabel_UsesTheSameWordsAsTheSynchronisationRulesList()
    {
        // "Inbound"/"Outbound" is the vocabulary every other JIM surface uses for rule direction; the Data Flow
        // page showing "Import"/"Export" instead would read as a different concept.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DataFlowDisplay.DirectionLabel(SyncRuleDirection.Import), Is.EqualTo("Inbound"));
            Assert.That(DataFlowDisplay.DirectionLabel(SyncRuleDirection.Export), Is.EqualTo("Outbound"));
        }
    }

    [Test]
    public void TargetName_ImportFlow_IsTheMetaverseAttributeItWrites()
    {
        var flow = BuildImportFlow(priority: 1);

        Assert.That(DataFlowDisplay.TargetName(flow), Is.EqualTo("Department"));
    }

    [Test]
    public void TargetName_ExportFlow_IsTheConnectedSystemAttributeItWrites()
    {
        var flow = BuildExportFlow();

        Assert.That(DataFlowDisplay.TargetName(flow), Is.EqualTo("department"));
    }

    [Test]
    public void SourceNames_KeepsEvaluationOrderAndNamesExpressionsAsSuch()
    {
        // Sources are evaluated lowest Order first, and the order is the behaviour: showing them shuffled would
        // misrepresent which source supplies the value. An expression has no attribute to name.
        var flow = BuildImportFlow(priority: 1);
        flow.Sources =
        [
            new DataFlowSource { Order = 1, Expression = "ToUpper(cs[\"dept\"])" },
            new DataFlowSource { Order = 0, ConnectedSystemAttributeId = 9, ConnectedSystemAttributeName = "dept" }
        ];

        Assert.That(DataFlowDisplay.SourceNames(flow), Is.EqualTo(new[] { "dept", "Expression" }));
    }

    [Test]
    public void TargetHref_ImportFlow_PointsAtTheAttributesPriorityOrder()
    {
        // The question an Import row provokes is "what else writes this, and who wins?", which Surface 2 answers.
        var flow = BuildImportFlow(priority: 1);

        Assert.That(DataFlowDisplay.TargetHref(flow), Is.EqualTo("/admin/schema/object-types/100?t=attributes"));
    }

    [Test]
    public void TargetHref_ExportFlow_PointsAtTheConnectedSystemsSchema()
    {
        // An Export target is a Connected System attribute, so there is no priority order to visit; the schema
        // the attribute belongs to is the useful destination.
        var flow = BuildExportFlow();

        Assert.That(DataFlowDisplay.TargetHref(flow), Is.EqualTo("/admin/connected-systems/3?t=schema"));
    }

    [Test]
    public void RuleHref_PointsAtTheOwningRulesAttributeFlowTab()
    {
        // Landing on the rule's Details tab would make the reader hunt for the mapping they clicked through for.
        var flow = BuildImportFlow(priority: 1);

        Assert.That(DataFlowDisplay.RuleHref(flow), Is.EqualTo("/admin/sync-rules/11?t=attribute-flow"));
    }

    [Test]
    public void PriorityTooltip_ContestedImportFlow_SaysThePositionDecidesTheValue()
    {
        var flow = BuildImportFlow(priority: 1);
        flow.ContributorCount = 3;

        Assert.That(DataFlowDisplay.PriorityTooltip(flow), Does.Contain("3"));
    }

    [Test]
    public void PriorityTooltip_SoleContributor_SaysThePositionDecidesNothing()
    {
        // A number next to a sole contributor invites an administrator to "fix" an ordering that has no effect.
        var flow = BuildImportFlow(priority: 1);
        flow.ContributorCount = 1;

        Assert.That(DataFlowDisplay.PriorityTooltip(flow), Does.Contain("only").IgnoreCase);
    }

    [Test]
    public void PriorityTooltip_UnrankedFlow_ExplainsWhatUnrankedMeans()
    {
        var flow = BuildImportFlow(priority: int.MaxValue);
        flow.ContributorCount = 2;

        Assert.That(DataFlowDisplay.PriorityTooltip(flow), Does.Contain("lowest").IgnoreCase);
    }

    [Test]
    public void PriorityTooltip_ExportFlow_ExplainsPriorityIsAnInboundConcern()
    {
        var flow = BuildExportFlow();

        Assert.That(DataFlowDisplay.PriorityTooltip(flow), Does.Contain("Inbound"));
    }

    private static DataFlowHeader BuildImportFlow(int priority) => new()
    {
        SyncRuleMappingId = 1,
        SyncRuleId = 11,
        SyncRuleName = "HR Import",
        SyncRuleEnabled = true,
        Direction = SyncRuleDirection.Import,
        ConnectedSystemId = 2,
        ConnectedSystemName = "HR System",
        ConnectedSystemObjectTypeId = 20,
        ConnectedSystemObjectTypeName = "Employee",
        MetaverseObjectTypeId = 100,
        MetaverseObjectTypeName = "Person",
        TargetMetaverseAttributeId = 500,
        TargetMetaverseAttributeName = "Department",
        Priority = priority,
        NullIsValue = false,
        Sources = [new DataFlowSource { Order = 0, ConnectedSystemAttributeId = 9, ConnectedSystemAttributeName = "dept" }]
    };

    private static DataFlowHeader BuildExportFlow() => new()
    {
        SyncRuleMappingId = 2,
        SyncRuleId = 12,
        SyncRuleName = "AD Export",
        SyncRuleEnabled = true,
        Direction = SyncRuleDirection.Export,
        ConnectedSystemId = 3,
        ConnectedSystemName = "Contoso AD",
        ConnectedSystemObjectTypeId = 30,
        ConnectedSystemObjectTypeName = "user",
        MetaverseObjectTypeId = 100,
        MetaverseObjectTypeName = "Person",
        TargetConnectedSystemAttributeId = 600,
        TargetConnectedSystemAttributeName = "department",
        EnforceState = true,
        Sources = [new DataFlowSource { Order = 0, MetaverseAttributeId = 500, MetaverseAttributeName = "Department" }]
    };
}
