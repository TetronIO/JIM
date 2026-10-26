// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// An inline <see cref="MudDialog"/> reads its <c>Options</c> only when it opens, so a dialog whose width depends on
/// what is chosen inside it (the Attribute Flow dialog, Medium for an Attribute source and ExtraLarge once there is
/// something to try) stayed at the width it opened with. <see cref="DialogOptionsSync"/> applies the options to the
/// open dialog whenever they differ from what it is showing.
/// </summary>
[TestFixture]
public class DialogOptionsSyncTests : JimComponentTestContext
{
    private static readonly DialogOptions Narrow = new() { FullWidth = true, MaxWidth = MaxWidth.Medium };
    private static readonly DialogOptions Wide = new() { FullWidth = true, MaxWidth = MaxWidth.ExtraLarge };

    [Test]
    public void DialogOptionsSync_OptionsChangeWhileOpen_AppliesThemToTheOpenDialog()
    {
        var dialog = OpenDialogShowing(Narrow);
        var cut = Render<CascadingValue<IMudDialogInstance>>(p => p
            .Add(c => c.Value, dialog.Object)
            .AddChildContent<DialogOptionsSync>(child => child.Add(c => c.Options, Narrow)));

        cut.FindComponent<DialogOptionsSync>().Render(p => p.Add(c => c.Options, Wide));

        dialog.Verify(d => d.SetOptionsAsync(Wide), Times.Once);
    }

    [Test]
    public void DialogOptionsSync_OptionsMatchTheOpenDialog_LeavesTheDialogAlone()
    {
        var dialog = OpenDialogShowing(Wide);

        Render<CascadingValue<IMudDialogInstance>>(p => p
            .Add(c => c.Value, dialog.Object)
            .AddChildContent<DialogOptionsSync>(child => child.Add(c => c.Options, Wide)));

        dialog.Verify(d => d.SetOptionsAsync(It.IsAny<DialogOptions>()), Times.Never);
    }

    [Test]
    public void DialogOptionsSync_OutsideADialog_RendersWithoutError()
    {
        Assert.That(() => Render<DialogOptionsSync>(p => p.Add(c => c.Options, Wide)), Throws.Nothing);
    }

    /// <summary>
    /// A dialog instance showing <paramref name="options"/>, whose options become whatever it is next given, as an
    /// open MudBlazor dialog's do.
    /// </summary>
    private static Mock<IMudDialogInstance> OpenDialogShowing(DialogOptions options)
    {
        var current = options;
        var dialog = new Mock<IMudDialogInstance>();
        dialog.SetupGet(d => d.Options).Returns(() => current);
        dialog.Setup(d => d.SetOptionsAsync(It.IsAny<DialogOptions>()))
            .Callback<DialogOptions>(o => current = o)
            .Returns(Task.CompletedTask);
        return dialog;
    }
}
