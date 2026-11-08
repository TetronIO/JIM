// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// #2818: a Delta Synchronisation loads a large group's member attribute without its values where Drift Detection is its
/// only reader, and Drift Detection takes the values the group and its Metaverse Object disagree on from the database
/// (<see cref="DriftHeldReferenceAnswer"/>) instead. These tests hold that path to the whole-load path's answer: the same
/// drifted attributes, the same corrections in the same order, the same errors, over a broad generated set of groups.
/// The database's statement is held to <see cref="DriftHeldReferenceAnswer.Compute"/> separately
/// (<c>DriftHeldReferenceAnswerDatabaseTests</c>), so between them the database's answer gives the whole load's outcome.
/// </summary>
[TestFixture]
public class DriftHeldReferenceDifferentialTests
{
    private const int Seed = 2818;
    private const int GroupCount = 4_000;
    private const int MinimumOutcomesPerKind = 40;

    private const int SystemId = 7;
    private const int OtherSystemId = 8;
    private const int GroupTypeId = 70;
    private const int GroupMvTypeId = 80;

    private ConnectedSystemObjectType _groupType = null!;
    private ConnectedSystemObjectTypeAttribute _member = null!;
    private ConnectedSystemObjectTypeAttribute _managedBy = null!;
    private ConnectedSystemObjectTypeAttribute _description = null!;
    private ConnectedSystemObjectTypeAttribute _externalId = null!;
    private MetaverseObjectType _groupMvType = null!;
    private MetaverseAttribute _staticMembers = null!;
    private MetaverseAttribute _owners = null!;
    private MetaverseAttribute _mvDescription = null!;
    private DriftDetectionService _drift = null!;

    /// <summary>
    /// What a generated group came to, so each is shown to be covered.
    /// </summary>
    private enum Outcome
    {
        NoDrift,
        DriftAddingOnly,
        DriftRemovingOnly,
        DriftAddingAndRemoving,
        HeldOnlyUnderAnotherDataType
    }

    [SetUp]
    public void SetUp()
    {
        _groupType = new ConnectedSystemObjectType { Id = GroupTypeId, Name = "group", ConnectedSystemId = SystemId };
        _externalId = Attribute(101, "objectGUID", AttributeDataType.Text, AttributePlurality.SingleValued, isExternalId: true);
        _member = Attribute(102, "member", AttributeDataType.Reference, AttributePlurality.MultiValued);
        _managedBy = Attribute(103, "managedBy", AttributeDataType.Reference, AttributePlurality.MultiValued);
        _description = Attribute(104, "description", AttributeDataType.Text, AttributePlurality.SingleValued);

        _groupMvType = new MetaverseObjectType { Id = GroupMvTypeId, Name = "Group", PluralName = "Groups" };
        _staticMembers = new MetaverseAttribute { Id = 201, Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        _owners = new MetaverseAttribute { Id = 202, Name = "Owners", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        _mvDescription = new MetaverseAttribute { Id = 203, Name = "Description", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };

        // The service reads nothing from the application for Drift Detection.
        _drift = new DriftDetectionService(null!);
    }

    [Test]
    public void EvaluateDrift_GroupLoadedWithoutItsMembers_GivesTheWholeLoadsOutcomeForEveryGeneratedGroup()
    {
        var random = new Random(Seed);
        var disagreements = new List<string>();
        var outcomes = new Dictionary<Outcome, int>();

        for (var group = 0; group < GroupCount; group++)
        {
            var (whole, lean, mvo, rules) = GenerateGroup(random);

            var expected = _drift.EvaluateDrift(whole, mvo, rules);

            var questions = DriftDetectionService.PlanHeldReferenceQuestions(lean, mvo, rules);
            var answers = questions
                .Select(question => DriftHeldReferenceAnswer.Compute(question,
                    whole.AttributeValues.Where(value => value.AttributeId == question.AttributeId),
                    value => value.ResolvedReferenceMetaverseObjectId ?? value.ReferenceValue?.MetaverseObjectId))
                .ToList();
            var actual = _drift.EvaluateDrift(lean, mvo, rules, heldReferenceAnswers: answers);

            var outcome = Classify(expected, answers);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;

            var expectedDescription = Describe(expected);
            var actualDescription = Describe(actual);
            if (expectedDescription != actualDescription && disagreements.Count < 10)
                disagreements.Add($"group {group}:\n  whole load: {expectedDescription}\n  lean load:  {actualDescription}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(disagreements, Is.Empty, "the lean load must give the whole load's outcome for every group");
            foreach (var outcome in Enum.GetValues<Outcome>())
                Assert.That(outcomes.GetValueOrDefault(outcome), Is.GreaterThanOrEqualTo(MinimumOutcomesPerKind), $"{outcome} is covered");
        }
    }

    /// <summary>
    /// No silent gap: a member attribute loaded without its values, compared without an answer, fails rather than reading
    /// as holding nothing.
    /// </summary>
    [Test]
    public void EvaluateDrift_LeanGroupWithoutAnAnswer_ThrowsRatherThanReadingNoMembers()
    {
        var (_, lean, mvo, rules) = GenerateGroup(new Random(1), forceManagedByRule: false);

        Assert.That(() => _drift.EvaluateDrift(lean, mvo, rules), Throws.InstanceOf<PartiallyLoadedObjectException>());
    }

    /// <summary>
    /// An answer to a question asked about other expected values (the Metaverse Object changed after it was asked) is not
    /// used: it fails rather than correct the target to a set the Metaverse Object no longer holds.
    /// </summary>
    [Test]
    public void EvaluateDrift_AnswerToOtherExpectedValues_Throws()
    {
        var (whole, lean, mvo, rules) = GenerateGroup(new Random(2), forceManagedByRule: false);
        var questions = DriftDetectionService.PlanHeldReferenceQuestions(lean, mvo, rules);
        var answers = questions
            .Select(question => DriftHeldReferenceAnswer.Compute(question with { ExpectedValues = [.. question.ExpectedValues, Guid.NewGuid()] },
                whole.AttributeValues.Where(value => value.AttributeId == question.AttributeId),
                value => value.ResolvedReferenceMetaverseObjectId))
            .ToList();

        Assert.That(() => _drift.EvaluateDrift(lean, mvo, rules, heldReferenceAnswers: answers), Throws.InstanceOf<PartiallyLoadedObjectException>());
    }

    /// <summary>
    /// An expression mapping reads the values the object holds (#2180), so it is never answered from the database: on a
    /// lean group it fails rather than read the loaded values as the whole set.
    /// </summary>
    [Test]
    public void EvaluateDrift_ExpressionMappingOnLeanAttribute_Throws()
    {
        var (_, lean, mvo, _) = GenerateGroup(new Random(3), forceManagedByRule: false);
        var rule = ExportRule(1, "Expression");
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 99, TargetConnectedSystemAttribute = _member, TargetConnectedSystemAttributeId = _member.Id,
            Sources = { new SyncRuleMappingSource { Expression = "mv[\"Static Members\"]" } }
        });

        Assert.That(() => _drift.EvaluateDrift(lean, mvo, [rule], heldReferenceAnswers: []), Throws.InstanceOf<PartiallyLoadedObjectException>());
    }

    private Outcome Classify(DriftDetectionResult whole, List<DriftHeldReferenceAnswer> answers)
    {
        if (answers.Any(a => a.Differs && a.HeldOnlyUnderAnotherDataType != null))
            return Outcome.HeldOnlyUnderAnotherDataType;

        var changes = whole.CorrectiveExports.SelectMany(pe => pe.AttributeValueChanges)
            .Where(change => change.AttributeId == _member.Id || change.AttributeId == _managedBy.Id)
            .ToList();
        var adds = changes.Any(change => change.ChangeType == PendingExportAttributeChangeType.Add);
        var removes = changes.Any(change => change.ChangeType == PendingExportAttributeChangeType.Remove);
        return (adds, removes) switch
        {
            (true, true) => Outcome.DriftAddingAndRemoving,
            (true, false) => Outcome.DriftAddingOnly,
            (false, true) => Outcome.DriftRemovingOnly,
            _ => Outcome.NoDrift
        };
    }

    /// <summary>
    /// Everything a drift result says that leaves the run: which attributes drifted under which rule, each correction in
    /// order with every field it persists but its fresh id, and each error.
    /// </summary>
    private static string Describe(DriftDetectionResult result)
    {
        var drifted = string.Join(", ", result.DriftedAttributes.Select(d => $"{d.Attribute.Id}@{d.ExportRule.Id}"));
        var exports = string.Join(" | ", result.CorrectiveExports.Select(pe =>
            $"{pe.ConnectedSystemObjectId}/{pe.ChangeType}/{pe.Status}/{pe.SourceMetaverseObjectId}/{pe.HasUnresolvedReferences}: " +
            string.Join("; ", pe.AttributeValueChanges.Select(c =>
                $"{c.ChangeType} {c.AttributeId} [{c.SyncRuleId} {c.SyncRuleName}] {c.StringValue}{c.IntValue}{c.LongValue}{c.GuidValue}{c.UnresolvedReferenceValue}"))));
        var errors = string.Join(", ", result.AttributeFlowErrors.Select(e => $"{e.Kind}:{e.SourceAttributeName}->{e.TargetAttributeName}"));
        return $"drifted [{drifted}] exports [{exports}] errors [{errors}]";
    }

    /// <summary>
    /// One group: a Connected System Object loaded whole, the same object loaded without its member values (and, under a
    /// second rule, without its managedBy values), its Metaverse Object, and the Enforce State export rules.
    /// </summary>
    private (ConnectedSystemObject Whole, ConnectedSystemObject Lean, MetaverseObject Mvo, List<SyncRule> Rules) GenerateGroup(
        Random random, bool? forceManagedByRule = null)
    {
        // The Metaverse Objects members can resolve to, and the Connected System Objects they are referenced through: some
        // unjoined, some two joined to one Metaverse Object, so two held values resolve to it.
        var mvoPool = Enumerable.Range(0, random.Next(1, 30)).Select(_ => Guid.NewGuid()).ToList();
        var referencedPool = Enumerable.Range(0, random.Next(1, 36)).Select(_ =>
        {
            var joined = random.Next(6) != 0;
            return (CsoId: Guid.NewGuid(), MvoId: joined ? mvoPool[random.Next(mvoPool.Count)] : (Guid?)null);
        }).ToList();

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _groupMvType };
        AddExpectedReferences(random, mvo, _staticMembers, mvoPool, referencedPool);
        var withManagedBy = forceManagedByRule ?? random.Next(3) == 0;
        if (withManagedBy)
            AddExpectedReferences(random, mvo, _owners, mvoPool, referencedPool);
        if (random.Next(2) == 0)
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), Attribute = _mvDescription, AttributeId = _mvDescription.Id, StringValue = $"d{random.Next(2)}" });

        // Changes staged on the Metaverse Object and not applied (a recall, an addition) are not what it holds: both paths
        // read what it holds.
        foreach (var held in mvo.AttributeValues.Where(_ => random.Next(8) == 0).ToList())
            mvo.PendingAttributeValueRemovals.Add(held);
        if (random.Next(8) == 0)
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), Attribute = _staticMembers, AttributeId = _staticMembers.Id, ReferenceValueId = mvoPool[0] });

        var whole = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = SystemId, TypeId = GroupTypeId, Type = _groupType,
            Status = ConnectedSystemObjectStatus.Normal, MetaverseObject = mvo, MetaverseObjectId = mvo.Id
        };
        var heldOnlyUnderAnotherType = random.Next(12) == 0;
        AddHeldValues(random, whole, _externalId, [], heldOnlyUnderAnotherType: false, $"g{random.Next()}");
        AddHeldValues(random, whole, _member, referencedPool, heldOnlyUnderAnotherType);
        if (withManagedBy)
            AddHeldValues(random, whole, _managedBy, referencedPool, heldOnlyUnderAnotherType: random.Next(12) == 0);
        if (random.Next(2) == 0)
            AddHeldValues(random, whole, _description, [], heldOnlyUnderAnotherType: false, $"d{random.Next(2)}");

        // Value id order, as the object's values are loaded.
        var ordered = whole.AttributeValues.OrderBy(value => value.Id).ToList();
        whole.AttributeValues.Clear();
        whole.AttributeValues.AddRange(ordered);

        var leftOut = new[] { _member }.Concat(withManagedBy ? [_managedBy] : []).ToList();
        var lean = new ConnectedSystemObject
        {
            Id = whole.Id, ConnectedSystemId = SystemId, TypeId = GroupTypeId, Type = _groupType, Status = whole.Status,
            MetaverseObject = mvo, MetaverseObjectId = mvo.Id,
            PartiallyLoadedAttributes = leftOut.ToDictionary(a => a.Id, a => (PartiallyLoadedAttribute)new SynchronisationPartiallyLoadedAttribute(
                a.Id, a.Name, whole.AttributeValues.Any(value => value.AttributeId == a.Id)))
        };
        lean.AttributeValues.AddRange(whole.AttributeValues.Where(value => leftOut.TrueForAll(a => a.Id != value.AttributeId)));

        var rules = new List<SyncRule>();
        var membership = ExportRule(1, "Group Membership");
        membership.AttributeFlowRules.Add(DirectMapping(11, _member, _staticMembers));
        membership.AttributeFlowRules.Add(DirectMapping(12, _description, _mvDescription));
        rules.Add(membership);
        if (withManagedBy)
        {
            var owners = ExportRule(2, "Group Owners");
            owners.AttributeFlowRules.Add(DirectMapping(21, _managedBy, _owners));
            rules.Add(owners);
        }

        // A rule that is not Enforce State, or for another system, is never drift-checked.
        if (random.Next(4) == 0)
        {
            var notEnforced = ExportRule(3, "Not Enforced");
            notEnforced.EnforceState = false;
            notEnforced.AttributeFlowRules.Add(DirectMapping(31, _member, _owners));
            rules.Add(notEnforced);
        }

        return (whole, lean, mvo, rules);
    }

    /// <summary>
    /// The Metaverse Object's values of a reference attribute: references to pool members (some twice), asserted-null
    /// markers, a reference to an object created in this run and not yet saved (its id only on the navigation), and
    /// values contributed by either system.
    /// </summary>
    private static void AddExpectedReferences(Random random, MetaverseObject mvo, MetaverseAttribute attribute, List<Guid> mvoPool,
        List<(Guid CsoId, Guid? MvoId)> referencedPool)
    {
        var count = random.Next(0, 24);
        for (var i = 0; i < count; i++)
        {
            var value = new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id,
                ContributedBySystemId = random.Next(2) == 0 ? SystemId : OtherSystemId
            };
            switch (random.Next(14))
            {
                case 0:
                    value.NullValue = true;
                    break;
                case 1:
                    value.ReferenceValue = new MetaverseObject { Id = mvoPool[random.Next(mvoPool.Count)] };
                    break;
                case 2 when referencedPool.Count > 0:
                    // A member the group no longer holds, or one it holds through an unjoined object: a stale extra value.
                    value.ReferenceValueId = Guid.NewGuid();
                    break;
                default:
                    value.ReferenceValueId = mvoPool[random.Next(mvoPool.Count)];
                    break;
            }

            mvo.AttributeValues.Add(value);
        }
    }

    /// <summary>
    /// The object's values of an attribute: references to pool objects (some twice), with what the page load resolved
    /// each to; references not yet resolved (only the text a Connector imported); and, now and then, values held under
    /// another data type, or only such values.
    /// </summary>
    private static void AddHeldValues(Random random, ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute,
        List<(Guid CsoId, Guid? MvoId)> referencedPool, bool heldOnlyUnderAnotherType, string? text = null)
    {
        if (attribute.Type != AttributeDataType.Reference)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, StringValue = text });
            return;
        }

        var count = random.Next(0, 26);
        for (var i = 0; i < count; i++)
        {
            var value = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id };
            if (heldOnlyUnderAnotherType)
            {
                value.StringValue = $"uid=u{random.Next(9)}";
            }
            else
            {
                switch (random.Next(12))
                {
                    case 0:
                        value.UnresolvedReferenceValue = $"uid=x{random.Next(9)}";
                        break;
                    case 1:
                        // A stray value held under another data type beside references: still read as no value.
                        value.StringValue = "stray";
                        break;
                    default:
                        var referenced = referencedPool[random.Next(referencedPool.Count)];
                        value.ReferenceValueId = referenced.CsoId;
                        value.UnresolvedReferenceValue = $"uid={referenced.CsoId:N}";
                        value.ResolvedReferenceMetaverseObjectId = referenced.MvoId;
                        break;
                }
            }

            cso.AttributeValues.Add(value);
        }
    }

    private ConnectedSystemObjectTypeAttribute Attribute(int id, string name, AttributeDataType type, AttributePlurality plurality, bool isExternalId = false)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = id, Name = name, Type = type, AttributePlurality = plurality, IsExternalId = isExternalId, ConnectedSystemObjectType = _groupType
        };
        _groupType.Attributes.Add(attribute);
        return attribute;
    }

    private SyncRule ExportRule(int id, string name) => new()
    {
        Id = id, Name = name, Enabled = true, EnforceState = true, Direction = SyncRuleDirection.Export,
        ConnectedSystemId = SystemId, ConnectedSystemObjectTypeId = GroupTypeId, ConnectedSystemObjectType = _groupType,
        MetaverseObjectTypeId = GroupMvTypeId, MetaverseObjectType = _groupMvType
    };

    private static SyncRuleMapping DirectMapping(int id, ConnectedSystemObjectTypeAttribute target, MetaverseAttribute source) => new()
    {
        Id = id, TargetConnectedSystemAttribute = target, TargetConnectedSystemAttributeId = target.Id,
        Sources = { new SyncRuleMappingSource { MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
    };
}
