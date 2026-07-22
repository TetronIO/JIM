// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Proves the bUnit toolchain renders components under net10.0 with NUnit. Component tests for the
/// causality visualisation arrive in Phase 2; this smoke test settles the project shape now.
/// </summary>
[TestFixture]
public class BunitSmokeTests
{
    [Test]
    public void Render_MinimalComponent_ProducesExpectedMarkup()
    {
        using var context = new BunitContext();

        var cut = context.Render<SmokeComponent>();

        cut.MarkupMatches("<p>bUnit smoke test</p>");
    }

    private sealed class SmokeComponent : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "p");
            builder.AddContent(1, "bUnit smoke test");
            builder.CloseElement();
        }
    }
}
