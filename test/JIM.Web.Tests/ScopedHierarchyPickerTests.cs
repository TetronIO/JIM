// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Staging;
using JIM.Utilities;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Container picker's own behaviour: which rows it shows, what each row says, and that editing one
/// tells the host something changed.
/// </summary>
/// <remarks>
/// The selection rules themselves are <see cref="ContainerSelectionEditor"/>'s and are tested there; these tests
/// are about the control. Assertions are on JIM's own markup and test ids, never on MudBlazor's generated class
/// names, per the suite's standing rule.
/// </remarks>
[TestFixture]
public class ScopedHierarchyPickerTests : JimComponentTestContext
{
    private int _nextId;

    [SetUp]
    public void SetUp() => _nextId = 1;

    [Test]
    public void Picker_RendersAContainerRowPerContainer_NamingTheContainerRatherThanItsDistinguishedName()
    {
        var sales = Container("Sales", "OU=Sales,OU=Corp,DC=example,DC=com");
        var partition = PartitionWith(Container("Corp", "OU=Corp,DC=example,DC=com", children: [sales]));

        var cut = Render(partition);

        var rows = cut.FindAll("[data-testid='jim-picker-row']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Has.Count.EqualTo(2), "a Container with children is expanded through when it holds the only branch");
            Assert.That(cut.Markup, Does.Contain(">Corp<"), "the Container's own name leads the row");
            Assert.That(cut.Markup, Does.Not.Contain(">OU=Corp,DC=example,DC=com<"),
                "the Distinguished Name belongs in the row's tooltip, not printed on every row");
        }
    }

    [Test]
    public void Picker_ReportsHowManyContainersAreSelected()
    {
        var partition = PartitionWith(
            Container("Corp", "OU=Corp,DC=example,DC=com", selected: true),
            Container("Groups", "OU=Groups,DC=example,DC=com"));

        var cut = Render(partition);

        Assert.That(cut.Find("[data-testid='jim-picker-count']").TextContent.Replace(" ", ""), Does.Contain("1of2"));
    }

    [Test]
    public void Picker_ForAContainerCoveredByASubtreeAncestor_SaysWhichAncestorCoversIt()
    {
        // Greying a row out without saying why leaves the administrator with no way to act on it.
        var sales = Container("Sales", "OU=Sales,OU=Corp,DC=example,DC=com");
        var corp = Container("Corp", "OU=Corp,DC=example,DC=com", selected: true, children: [sales]);
        var partition = PartitionWith(corp);
        ContainerSelectionEditor.RecalculateCoverage(partition);

        var cut = Render(partition);

        Assert.That(cut.Find("[data-testid='jim-picker-covered']").TextContent, Does.Contain("Corp"));
    }

    [Test]
    public void Picker_ShowsTheScopeControlOnlyOnSelectedContainers()
    {
        var partition = PartitionWith(
            Container("Corp", "OU=Corp,DC=example,DC=com", selected: true),
            Container("Groups", "OU=Groups,DC=example,DC=com"));

        var cut = Render(partition);

        // Scope means nothing on an unselected Container, so exactly one row carries the control.
        Assert.That(cut.FindAll("[data-testid='jim-picker-scope-onelevel']"), Has.Count.EqualTo(1));
    }

    [Test]
    public void Picker_ChoosingThisLevel_NarrowsTheContainerAndReleasesItsChildren()
    {
        var sales = Container("Sales", "OU=Sales,OU=Corp,DC=example,DC=com");
        var corp = Container("Corp", "OU=Corp,DC=example,DC=com", selected: true, children: [sales]);
        var partition = PartitionWith(corp);
        ContainerSelectionEditor.RecalculateCoverage(partition);
        var cut = Render(partition);

        cut.Find("[data-testid='jim-picker-scope-onelevel']").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.Scope, Is.EqualTo(ConnectedSystemContainerScope.OneLevel));
            Assert.That(sales.Included, Is.False, "a One Level parent no longer covers its children, so they become selectable");
        }
    }

    [Test]
    public void Picker_ChoosingTheScopeAlreadyInEffect_ChangesNothing()
    {
        // The control is two segments, not a toggle: clicking the active one must not flip to the other.
        var corp = Container("Corp", "OU=Corp,DC=example,DC=com", selected: true);
        var partition = PartitionWith(corp);
        var cut = Render(partition);

        cut.Find("[data-testid='jim-picker-scope-subtree']").Click();

        Assert.That(corp.Scope, Is.EqualTo(ConnectedSystemContainerScope.Subtree));
    }

    [Test]
    public void Picker_EditingTheSelection_RaisesOnChanged()
    {
        var partition = PartitionWith(Container("Corp", "OU=Corp,DC=example,DC=com", selected: true));
        var raised = 0;
        var cut = Render(partition, () => raised++);

        cut.Find("[data-testid='jim-picker-scope-onelevel']").Click();

        Assert.That(raised, Is.EqualTo(1), "the host gates its save button and its stale-preview notice on this");
    }

    [Test]
    public void Picker_Clear_DeselectsEveryContainer()
    {
        var partition = PartitionWith(
            Container("Corp", "OU=Corp,DC=example,DC=com", selected: true),
            Container("Groups", "OU=Groups,DC=example,DC=com", selected: true));
        var cut = Render(partition);

        cut.Find("[data-testid='jim-picker-clear']").Click();

        Assert.That(ContainerSelectionEditor.CountSelected(partition), Is.Zero);
    }

    [Test]
    public void Picker_WithAnEmptyPartition_SaysSoRatherThanRenderingNothing()
    {
        var cut = Render(PartitionWith());

        Assert.That(cut.Markup, Does.Contain("holds no Containers"));
    }

    [Test]
    public void Picker_WithALargeHierarchy_OpensOnlyAsFarAsItsSelections()
    {
        // Expanding a couple of hundred OUs on arrival is a wall to scroll rather than a control to use. What must
        // stay visible is the selection, so the administrator can see what the system imports without hunting.
        var selected = Container("Selected", "OU=Selected,OU=Branch00,DC=example,DC=com", selected: true);
        var branches = Enumerable.Range(0, 30)
            .Select(i => Container($"Branch{i:00}", $"OU=Branch{i:00},DC=example,DC=com",
                children: i == 0 ? [selected] : [Container($"Hidden{i:00}", $"OU=Hidden{i:00},OU=Branch{i:00},DC=example,DC=com")]))
            .ToArray();
        var partition = PartitionWith(branches);

        var cut = Render(partition);

        var names = cut.FindAll("[data-testid='jim-picker-row']")
            .Select(r => r.GetAttribute("data-container-name"))
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(names, Does.Contain("Selected"), "the branch holding a selection is opened");
            Assert.That(names, Does.Not.Contain("Hidden01"), "branches with nothing selected stay closed");
        }
    }

    #region Exclusions (#1255)

    [Test]
    public void Picker_ForAContainerCoveredByASelection_OffersToExcludeIt()
    {
        // Exclusion is meaningful on exactly the rows an ancestor already reaches, so the label saying why the row
        // cannot be ticked is where the action belongs.
        var partition = PartitionWith(Container("Corp", "OU=Corp,DC=example,DC=com", selected: true,
            children: [Container("Service Accounts", "OU=Service Accounts,OU=Corp,DC=example,DC=com")]));
        ContainerSelectionEditor.RecalculateCoverage(partition);

        var cut = Render(partition);

        Assert.That(cut.FindAll("[data-testid='jim-picker-exclude']"), Has.Count.EqualTo(1));
    }

    [Test]
    public void Picker_OffersExclusionNowhereElse()
    {
        // A Container nothing reaches, and one selected in its own right, both have nothing to be carved out of.
        var partition = PartitionWith(
            Container("Corp", "OU=Corp,DC=example,DC=com", selected: true),
            Container("Groups", "OU=Groups,DC=example,DC=com"));
        ContainerSelectionEditor.RecalculateCoverage(partition);

        var cut = Render(partition);

        Assert.That(cut.FindAll("[data-testid='jim-picker-exclude']"), Is.Empty);
    }

    [Test]
    public void Picker_BeneathAOneLevelSelection_OffersNoExclusion()
    {
        // A One Level selection reaches no Container beneath it, so an exclusion there would carve out nothing.
        var serviceAccounts = Container("Service Accounts", "OU=Service Accounts,OU=Corp,DC=example,DC=com");
        var corp = Container("Corp", "OU=Corp,DC=example,DC=com", selected: true, children: [serviceAccounts]);
        corp.Scope = ConnectedSystemContainerScope.OneLevel;
        var partition = PartitionWith(corp);
        ContainerSelectionEditor.RecalculateCoverage(partition);

        var cut = Render(partition);

        Assert.That(cut.FindAll("[data-testid='jim-picker-exclude']"), Is.Empty);
    }

    [Test]
    public void Picker_ExcludingAContainer_CarvesItOutAndSaysWhichSelectionItLeft()
    {
        var serviceAccounts = Container("Service Accounts", "OU=Service Accounts,OU=Corp,DC=example,DC=com");
        var partition = PartitionWith(Container("Corp", "OU=Corp,DC=example,DC=com", selected: true, children: [serviceAccounts]));
        ContainerSelectionEditor.RecalculateCoverage(partition);
        var cut = Render(partition);

        cut.Find("[data-testid='jim-picker-exclude']").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serviceAccounts.Excluded, Is.True);
            Assert.That(cut.Find("[data-testid='jim-picker-excluded']").TextContent, Does.Contain("Corp"));
        }
    }

    [Test]
    public void Picker_ForAnExcludedContainer_OffersToIncludeItAgain()
    {
        var serviceAccounts = Container("Service Accounts", "OU=Service Accounts,OU=Corp,DC=example,DC=com", excluded: true);
        var partition = PartitionWith(Container("Corp", "OU=Corp,DC=example,DC=com", selected: true, children: [serviceAccounts]));
        ContainerSelectionEditor.RecalculateCoverage(partition);
        var cut = Render(partition);

        cut.Find("[data-testid='jim-picker-include']").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serviceAccounts.Excluded, Is.False);
            Assert.That(serviceAccounts.Selected, Is.False, "including hands the Container back to its ancestors rather than selecting it");
            Assert.That(serviceAccounts.Included, Is.True, "Corp's selection reaches it again");
        }
    }

    [Test]
    public void Picker_ForAnExcludedContainer_DoesNotAlsoOfferItsTickBox()
    {
        // Ticking would clear the exclusion too, by a second route with a different meaning. One row, one action.
        var serviceAccounts = Container("Service Accounts", "OU=Service Accounts,OU=Corp,DC=example,DC=com", excluded: true);
        var partition = PartitionWith(Container("Corp", "OU=Corp,DC=example,DC=com", selected: true, children: [serviceAccounts]));
        ContainerSelectionEditor.RecalculateCoverage(partition);

        var cut = Render(partition);

        var row = cut.FindAll("[data-testid='jim-picker-row']")
            .Single(r => r.GetAttribute("data-container-name") == "Service Accounts");
        Assert.That(row.QuerySelector("input[type=checkbox]")!.HasAttribute("disabled"), Is.True);
    }

    [Test]
    public void Picker_ForAContainerInsideAnExclusion_SaysWhichExclusionCarvedItOutAndStillOffersItsTickBox()
    {
        // Rendered as plain and unselected it would read as "nothing has been decided here", when something has.
        // Ticking it is meaningful: it brings the branch back into scope.
        var app1 = Container("App1", "OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=com");
        var serviceAccounts = Container("Service Accounts", "OU=Service Accounts,OU=Corp,DC=example,DC=com",
            excluded: true, children: [app1]);
        var partition = PartitionWith(Container("Corp", "OU=Corp,DC=example,DC=com", selected: true, children: [serviceAccounts]));
        ContainerSelectionEditor.RecalculateCoverage(partition);

        var cut = Render(partition);

        var row = cut.FindAll("[data-testid='jim-picker-row']")
            .Single(r => r.GetAttribute("data-container-name") == "App1");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.QuerySelector("[data-testid='jim-picker-excluded-by-ancestor']")!.TextContent,
                Does.Contain("Service Accounts"));
            Assert.That(row.QuerySelector("input[type=checkbox]")!.HasAttribute("disabled"), Is.False);
        }
    }

    [Test]
    public void Picker_SelectingAContainerInsideAnExclusion_BringsThatBranchBackIntoScope()
    {
        var app1 = Container("App1", "OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=com");
        var serviceAccounts = Container("Service Accounts", "OU=Service Accounts,OU=Corp,DC=example,DC=com",
            excluded: true, children: [app1]);
        var partition = PartitionWith(Container("Corp", "OU=Corp,DC=example,DC=com", selected: true, children: [serviceAccounts]));
        ContainerSelectionEditor.RecalculateCoverage(partition);
        var cut = Render(partition);

        var row = cut.FindAll("[data-testid='jim-picker-row']")
            .Single(r => r.GetAttribute("data-container-name") == "App1");
        row.QuerySelector("input[type=checkbox]")!.Change(true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(app1.Selected, Is.True);
            Assert.That(serviceAccounts.Excluded, Is.True, "the surrounding exclusion still stands; only this branch came back");
        }
    }

    [Test]
    public void Picker_ExcludingAContainer_RaisesOnChanged()
    {
        var partition = PartitionWith(Container("Corp", "OU=Corp,DC=example,DC=com", selected: true,
            children: [Container("Service Accounts", "OU=Service Accounts,OU=Corp,DC=example,DC=com")]));
        ContainerSelectionEditor.RecalculateCoverage(partition);
        var raised = 0;
        var cut = Render(partition, () => raised++);

        cut.Find("[data-testid='jim-picker-exclude']").Click();

        Assert.That(raised, Is.EqualTo(1), "the host gates its save button and its stale-preview notice on this");
    }

    #endregion

    #region Helpers

    private IRenderedComponent<ScopedHierarchyPicker> Render(ConnectedSystemPartition partition, Action? onChanged = null) =>
        Render<ScopedHierarchyPicker>(p => p
            .Add(c => c.Partition, partition)
            .Add(c => c.OnChanged, () => onChanged?.Invoke()));

    private ConnectedSystemContainer Container(
        string name,
        string externalId,
        bool selected = false,
        bool excluded = false,
        IEnumerable<ConnectedSystemContainer>? children = null)
    {
        var container = new ConnectedSystemContainer
        {
            Id = _nextId++,
            Name = name,
            ExternalId = externalId,
            Selected = selected,
            Excluded = excluded
        };

        foreach (var child in children ?? [])
            container.AddChildContainer(child);

        return container;
    }

    private ConnectedSystemPartition PartitionWith(params ConnectedSystemContainer[] rootContainers)
    {
        var partition = new ConnectedSystemPartition
        {
            Id = _nextId++,
            Name = "DC=example,DC=com",
            Selected = true,
            Containers = [.. rootContainers]
        };

        foreach (var rootContainer in rootContainers)
            rootContainer.Partition = partition;

        return partition;
    }

    #endregion
}
