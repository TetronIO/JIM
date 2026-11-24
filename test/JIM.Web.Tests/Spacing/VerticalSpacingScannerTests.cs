// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using NUnit.Framework;

namespace JIM.Web.Tests.Spacing;

/// <summary>
/// Tests for <see cref="VerticalSpacingScanner"/> (#3095): what counts as a hand-placed vertical margin in Razor markup.
/// </summary>
[TestFixture]
public class VerticalSpacingScannerTests
{
    [TestCase("<MudPaper Class=\"pa-4 mt-6\">", "mt-6")]
    [TestCase("<MudText Class=\"mb-2\">", "mb-2")]
    [TestCase("<div class=\"my-3 d-flex\">", "my-3")]
    [TestCase("<MudPaper Class=\"ma-4\">", "ma-4")]
    [TestCase("<MudStack Class=\"mt-n2\">", "mt-n2")]
    [TestCase("<VirtualisedDataGrid GridClass=\"mt-5\">", "mt-5")]
    public void Scan_VerticalMarginUtility_IsFound(string markup, string expected)
    {
        Assert.That(VerticalSpacingScanner.Scan(markup).Select(f => f.Token), Is.EqualTo(new[] { expected }));
    }

    [TestCase("<MudPaper Class=\"pa-4 ml-2 mx-3 pt-6\">")]
    [TestCase("<MudPaper Class=\"mt-0 ma-0 mb-auto\">")]
    [TestCase("<div class=\"jim-mt-4-like\">")]
    [TestCase("<div class=\"format-mt-4\">")]
    public void Scan_HorizontalPaddingZeroOrNonUtility_IsNotFound(string markup)
    {
        Assert.That(VerticalSpacingScanner.Scan(markup), Is.Empty);
    }

    [Test]
    public void Scan_RazorComment_IsIgnoredAndLineNumbersStayTrue()
    {
        var markup = "@* mt-6 is described here\n   across two lines mb-2 *@\n<MudText Class=\"mt-4\" />";

        var findings = VerticalSpacingScanner.Scan(markup);

        Assert.That(findings.Select(f => (f.Line, f.Token)), Is.EqualTo(new[] { (3, "mt-4") }));
    }

    [Test]
    public void Scan_ExemptionOnTheLineOrTwoAbove_IsNotFound()
    {
        // Line 3 is exempted by the marker two lines above, line 6 by its own; line 9 has none within reach.
        var markup = "@* spacing: exempt - a specimen of every margin *@\n\n<MudText Class=\"mt-4\" />\n\n\n" +
                     "<MudText Class=\"mb-1\" /> @* spacing: exempt - x *@\n\n\n<MudText Class=\"mt-2\" />";

        var findings = VerticalSpacingScanner.Scan(markup);

        Assert.That(findings.Select(f => f.Token), Is.EqualTo(new[] { "mt-2" }));
    }
}
