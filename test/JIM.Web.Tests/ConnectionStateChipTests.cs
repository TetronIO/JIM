// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.IO;
using Bunit;
using JIM.Models.Staging;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="ConnectionStateChip"/>, the one rendering of a derived connection State
/// (#1519, D-S7). The Metaverse Object's Connections tab and the Connector Space list both show it, so a
/// second definition of the label or the colour would let the two pages describe the same object
/// differently; these tests pin the shared component, and the source-shape test below pins the reuse.
/// </summary>
[TestFixture]
public class ConnectionStateChipTests : JimComponentTestContext
{
    private static Array AllStates => Enum.GetValues<ConnectedSystemObjectConnectionState>();

    [TestCaseSource(nameof(AllStates))]
    public void Render_EveryState_CarriesItsLabelAndStateAttribute(ConnectedSystemObjectConnectionState state)
    {
        var cut = Render<ConnectionStateChip>(p => p.Add(c => c.State, state));

        var chip = cut.Find(".jim-connection-state-chip");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.TextContent.Trim(), Is.EqualTo(ConnectionStateChip.GetConnectionStateLabel(state)));
            Assert.That(chip.GetAttribute("data-state"), Is.EqualTo(state.ToString()));
        }
    }

    [Test]
    public void Render_WithExtraClass_KeepsTheChipsOwnClasses()
    {
        var cut = Render<ConnectionStateChip>(p => p
            .Add(c => c.State, ConnectedSystemObjectConnectionState.InSync)
            .Add(c => c.Class, "mt-2"));

        var chip = cut.Find(".jim-connection-state-chip");
        Assert.That(chip.GetAttribute("class"), Does.Contain("mt-2"));
    }

    /// <summary>
    /// The Connector Space list must render the shared chip rather than its own State markup. A source-shape
    /// test for the same reason as <see cref="TypesViewAdministratorGateTests"/>: the page is a virtualised
    /// grid whose render needs data loading and DI wiring no other test here needs, and the failure this
    /// guards (a second, drifting State rendering) is structural. See test/CLAUDE.md.
    /// </summary>
    [Test]
    public void ConnectedSystemObjectList_RendersTheSharedStateChip()
    {
        var dir = NUnit.Framework.TestContext.CurrentContext.TestDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "JIM.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.That(dir, Is.Not.Null, "Could not locate repository root (JIM.sln) from the test directory.");

        var path = Path.Combine(dir!, "src", "JIM.Web", "Pages", "Admin", "ConnectedSystemObjectList.razor");
        Assert.That(File.Exists(path), Is.True, $"Expected to find {path}");

        var source = File.ReadAllText(path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Contain("<ConnectionStateChip State=\"context.Item.State\" />"),
                "the Connector Space list should render the shared chip for each row's derived State");
            Assert.That(source, Does.Not.Contain("jim-connection-state-chip"),
                "the chip's own class belongs to ConnectionStateChip; finding it here means the markup was copied rather than reused");
        }
    }
}
