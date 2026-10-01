// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using Microsoft.JSInterop;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers <see cref="ExpressionEditor"/>: the textarea an administrator types into, the highlighted overlay drawn
/// beneath it, the field furniture it shares with MudBlazor's outlined inputs, and inserting text at the caret.
/// </summary>
[TestFixture]
public class ExpressionEditorTests : JimComponentTestContext
{
    private const string GetSelection = "jimInterop.getTextSelection";
    private const string SetSelection = "jimInterop.setTextSelection";

    [Test]
    public void ExpressionEditor_WithValue_RendersValueInTextareaAndHighlightedOverlay()
    {
        var cut = Render<ExpressionEditor>(p => p.Add(c => c.Value, "Lower(cs[\"firstName\"])"));

        var textarea = cut.Find("textarea");
        var overlay = cut.Find("pre");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(textarea.GetAttribute("value") ?? textarea.TextContent, Is.EqualTo("Lower(cs[\"firstName\"])"));
            Assert.That(overlay.GetAttribute("aria-hidden"), Is.EqualTo("true"));
            Assert.That(overlay.QuerySelector(".jim-expr-accessor-cs")?.TextContent, Is.EqualTo("cs[\"firstName\"]"));
            Assert.That(overlay.QuerySelector(".jim-expr-function")?.TextContent, Is.EqualTo("Lower"));
        }
    }

    [Test]
    public void ExpressionEditor_OverlayTextContent_EqualsValue()
    {
        const string value = "IIF(mv[\"Active\"] == true,\n\tUpper(cs[\"Name\"]), \"<b>\")";

        var cut = Render<ExpressionEditor>(p => p.Add(c => c.Value, value));

        Assert.That(cut.Find("pre").TextContent, Is.EqualTo(value));
    }

    [Test]
    public void ExpressionEditor_ValueEndingInNewline_PadsOverlaySoItsLastLineHasHeight()
    {
        var cut = Render<ExpressionEditor>(p => p.Add(c => c.Value, "a\n"));

        var overlayText = cut.Find("pre").TextContent;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(overlayText, Does.StartWith("a\n"));
            Assert.That(overlayText.Length, Is.EqualTo(3), "A pre drops a trailing empty line; one padding character keeps it.");
            Assert.That(char.IsWhiteSpace(overlayText[^1]) || overlayText[^1] == '​', Is.True);
        }
    }

    [Test]
    public void ExpressionEditor_Textarea_DisablesBrowserTextAssistance()
    {
        var cut = Render<ExpressionEditor>(p => p.Add(c => c.Id, "expression-input"));

        var textarea = cut.Find("textarea");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(textarea.Id, Is.EqualTo("expression-input"));
            Assert.That(textarea.GetAttribute("spellcheck"), Is.EqualTo("false"));
            Assert.That(textarea.GetAttribute("autocomplete"), Is.EqualTo("off"));
            Assert.That(textarea.GetAttribute("autocapitalize"), Is.EqualTo("off"));
        }
    }

    [Test]
    public void ExpressionEditor_OnInput_RaisesValueChangedImmediately()
    {
        string? raised = null;
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, "mv")
            .Add(c => c.ValueChanged, value => raised = value));

        cut.Find("textarea").Input("mv[\"x\"]");

        Assert.That(raised, Is.EqualTo("mv[\"x\"]"));
    }

    [Test]
    public void ExpressionEditor_OnInput_RedrawsOverlayWithoutWaitingForParent()
    {
        var cut = Render<ExpressionEditor>(p => p.Add(c => c.Value, string.Empty));

        cut.Find("textarea").Input("mv[\"x\"]");

        Assert.That(cut.Find("pre").QuerySelector(".jim-expr-accessor-mv"), Is.Not.Null);
    }

    [Test]
    public void ExpressionEditor_FieldParameters_ReachTheOutlinedField()
    {
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Label, "Expression")
            .Add(c => c.HelperText, "Reads the source object's attributes.")
            .Add(c => c.Error, true)
            .Add(c => c.ErrorText, "Not a valid expression"));

        var field = cut.FindComponent<MudField>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(field.Label, Is.EqualTo("Expression"));
            Assert.That(field.Variant, Is.EqualTo(Variant.Outlined));
            Assert.That(field.HelperText, Is.EqualTo("Reads the source object's attributes."));
            Assert.That(field.Error, Is.True);
            Assert.That(field.ErrorText, Is.EqualTo("Not a valid expression"));
            Assert.That(cut.Find("textarea").GetAttribute("aria-invalid"), Is.EqualTo("true"));
            Assert.That(cut.Find("textarea").GetAttribute("aria-label"), Is.EqualTo("Expression"));
        }
    }

    [Test]
    public void ExpressionEditor_Placeholder_IsOnTheTextarea()
    {
        var cut = Render<ExpressionEditor>(p => p.Add(c => c.Placeholder, "e.g. Lower(cs[\"firstName\"])"));

        Assert.That(cut.Find("textarea").GetAttribute("placeholder"), Is.EqualTo("e.g. Lower(cs[\"firstName\"])"));
    }

    [Test]
    public void ExpressionEditor_EmptyWithPlaceholder_SizesOverlayToPlaceholderInvisibly()
    {
        var cut = Render<ExpressionEditor>(p => p.Add(c => c.Placeholder, "e.g. <long> placeholder"));

        var sizer = cut.Find("pre").QuerySelector(".jim-expression-editor-placeholder-sizer");

        Assert.That(sizer?.TextContent, Is.EqualTo("e.g. <long> placeholder"),
            "The overlay sizes the field, so an empty field must still be as tall as its placeholder.");
    }

    [TestCase(null, null, false)]
    [TestCase(null, "e.g. Lower(x)", true)]
    [TestCase("1", null, true)]
    public void ExpressionEditor_LabelPosition_LiftsIntoNotchWhenTextOrPlaceholderPresent(string? value, string? placeholder, bool lifted)
    {
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Label, "Expression")
            .Add(c => c.Value, value)
            .Add(c => c.Placeholder, placeholder));

        Assert.That(cut.FindComponent<MudField>().Instance.Class, lifted ? Does.Contain("jim-expression-editor-shrink") : Does.Not.Contain("jim-expression-editor-shrink"),
            "the label must leave the text area whenever something else occupies it, or the two overlap");
    }

    [Test]
    public void ExpressionEditor_Disabled_DisablesTextareaAndField()
    {
        var cut = Render<ExpressionEditor>(p => p.Add(c => c.Disabled, true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("textarea").HasAttribute("disabled"), Is.True);
            Assert.That(cut.FindComponent<MudField>().Instance.Disabled, Is.True);
        }
    }

    [Test]
    public void ExpressionEditor_Class_IsAppliedToTheField()
    {
        var cut = Render<ExpressionEditor>(p => p.Add(c => c.Class, "mt-5"));

        Assert.That(cut.FindComponent<MudField>().Instance.Class, Does.Contain("mt-5"));
    }

    [Test]
    public void ExpressionEditor_RequiredAndEmpty_ShowsNoErrorUntilTouched()
    {
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Label, "Expression")
            .Add(c => c.Required, true));

        Assert.That(cut.FindComponent<MudField>().Instance.Error, Is.False);
    }

    [Test]
    public void ExpressionEditor_RequiredAndEmptyAfterBlur_ShowsRequiredError()
    {
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Label, "Expression")
            .Add(c => c.Required, true));

        cut.Find("textarea").Blur();

        var field = cut.FindComponent<MudField>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(field.Error, Is.True);
            Assert.That(field.ErrorText, Is.EqualTo("Required"));
            Assert.That(cut.Find("textarea").GetAttribute("aria-required"), Is.EqualTo("true"));
        }
    }

    [Test]
    public void ExpressionEditor_RequiredWithValueAfterBlur_ShowsNoError()
    {
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, "1")
            .Add(c => c.Required, true));

        cut.Find("textarea").Blur();

        Assert.That(cut.FindComponent<MudField>().Instance.Error, Is.False);
    }

    // ─── InsertAtCursorAsync ───

    [Test]
    public async Task InsertAtCursorAsync_CaretInMiddle_InsertsAtCaretAndRaisesValueChangedAsync()
    {
        JSInterop.Setup<int[]>(GetSelection, _ => true).SetResult([6, 6]);
        string? raised = null;
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, "Lower()")
            .Add(c => c.ValueChanged, value => raised = value));

        await cut.InvokeAsync(() => cut.Instance.InsertAtCursorAsync("cs[\"firstName\"]"));

        Assert.That(raised, Is.EqualTo("Lower(cs[\"firstName\"])"));
    }

    [Test]
    public async Task InsertAtCursorAsync_WithSelection_ReplacesSelectionAsync()
    {
        JSInterop.Setup<int[]>(GetSelection, _ => true).SetResult([6, 9]);
        string? raised = null;
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, "Lower(old)")
            .Add(c => c.ValueChanged, value => raised = value));

        await cut.InvokeAsync(() => cut.Instance.InsertAtCursorAsync("mv[\"x\"]"));

        Assert.That(raised, Is.EqualTo("Lower(mv[\"x\"])"));
    }

    [Test]
    public async Task InsertAtCursorAsync_AfterInsert_MovesCaretAfterInsertedTextAndFocusesAsync()
    {
        JSInterop.Setup<int[]>(GetSelection, _ => true).SetResult([6, 9]);
        var cut = Render<ExpressionEditor>(p => p.Add(c => c.Value, "Lower(old)"));

        await cut.InvokeAsync(() => cut.Instance.InsertAtCursorAsync("mv[\"x\"]"));

        var invocation = JSInterop.VerifyInvoke(SetSelection);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(invocation.Arguments[1], Is.EqualTo(13));
            Assert.That(invocation.Arguments[2], Is.EqualTo(13));
            Assert.That(invocation.Arguments[3], Is.EqualTo("Lower(mv[\"x\"])"));
        }
    }

    [Test]
    public async Task InsertAtCursorAsync_RedrawsOverlayWithInsertedTextAsync()
    {
        JSInterop.Setup<int[]>(GetSelection, _ => true).SetResult([0, 0]);
        var cut = Render<ExpressionEditor>(p => p.Add(c => c.Value, string.Empty));

        await cut.InvokeAsync(() => cut.Instance.InsertAtCursorAsync("cs[\"a\"]"));

        Assert.That(cut.Find("pre").QuerySelector(".jim-expr-accessor-cs")?.TextContent, Is.EqualTo("cs[\"a\"]"));
    }

    [Test]
    public async Task InsertAtCursorAsync_NullValue_InsertsTextAsync()
    {
        JSInterop.Setup<int[]>(GetSelection, _ => true).SetResult([0, 0]);
        string? raised = null;
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, null)
            .Add(c => c.ValueChanged, value => raised = value));

        await cut.InvokeAsync(() => cut.Instance.InsertAtCursorAsync("mv[\"a\"]"));

        Assert.That(raised, Is.EqualTo("mv[\"a\"]"));
    }

    [Test]
    public async Task InsertAtCursorAsync_SelectionBeyondValue_ClampsToEndAsync()
    {
        JSInterop.Setup<int[]>(GetSelection, _ => true).SetResult([50, 80]);
        string? raised = null;
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, "ab")
            .Add(c => c.ValueChanged, value => raised = value));

        await cut.InvokeAsync(() => cut.Instance.InsertAtCursorAsync("c"));

        Assert.That(raised, Is.EqualTo("abc"));
    }

    [Test]
    public async Task InsertAtCursorAsync_CircuitDisconnected_AppendsAtEndAndStillRaisesValueChangedAsync()
    {
        JSInterop.Setup<int[]>(GetSelection, _ => true).SetException(new JSDisconnectedException("gone"));
        JSInterop.SetupVoid(SetSelection, _ => true).SetException(new JSDisconnectedException("gone"));
        string? raised = null;
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, "Lower(x) + ")
            .Add(c => c.ValueChanged, value => raised = value));

        await cut.InvokeAsync(() => cut.Instance.InsertAtCursorAsync("\"y\""));

        Assert.That(raised, Is.EqualTo("Lower(x) + \"y\""));
    }

    [Test]
    public async Task InsertAtCursorAsync_InteropNotReady_AppendsAtEndAsync()
    {
        JSInterop.Setup<int[]>(GetSelection, _ => true).SetException(new InvalidOperationException("prerendering"));
        string? raised = null;
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, "a")
            .Add(c => c.ValueChanged, value => raised = value));

        await cut.InvokeAsync(() => cut.Instance.InsertAtCursorAsync("b"));

        Assert.That(raised, Is.EqualTo("ab"));
    }
}
