// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// #2817: a Delta Import may load a group without most of its member values, standing an answer from the database in for
/// the rest. A value it did not load must never read as one the group does not hold: the comparison uses a partly loaded
/// attribute only with the answer it was loaded for, and refuses to compare it any other way. And it asks only about what
/// it can ask exactly: the multi-valued reference attributes the imported object carries, as the comparison will read them.
/// </summary>
[TestFixture]
public class ImportPartiallyLoadedAttributeTests
{
    private static readonly MethodInfo UpdateMethod = typeof(SyncImportTaskProcessor).GetMethod(
        "UpdateConnectedSystemObjectFromImportObject", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("UpdateConnectedSystemObjectFromImportObject not found via reflection.");

    private ConnectedSystemObjectType _groupType = null!;
    private ConnectedSystemObjectTypeAttribute _member = null!;
    private ConnectedSystemObjectTypeAttribute _manager = null!;
    private ConnectedSystemObjectTypeAttribute _externalId = null!;

    [SetUp]
    public void SetUp()
    {
        _groupType = new ConnectedSystemObjectType { Id = 2, Name = "group" };
        _externalId = new ConnectedSystemObjectTypeAttribute
            { Id = 20, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, ConnectedSystemObjectType = _groupType };
        _member = new ConnectedSystemObjectTypeAttribute
            { Id = 21, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, ConnectedSystemObjectType = _groupType };
        _manager = new ConnectedSystemObjectTypeAttribute
            { Id = 22, Name = "managedBy", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = _groupType };
        _groupType.Attributes.AddRange([_externalId, _member, _manager]);
    }

    [Test]
    public void Comparison_PartlyLoadedAttributeAnsweredForOtherValues_RefusesToCompare()
    {
        var group = PartlyLoadedGroup(answeredFor: ["uid=a", "uid=b"]);

        var thrown = Assert.Throws<TargetInvocationException>(() => Compare(ImportOf(["uid=a", "uid=c"]), group));

        Assert.That(thrown!.InnerException, Is.TypeOf<InvalidOperationException>());
        Assert.That(group.PendingAttributeValueRemovals, Is.Empty, "Nothing staged from a comparison it refused");
    }

    [Test]
    public void Comparison_PartlyLoadedAttributeTheImportDoesNotCarry_RefusesToCompare()
    {
        var group = PartlyLoadedGroup(answeredFor: ["uid=a"]);

        var thrown = Assert.Throws<TargetInvocationException>(() => Compare(ImportOf(members: null), group));

        Assert.That(thrown!.InnerException, Is.TypeOf<InvalidOperationException>(),
            "Removing every member it loaded would leave the members it did not load behind");
        Assert.That(group.PendingAttributeValueRemovals, Is.Empty);
    }

    [Test]
    public void Comparison_AnswerForAnotherObject_RefusesToCompare()
    {
        var group = PartlyLoadedGroup(answeredFor: ["uid=a"], answeredObjectId: Guid.NewGuid());

        var thrown = Assert.Throws<TargetInvocationException>(() => Compare(ImportOf(["uid=a"]), group));

        Assert.That(thrown!.InnerException, Is.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Comparison_AnsweredForTheImportedValues_StagesOnlyWhatTheAnswerLeavesUndecided()
    {
        // The group holds a (matched exactly, so not loaded) and b (unmatched, so loaded); the import carries a and c.
        var group = PartlyLoadedGroup(answeredFor: ["uid=a", "uid=c"], unmatchedLoaded: "uid=b", unmatchedIncoming: ["uid=c"]);

        Compare(ImportOf(["uid=a", "uid=c"]), group);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.PendingAttributeValueRemovals.Where(v => v.Attribute == _member).Select(v => v.UnresolvedReferenceValue), Is.EqualTo(new[] { "uid=b" }));
            Assert.That(group.PendingAttributeValueAdditions.Where(v => v.Attribute == _member).Select(v => v.UnresolvedReferenceValue), Is.EqualTo(new[] { "uid=c" }),
                "a was matched exactly, so it is held and not added again");
        }
    }

    [Test]
    public void HeldReferenceValueQuestionsFor_GroupImport_AsksAboutItsMultiValuedReferencesAsTheComparisonReadsThem()
    {
        var importObject = ImportOf(["uid=a", "UID=A", "uid=b"]);
        importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "managedBy", ReferenceValues = ["uid=boss"] });
        var csoId = Guid.NewGuid();

        var questions = SyncImportTaskProcessor.HeldReferenceValueQuestionsFor(importObject, csoId, _groupType);

        Assert.That(questions, Has.Count.EqualTo(1), "Not the single-valued reference");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(questions[0].ConnectedSystemObjectId, Is.EqualTo(csoId));
            Assert.That(questions[0].AttributeId, Is.EqualTo(_member.Id));
            Assert.That(questions[0].Values, Is.EqualTo(new[] { "uid=a", "uid=b" }), "Distinct ignoring case, first kept, as the comparison deduplicates");
        }
    }

    // Built in code: an attribute argument cannot carry half a surrogate pair (metadata stores it as UTF-8).
    private static IEnumerable<TestCaseData> ValuesTheDatabaseCannotHold()
    {
        yield return new TestCaseData("uid=nul\0,dc=x").SetName("HeldReferenceValueQuestionsFor_ValueWithNulCharacter_AsksNothing");
        yield return new TestCaseData("uid=" + (char)0xD800 + ",dc=x").SetName("HeldReferenceValueQuestionsFor_ValueWithUnpairedSurrogate_AsksNothing");
        yield return new TestCaseData("uid=" + (char)0xDC00 + ",dc=x").SetName("HeldReferenceValueQuestionsFor_ValueWithLoneLowSurrogate_AsksNothing");
    }

    [TestCaseSource(nameof(ValuesTheDatabaseCannotHold))]
    public void HeldReferenceValueQuestionsFor_ValueTheDatabaseCannotHold_AsksNothing(string value)
    {
        var questions = SyncImportTaskProcessor.HeldReferenceValueQuestionsFor(ImportOf(["uid=a", value]), Guid.NewGuid(), _groupType);

        Assert.That(questions, Is.Empty);
    }

    [Test]
    public void HeldReferenceValueQuestionsFor_ValueOutsideTheBasicMultilingualPlane_IsAsked()
    {
        var questions = SyncImportTaskProcessor.HeldReferenceValueQuestionsFor(ImportOf(["uid=a", "uid=\U0001F600,dc=x"]), Guid.NewGuid(), _groupType);

        Assert.That(questions, Has.Count.EqualTo(1));
    }

    [Test]
    public void HeldReferenceValueQuestionsFor_AttributeWithUnreadableValue_AsksNothing()
    {
        var importObject = ImportOf(["uid=a"]);
        importObject.AddAttributeValueError("member", "garbage", "not a DN");

        Assert.That(SyncImportTaskProcessor.HeldReferenceValueQuestionsFor(importObject, Guid.NewGuid(), _groupType), Is.Empty);
    }

    [Test]
    public void HeldReferenceValueQuestionsFor_NoMembersCarried_AsksNothing()
    {
        Assert.That(SyncImportTaskProcessor.HeldReferenceValueQuestionsFor(ImportOf([]), Guid.NewGuid(), _groupType), Is.Empty);
        Assert.That(SyncImportTaskProcessor.HeldReferenceValueQuestionsFor(ImportOf(members: null), Guid.NewGuid(), _groupType), Is.Empty);
    }

    private ConnectedSystemObject PartlyLoadedGroup(List<string> answeredFor, Guid? answeredObjectId = null, string? unmatchedLoaded = null,
        List<string>? unmatchedIncoming = null)
    {
        var group = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = _groupType, TypeId = _groupType.Id, ExternalIdAttributeId = _externalId.Id };
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            { Id = Guid.NewGuid(), Attribute = _externalId, AttributeId = _externalId.Id, GuidValue = Guid.NewGuid(), ConnectedSystemObject = group });

        var answer = new HeldReferenceValueAnswer(new HeldReferenceValueQuestion(answeredObjectId ?? group.Id, _member.Id, answeredFor));
        if (unmatchedLoaded != null)
        {
            var loaded = new ConnectedSystemObjectAttributeValue
                { Id = Guid.NewGuid(), Attribute = _member, AttributeId = _member.Id, UnresolvedReferenceValue = unmatchedLoaded, ConnectedSystemObject = group };
            group.AttributeValues.Add(loaded);
            answer.UnmatchedValueIds.Add(loaded.Id);
        }

        foreach (var value in unmatchedIncoming ?? [])
            answer.UnmatchedIncomingValues.Add(value);

        group.PartiallyLoadedAttributes = new Dictionary<int, PartiallyLoadedAttribute>
        {
            [_member.Id] = new ImportPartiallyLoadedAttribute(_member.Id, _member.Name, answer)
        };
        return group;
    }

    private static ConnectedSystemImportObject ImportOf(List<string>? members)
    {
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = "group",
            Attributes = [new ConnectedSystemImportObjectAttribute { Name = "objectGUID", GuidValues = [Guid.NewGuid()] }]
        };
        if (members != null)
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "member", ReferenceValues = members });
        return importObject;
    }

    private void Compare(ConnectedSystemImportObject importObject, ConnectedSystemObject group) =>
        UpdateMethod.Invoke(null, [importObject, group, _groupType, new ActivityRunProfileExecutionItem(), new Dictionary<Guid, string>()]);
}
