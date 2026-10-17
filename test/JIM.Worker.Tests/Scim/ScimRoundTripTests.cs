// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Core;
using JIM.Scim;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// The writer and the reader have to invert each other exactly: <c>Read(Write(x)) == x</c> for every attribute JIM
/// can export. A value that does not survive the round trip is a change JIM records as exported and the confirming
/// import reports as never applied (#2090).
/// <para>
/// The cases are generated from <see cref="ScimAttributeMapper.FlattenSchema"/> over the core schemas rather than
/// listed by hand, so an attribute shape added later is covered without a new test.
/// </para>
/// </summary>
public class ScimRoundTripTests
{
    private static List<ScimFlattenedAttribute> UserSchema()
    {
        var attributes = ScimCommonAttributes.For(ScimUrns.User);
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.User()));
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.EnterpriseUser(), ScimAttributeMapper.DeriveNamePrefix(ScimCoreSchemas.EnterpriseUser())));
        return attributes;
    }

    private static List<ScimFlattenedAttribute> GroupSchema()
    {
        var attributes = ScimCommonAttributes.For(ScimUrns.Group);
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.Group()));
        return attributes;
    }

    private static IEnumerable<TestCaseData> WritableAttributes()
    {
        foreach (var (baseUrn, schema) in new[] { (ScimUrns.User, UserSchema()), (ScimUrns.Group, GroupSchema()) })
        {
            foreach (var attribute in schema.Where(a => a.Writability != AttributeWritability.ReadOnly))
                yield return new TestCaseData(baseUrn, attribute.Name).SetName($"RoundTrip_{baseUrn.Split(':').Last()}_{attribute.Name}");
        }
    }

    [TestCaseSource(nameof(WritableAttributes))]
    public void BuildResourceThenRead_EveryWritableAttribute_ReadsBackExactlyTheValuesWritten(string baseUrn, string attributeName)
    {
        var schema = baseUrn == ScimUrns.User ? UserSchema() : GroupSchema();
        var attribute = schema.Single(a => a.Name == attributeName);
        var written = SampleValues(attribute);

        var resource = ScimResourceWriter.BuildResource([new ScimAttributeWrite(attributeName, written)], schema, baseUrn).Resource;
        using var document = JsonDocument.Parse(resource.ToJsonString());
        var read = ScimResourceReader.Read(document.RootElement, [attribute]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Warnings, Is.Empty);
            Assert.That(read.UnreadableValues, Is.Empty);
            Assert.That(ReadValues(read, attributeName), Is.EquivalentTo(written.Select(ScimValueFormatter.ToText)),
                $"{attributeName} wrote {resource.ToJsonString()}");
        }
    }

    [Test]
    public void WritableAttributes_CoverEveryShapeTheCoreSchemasHave()
    {
        // Guards the generator: an empty or narrowed source would pass every case vacuously.
        var names = WritableAttributes().Select(c => (string)c.Arguments[1]!).ToList();

        Assert.That(names, Is.SupersetOf(new[]
        {
            "userName", "active", "name.givenName", "emails.work", "emails.primary", "addresses.work.streetAddress",
            "roles.value", "entitlements.value", "x509Certificates.value", "enterpriseUser.manager", "members"
        }));
    }

    /// <summary>
    /// Two distinct values for a multi-valued attribute, so a writer keeping only the first is caught; one otherwise.
    /// </summary>
    private static List<object?> SampleValues(ScimFlattenedAttribute attribute)
    {
        var count = attribute.AttributePlurality == AttributePlurality.MultiValued ? 2 : 1;
        return Enumerable.Range(1, count).Select(i => Sample(attribute, i)).ToList();
    }

    private static object? Sample(ScimFlattenedAttribute attribute, int i)
    {
        return attribute.Type switch
        {
            AttributeDataType.Boolean => true,
            AttributeDataType.Number => i,
            AttributeDataType.LongNumber => (long)i,
            AttributeDataType.Decimal => i + 0.5m,
            AttributeDataType.DateTime => new DateTime(2026, 1, i, 9, 0, 0, DateTimeKind.Utc),
            AttributeDataType.Binary => new byte[] { (byte)i, 0xAB },
            AttributeDataType.Guid => new Guid(i, 0, 0, new byte[8]),
            _ => $"{attribute.Name}-{i}"
        };
    }

    private static List<string?> ReadValues(ScimResourceReadResult read, string attributeName)
    {
        var attribute = read.Attributes.SingleOrDefault(a => a.Name == attributeName);
        if (attribute == null)
            return [];

        return attribute.StringValues.Cast<object>()
            .Concat(attribute.ReferenceValues)
            .Concat(attribute.IntValues.Cast<object>())
            .Concat(attribute.LongValues.Cast<object>())
            .Concat(attribute.DecimalValues.Cast<object>())
            .Concat(attribute.HasDateTimeValues ? attribute.DateTimeValues.Cast<object>() : [])
            .Concat(attribute.GuidValues.Cast<object>())
            .Concat(attribute.ByteValues)
            .Concat(attribute.HasBoolValues ? attribute.BoolValues.Cast<object>() : [])
            .Select(ScimValueFormatter.ToText)
            .ToList();
    }
}
