// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Pins the Object Type paging order a synchronisation derives from its Synchronisation Rules (#2492): referenced
/// types come before the types that reference them, so references between types resolve in the main page loop.
/// </summary>
[TestFixture]
public class SyncObjectTypeOrderingTests
{
    private static readonly MetaverseAttribute MvReference = new() { Id = 1000, Name = "Reference", Type = AttributeDataType.Reference };
    private static readonly MetaverseAttribute MvText = new() { Id = 1001, Name = "Text", Type = AttributeDataType.Text };

    [Test]
    public void Compute_NoReferenceMappings_OrdersByTypeId()
    {
        var group = Type(5, "group");
        var person = Type(2, "person");
        var rules = new List<SyncRule> { ImportRule(group, MvText, Attribute(group, 50, AttributeDataType.Text)) };

        var order = SyncObjectTypeOrdering.Compute([group, person], rules);

        Assert.That(order.TypeIds, Is.EqualTo(new[] { 2, 5 }));
    }

    [Test]
    public void Compute_DeclaredTargetType_PlacesReferencedTypeFirstEvenWhenItsIdIsHigher()
    {
        var group = Type(1, "group");
        var person = Type(9, "person");
        var member = Attribute(group, 10, AttributeDataType.Reference, referencedTypeId: person.Id);
        var rules = new List<SyncRule> { ImportRule(group, MvReference, member) };

        var order = SyncObjectTypeOrdering.Compute([group, person], rules);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(order.TypeIds, Is.EqualTo(new[] { 9, 1 }));
            Assert.That(order.Groups, Has.Count.EqualTo(2));
            Assert.That(order.SelfReferencingTypeIds, Is.Empty);
        }
    }

    [Test]
    public void Compute_UndeclaredTargetType_PlacesTypesWithoutReferenceMappingsFirst()
    {
        // LDAP's member declares no target type: it may point at any type, the group's own type included.
        var group = Type(1, "group");
        var person = Type(9, "person");
        var member = Attribute(group, 10, AttributeDataType.Reference);
        var rules = new List<SyncRule>
        {
            ImportRule(group, MvReference, member),
            ImportRule(person, MvText, Attribute(person, 90, AttributeDataType.Text)),
        };

        var order = SyncObjectTypeOrdering.Compute([group, person], rules);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(order.TypeIds, Is.EqualTo(new[] { 9, 1 }));
            Assert.That(order.SelfReferencingTypeIds, Is.EquivalentTo(new[] { 1 }));
        }
    }

    /// <summary>
    /// The common Active Directory shape: member and manager both declare no target type. Assumed to point anywhere,
    /// person and group would reach each other and collapse into one group; the targets observed in the data
    /// (managers are people, members are people) separate them, people first.
    /// </summary>
    [Test]
    public void Compute_UndeclaredTargetsWithObservedTargetTypes_UsesTheObservedTypes()
    {
        var person = Type(1, "person");
        var group = Type(2, "group");
        var manager = Attribute(person, 10, AttributeDataType.Reference);
        var member = Attribute(group, 20, AttributeDataType.Reference);
        var rules = new List<SyncRule> { ImportRule(person, MvReference, manager), ImportRule(group, MvReference, member) };
        var observed = new Dictionary<int, IReadOnlySet<int>>
        {
            [manager.Id] = new HashSet<int> { person.Id },
            [member.Id] = new HashSet<int> { person.Id },
        };

        var schemaOnly = SyncObjectTypeOrdering.Compute([group, person], rules);
        var withObserved = SyncObjectTypeOrdering.Compute([group, person], rules, observed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schemaOnly.Groups, Has.Count.EqualTo(1), "Without observations the two types reach each other.");
            Assert.That(withObserved.Groups.Select(g => g.ToArray()), Is.EqualTo(new[] { new[] { 1 }, new[] { 2 } }));
            Assert.That(withObserved.SelfReferencingTypeIds, Is.EquivalentTo(new[] { 1 }));
        }
    }

    [Test]
    public void Compute_UndeclaredTargetObservedToHoldNoValues_AddsNoEdges()
    {
        var group = Type(1, "group");
        var person = Type(9, "person");
        var member = Attribute(group, 10, AttributeDataType.Reference);
        var observed = new Dictionary<int, IReadOnlySet<int>> { [member.Id] = new HashSet<int>() };

        var order = SyncObjectTypeOrdering.Compute([group, person], [ImportRule(group, MvReference, member)], observed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(order.TypeIds, Is.EqualTo(new[] { 1, 9 }));
            Assert.That(order.SelfReferencingTypeIds, Is.Empty);
        }
    }

    [Test]
    public void Compute_UndeclaredTargetNotObserved_StillAssumesEveryType()
    {
        var group = Type(1, "group");
        var person = Type(9, "person");
        var member = Attribute(group, 10, AttributeDataType.Reference);

        var order = SyncObjectTypeOrdering.Compute([group, person], [ImportRule(group, MvReference, member)],
            new Dictionary<int, IReadOnlySet<int>>());

        Assert.That(order.TypeIds, Is.EqualTo(new[] { 9, 1 }));
    }

    [Test]
    public void GetUndeclaredMappedReferenceAttributeIds_ReturnsOnlyMappedReferenceAttributesWithNoDeclaredTarget()
    {
        var group = Type(1, "group");
        var person = Type(9, "person");
        var member = Attribute(group, 10, AttributeDataType.Reference);
        var owner = Attribute(group, 11, AttributeDataType.Reference, referencedTypeId: person.Id);
        Attribute(group, 12, AttributeDataType.Reference);
        var rules = new List<SyncRule> { ImportRule(group, MvReference, member, owner) };

        var ids = SyncObjectTypeOrdering.GetUndeclaredMappedReferenceAttributeIds([group, person], rules);

        Assert.That(ids, Is.EquivalentTo(new[] { member.Id }));
    }

    [Test]
    public void Compute_SelfReference_StaysAGroupOfOneAndIsReported()
    {
        var person = Type(3, "person");
        var manager = Attribute(person, 30, AttributeDataType.Reference, referencedTypeId: person.Id);

        var order = SyncObjectTypeOrdering.Compute([person], [ImportRule(person, MvReference, manager)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(order.Groups, Has.Count.EqualTo(1));
            Assert.That(order.TypeIds, Is.EqualTo(new[] { 3 }));
            Assert.That(order.SelfReferencingTypeIds, Is.EquivalentTo(new[] { 3 }));
        }
    }

    [Test]
    public void Compute_TwoTypeCycle_CollapsesIntoOneGroupAfterItsDependencies()
    {
        // a -> b, b -> a: one group. Both reference c, so c comes first; d is unrelated and has the lowest id.
        var a = Type(4, "a");
        var b = Type(2, "b");
        var c = Type(8, "c");
        var d = Type(1, "d");
        var rules = new List<SyncRule>
        {
            ImportRule(a, MvReference, Attribute(a, 40, AttributeDataType.Reference, referencedTypeId: b.Id),
                Attribute(a, 41, AttributeDataType.Reference, referencedTypeId: c.Id)),
            ImportRule(b, MvReference, Attribute(b, 20, AttributeDataType.Reference, referencedTypeId: a.Id),
                Attribute(b, 21, AttributeDataType.Reference, referencedTypeId: c.Id)),
        };

        var order = SyncObjectTypeOrdering.Compute([a, b, c, d], rules);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(order.Groups.Select(g => g.ToArray()), Is.EqualTo(new[] { new[] { 1 }, new[] { 8 }, new[] { 2, 4 } }));
            Assert.That(order.SelfReferencingTypeIds, Is.Empty);
        }
    }

    [Test]
    public void Compute_ExportRulesAndUnmappedReferenceAttributes_AddNoEdges()
    {
        // The deferral only ever considers attributes an import mapping flows to a Metaverse reference, so neither
        // an export rule nor a reference attribute flowing to a text attribute may reorder anything.
        var group = Type(1, "group");
        var person = Type(9, "person");
        var member = Attribute(group, 10, AttributeDataType.Reference, referencedTypeId: person.Id);
        var exportRule = ImportRule(group, MvReference, member);
        exportRule.Direction = SyncRuleDirection.Export;
        var rules = new List<SyncRule> { exportRule, ImportRule(group, MvText, member) };

        var order = SyncObjectTypeOrdering.Compute([group, person], rules);

        Assert.That(order.TypeIds, Is.EqualTo(new[] { 1, 9 }));
    }

    [Test]
    public void Compute_SameInputInAnyOrder_ProducesTheSameOrder()
    {
        var a = Type(3, "a");
        var b = Type(1, "b");
        var c = Type(2, "c");
        var rules = new List<SyncRule>
        {
            ImportRule(a, MvReference, Attribute(a, 30, AttributeDataType.Reference, referencedTypeId: c.Id)),
        };

        var first = SyncObjectTypeOrdering.Compute([a, b, c], rules);
        var second = SyncObjectTypeOrdering.Compute([c, a, b], rules);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.TypeIds, Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(second.TypeIds, Is.EqualTo(first.TypeIds));
        }
    }

    [Test]
    public void Describe_NamesGroupsInOrder()
    {
        var order = new SyncObjectTypeOrder([[2], [5, 7]], new HashSet<int>());
        var names = new Dictionary<int, string> { [2] = "person", [5] = "group", [7] = "contact" };

        Assert.That(order.Describe(id => names[id]), Is.EqualTo("person, then (group, contact)"));
    }

    private static ConnectedSystemObjectType Type(int id, string name) => new() { Id = id, Name = name };

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, int id, AttributeDataType dataType, int? referencedTypeId = null)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = id,
            Name = $"attr{id}",
            Type = dataType,
            ConnectedSystemObjectType = type,
            ReferencedObjectTypeId = referencedTypeId,
        };
        type.Attributes.Add(attribute);
        return attribute;
    }

    private static SyncRule ImportRule(ConnectedSystemObjectType type, MetaverseAttribute target, params ConnectedSystemObjectTypeAttribute[] sources)
    {
        var rule = new SyncRule
        {
            Id = type.Id * 100 + type.Attributes.Count,
            Name = $"rule-{type.Name}",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemObjectType = type,
            ConnectedSystemObjectTypeId = type.Id,
        };
        foreach (var source in sources)
        {
            var mapping = new SyncRuleMapping { TargetMetaverseAttribute = target, TargetMetaverseAttributeId = target.Id };
            mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id });
            rule.AttributeFlowRules.Add(mapping);
        }
        return rule;
    }
}
