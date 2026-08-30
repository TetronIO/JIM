// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using AngleSharp.Dom;
using Bunit;
using JIM.Web.Models;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the shared destructive-change confirmation dialog. Its whole job is to stand between an
/// administrator and an irreversible change, so the gating rules (blocked, typed confirmation, no
/// double-submit) are the behaviour that matters; getting them wrong either destroys data without
/// consent or blocks a legitimate change.
/// </summary>
[TestFixture]
public class ConsequenceConfirmationDialogTests : JimComponentTestContext
{
    private const string ConfirmButtonMarker = "jim-consequence-confirm";
    private const string CancelButtonMarker = "jim-consequence-cancel";
    private const string PhraseFieldMarker = "jim-consequence-phrase";

    /// <summary>
    /// Shows the dialog through MudBlazor's dialog service (the way callers open it) and returns the
    /// rendered provider so tests can assert on the dialog's markup.
    ///
    /// Showing a dialog is dispatched onto the renderer rather than performed inline, so the dialog's
    /// first render is not guaranteed to have landed when the call returns. Every test here goes on to
    /// query the dialog's markup immediately, which on a loaded machine can run before that render
    /// completes and fail with an element that is merely not there yet. Waiting for the confirm button
    /// (rendered unconditionally, whatever the dialog's state) makes the handover deterministic for
    /// every test in the fixture rather than leaving each one to race.
    /// </summary>
    private IRenderedComponent<MudDialogProvider> ShowDialog(DialogParameters<ConsequenceConfirmationDialog> parameters)
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        provider.InvokeAsync(() => dialogService.ShowAsync<ConsequenceConfirmationDialog>("Confirm", parameters));
        provider.WaitForElement($"[data-testid='{ConfirmButtonMarker}']");

        return provider;
    }

    private static IElement ConfirmButton(IRenderedComponent<MudDialogProvider> provider) =>
        provider.Find($"[data-testid='{ConfirmButtonMarker}']");

    private static bool ConfirmIsDisabled(IRenderedComponent<MudDialogProvider> provider) =>
        ConfirmButton(provider).HasAttribute("disabled");

    /// <summary>
    /// Drives the confirmation field with an input event rather than change, because the field sets
    /// Immediate="true" (JIM.Web/CLAUDE.md: typed inputs that gate live UI must commit on input, not
    /// blur). If that attribute is ever dropped, these tests fail with a missing oninput handler,
    /// which is the point.
    /// </summary>
    private static void TypePhrase(IRenderedComponent<MudDialogProvider> provider, string phrase) =>
        provider.Find($"[data-testid='{PhraseFieldMarker}'] input").Input(phrase);

    #region Blocking

    [Test]
    public void ConsequenceConfirmationDialog_WhenBlocked_DisablesConfirm()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.Blockers, [new ConsequenceBlocker { Headline = "3 objects still hold a value" }] },
            { x => x.ConfirmButtonText, "Delete" }
        });

        Assert.That(ConfirmIsDisabled(provider), Is.True);
    }

    [Test]
    public void ConsequenceConfirmationDialog_WhenBlocked_RendersBlockerHeadline()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.Blockers, [new ConsequenceBlocker { Headline = "3 objects still hold a value" }] }
        });

        Assert.That(provider.Markup, Does.Contain("3 objects still hold a value"));
    }

    [Test]
    public void ConsequenceConfirmationDialog_WhenBlocked_CancelButtonReadsClose()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.Blockers, [new ConsequenceBlocker { Headline = "Blocked" }] }
        });

        Assert.That(provider.Find($"[data-testid='{CancelButtonMarker}']").TextContent.Trim(), Is.EqualTo("Close"));
    }

    [Test]
    public void ConsequenceConfirmationDialog_WhenNotBlocked_CancelButtonReadsCancel()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmButtonText, "Delete" }
        });

        Assert.That(provider.Find($"[data-testid='{CancelButtonMarker}']").TextContent.Trim(), Is.EqualTo("Cancel"));
    }

    [Test]
    public void ConsequenceConfirmationDialog_WhenBlocked_DoesNotOfferTypedConfirmation()
    {
        // A blocked change can never proceed, so asking the administrator to type the name would
        // imply it could.
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.Blockers, [new ConsequenceBlocker { Headline = "Blocked" }] },
            { x => x.ConfirmationPhrase, "Payroll" }
        });

        Assert.That(provider.FindAll($"[data-testid='{PhraseFieldMarker}']"), Is.Empty);
    }

    #endregion

    #region Typed confirmation

    [Test]
    public void ConsequenceConfirmationDialog_NoPhraseRequired_EnablesConfirmImmediately()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmButtonText, "Delete" }
        });

        Assert.That(ConfirmIsDisabled(provider), Is.False);
    }

    [Test]
    public void ConsequenceConfirmationDialog_PhraseRequiredAndNotTyped_DisablesConfirm()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmationPhrase, "Payroll" }
        });

        Assert.That(ConfirmIsDisabled(provider), Is.True);
    }

    [Test]
    public void ConsequenceConfirmationDialog_PhraseTypedIncorrectly_DisablesConfirm()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmationPhrase, "Payroll" }
        });

        TypePhrase(provider, "Payrol");

        Assert.That(ConfirmIsDisabled(provider), Is.True);
    }

    [Test]
    public void ConsequenceConfirmationDialog_PhraseTypedWithWrongCase_DisablesConfirm()
    {
        // Ordinal comparison: a destructive confirmation should not accept a near miss.
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmationPhrase, "Payroll" }
        });

        TypePhrase(provider, "payroll");

        Assert.That(ConfirmIsDisabled(provider), Is.True);
    }

    [Test]
    public void ConsequenceConfirmationDialog_PhraseTypedExactly_EnablesConfirm()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmationPhrase, "Payroll" }
        });

        TypePhrase(provider, "Payroll");

        Assert.That(ConfirmIsDisabled(provider), Is.False);
    }

    [Test]
    public void ConsequenceConfirmationDialog_PhraseTypedWithSurroundingWhitespace_EnablesConfirm()
    {
        // Trimmed before comparison: pasting a name often brings whitespace with it, and refusing
        // that is friction without any safety benefit.
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmationPhrase, "Payroll" }
        });

        TypePhrase(provider, "  Payroll  ");

        Assert.That(ConfirmIsDisabled(provider), Is.False);
    }

    #endregion

    #region Confirming

    [Test]
    public void ConsequenceConfirmationDialog_ConfirmClicked_InvokesCallback()
    {
        var invoked = false;
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmButtonText, "Delete" },
            { x => x.OnConfirmAsync, () => { invoked = true; return Task.FromResult(true); } }
        });

        ConfirmButton(provider).Click();

        Assert.That(invoked, Is.True);
    }

    [Test]
    public void ConsequenceConfirmationDialog_ConfirmSucceeds_ClosesDialog()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmButtonText, "Delete" },
            { x => x.OnConfirmAsync, () => Task.FromResult(true) }
        });

        ConfirmButton(provider).Click();

        Assert.That(provider.FindAll($"[data-testid='{ConfirmButtonMarker}']"), Is.Empty);
    }

    [Test]
    public void ConsequenceConfirmationDialog_ConfirmFails_KeepsDialogOpen()
    {
        // The caller reports failure by returning false; the administrator keeps their context and
        // can retry rather than losing the dialog behind a snackbar.
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmButtonText, "Delete" },
            { x => x.OnConfirmAsync, () => Task.FromResult(false) }
        });

        ConfirmButton(provider).Click();

        Assert.That(provider.FindAll($"[data-testid='{ConfirmButtonMarker}']"), Is.Not.Empty);
    }

    [Test]
    public void ConsequenceConfirmationDialog_ConfirmFails_ReenablesConfirmForRetry()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmButtonText, "Delete" },
            { x => x.OnConfirmAsync, () => Task.FromResult(false) }
        });

        ConfirmButton(provider).Click();

        Assert.That(ConfirmIsDisabled(provider), Is.False);
    }

    [Test]
    public void ConsequenceConfirmationDialog_WhileConfirming_DisablesConfirmToPreventDoubleSubmit()
    {
        var release = new TaskCompletionSource<bool>();
        var callCount = 0;
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmButtonText, "Delete" },
            { x => x.OnConfirmAsync, () => { callCount++; return release.Task; } }
        });

        ConfirmButton(provider).Click();

        // The confirmation is still in flight, so the disabled state holds until the task is released
        // below: waiting for the re-render rather than sampling it once removes the race between the
        // click's render batch and this assertion, without weakening what is being asserted.
        provider.WaitForAssertion(() => Assert.That(ConfirmIsDisabled(provider), Is.True));
        release.SetResult(true);

        Assert.That(callCount, Is.EqualTo(1));
    }

    [Test]
    public void ConsequenceConfirmationDialog_WhileConfirming_ShowsBusyText()
    {
        var release = new TaskCompletionSource<bool>();
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.ConfirmButtonText, "Delete" },
            { x => x.BusyText, "Deleting..." },
            { x => x.OnConfirmAsync, () => release.Task }
        });

        ConfirmButton(provider).Click();

        // As above: the busy text stays on screen until the confirmation task is released.
        provider.WaitForAssertion(() => Assert.That(provider.Markup, Does.Contain("Deleting...")));
        release.SetResult(true);
    }

    #endregion

    #region Loading

    [Test]
    public void ConsequenceConfirmationDialog_WhileLoading_DisablesConfirm()
    {
        // The impact is not yet known, so there is nothing to consent to.
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.Loading, true },
            { x => x.ConfirmButtonText, "Delete" }
        });

        Assert.That(ConfirmIsDisabled(provider), Is.True);
    }

    #endregion

    #region Content

    [Test]
    public void ConsequenceConfirmationDialog_WithSafeMessage_RendersItWhenNotBlocked()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.SafeMessage, "Safe to delete; no object data will be lost." }
        });

        Assert.That(provider.Markup, Does.Contain("Safe to delete; no object data will be lost."));
    }

    [Test]
    public void ConsequenceConfirmationDialog_WithSafeMessage_SuppressesItWhenBlocked()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.SafeMessage, "Safe to delete." },
            { x => x.Blockers, [new ConsequenceBlocker { Headline = "Blocked" }] }
        });

        Assert.That(provider.Markup, Does.Not.Contain("Safe to delete."));
    }

    [Test]
    public void ConsequenceConfirmationDialog_WithIrreversibleWarning_RendersItWhenNotBlocked()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.IrreversibleWarning, "This action is permanent and cannot be undone." }
        });

        Assert.That(provider.Markup, Does.Contain("This action is permanent and cannot be undone."));
    }

    [Test]
    public void ConsequenceConfirmationDialog_WithIrreversibleWarning_SuppressesItWhenBlocked()
    {
        // Nothing irreversible can happen whilst the change is blocked, so warning about it would
        // misrepresent the situation.
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.IrreversibleWarning, "This action is permanent and cannot be undone." },
            { x => x.Blockers, [new ConsequenceBlocker { Headline = "Blocked" }] }
        });

        Assert.That(provider.Markup, Does.Not.Contain("This action is permanent and cannot be undone."));
    }

    [Test]
    public void ConsequenceConfirmationDialog_WithConsequences_RendersHeadlineAndItems()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            {
                x => x.Consequences, new ConsequenceGroup
                {
                    Headline = "These 2 reference(s) will also be removed",
                    Items =
                    [
                        new ConsequenceItem { Text = "Predefined Search: All Employees" },
                        new ConsequenceItem { Text = "Attribute binding: User.Department" }
                    ]
                }
            }
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Contain("These 2 reference(s) will also be removed"));
            Assert.That(provider.Markup, Does.Contain("Predefined Search: All Employees"));
            Assert.That(provider.Markup, Does.Contain("Attribute binding: User.Department"));
        }
    }

    [Test]
    public void ConsequenceConfirmationDialog_WithCounts_RendersLabelsAndThousandsSeparatedValues()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.Counts, [new ImpactCount { Label = "Connected System Objects", Count = 12405 }] }
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Contain("Connected System Objects"));
            Assert.That(provider.Markup, Does.Contain("12,405"));
        }
    }

    [Test]
    public void ConsequenceConfirmationDialog_GroupedCounts_RendersGroupAndSubgroupHeaders()
    {
        // Counts regrouped by fate (#809): rows carrying a Group render under a group header row, and rows
        // carrying a Subgroup render under an indented subgroup header within it.
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            {
                x => x.Counts, new List<ImpactCount>
                {
                    new() { Group = "Removed with the system", Label = "Connected System Objects", Count = 1200 },
                    new() { Group = "Affected, not removed", Subgroup = "Metaverse Objects", Label = "Joined", Count = 340 },
                    new() { Group = "Affected, not removed", Label = "Activities", Count = 12, Note = "kept" }
                }
            }
        });

        using (Assert.EnterMultipleScope())
        {
            var groupHeaders = provider.FindAll("[data-testid='jim-consequence-count-group']");
            var subgroupHeaders = provider.FindAll("[data-testid='jim-consequence-count-subgroup']");
            Assert.That(groupHeaders, Has.Count.EqualTo(2), "each distinct group renders one header row");
            Assert.That(subgroupHeaders, Has.Count.EqualTo(1), "each distinct subgroup renders one header row");
            Assert.That(groupHeaders[0].TextContent, Does.Contain("Removed with the system"));
            Assert.That(groupHeaders[1].TextContent, Does.Contain("Affected, not removed"));
            Assert.That(subgroupHeaders[0].TextContent, Does.Contain("Metaverse Objects"));
        }
    }

    [Test]
    public void ConsequenceConfirmationDialog_GroupedCounts_EmitsEachHeaderOnce()
    {
        // Consecutive rows sharing a group (or subgroup) share one header rather than repeating it per row.
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            {
                x => x.Counts, new List<ImpactCount>
                {
                    new() { Group = "Removed with the system", Label = "Connected System Objects", Count = 1 },
                    new() { Group = "Removed with the system", Label = "Synchronisation Rules", Count = 2 },
                    new() { Group = "Affected, not removed", Subgroup = "Metaverse Objects", Label = "Joined", Count = 3 },
                    new() { Group = "Affected, not removed", Subgroup = "Metaverse Objects", Label = "Contributed attribute values", Count = 4 }
                }
            }
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FindAll("[data-testid='jim-consequence-count-group']"), Has.Count.EqualTo(2));
            Assert.That(provider.FindAll("[data-testid='jim-consequence-count-subgroup']"), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void ConsequenceConfirmationDialog_UngroupedCounts_RenderWithoutHeaders()
    {
        // Existing callers pass flat lists; they must render exactly as before groups existed.
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.Counts, [new ImpactCount { Label = "Connected System Objects", Count = 12405 }] }
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FindAll("[data-testid='jim-consequence-count-group']"), Is.Empty);
            Assert.That(provider.FindAll("[data-testid='jim-consequence-count-subgroup']"), Is.Empty);
        }
    }

    [Test]
    public void ConsequenceConfirmationDialog_WithWarnings_RendersEach()
    {
        var provider = ShowDialog(new DialogParameters<ConsequenceConfirmationDialog>
        {
            { x => x.Warnings, ["A synchronisation run is in progress.", "This system has unexported changes."] }
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Contain("A synchronisation run is in progress."));
            Assert.That(provider.Markup, Does.Contain("This system has unexported changes."));
        }
    }

    #endregion
}
