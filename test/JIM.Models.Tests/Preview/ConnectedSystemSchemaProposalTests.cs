// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.Preview;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Preview;

/// <summary>
/// The vocabulary a Connected System schema selection preview is written in (#1475, #827 gap G6).
///
/// Every claim the adapter makes rests on the comparison here answering honestly, and the ways it can lie quietly
/// are what these tests pin: an anchor that looks newly selected on a proposal that changed nothing, a set compared
/// as a sequence so a reordered payload reads as a change, and a rename read as a synchronisation-affecting edit.
/// </summary>
[TestFixture]
public class ConnectedSystemSchemaProposalTests
{
    private const int UserTypeId = 100;
    private const int GroupTypeId = 200;

    [Test]
    public void FromCurrentConfiguration_AProposalOfWhatIsAlreadyInForce_DescribesTheSameSchema()
    {
        var objectTypes = new List<ConnectedSystemObjectType> { BuildUserType(), BuildGroupType() };

        var baseline = ConnectedSystemSchemaProposal.FromCurrentConfiguration(objectTypes);
        var proposal = ConnectedSystemSchemaProposal.FromCurrentConfiguration(objectTypes);

        Assert.That(baseline.DescribesSameSchemaAs(proposal), Is.True,
            "asking what the configuration already in force would do must answer 'nothing', which is the cheapest " +
            "honest answer a preview can give and the one every other proposal shape provides");
    }

    [Test]
    public void FromCurrentConfiguration_AnExternalIdThatIsNotSelected_IsStillCarriedAsSelected()
    {
        // The anchor is selected implicitly and cannot be deselected. Reading it off the Selected column alone
        // would leave it out of the baseline, so a proposal that carried it would look like one that selects a new
        // attribute, on a save where nothing changed.
        var objectType = BuildUserType();
        objectType.Attributes.Single(a => a.IsExternalId).Selected = false;

        var proposal = ConnectedSystemSchemaProposal.FromCurrentConfiguration([objectType]);

        Assert.That(proposal.For(UserTypeId)!.SelectedAttributeIds, Does.Contain(1));
    }

    [Test]
    public void DescribesSameSchemaAs_TheSameAttributesInADifferentOrder_IsNotAChange()
    {
        // Attribute selection is a set. No Connector reads it in order, so a payload that arrives sorted
        // differently must not report a schema change and set a preview running over the whole system.
        var first = new ConnectedSystemSchemaProposal([Selection([1, 2, 3])]);
        var second = new ConnectedSystemSchemaProposal([Selection([3, 1, 2])]);

        Assert.That(first.DescribesSameSchemaAs(second), Is.True);
    }

    [Test]
    public void DescribesSameSchemaAs_TheSameObjectTypesInADifferentOrder_IsNotAChange()
    {
        var first = new ConnectedSystemSchemaProposal([Selection([1], UserTypeId), Selection([1], GroupTypeId)]);
        var second = new ConnectedSystemSchemaProposal([Selection([1], GroupTypeId), Selection([1], UserTypeId)]);

        Assert.That(first.DescribesSameSchemaAs(second), Is.True);
    }

    [Test]
    public void DescribesSameSchemaAs_ATypeRenamedAndNothingElse_IsNotAChange()
    {
        // The name is display material, carried so the preview reads in the administrator's vocabulary and still
        // does after the type is renamed. It changes nothing about what synchronisation does.
        var first = new ConnectedSystemSchemaProposal([Selection([1]) with { Name = "User" }]);
        var second = new ConnectedSystemSchemaProposal([Selection([1]) with { Name = "Person" }]);

        Assert.That(first.DescribesSameSchemaAs(second), Is.True);
    }

    [Test]
    public void DescribesSameSchemaAs_EachSettingInTurn_IsAChange()
    {
        var baseline = Selection([1, 2]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new ConnectedSystemSchemaProposal([baseline])
                .DescribesSameSchemaAs(new ConnectedSystemSchemaProposal([baseline with { Selected = false }])),
                Is.False, "the Object Type's own selection");

            Assert.That(new ConnectedSystemSchemaProposal([baseline])
                .DescribesSameSchemaAs(new ConnectedSystemSchemaProposal(
                    [baseline with { RemoveContributedAttributesOnObsoletion = false }])),
                Is.False, "the obsoletion recall toggle");

            Assert.That(new ConnectedSystemSchemaProposal([baseline])
                .DescribesSameSchemaAs(new ConnectedSystemSchemaProposal([Selection([1])])),
                Is.False, "an attribute leaving the selection");

            Assert.That(new ConnectedSystemSchemaProposal([baseline])
                .DescribesSameSchemaAs(new ConnectedSystemSchemaProposal([Selection([1, 2, 3])])),
                Is.False, "an attribute joining it");
        }
    }

    [Test]
    public void DescribesSameSchemaAs_AnObjectTypeThePayloadDoesNotMention_IsAChange()
    {
        // A payload that drops a type says something different from one that carries it deselected, and the two
        // must not compare equal: the adapter reads "not mentioned" as "left alone", so silently equating them
        // would let a truncated payload pass as no change.
        var whole = new ConnectedSystemSchemaProposal([Selection([1], UserTypeId), Selection([1], GroupTypeId)]);
        var partial = new ConnectedSystemSchemaProposal([Selection([1], UserTypeId)]);

        Assert.That(whole.DescribesSameSchemaAs(partial), Is.False);
    }

    [Test]
    public void For_AnObjectTypeThePayloadDoesNotMention_IsNull()
    {
        var proposal = new ConnectedSystemSchemaProposal([Selection([1], UserTypeId)]);

        Assert.That(proposal.For(GroupTypeId), Is.Null,
            "saying nothing about a type must be distinguishable from proposing it deselected");
    }

    [Test]
    public void AttributesDeselectedFrom_TheAttributesTheProposalDrops_AreNamedInOrder()
    {
        var stored = Selection([1, 2, 3, 4]);
        var proposed = Selection([1, 3]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposed.AttributesDeselectedFrom(stored), Is.EqualTo(new[] { 2, 4 }));
            Assert.That(proposed.AttributesSelectedBeyond(stored), Is.Empty);
        }
    }

    [Test]
    public void AttributesSelectedBeyond_TheAttributesTheProposalAdds_AreNamedInOrder()
    {
        var stored = Selection([1]);
        var proposed = Selection([1, 5, 3]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposed.AttributesSelectedBeyond(stored), Is.EqualTo(new[] { 3, 5 }));
            Assert.That(proposed.AttributesDeselectedFrom(stored), Is.Empty);
        }
    }

    [Test]
    public void AttributesDeselectedFrom_NoStoredSelectionAtAll_TakesNothingAway()
    {
        // A type that is newly discovered has no stored selection to compare against. Reading null as "everything
        // was selected" would report every attribute as being deselected by a proposal that selects some.
        var proposed = Selection([1, 2]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposed.AttributesDeselectedFrom(null), Is.Empty);
            Assert.That(proposed.AttributesSelectedBeyond(null), Is.EqualTo(new[] { 1, 2 }));
        }
    }

    [Test]
    public void DescribesSameSchemaAs_Null_IsNotTheSame() =>
        Assert.That(new ConnectedSystemSchemaProposal([Selection([1])]).DescribesSameSchemaAs(null), Is.False);

    private static ConnectedSystemObjectTypeSelectionProposal Selection(int[] attributeIds, int objectTypeId = UserTypeId) =>
        new(objectTypeId, "User", Selected: true, RemoveContributedAttributesOnObsoletion: true, attributeIds);

    private static ConnectedSystemObjectType BuildUserType() =>
        new()
        {
            Id = UserTypeId,
            Name = "User",
            Selected = true,
            RemoveContributedAttributesOnObsoletion = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "objectGuid", Type = AttributeDataType.Guid, IsExternalId = true },
                new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Text, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "department", Type = AttributeDataType.Text }
            ]
        };

    private static ConnectedSystemObjectType BuildGroupType() =>
        new()
        {
            Id = GroupTypeId,
            Name = "Group",
            Selected = false,
            RemoveContributedAttributesOnObsoletion = false,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 10, Name = "objectGuid", Type = AttributeDataType.Guid, IsExternalId = true }
            ]
        };
}
