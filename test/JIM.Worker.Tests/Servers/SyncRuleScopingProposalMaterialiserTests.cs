// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Application.Servers.Preview;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Search;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Turning a proposed criteria tree back into a Synchronisation Rule the evaluator can be asked about (#1436).
///
/// The sharp edge this exists for: <see cref="ScopingEvaluationServer"/> reads each criterion's attribute ENTITY,
/// not its id, and a criterion whose attribute navigation is null evaluates false. A materialiser that carried the
/// ids across and left the navigations unset would therefore produce a rule that silently matches nothing, and a
/// preview that confidently reported every object leaving scope.
/// </summary>
[TestFixture]
public class SyncRuleScopingProposalMaterialiserTests
{
    [Test]
    public void Materialise_ImportProposal_AttachesTheConnectedSystemAttributeEntities()
    {
        var storedRule = BuildImportRule();
        var proposal = new SyncRuleScopingProposal([Group(CsCriterion(DepartmentAttributeId, "Sales"))]);

        var standIn = SyncRuleScopingProposalMaterialiser.Materialise(storedRule, proposal, ConnectedSystemAttributes(), []);

        var criterion = standIn.ObjectScopingCriteriaGroups.Single().Criteria.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.ConnectedSystemAttribute, Is.Not.Null,
                "the evaluator reads the attribute entity; a null navigation matches nothing at all");
            Assert.That(criterion.ConnectedSystemAttribute!.Id, Is.EqualTo(DepartmentAttributeId));
            Assert.That(criterion.ConnectedSystemAttributeId, Is.EqualTo(DepartmentAttributeId));
        }
    }

    [Test]
    public void Materialise_ImportProposal_ActuallyScopesWhenEvaluated()
    {
        // The assertion that would have caught a half-attached stand-in: put the materialised rule to the real
        // evaluator and require it to tell two objects apart.
        var storedRule = BuildImportRule();
        var proposal = new SyncRuleScopingProposal([Group(CsCriterion(DepartmentAttributeId, "Sales"))]);
        var standIn = SyncRuleScopingProposalMaterialiser.Materialise(storedRule, proposal, ConnectedSystemAttributes(), []);
        var evaluator = new ScopingEvaluationServer();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evaluator.IsCsoInScopeForImportRule(CsoWithDepartment("Sales"), standIn), Is.True);
            Assert.That(evaluator.IsCsoInScopeForImportRule(CsoWithDepartment("Engineering"), standIn), Is.False);
        }
    }

    [Test]
    public void Materialise_ExportProposal_AttachesTheMetaverseAttributeEntities()
    {
        var storedRule = BuildExportRule();
        var proposal = new SyncRuleScopingProposal([Group(MvCriterion(CountryAttributeId, "UK"))]);

        var standIn = SyncRuleScopingProposalMaterialiser.Materialise(storedRule, proposal, [], MetaverseAttributes());

        var criterion = standIn.ObjectScopingCriteriaGroups.Single().Criteria.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.MetaverseAttribute, Is.Not.Null);
            Assert.That(criterion.MetaverseAttribute!.Id, Is.EqualTo(CountryAttributeId));
        }
    }

    [Test]
    public void Materialise_NestedGroups_ArePreservedWithTheirCombiningRule()
    {
        var storedRule = BuildImportRule();
        var proposal = new SyncRuleScopingProposal([
            new SyncRuleScopingCriteriaGroupProposal(
                SearchGroupType.All,
                [CsCriterion(DepartmentAttributeId, "Sales")],
                [new SyncRuleScopingCriteriaGroupProposal(SearchGroupType.Any, [CsCriterion(DepartmentAttributeId, "Marketing")], [])])
        ]);

        var standIn = SyncRuleScopingProposalMaterialiser.Materialise(storedRule, proposal, ConnectedSystemAttributes(), []);

        var group = standIn.ObjectScopingCriteriaGroups.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.Type, Is.EqualTo(SearchGroupType.All));
            Assert.That(group.ChildGroups, Has.Count.EqualTo(1));
            Assert.That(group.ChildGroups[0].Type, Is.EqualTo(SearchGroupType.Any));
            Assert.That(group.ChildGroups[0].Criteria.Single().ConnectedSystemAttribute, Is.Not.Null,
                "a nested criterion needs its attribute attaching exactly as a top-level one does");
        }
    }

    [Test]
    public void Materialise_CriterionNamingAnUnknownAttribute_Throws()
    {
        // Silently dropping it would leave a narrower proposal evaluating as though the criterion were not there,
        // which reads as a wider scope than the administrator asked for: the failure direction that pulls objects
        // IN rather than out, and the one nobody would notice in a count.
        var storedRule = BuildImportRule();
        var proposal = new SyncRuleScopingProposal([Group(CsCriterion(connectedSystemAttributeId: 999, "Sales"))]);

        Assert.That(() => SyncRuleScopingProposalMaterialiser.Materialise(storedRule, proposal, ConnectedSystemAttributes(), []),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Materialise_LeavesTheStoredRuleUntouched()
    {
        // The stand-in must never be the loaded rule with its criteria swapped: the adapter compares the two, and
        // an in-place edit would make it compare the proposal against itself and report that nothing would change.
        var storedRule = BuildImportRule();
        storedRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All });
        var proposal = new SyncRuleScopingProposal([Group(CsCriterion(DepartmentAttributeId, "Sales"))]);

        var standIn = SyncRuleScopingProposalMaterialiser.Materialise(storedRule, proposal, ConnectedSystemAttributes(), []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(standIn, Is.Not.SameAs(storedRule));
            Assert.That(storedRule.ObjectScopingCriteriaGroups.Single().Criteria, Is.Empty,
                "the stored rule's own criteria must be exactly as they were loaded");
            Assert.That(standIn.Id, Is.EqualTo(storedRule.Id), "the stand-in stands in for that rule, so it keeps its id");
            Assert.That(standIn.Direction, Is.EqualTo(storedRule.Direction));
        }
    }

    #region helpers

    private const int DepartmentAttributeId = 7;
    private const int CountryAttributeId = 11;

    private static SyncRuleScopingCriteriaGroupProposal Group(params SyncRuleScopingCriterionProposal[] criteria) =>
        new(SearchGroupType.All, criteria, []);

    private static SyncRuleScopingCriterionProposal CsCriterion(int connectedSystemAttributeId, string value) =>
        new(null, connectedSystemAttributeId, SearchComparisonType.Equals, StringValue: value);

    private static SyncRuleScopingCriterionProposal MvCriterion(int metaverseAttributeId, string value) =>
        new(metaverseAttributeId, null, SearchComparisonType.Equals, StringValue: value);

    private static List<ConnectedSystemObjectTypeAttribute> ConnectedSystemAttributes() =>
    [
        new() { Id = DepartmentAttributeId, Name = "department", Type = AttributeDataType.Text }
    ];

    private static List<MetaverseAttribute> MetaverseAttributes() =>
    [
        new() { Id = CountryAttributeId, Name = "Country", Type = AttributeDataType.Text }
    ];

    private static SyncRule BuildImportRule() => new()
    {
        Id = 3,
        Name = "HR Import",
        Direction = SyncRuleDirection.Import,
        Enabled = true,
        ConnectedSystemId = 1,
        ConnectedSystemObjectTypeId = 2,
        MetaverseObjectTypeId = 4
    };

    private static SyncRule BuildExportRule() => new()
    {
        Id = 5,
        Name = "AD Export",
        Direction = SyncRuleDirection.Export,
        Enabled = true,
        ConnectedSystemId = 1,
        ConnectedSystemObjectTypeId = 2,
        MetaverseObjectTypeId = 4
    };

    private static ConnectedSystemObject CsoWithDepartment(string department)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = 1, TypeId = 2 };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = DepartmentAttributeId,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = DepartmentAttributeId, Name = "department", Type = AttributeDataType.Text },
            StringValue = department
        });
        return cso;
    }

    #endregion
}
