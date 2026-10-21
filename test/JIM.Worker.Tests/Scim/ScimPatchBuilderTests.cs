// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Messages;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// Turning JIM attribute changes into SCIM PATCH operations. The failure that matters is an operation whose path
/// reaches further than the change did: RFC 7644 section 3.5.2.2 reads a remove naming a multi-valued attribute by its
/// bare path as removing every value it holds, so a change taking one value away has to name that value.
/// </summary>
public class ScimPatchBuilderTests
{
    private const string CustomUrn = "urn:example:params:scim:schemas:extension:custom:2.0:User";

    /// <summary>
    /// A provider's own extension, carrying the multi-valued simple attributes the RFC 7643 core schemas never have.
    /// </summary>
    private static ScimSchema CustomSchema()
    {
        return new ScimSchema
        {
            Id = CustomUrn,
            Name = "Custom",
            Attributes =
            [
                MultiValued("badges", ScimAttributeTypes.String),
                MultiValued("scores", ScimAttributeTypes.Integer),
                MultiValued("weights", ScimAttributeTypes.Decimal),
                MultiValued("reviewDates", ScimAttributeTypes.DateTime)
            ]
        };

        static ScimSchemaAttribute MultiValued(string name, string type) => new() { Name = name, Type = type, MultiValued = true };
    }

    /// <summary>
    /// The User schema as discovery builds it, with the custom extension under its <c>custom</c> prefix.
    /// </summary>
    private static List<ScimFlattenedAttribute> UserSchema()
    {
        var attributes = ScimCommonAttributes.For(ScimUrns.User);
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.User()));
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(CustomSchema(), "custom"));
        return attributes;
    }

    /// <summary>
    /// The same attributes defined by a resource type's own base schema, so their paths carry no URN.
    /// </summary>
    private static List<ScimFlattenedAttribute> BaseSchema()
    {
        return ScimAttributeMapper.FlattenSchema(CustomSchema());
    }

    private static List<ScimFlattenedAttribute> GroupSchema()
    {
        var attributes = ScimCommonAttributes.For(ScimUrns.Group);
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.Group()));
        return attributes;
    }

    private static ScimPatchOperation Single(ScimAttributeChange change, List<ScimFlattenedAttribute> schema)
    {
        var result = ScimPatchBuilder.Build([change], schema);
        Assert.That(result.UnknownAttributes, Is.Empty);
        return result.Operations.Single();
    }

    #region multi-valued simple attributes
    [Test]
    public void Build_RemovingOneValueOfAMultiValuedSimpleAttribute_NamesTheValueInAFilter()
    {
        var operation = Single(new ScimAttributeChange("badges", ScimPatchOperations.Remove, "gold"), BaseSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operation.Op, Is.EqualTo(ScimPatchOperations.Remove));
            Assert.That(operation.Path, Is.EqualTo("badges[value eq \"gold\"]"));
            Assert.That(operation.Value, Is.Null, "a removal is expressed by its path alone");
        }
    }

    [Test]
    public void Build_RemovingOneValueOfAMultiValuedExtensionAttribute_NamesTheValueInAUrnQualifiedFilter()
    {
        var operation = Single(new ScimAttributeChange("custom.badges", ScimPatchOperations.Remove, "gold"), UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operation.Path, Is.EqualTo($"{CustomUrn}:badges[value eq \"gold\"]"));
            Assert.That(operation.Value, Is.Null);
        }
    }

    [Test]
    public void Build_AddingOneValueThenRemovingAnother_TheRemovalDoesNotReachTheAddedValue()
    {
        // Applied in order, an add followed by a bare-path remove took the value just added along with everything else.
        var result = ScimPatchBuilder.Build(
        [
            new ScimAttributeChange("custom.badges", ScimPatchOperations.Add, "platinum"),
            new ScimAttributeChange("custom.badges", ScimPatchOperations.Remove, "gold")
        ], UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Operations, Has.Count.EqualTo(2));
            Assert.That(result.Operations[0].Op, Is.EqualTo(ScimPatchOperations.Add));
            Assert.That(result.Operations[0].Value?.ToJsonString(), Does.Contain("platinum"));
            Assert.That(result.Operations[1].Path, Is.EqualTo($"{CustomUrn}:badges[value eq \"gold\"]"));
        }
    }

    [Test]
    public void Build_ClearingAMultiValuedSimpleAttribute_RemovesItByItsPathAlone()
    {
        // A removal naming no value is a clear (#2182): every value is meant to go, and a filter on no value would
        // match nothing and remove nothing.
        var operation = Single(new ScimAttributeChange("custom.badges", ScimPatchOperations.Remove, null), UserSchema());

        Assert.That(operation.Path, Is.EqualTo($"{CustomUrn}:badges"));
    }

    [Test]
    public void Build_AddingAValueToAMultiValuedSimpleAttribute_SendsTheValueAsAnArray()
    {
        // RFC 7644 section 3.5.2.1 gives a multi-valued target's value as an array; a strict provider refuses a scalar
        // (#2215).
        var operation = Single(new ScimAttributeChange("custom.badges", ScimPatchOperations.Add, "gold"), UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operation.Path, Is.EqualTo($"{CustomUrn}:badges"));
            Assert.That(operation.Value?.ToJsonString(), Is.EqualTo("""["gold"]"""));
        }
    }

    [Test]
    public void Build_ReplacingAMultiValuedSimpleAttribute_SendsTheSetOfOneAsAnArray()
    {
        // An Update against a multi-valued target means the set is exactly this value (#2182); section 3.5.2.3 replaces
        // every value when no filter is given, so the value has to be that set.
        var operation = Single(new ScimAttributeChange("custom.scores", ScimPatchOperations.Replace, 42L), UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operation.Op, Is.EqualTo(ScimPatchOperations.Replace));
            Assert.That(operation.Value?.ToJsonString(), Is.EqualTo("[42]"));
        }
    }

    [Test]
    public void Build_SeveralValuesAddedToAMultiValuedSimpleAttribute_AreOneOperationCarryingEveryValue()
    {
        var operations = ScimPatchBuilder.Build(
        [
            new ScimAttributeChange("badges", ScimPatchOperations.Add, "gold"),
            new ScimAttributeChange("badges", ScimPatchOperations.Add, "silver")
        ], BaseSchema()).Operations;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operations, Has.Count.EqualTo(1));
            Assert.That(operations[0].Value?.ToJsonString(), Is.EqualTo("""["gold","silver"]"""));
        }
    }

    [Test]
    public void Build_ReplacingASingleValuedSimpleAttribute_SendsAScalar()
    {
        var operation = Single(new ScimAttributeChange("title", ScimPatchOperations.Replace, "Engineer"), UserSchema());

        Assert.That(operation.Value?.ToJsonString(), Is.EqualTo("\"Engineer\""));
    }

    [Test]
    public void Build_RemovingASingleValuedSimpleAttribute_RemovesItByItsPathAlone()
    {
        var operation = Single(new ScimAttributeChange("title", ScimPatchOperations.Remove, "Engineer"), UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operation.Path, Is.EqualTo("title"));
            Assert.That(operation.Value, Is.Null);
        }
    }
    #endregion

    #region filter literals
    [Test]
    public void Build_RemovingAValueContainingAQuote_EscapesItSoTheFilterStaysOneValue()
    {
        var operation = Single(new ScimAttributeChange("badges", ScimPatchOperations.Remove, "o\"brien"), BaseSchema());

        Assert.That(operation.Path, Is.EqualTo("badges[value eq \"o\\\"brien\"]"));
    }

    [Test]
    public void Build_RemovingAValueEndingInABackslash_EscapesItSoTheClosingQuoteSurvives()
    {
        // Unescaped, the trailing backslash escapes the closing quote and the filter never ends.
        var operation = Single(new ScimAttributeChange("badges", ScimPatchOperations.Remove, "ends\\"), BaseSchema());

        Assert.That(operation.Path, Is.EqualTo("badges[value eq \"ends\\\\\"]"));
    }

    [Test]
    public void Build_RemovingAnIntegerValue_ComparesItAsAnUnquotedNumber()
    {
        var operation = Single(new ScimAttributeChange("custom.scores", ScimPatchOperations.Remove, 42L), UserSchema());

        Assert.That(operation.Path, Is.EqualTo($"{CustomUrn}:scores[value eq 42]"));
    }

    [Test]
    public void Build_RemovingADecimalValue_ComparesItAsAnUnquotedNumber()
    {
        var operation = Single(new ScimAttributeChange("custom.weights", ScimPatchOperations.Remove, 1.5m), UserSchema());

        Assert.That(operation.Path, Is.EqualTo($"{CustomUrn}:weights[value eq 1.5]"));
    }

    [Test]
    [SetCulture("de-DE")]
    public void Build_RemovingADateTimeValue_ComparesItInTheWireFormRatherThanTheCurrentCulture()
    {
        var operation = Single(
            new ScimAttributeChange("custom.reviewDates", ScimPatchOperations.Remove, new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc)),
            UserSchema());

        Assert.That(operation.Path, Is.EqualTo($"{CustomUrn}:reviewDates[value eq \"2026-03-01T09:30:00Z\"]"));
    }

    [Test]
    public void Build_AddingAMultiValuedComplexValue_AppendsOneEntryToTheAttribute()
    {
        // A path of "roles.value" names every entry's value; RFC 7644 section 3.5.2.1 replaces an existing target (#2090).
        var operation = Single(new ScimAttributeChange("roles.value", ScimPatchOperations.Add, "Owner"), UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operation.Op, Is.EqualTo(ScimPatchOperations.Add));
            Assert.That(operation.Path, Is.EqualTo("roles"));
            Assert.That(operation.Value?.ToJsonString(), Is.EqualTo("""[{"value":"Owner"}]"""));
        }
    }

    [Test]
    public void Build_RemovingAMultiValuedComplexValue_NamesTheEntryInAFilter()
    {
        var operation = Single(new ScimAttributeChange("entitlements.value", ScimPatchOperations.Remove, "Auditor"), UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operation.Path, Is.EqualTo("entitlements[value eq \"Auditor\"]"));
            Assert.That(operation.Value, Is.Null);
        }
    }

    [Test]
    public void Build_RemovingAMultiValuedComplexValueContainingAQuote_EscapesIt()
    {
        var operation = Single(new ScimAttributeChange("roles.value", ScimPatchOperations.Remove, "say \"hi\""), UserSchema());

        Assert.That(operation.Path, Is.EqualTo("roles[value eq \"say \\\"hi\\\"\"]"));
    }

    [Test]
    public void Build_ClearingAMultiValuedComplexValue_RemovesTheAttributeByItsPathAlone()
    {
        var operation = Single(new ScimAttributeChange("roles.value", ScimPatchOperations.Remove, null), UserSchema());

        Assert.That(operation.Path, Is.EqualTo("roles"));
    }

    [Test]
    public void Build_ChangingTheTypeOfAMultiValuedComplexEntry_IsReportedRatherThanSent()
    {
        var result = ScimPatchBuilder.Build([new ScimAttributeChange("roles.type", ScimPatchOperations.Add, "hr")], UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Operations, Is.Empty);
            Assert.That(result.UnknownAttributes, Is.EqualTo(new[] { "roles.type" }));
        }
    }

    [Test]
    public void Build_RemovingAReferenceContainingAQuoteAndABackslash_EscapesBoth()
    {
        var operation = Single(new ScimAttributeChange("members", ScimPatchOperations.Remove, "a\"b\\"), GroupSchema());

        Assert.That(operation.Path, Is.EqualTo("members[value eq \"a\\\"b\\\\\"]"));
    }
    #endregion
    #region merging adds
    [Test]
    public void Build_SeveralMembersAdded_AreOneOperationCarryingEveryMember()
    {
        // #2094: providers cap the operations in a request, not the values in one.
        var changes = new List<ScimAttributeChange>
        {
            new("members", ScimPatchOperations.Add, "a"),
            new("members", ScimPatchOperations.Add, "b"),
            new("members", ScimPatchOperations.Add, "c")
        };

        var operations = ScimPatchBuilder.Build(changes, GroupSchema()).Operations;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operations, Has.Count.EqualTo(1));
            Assert.That(operations[0].Value!.AsArray().Select(m => m!["value"]!.GetValue<string>()), Is.EqualTo(new[] { "a", "b", "c" }));
        }
    }

    [Test]
    public void Build_AddsSeparatedByARemovalOfTheSameAttribute_AreNotMergedAcrossIt()
    {
        // Merging the last add into the first would apply it before the removal, changing the outcome.
        var changes = new List<ScimAttributeChange>
        {
            new("members", ScimPatchOperations.Add, "a"),
            new("members", ScimPatchOperations.Remove, "a"),
            new("members", ScimPatchOperations.Add, "a")
        };

        var operations = ScimPatchBuilder.Build(changes, GroupSchema()).Operations;

        Assert.That(operations.Select(o => o.Op), Is.EqualTo(new[] { "add", "remove", "add" }));
    }

    [Test]
    public void Build_AddsSeparatedByAChangeToAnotherAttribute_AreStillMerged()
    {
        var changes = new List<ScimAttributeChange>
        {
            new("members", ScimPatchOperations.Add, "a"),
            new("displayName", ScimPatchOperations.Replace, "Engineers"),
            new("members", ScimPatchOperations.Add, "b")
        };

        var operations = ScimPatchBuilder.Build(changes, GroupSchema()).Operations;

        Assert.That(operations.Select(o => o.Path), Is.EqualTo(new[] { "members", "displayName" }));
    }
    #endregion
}
