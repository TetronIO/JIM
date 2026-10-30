// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Connectors.Sql;
using JIM.Web.Pages.Admin.Components.Sql;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers when a catalogue name picker commits what is typed into it (#2570): once the name is finished (on leaving
/// the field or pressing Enter), never part-way through. Committing each keystroke added an anchor column named after
/// the first letter typed, and read the catalogue for every prefix of a table's name.
/// </summary>
[TestFixture]
public class SqlCatalogueNameFieldTests : JimComponentTestContext
{
    [Test]
    public void TypingInTheField_CommitsNothingUntilTheNameIsFinished()
    {
        var committed = new List<string?>();
        var cut = Render<SqlCatalogueNameField>(p => p
            .Add(c => c.Label, "Table or view")
            .Add(c => c.Options, ["APP_USERS", "APP_USER_ROLES"])
            .Add(c => c.ValueChanged, (string? value) => committed.Add(value)));

        var input = cut.Find("input");
        input.Input("A");
        input.Input("AP");
        input.Input("APP_U");

        Assert.That(committed, Is.Empty, "a name part-way through being typed is not a name");
    }

    [Test]
    public void LeavingTheField_CommitsTheNameTyped()
    {
        // Where the catalogue cannot be read the field is free text, so a typed name has to be committed somehow.
        var committed = new List<string?>();
        var cut = Render<SqlCatalogueNameField>(p => p
            .Add(c => c.Label, "Table or view")
            .Add(c => c.ValueChanged, (string? value) => committed.Add(value)));

        var input = cut.Find("input");
        input.Input("EMPLOYEES");
        LeaveByTab(input);

        cut.WaitForAssertion(() => Assert.That(committed, Is.EqualTo(new[] { "EMPLOYEES" })));
    }

    [Test]
    public void AnchorPicker_TypingAColumnName_AddsNoColumnUntilItIsFinished()
    {
        var objectType = new SqlObjectTypeDocument { Name = "PersonView", Table = "V_EMPLOYEES" };
        var cut = Render<SqlAnchorColumnsField>(p => p
            .Add(c => c.ObjectType, objectType)
            .Add(c => c.ColumnOptions, ["EMPLOYEE_ID", "EMAIL"]));

        var input = cut.Find("input");
        input.Input("E");
        input.Input("EM");
        input.Input("EMP");

        Assert.That(objectType.AnchorColumns ?? [], Is.Empty);
    }

    [Test]
    public void AnchorPicker_PartOfANameCommittedByLeavingTheField_IsNotAColumn()
    {
        // Clicking a list item blurs the field before the click lands, which commits what was typed so far.
        var objectType = new SqlObjectTypeDocument { Name = "Person", Table = "EMPLOYEES" };
        var cut = Render<SqlAnchorColumnsField>(p => p
            .Add(c => c.ObjectType, objectType)
            .Add(c => c.ColumnOptions, ["EMPLOYEE_ID", "DEPARTMENT_ID"]));

        var input = cut.Find("input");
        input.Input("EP");
        LeaveByTab(input);

        Assert.That(objectType.AnchorColumns ?? [], Is.Empty, "'EP' is not one of the table's columns");
    }

    [Test]
    public void AnchorPicker_AColumnChosen_IsAddedInTheDatabasesSpellingAndThePickerEmptied()
    {
        var objectType = new SqlObjectTypeDocument { Name = "Person", Table = "EMPLOYEES" };
        var cut = Render<SqlAnchorColumnsField>(p => p
            .Add(c => c.ObjectType, objectType)
            .Add(c => c.ColumnOptions, ["EMPLOYEE_ID", "DEPARTMENT_ID"]));

        // As choosing it from the list does; bUnit does not render the list's popover to click in.
        var picker = cut.FindComponent<SqlCatalogueNameField>();
        cut.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync("department_id")).Wait();

        cut.WaitForAssertion(() =>
        {
            Assert.That(objectType.AnchorColumns, Is.EqualTo(new[] { "DEPARTMENT_ID" }));
            Assert.That(cut.Find("input").GetAttribute("value") ?? "", Is.Empty, "the picker is ready for the next column");
        });
    }

    [Test]
    public void AnchorPicker_ColumnsUnknown_AddsTheNameTyped()
    {
        // Where the database could not be read there is no list, and the name typed is all there is.
        var objectType = new SqlObjectTypeDocument { Name = "Person", Table = "EMPLOYEES" };
        var cut = Render<SqlAnchorColumnsField>(p => p.Add(c => c.ObjectType, objectType));

        var input = cut.Find("input");
        input.Input("EMPLOYEE_ID");
        LeaveByTab(input);

        cut.WaitForAssertion(() => Assert.That(objectType.AnchorColumns, Is.EqualTo(new[] { "EMPLOYEE_ID" })));
    }

    /// <summary>Leaves the field as a person does: Tab closes the list, then the field loses focus.</summary>
    private static void LeaveByTab(AngleSharp.Dom.IElement input)
    {
        input.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Tab" });
        input.Blur();
    }

    [Test]
    public void Matching_PutsTheNameTypedFirstThenNamesStartingWithIt()
    {
        var matches = SqlCatalogueNameField.Matching(["ARCHIVE_HR", "HR_STAGING", "HR", "CHR"], "hr");

        Assert.That(matches, Is.EqualTo(new[] { "HR", "HR_STAGING", "ARCHIVE_HR", "CHR" }));
    }

    [Test]
    public void ClearButton_PassesOnNoNameAtOnce()
    {
        var committed = new List<string?>();
        var cut = Render<SqlCatalogueNameField>(p => p
            .Add(c => c.Label, "Table or view")
            .Add(c => c.Value, "EMPLOYEES")
            .Add(c => c.Options, ["EMPLOYEES"])
            .Add(c => c.ValueChanged, (string? value) => committed.Add(value)));

        var autocomplete = cut.FindComponent<MudBlazor.MudAutocomplete<string>>();
        cut.InvokeAsync(() => autocomplete.Instance.OnClearButtonClick.InvokeAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs())).Wait();

        Assert.That(committed, Is.EqualTo(new string?[] { null }), "an emptied field holding its old name until the next blur would save the old name");
    }

    [Test]
    public void FocusingTheField_DoesNotHighlightItsText()
    {
        // Highlighting on focus is a round trip to the browser: a key typed before it lands is highlighted with the
        // rest and replaced by the next key, so clicking into the field and typing HR left R.
        var cut = Render<SqlCatalogueNameField>(p => p
            .Add(c => c.Label, "Schema")
            .Add(c => c.Options, ["HR"]));

        Assert.That(cut.FindComponent<MudBlazor.MudAutocomplete<string>>().Instance.SelectOnActivation, Is.False);
    }
}
