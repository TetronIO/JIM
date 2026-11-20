// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A Run Profile Execution Item's Imported Object section (#874): what the Connector returned for an object the import
/// rejected, with the attributes the error is about, withheld credentials and anything left out marked as such.
/// </summary>
[TestFixture]
public class ImportedObjectSectionTests : JimComponentTestContext
{
    private static ConnectedSystemImportObjectAttribute Text(string name, params string[] values) =>
        new() { Name = name, Type = AttributeDataType.Text, StringValues = values.ToList() };

    private static ImportedObjectSnapshot Recorded(string[]? namedInError, params ConnectedSystemImportObjectAttribute[] attributes) =>
        ImportedObjectSnapshot.Record(new ConnectedSystemImportObject
        {
            ObjectType = "person",
            ChangeType = ObjectChangeType.Added,
            Attributes = attributes.ToList()
        }, namedInError);

    [Test]
    public void Render_Attributes_ShowsEachAttributeAndItsValues()
    {
        var cut = Render<ImportedObjectSection>(p => p.Add(c => c.Snapshot,
            Recorded(null, Text("employeeId", "100482"), Text("mail", "priya.shah@example.org"))));

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("employeeId").And.Contain("100482"));
            Assert.That(cut.Markup, Does.Contain("priya.shah@example.org"));
        });
    }

    [Test]
    public void Render_AttributesNamedInTheError_AreMarked()
    {
        var cut = Render<ImportedObjectSection>(p => p.Add(c => c.Snapshot,
            Recorded(["department"], Text("employeeId", "100482"), Text("Department", "Finance"), Text("department", "Finance Operations"))));

        Assert.That(cut.FindAll("[data-testid=jim-imported-object-named-in-error]"), Has.Count.EqualTo(2));
    }

    [Test]
    public void Render_CredentialAttribute_SaysItsValuesWereNotRecorded()
    {
        var cut = Render<ImportedObjectSection>(p => p.Add(c => c.Snapshot, Recorded(null, Text("initialPassword", "Hunter2!"))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid=jim-imported-object-withheld]"), Has.Count.EqualTo(1));
            Assert.That(cut.Markup, Does.Not.Contain("Hunter2"));
        }
    }

    [Test]
    public void Render_ValuesLeftOut_SaysHowMany()
    {
        var values = Enumerable.Range(1, ImportedObjectSnapshot.MaxValuesPerAttribute + 214).Select(i => $"smtp:alias{i}@example.org").ToArray();

        var cut = Render<ImportedObjectSection>(p => p.Add(c => c.Snapshot, Recorded(null, Text("proxyAddresses", values))));

        Assert.That(cut.Find("[data-testid=jim-imported-object-omitted-values]").TextContent, Does.Contain("214"));
    }

    [Test]
    public void Render_AttributesLeftOut_NamesThem()
    {
        var snapshot = Recorded(null, Text("employeeId", "100482"));
        snapshot.OmittedAttributeCount = 2;
        snapshot.OmittedAttributeNames = ["memberOf", "notes"];

        var cut = Render<ImportedObjectSection>(p => p.Add(c => c.Snapshot, snapshot));

        Assert.That(cut.Find("[data-testid=jim-imported-object-omitted-attributes]").TextContent, Does.Contain("memberOf").And.Contain("notes"));
    }

    [Test]
    public void Render_NothingLeftOut_SaysNothingAboutIt()
    {
        var cut = Render<ImportedObjectSection>(p => p.Add(c => c.Snapshot, Recorded(null, Text("employeeId", "100482"))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid=jim-imported-object-omitted-attributes]"), Is.Empty);
            Assert.That(cut.FindAll("[data-testid=jim-imported-object-omitted-values]"), Is.Empty);
        }
    }

    [Test]
    public void Render_RunLimitReached_SaysWhyInPlaceOfTheAttributes()
    {
        var cut = Render<ImportedObjectSection>(p => p.Add(c => c.Snapshot, ImportedObjectSnapshot.CreateRunLimitReached()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid=jim-imported-object-run-limit]"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll("[data-testid=jim-imported-object-view]"), Is.Empty, "There is nothing to choose a view of.");
        }
    }

    [Test]
    public void ChoosingJson_ShowsTheRecordAsJson()
    {
        var cut = Render<ImportedObjectSection>(p => p.Add(c => c.Snapshot, Recorded(null, Text("employeeId", "100482"))));

        cut.Find("[data-testid=jim-imported-object-view-json]").Click();

        cut.WaitForAssertion(() => Assert.That(cut.FindAll("[data-testid=jim-imported-object-json]"), Has.Count.EqualTo(1)));
    }
}
