// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The logic behind a Run Profile Execution Item's Attribute Values Not Imported section (#2066): when it shows, what
/// its search matches, and where its next step links.
/// </summary>
[TestFixture]
public class AttributeValueErrorDisplayTests
{
    private static ImportedAttributeValueError Value(string attributeName, string? value) =>
        new() { AttributeName = attributeName, AttributeType = AttributeDataType.DateTime, Value = value, Reason = "Not a valid date and time." };

    [TestCase(null)]
    [TestCase("")]
    [TestCase("{ not json")]
    [TestCase("{\"total\":0,\"values\":[]}")]
    public void ForItem_NothingRecorded_ShowsNoSection(string? json)
    {
        Assert.That(AttributeValueErrorDisplay.ForItem(json), Is.Null,
            "An item recorded before the values were kept, or by a Connector that reports a message alone, keeps its Error Details.");
    }

    [Test]
    public void ForItem_ValuesRecorded_ShowsThem()
    {
        var json = new ImportedAttributeValueErrors { Total = 1, Values = [Value("startDate", "31/02/2026")] }.ToJson();

        Assert.That(AttributeValueErrorDisplay.ForItem(json)?.Values.Single().Value, Is.EqualTo("31/02/2026"));
    }

    [TestCase(null, 3)]
    [TestCase("", 3)]
    [TestCase("STARTDATE", 2)]
    [TestCase("31/02", 1)]
    [TestCase("nothing like it", 0)]
    public void Matching_SearchText_MatchesAttributeOrValueWhateverItsCase(string? searchText, int expected)
    {
        var values = new[] { Value("startDate", "31/02/2026"), Value("startDate", "30/02/2026"), Value("photo", null) };

        Assert.That(AttributeValueErrorDisplay.Matching(values, searchText).Count(), Is.EqualTo(expected));
    }

    [Test]
    public void SchemaHref_WithAnObjectType_OpensTheSchemaOnIt()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AttributeValueErrorDisplay.SchemaHref(4, "person record"), Is.EqualTo("/admin/connected-systems/4?t=schema&ot=person%20record"));
            Assert.That(AttributeValueErrorDisplay.SchemaHref(4, null), Is.EqualTo("/admin/connected-systems/4?t=schema"));
        }
    }

    [Test]
    public void FoundBy_ValueJimCouldNotHold_NamesJimAndAConnectorValueNamesTheConnector()
    {
        // #2153: a value JIM itself refused was read; saying the Connector could not read it would send the
        // administrator to the wrong place.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AttributeValueErrorDisplay.FoundBy(ImportAttributeValueErrorSource.Jim), Is.EqualTo("JIM"));
            Assert.That(AttributeValueErrorDisplay.FoundBy(ImportAttributeValueErrorSource.Connector), Is.EqualTo("Connector"));
        }
    }
}
