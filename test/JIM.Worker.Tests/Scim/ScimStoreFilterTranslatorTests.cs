// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scim;
using JIM.Scim;
using JIM.Scim.Messages;
using JIM.Scim.Service;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// A list request's filter (RFC 7644 section 3.4.2.2) becomes the store's predicate model, which the repository renders
/// as parameterised SQL, so JIM.Data never depends on JIM.Scim. Every rule comes from the resource type's definitions:
/// equality on a unique attribute reads the unique-value rows, anything else on a string or Boolean attribute is
/// matched in the payload. Whatever falls outside the day-one subset is refused with <c>400</c> <c>invalidFilter</c>,
/// never approximated.
/// </summary>
public class ScimStoreFilterTranslatorTests
{
    private static readonly ScimServiceResourceType User = CoreType("User");
    private static readonly ScimServiceResourceType Group = CoreType("Group");

    [Test]
    public void UserNameEq_ReadsTheUniqueValueRowsWithTheValueFolded()
    {
        var filter = Translate("""userName eq "Ada.Lovelace" """, User);

        Assert.That(filter, Is.EqualTo(new ScimStoreUniqueValueFilter("userName", "ADA.LOVELACE")));
    }

    [Test]
    public void ExternalIdEq_IsCaseExact()
    {
        Assert.That(Translate("""externalId eq "E-1a" """, User), Is.EqualTo(new ScimStoreUniqueValueFilter("externalId", "E-1a")));
    }

    [Test]
    public void AttributeNamesAndOperators_AreCaseInsensitive()
    {
        Assert.That(Translate("""USERNAME EQ "ada" """, User), Is.EqualTo(new ScimStoreUniqueValueFilter("userName", "ADA")));
    }

    [Test]
    public void QualifiedByTheBaseUrn_IsTheSameAttribute()
    {
        Assert.That(Translate($"""{ScimUrns.User}:userName eq "ada" """, User), Is.EqualTo(new ScimStoreUniqueValueFilter("userName", "ADA")));
    }

    [Test]
    public void DisplayNameEq_IsMatchedInThePayload()
    {
        var filter = (ScimStoreTextFilter)Translate("""displayName eq "Staff" """, Group);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.Path, Is.EqualTo(new[] { "displayName" }));
            Assert.That(filter.MultiValued, Is.False);
            Assert.That(filter.EntryMember, Is.Null);
            Assert.That(filter.Match, Is.EqualTo(ScimStoreTextMatch.Equal));
            Assert.That(filter.Text, Is.EqualTo("Staff"));
            Assert.That(filter.CaseExact, Is.False);
        }
    }

    [TestCase("co", ScimStoreTextMatch.Contains)]
    [TestCase("sw", ScimStoreTextMatch.StartsWith)]
    public void ContainsAndStartsWith_OnAUniqueAttribute_AreMatchedInThePayload(string op, ScimStoreTextMatch expected)
    {
        var filter = (ScimStoreTextFilter)Translate($"""userName {op} "ada" """, User);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.Path, Is.EqualTo(new[] { "userName" }));
            Assert.That(filter.Match, Is.EqualTo(expected));
        }
    }

    [Test]
    public void EmailsValue_IsMatchedInEachEntryOfTheMultiValuedAttribute()
    {
        var filter = (ScimStoreTextFilter)Translate("""emails.value eq "ada@example.com" """, User);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.Path, Is.EqualTo(new[] { "emails" }));
            Assert.That(filter.MultiValued, Is.True);
            Assert.That(filter.EntryMember, Is.EqualTo("value"));
        }
    }

    [Test]
    public void SubAttributeOfASingleComplexAttribute_IsAPathIntoIt()
    {
        var filter = (ScimStoreTextFilter)Translate("""name.familyName sw "Love" """, User);

        Assert.That(filter.Path, Is.EqualTo(new[] { "name", "familyName" }));
    }

    [Test]
    public void ExtensionAttribute_IsAPathUnderItsUrn()
    {
        var filter = (ScimStoreTextFilter)Translate($"""{ScimUrns.EnterpriseUser}:department eq "R&D" """, User);

        Assert.That(filter.Path, Is.EqualTo(new[] { ScimUrns.EnterpriseUser, "department" }));
    }

    [Test]
    public void BooleanEq_IsMatchedInThePayload()
    {
        var filter = (ScimStoreBooleanFilter)Translate("active eq false", User);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.Path, Is.EqualTo(new[] { "active" }));
            Assert.That(filter.Value, Is.False);
        }
    }

    [Test]
    public void IdEq_IsTheResourceId()
    {
        var id = Guid.NewGuid();

        Assert.That(Translate($"""id eq "{id}" """, User), Is.EqualTo(new ScimStoreIdFilter(id)));
    }

    [Test]
    public void IdEqSomethingThatIsNotAnId_MatchesNothing()
    {
        Assert.That(Translate("""id eq "not-an-id" """, User), Is.SameAs(ScimStoreNothingFilter.Instance));
    }

    [TestCase("gt", ScimStoreComparison.GreaterThan)]
    [TestCase("ge", ScimStoreComparison.GreaterOrEqual)]
    [TestCase("lt", ScimStoreComparison.LessThan)]
    [TestCase("le", ScimStoreComparison.LessOrEqual)]
    [TestCase("eq", ScimStoreComparison.Equal)]
    public void MetaLastModified_ComparesTheStamp(string op, ScimStoreComparison expected)
    {
        var filter = Translate($"""meta.lastModified {op} "2026-10-18T08:00:00Z" """, User);

        Assert.That(filter, Is.EqualTo(new ScimStoreLastModifiedFilter(expected, new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc))));
    }

    [Test]
    public void AndAndOr_KeepTheirShape()
    {
        var filter = Translate("""userName eq "a" or (userName eq "b" and active eq true)""", User);

        var or = (ScimStoreLogicalFilter)filter;
        var and = (ScimStoreLogicalFilter)or.Right;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(or.Operator, Is.EqualTo(ScimStoreLogicalOperator.Or));
            Assert.That(or.Left, Is.EqualTo(new ScimStoreUniqueValueFilter("userName", "A")));
            Assert.That(and.Operator, Is.EqualTo(ScimStoreLogicalOperator.And));
        }
    }

    [TestCase("""userName ew "a" """, TestName = "Translate_EndsWith_IsRefused")]
    [TestCase("""userName ne "a" """, TestName = "Translate_NotEqual_IsRefused")]
    [TestCase("""userName pr""", TestName = "Translate_Present_IsRefused")]
    [TestCase("""not (userName eq "a")""", TestName = "Translate_Not_IsRefused")]
    [TestCase("""emails[type eq "work"]""", TestName = "Translate_ValuePath_IsRefused")]
    [TestCase("""displayName gt "a" """, TestName = "Translate_OrderingOnAString_IsRefused")]
    [TestCase("""meta.lastModified co "2026" """, TestName = "Translate_ContainsOnTheStamp_IsRefused")]
    [TestCase("""meta.lastModified gt "yesterday" """, TestName = "Translate_StampThatIsNotAnInstant_IsRefused")]
    [TestCase("""meta.created gt "2026-10-18T08:00:00Z" """, TestName = "Translate_OtherMetaAttribute_IsRefused")]
    [TestCase("""favouriteColour eq "green" """, TestName = "Translate_UndeclaredAttribute_IsRefused")]
    [TestCase("""password eq "x" """, TestName = "Translate_WriteOnlyAttribute_IsRefused")]
    [TestCase("""groups.value eq "x" """, TestName = "Translate_ReadOnlyServerStatedAttribute_IsRefused")]
    [TestCase("""name eq "x" """, TestName = "Translate_ComplexAttributeWithoutASubAttribute_IsRefused")]
    [TestCase("""userName eq 7""", TestName = "Translate_NumberForAString_IsRefused")]
    [TestCase("""userName eq null""", TestName = "Translate_NullValue_IsRefused")]
    [TestCase("""active co "t" """, TestName = "Translate_ContainsOnABoolean_IsRefused")]
    [TestCase("""id co "a" """, TestName = "Translate_ContainsOnTheId_IsRefused")]
    [TestCase("""userName eq "a" and""", TestName = "Translate_MalformedFilter_IsRefused")]
    public void Translate_OutsideTheSubset_IsInvalidFilter(string filter)
    {
        var translation = ScimStoreFilterTranslator.Translate(filter, User);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(translation.Filter, Is.Null);
            Assert.That(translation.Error?.StatusCode, Is.EqualTo(400));
            Assert.That(translation.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidFilter));
        }
    }

    [Test]
    public void Translate_ReferenceAttribute_IsRefused()
    {
        var translation = ScimStoreFilterTranslator.Translate("""members.value eq "x" """, Group);

        Assert.That(translation.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidFilter), "members are rows, not payload; filtering on them is not in the subset");
    }

    [Test]
    public void Translate_Error_NeverRepeatsTheLiteral()
    {
        var translation = ScimStoreFilterTranslator.Translate("""password eq "S3cret-Value!" """, User);

        Assert.That(translation.Error?.Detail, Does.Not.Contain("S3cret"));
    }

    private static ScimStoreFilter Translate(string filter, ScimServiceResourceType type)
    {
        var translation = ScimStoreFilterTranslator.Translate(filter, type);
        Assert.That(translation.Error, Is.Null, translation.Error?.Detail);
        return translation.Filter!;
    }

    private static ScimServiceResourceType CoreType(string name)
    {
        var (schemas, types) = ScimCoreDefinitions.Rows();
        for (var i = 0; i < types.Count; i++)
            types[i].Id = i + 1;
        return ScimServiceDefinitions.Compile(types, schemas).FindByName(name)!;
    }
}
