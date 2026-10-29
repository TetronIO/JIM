// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// How the review reads what a write outside synchronisation recorded (#2615) against the Metaverse Object as it stands
/// when the review runs: the object's current values for the attributes that changed, and the recorded removals it does
/// not hold again.
/// </summary>
[TestFixture]
public class UnsynchronisedChangeEvaluationTests
{
    private const int Email = 7;
    private const int Aliases = 9;
    private const int Manager = 11;
    private const int DeletedAttribute = 99;

    private static readonly int[] ExistingAttributes = [Email, Aliases, Manager];

    private static MetaverseObjectAttributeValue Held(int attributeId, string? text = null, Guid? reference = null) =>
        new() { Id = Guid.NewGuid(), AttributeId = attributeId, StringValue = text, ReferenceValueId = reference };

    private static MetaverseObjectUnsynchronisedRemoval Removal(int attributeId, string? text = null, Guid? reference = null) =>
        new() { AttributeId = attributeId, StringValue = text, ReferenceValueId = reference };

    private static MetaverseObjectUnsynchronisedChanges Recorded(int[] attributeIds, params MetaverseObjectUnsynchronisedRemoval[] removals) =>
        new() { AttributeIds = [.. attributeIds], Removals = [.. removals] };

    [Test]
    public void Build_ChangedAttribute_ReadsTheObjectsCurrentValuesForItAndNoOther()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var email = Held(Email, "new@panoply.local");
        mvo.AttributeValues.AddRange([email, Held(Aliases, "j@panoply.local")]);

        var input = UnsynchronisedChangeEvaluation.Build(mvo, Recorded([Email]), ExistingAttributes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(input.ChangedAttributes, Is.EqualTo(new[] { email }), "only the attribute that changed; the object's other values are not re-sent");
            Assert.That(input.RemovedAttributes, Is.Null);
            Assert.That(input.SkippedAttributeIds, Is.Empty);
        }
    }

    [Test]
    public void Build_RemovedValue_FollowsTheCurrentValuesAndIsInTheRemovedSet()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var kept = Held(Aliases, "keep@panoply.local");
        mvo.AttributeValues.Add(kept);

        var input = UnsynchronisedChangeEvaluation.Build(mvo, Recorded([Aliases], Removal(Aliases, "gone@panoply.local")), ExistingAttributes);

        var removed = input.ChangedAttributes.Last();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(input.ChangedAttributes.First(), Is.SameAs(kept), "removals follow additions, as in a synchronisation's own changed list");
            Assert.That(removed.StringValue, Is.EqualTo("gone@panoply.local"));
            Assert.That(removed.Id, Is.EqualTo(Guid.Empty), "unsaved, so export staging matches it by content");
            Assert.That(input.RemovedAttributes, Is.EquivalentTo(new[] { removed }));
        }
    }

    [Test]
    public void Build_ValueRemovedThenGivenBack_IsNotRemoved()
    {
        // Edit order never matters: the record says the value was removed, but the object holds it again, so removing it
        // from the targets would delete a value the Metaverse still has.
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.Add(Held(Aliases, "back@panoply.local"));

        var input = UnsynchronisedChangeEvaluation.Build(mvo,
            Recorded([Aliases], Removal(Aliases, "back@panoply.local"), Removal(Aliases, "gone@panoply.local")), ExistingAttributes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(input.RemovedAttributes!.Select(v => v.StringValue), Is.EqualTo(new[] { "gone@panoply.local" }));
            Assert.That(input.ChangedAttributes.Select(v => v.StringValue), Is.EqualTo(new[] { "back@panoply.local", "gone@panoply.local" }));
        }
    }

    [Test]
    public void Build_EveryRemovalGivenBack_HasNoRemovedSet()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.Add(Held(Email, "same@panoply.local"));

        var input = UnsynchronisedChangeEvaluation.Build(mvo, Recorded([Email], Removal(Email, "same@panoply.local")), ExistingAttributes);

        Assert.That(input.RemovedAttributes, Is.Null);
    }

    [Test]
    public void Build_ReferenceRemovedThenGivenBack_IsNotRemoved()
    {
        var group = Guid.NewGuid();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.Add(Held(Manager, reference: group));

        var input = UnsynchronisedChangeEvaluation.Build(mvo, Recorded([Manager], Removal(Manager, reference: group)), ExistingAttributes);

        Assert.That(input.RemovedAttributes, Is.Null, "matched by the referenced object, as export staging matches it");
    }

    [Test]
    public void Build_SameTextHeldUnderAnotherAttribute_IsStillRemoved()
    {
        // The attribute is part of what a value is: an alias equal to the old email is not the email given back.
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.Add(Held(Aliases, "old@panoply.local"));

        var input = UnsynchronisedChangeEvaluation.Build(mvo, Recorded([Email], Removal(Email, "old@panoply.local")), ExistingAttributes);

        Assert.That(input.RemovedAttributes!.Select(v => v.AttributeId), Is.EqualTo(new[] { Email }));
    }

    [Test]
    public void Build_DateGivenBackAtADifferentKind_IsNotRemoved()
    {
        // A date read from the database arrives as local time to the microsecond; the record holds UTC to the tick.
        var recordedDate = new DateTime(2026, 10, 29, 9, 30, 15, DateTimeKind.Utc).AddTicks(7);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { AttributeId = Email, DateTimeValue = recordedDate.ToLocalTime().AddTicks(-7) });

        var input = UnsynchronisedChangeEvaluation.Build(mvo,
            Recorded([Email], new MetaverseObjectUnsynchronisedRemoval { AttributeId = Email, DateTimeValue = recordedDate }), ExistingAttributes);

        Assert.That(input.RemovedAttributes, Is.Null);
    }

    [Test]
    public void Build_AttributeThatNoLongerExists_IsSkippedAndReportedWithoutFailing()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var email = Held(Email, "new@panoply.local");
        mvo.AttributeValues.AddRange([email, Held(DeletedAttribute, "stale")]);

        var input = UnsynchronisedChangeEvaluation.Build(mvo,
            Recorded([Email, DeletedAttribute], Removal(DeletedAttribute, "gone")), ExistingAttributes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(input.SkippedAttributeIds, Is.EqualTo(new[] { DeletedAttribute }));
            Assert.That(input.ChangedAttributes, Is.EqualTo(new[] { email }), "the surviving attribute is still delivered");
            Assert.That(input.RemovedAttributes, Is.Null, "and nothing is staged for the missing one");
        }
    }

    [Test]
    public void Build_AssertedNullMarker_IsNotAValueTheRemovalCanBeGivenBackBy()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { AttributeId = Email, NullValue = true });

        var input = UnsynchronisedChangeEvaluation.Build(mvo, Recorded([Email], Removal(Email, "old@panoply.local")), ExistingAttributes);

        Assert.That(input.RemovedAttributes!.Select(v => v.StringValue), Is.EqualTo(new[] { "old@panoply.local" }));
    }
}
