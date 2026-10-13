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
    private const string AssistInsert = "jimExpressionAssist.insert";

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
    public void ExpressionEditor_SyntaxCheckReportsAnError_ShowsItUnderTheField()
    {
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, "lowr(cs[\"mail\"])")
            .Add(c => c.SyntaxCheck, _ => "Unknown identifier 'lowr'."));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Unknown identifier 'lowr'."));
            Assert.That(cut.Find("textarea").GetAttribute("aria-invalid"), Is.EqualTo("true"));
        }
    }

    [Test]
    public void ExpressionEditor_SyntaxCheckFindsNothing_ShowsNoError()
    {
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, "Lower(cs[\"mail\"])")
            .Add(c => c.SyntaxCheck, _ => null));

        Assert.That(cut.Find("textarea").GetAttribute("aria-invalid"), Is.EqualTo("false"));
    }

    [Test]
    public void ExpressionEditor_WhileFirstTyped_HoldsTheSyntaxErrorBackUntilTheFieldIsLeft()
    {
        // Every half-typed expression is a syntax error; reporting each keystroke turns the field red mid-word.
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, string.Empty)
            .Add(c => c.SyntaxCheck, _ => "Unterminated string literal."));

        cut.Find("textarea").Input("Lower(\"");
        var whileTyping = cut.Find("textarea").GetAttribute("aria-invalid");
        cut.Find("textarea").Blur();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileTyping, Is.EqualTo("false"));
            Assert.That(cut.Markup, Does.Contain("Unterminated string literal."));
        }
    }

    [Test]
    public void ExpressionEditor_OnceLeft_ReportsTheSyntaxErrorAsItIsTyped()
    {
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, "Lower(cs[\"mail\"])")
            .Add(c => c.SyntaxCheck, text => text.EndsWith(')') ? null : "Unterminated call."));
        cut.Find("textarea").Blur();

        cut.Find("textarea").Input("Lower(cs[\"mail\"]");

        Assert.That(cut.Markup, Does.Contain("Unterminated call."));
    }

    [Test]
    public void ExpressionEditor_HostErrorAndSyntaxError_ShowsTheHostsError()
    {
        var cut = Render<ExpressionEditor>(p => p
            .Add(c => c.Value, "x(")
            .Add(c => c.Error, true)
            .Add(c => c.ErrorText, "The host's own error.")
            .Add(c => c.SyntaxCheck, _ => "A syntax error."));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("The host's own error."));
            Assert.That(cut.Markup, Does.Not.Contain("A syntax error."));
        }
    }

    #region Suggestions and signature help (#2053)

    private IRenderedComponent<ExpressionEditor> RenderWithAssist(string value, Action<string?>? onChanged = null) =>
        Render<ExpressionEditor>(p => p
            .Add(c => c.Value, value)
            .Add(c => c.ConnectedSystemAttributeNames, new[] { "mail", "proxyAddresses" })
            .Add(c => c.ValueChanged, (string? v) => onChanged?.Invoke(v)));

    [Test]
    public void ExpressionEditor_TypingAPartFunctionName_ListsTheFunctionsWithTheirSignatures()
    {
        var cut = RenderWithAssist("Sta");

        cut.InvokeAsync(() => cut.Instance.OnCaretMoved("Sta", 3, 3, typed: true));

        var list = cut.Find("[data-testid=jim-expression-suggestions]");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list.TextContent, Does.Contain("StartsWith(value, prefix[, ignoreCase])"));
            Assert.That(cut.Find("textarea").GetAttribute("data-assist-open"), Is.EqualTo("true"));
            Assert.That(cut.Find("textarea").GetAttribute("aria-activedescendant"), Is.Not.Null);
        }
    }

    [Test]
    public void ExpressionEditor_TypingInsideAnAccessor_ListsTheHostsAttributes()
    {
        var cut = RenderWithAssist("cs[\"pro");

        cut.InvokeAsync(() => cut.Instance.OnCaretMoved("cs[\"pro", 7, 7, typed: true));

        Assert.That(cut.Find("[data-testid=jim-expression-suggestions]").TextContent, Does.Contain("proxyAddresses"));
    }

    [Test]
    public void ExpressionEditor_EnterOnASuggestion_AsksTheBrowserToInsertItInPlaceOfWhatWasTyped()
    {
        // The browser does the insert, so a key typed while the choice travelled to the server and back is kept: the
        // server writing the whole value would put it back as it stood when Enter was pressed.
        JSInterop.Setup<bool>(AssistInsert, _ => true).SetResult(true);
        string? changed = null;
        var cut = RenderWithAssist("Lower(cs[\"pro", v => changed = v);
        cut.InvokeAsync(() => cut.Instance.OnCaretMoved("Lower(cs[\"pro", 13, 13, typed: true));

        cut.InvokeAsync(() => cut.Instance.OnAssistKey("Enter"));

        var invocation = JSInterop.VerifyInvoke(AssistInsert);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(invocation.Arguments.Skip(1), Is.EqualTo(new object[] { 10, 13, "pro", "proxyAddresses\"]" }));
            Assert.That(changed, Is.Null, "the browser's input event carries the new value, not the server");
            Assert.That(cut.FindAll("[data-testid=jim-expression-suggestions]"), Is.Empty);
        }
    }

    [Test]
    public void ExpressionEditor_EnterOnASuggestionWithoutJavaScript_InsertsItOnTheServer()
    {
        JSInterop.Setup<bool>(AssistInsert, _ => true).SetException(new InvalidOperationException("prerendering"));
        string? changed = null;
        var cut = RenderWithAssist("Lower(cs[\"pro", v => changed = v);
        cut.InvokeAsync(() => cut.Instance.OnCaretMoved("Lower(cs[\"pro", 13, 13, typed: true));

        cut.InvokeAsync(() => cut.Instance.OnAssistKey("Enter"));

        Assert.That(changed, Is.EqualTo("Lower(cs[\"proxyAddresses\"]"));
    }

    [Test]
    public void ExpressionEditor_ArrowDown_MovesToTheNextSuggestion()
    {
        var cut = RenderWithAssist("Is");
        cut.InvokeAsync(() => cut.Instance.OnCaretMoved("Is", 2, 2, typed: true));

        cut.InvokeAsync(() => cut.Instance.OnAssistKey("ArrowDown"));

        var options = cut.FindAll("[role=option]");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options[0].GetAttribute("aria-selected"), Is.EqualTo("false"));
            Assert.That(options[1].GetAttribute("aria-selected"), Is.EqualTo("true"));
        }
    }

    [Test]
    public void ExpressionEditor_Escape_ClosesTheSuggestions()
    {
        var cut = RenderWithAssist("Sta");
        cut.InvokeAsync(() => cut.Instance.OnCaretMoved("Sta", 3, 3, typed: true));

        cut.InvokeAsync(() => cut.Instance.OnAssistKey("Escape"));

        Assert.That(cut.FindAll("[data-testid=jim-expression-suggestions]"), Is.Empty);
    }

    [Test]
    public void ExpressionEditor_CaretInsideACall_ShowsItsSignatureWithTheParameterItIsOn()
    {
        var cut = RenderWithAssist("StartsWith(cs[\"mail\"], ");

        cut.InvokeAsync(() => cut.Instance.OnCaretMoved("StartsWith(cs[\"mail\"], ", 23, 23, typed: false));

        var signature = cut.Find("[data-testid=jim-expression-signature]");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(signature.TextContent, Does.Contain("StartsWith(value, prefix[, ignoreCase])"));
            Assert.That(signature.QuerySelector(".jim-expression-signature-active")?.TextContent, Is.EqualTo("prefix"));
        }
    }

    [Test]
    public void ExpressionEditor_CaretMovedWithoutTyping_OpensNoSuggestions()
    {
        var cut = RenderWithAssist("Sta");

        cut.InvokeAsync(() => cut.Instance.OnCaretMoved("Sta", 3, 3, typed: false));

        Assert.That(cut.FindAll("[data-testid=jim-expression-suggestions]"), Is.Empty);
    }

    [Test]
    public void ExpressionEditor_AssistOff_NeverSuggests()
    {
        var cut = Render<ExpressionEditor>(p => p.Add(c => c.Value, "Sta").Add(c => c.Assist, false));

        cut.InvokeAsync(() => cut.Instance.OnCaretMoved("Sta", 3, 3, typed: true));

        Assert.That(cut.FindAll("[data-testid=jim-expression-suggestions]"), Is.Empty);
    }

    #endregion

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
