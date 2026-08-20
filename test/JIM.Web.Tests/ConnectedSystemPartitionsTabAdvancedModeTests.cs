// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Utilities;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers Advanced Mode on the Partitions and Containers tab: the same Container Scope, edited as text.
/// </summary>
/// <remarks>
/// What the text means is <see cref="ContainerScopeText"/>'s and is tested there. These are about the two things
/// only the tab decides, both of which lose an administrator's work if they are wrong: that switching into
/// Advanced shows the scope currently in force rather than an empty box, and that switching back out applies what
/// they wrote rather than discarding it.
/// </remarks>
[TestFixture]
public class ConnectedSystemPartitionsTabAdvancedModeTests : JimComponentTestContext
{
    public ConnectedSystemPartitionsTabAdvancedModeTests()
    {
        // Registered in the constructor rather than a SetUp: bUnit builds its service provider on the first
        // render and refuses registrations after that.
        Services.AddSingleton<IJimApplicationFactory>(new UnusedJimApplicationFactory());
        Services.AddSingleton<IConfigurationChangePreviewStarter>(new UnusedPreviewStarter());
    }

    /// <summary>
    /// Nothing here previews; the tab injects the starter, so it has to be resolvable, and this refuses rather
    /// than returning an id nothing produced.
    /// </summary>
    private sealed class UnusedPreviewStarter : IConfigurationChangePreviewStarter
    {
        public Task<Guid?> StartAsync(ConfigurationChangePreviewRequest request) =>
            throw new InvalidOperationException("The Partitions tab started a preview while only editing the in-memory selection.");
    }

    /// <summary>
    /// Nothing in these tests saves, so reaching the application layer means the tab did something it should not
    /// have; this throws rather than quietly returning something unusable.
    /// </summary>
    private sealed class UnusedJimApplicationFactory : IJimApplicationFactory
    {
        public JimApplication Create() =>
            throw new InvalidOperationException("The Partitions tab reached the application layer while only editing the in-memory selection.");
    }

    [Test]
    public void SwitchingToAdvanced_ShowsTheScopeCurrentlyInForce()
    {
        var connectedSystem = ConnectedSystemWithHierarchy();
        Container(connectedSystem, "Corp").Selected = true;
        Container(connectedSystem, "Service Accounts").Excluded = true;

        var cut = Render(connectedSystem);
        SwitchToAdvanced(cut);

        Assert.That(cut.Find("textarea[data-testid='jim-scope-text']").GetAttribute("value"), Is.EqualTo(
            """
            include OU=Corp,DC=example,DC=com
            exclude OU=Service Accounts,OU=Corp,DC=example,DC=com
            """));
    }

    [Test]
    public void ApplyingText_EditsTheSelectionTheTreeShows()
    {
        var connectedSystem = ConnectedSystemWithHierarchy();
        var cut = Render(connectedSystem);
        SwitchToAdvanced(cut);

        SetText(cut, "include OU=Corp,DC=example,DC=com");
        cut.Find("[data-testid='jim-scope-text-apply']").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Container(connectedSystem, "Corp").Selected, Is.True);
            Assert.That(cut.FindAll("[data-testid='jim-scope-text-errors']"), Is.Empty);
            Assert.That(cut.Find("[data-testid='jim-scope-pending']").TextContent, Does.Contain("Unsaved changes"),
                "an applied text is an edit to what this system imports, and has to be saved like any other");
        }
    }

    [Test]
    public void ApplyingTextThatNamesNoContainer_ChangesNothingAndSaysWhichLine()
    {
        var connectedSystem = ConnectedSystemWithHierarchy();
        var cut = Render(connectedSystem);
        SwitchToAdvanced(cut);

        SetText(cut,
            """
            include OU=Corp,DC=example,DC=com
            exclude OU=Contractors,OU=Corp,DC=example,DC=com
            """);
        cut.Find("[data-testid='jim-scope-text-apply']").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Container(connectedSystem, "Corp").Selected, Is.False,
                "a text that cannot be applied in full is not applied at all");
            Assert.That(cut.Find("[data-testid='jim-scope-text-errors']").TextContent, Does.Contain("Line 2"));
        }
    }

    [Test]
    public void LeavingAdvancedMode_AppliesTheTextRatherThanDiscardingIt()
    {
        var connectedSystem = ConnectedSystemWithHierarchy();
        var cut = Render(connectedSystem);
        SwitchToAdvanced(cut);

        SetText(cut, "include OU=Corp,DC=example,DC=com");
        SwitchToSimple(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Container(connectedSystem, "Corp").Selected, Is.True,
                "the text is the pending edit; dropping it on a click of Simple would lose work with nothing said");
            Assert.That(cut.FindAll("textarea[data-testid='jim-scope-text']"), Is.Empty, "the tree is showing again");
        }
    }

    [Test]
    public void LeavingAdvancedModeWithTextThatCannotBeApplied_StaysPutAndShowsWhy()
    {
        var connectedSystem = ConnectedSystemWithHierarchy();
        var cut = Render(connectedSystem);
        SwitchToAdvanced(cut);

        SetText(cut, "include OU=Contractors,DC=example,DC=com");
        SwitchToSimple(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("textarea[data-testid='jim-scope-text']"), Is.Not.Empty,
                "switching away would leave the administrator no way back to the text they wrote");
            Assert.That(cut.Find("[data-testid='jim-scope-text-errors']").TextContent, Does.Contain("Line 1"));
        }
    }

    #region Helpers

    private IRenderedComponent<ConnectedSystemPartitionsTab> Render(ConnectedSystem connectedSystem) =>
        Render<ConnectedSystemPartitionsTab>(parameters => parameters
            .Add(p => p.ConnectedSystem, connectedSystem)
            .Add(p => p.PartitionsAndHierarchiesText, "partitions and containers")
            .Add(p => p.PartitionAndHierarchyText, "partition and container"));

    private static void SwitchToAdvanced(IRenderedComponent<ConnectedSystemPartitionsTab> cut) =>
        ToggleItem(cut, "Advanced").Click();

    private static void SwitchToSimple(IRenderedComponent<ConnectedSystemPartitionsTab> cut) =>
        ToggleItem(cut, "Simple").Click();

    private static AngleSharp.Dom.IElement ToggleItem(IRenderedComponent<ConnectedSystemPartitionsTab> cut, string text) =>
        cut.FindAll("[data-testid='jim-scope-mode'] .mud-toggle-item")
            .Single(item => item.TextContent.Contains(text, StringComparison.Ordinal));

    private static void SetText(IRenderedComponent<ConnectedSystemPartitionsTab> cut, string text) =>
        // Immediate="true", so the field commits on input rather than on blur.
        cut.Find("textarea[data-testid='jim-scope-text']").Input(text);

    private static ConnectedSystemContainer Container(ConnectedSystem connectedSystem, string name) =>
        ContainerSelectionEditor.Flatten(connectedSystem.Partitions![0]).Single(c => c.Name == name);

    /// <summary>
    /// DC=example,DC=com holding OU=Corp with OU=Service Accounts beneath it: enough to express a carve-out.
    /// </summary>
    private static ConnectedSystem ConnectedSystemWithHierarchy()
    {
        var serviceAccounts = new ConnectedSystemContainer
        {
            Id = 22, Name = "Service Accounts", ExternalId = "OU=Service Accounts,OU=Corp,DC=example,DC=com"
        };
        var corp = new ConnectedSystemContainer { Id = 21, Name = "Corp", ExternalId = "OU=Corp,DC=example,DC=com" };
        corp.AddChildContainer(serviceAccounts);

        var partition = new ConnectedSystemPartition
        {
            Id = 11, Name = "DC=example,DC=com", Selected = true, Containers = [corp]
        };
        corp.Partition = partition;

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Directory",
            Partitions = [partition],
            ConnectorDefinition = new ConnectorDefinition
            {
                Id = 1, Name = "JIM LDAP Connector", SupportsPartitions = true, SupportsPartitionContainers = true
            }
        };
    }

    #endregion
}
