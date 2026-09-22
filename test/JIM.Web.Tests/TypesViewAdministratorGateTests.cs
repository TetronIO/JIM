// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.IO;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Source-shape test over <c>Types/View.razor</c> for the Metaverse Object detail page's two Sync Preview
/// affordances (#1519, D-S6): the Connections tab and its Preview Outbound
/// Synchronisation card must each sit inside an <c>AuthorizeView Roles="Administrator"</c> gate, as
/// the Password tab already does (#1172).
/// <para>
/// A source-shape test rather than a bUnit render: bUnit's <c>AuthorizeView</c> support needs a
/// configured <c>AuthenticationStateProvider</c> and cascading authentication state, which the page
/// otherwise pulls in from routing and DI wiring this page does not need for any other test here; the
/// markup nesting is unambiguous to check textually, and the failure mode this guards (a preview
/// affordance added outside the gate) is exactly a structural one. See test/CLAUDE.md > "Convention
/// sweeps live here too".
/// </para>
/// </summary>
[TestFixture]
public class TypesViewAdministratorGateTests
{
    private static string ReadViewRazorSource()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "JIM.Web", "Pages", "Types", "View.razor");
        Assert.That(File.Exists(path), Is.True, $"Expected to find {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "JIM.sln")))
            dir = Directory.GetParent(dir)?.FullName;

        Assert.That(dir, Is.Not.Null, "Could not locate repository root (JIM.sln) from the test directory.");
        return dir!;
    }

    /// <summary>
    /// True when <paramref name="marker"/> appears inside an
    /// <c>&lt;AuthorizeView Roles="Administrator"&gt;...&lt;/AuthorizeView&gt;</c> span: the nearest
    /// preceding gate opening has no intervening close before the marker.
    /// </summary>
    private static bool IsInsideAdministratorGate(string source, string marker)
    {
        var markerIndex = source.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.That(markerIndex, Is.GreaterThanOrEqualTo(0), $"Expected to find '{marker}' in View.razor.");

        var gateIndex = source.LastIndexOf("<AuthorizeView Roles=\"Administrator\"", markerIndex, System.StringComparison.Ordinal);
        if (gateIndex < 0)
            return false;

        var closeIndex = source.IndexOf("</AuthorizeView>", gateIndex, System.StringComparison.Ordinal);
        return closeIndex > markerIndex;
    }

    [Test]
    public void ConnectionsTab_SitsInsideAnAdministratorAuthorizeView()
    {
        var source = ReadViewRazorSource();

        Assert.That(IsInsideAdministratorGate(source, "Text=\"Connections\""), Is.True,
            "The Connections tab must be gated by <AuthorizeView Roles=\"Administrator\">.");
    }

    [Test]
    public void PreviewOutboundAction_SitsInsideAnAdministratorAuthorizeView()
    {
        var source = ReadViewRazorSource();

        Assert.That(IsInsideAdministratorGate(source, "data-testid=\"jim-mvo-preview-outbound\""), Is.True,
            "The Preview Outbound Synchronisation action must be gated by <AuthorizeView Roles=\"Administrator\">.");
    }

    [Test]
    public void ConnectionPreviewButton_SitsInsideAnAdministratorAuthorizeView()
    {
        var source = ReadViewRazorSource();

        Assert.That(IsInsideAdministratorGate(source, "MetaverseObjectConnectionsTable"), Is.True,
            "The Connections tab's table must be gated by <AuthorizeView Roles=\"Administrator\">.");
    }

    [Test]
    public void ConnectionsTab_CarriesTheConnectorCountBadge()
    {
        var source = ReadViewRazorSource();

        var tabIndex = source.IndexOf("Text=\"Connections\"", System.StringComparison.Ordinal);
        Assert.That(tabIndex, Is.GreaterThanOrEqualTo(0));
        var tabDeclaration = source.Substring(tabIndex, 260);
        Assert.That(tabDeclaration, Does.Contain("BadgeData=\"@(_connectorCount"),
            "The Connections tab badges how many Connected System Objects are joined, as the Changes tab badges its count.");
    }
}
