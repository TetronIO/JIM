// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Utilities.Tests;

/// <summary>
/// Covers the rules that decide what a Container tree looks like after an administrator ticks a box or changes a
/// Container's scope. These lived in the Partitions and Containers tab's <c>@code</c> block, where nothing could
/// reach them: the cascade down a branch, the roll-up to a parent, the partition auto-selection and the coverage
/// recalculation were all untested, and each of them can silently take objects out of import scope.
/// </summary>
[TestFixture]
public class ContainerSelectionEditorTests
{
    private static int _nextId;

    [SetUp]
    public void SetUp() => _nextId = 1;

    #region Selecting and deselecting

    [Test]
    public void ToggleSelected_OnAnUnselectedContainer_SelectsIt()
    {
        var partition = PartitionWith(Container("Corp"));
        var corp = Root(partition, "Corp");

        ContainerSelectionEditor.ToggleSelected(corp);

        Assert.That(corp.Selected, Is.True);
    }

    [Test]
    public void ToggleSelected_OnASelectedContainer_DeselectsIt()
    {
        var partition = PartitionWith(Container("Corp", selected: true));
        var corp = Root(partition, "Corp");

        ContainerSelectionEditor.ToggleSelected(corp);

        Assert.That(corp.Selected, Is.False);
    }

    [Test]
    public void ToggleSelected_OnASubtreeContainer_CoversItsDescendantsAndClearsTheirSelections()
    {
        var sales = Container("Sales", selected: true);
        var corp = Container("Corp", children: [sales]);
        var partition = PartitionWith(corp);

        ContainerSelectionEditor.ToggleSelected(corp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.Selected, Is.True);
            Assert.That(sales.Selected, Is.False, "a Subtree parent's search already returns the child, so a separate selection would import it twice");
            Assert.That(sales.Included, Is.True);
        }
        Assert.That(partition.Selected, Is.True);
    }

    [Test]
    public void ToggleSelected_OnAOneLevelContainer_LeavesItsDescendantsSelectable()
    {
        var sales = Container("Sales");
        var corp = Container("Corp", scope: ConnectedSystemContainerScope.OneLevel, children: [sales]);
        var partition = PartitionWith(corp);

        ContainerSelectionEditor.ToggleSelected(corp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.Selected, Is.True);
            Assert.That(sales.Included, Is.False, "a One Level parent's search stops short of its children, so they stay selectable in their own right");
        }
        Assert.That(partition.Selected, Is.True);
    }

    [Test]
    public void ToggleSelected_OnAnIncludedContainer_ClearsBothFlagsRatherThanSelectingIt()
    {
        // A covered Container is shown ticked-through; clicking it means "stop covering me", not "select me".
        var sales = Container("Sales");
        var corp = Container("Corp", selected: true, children: [sales]);
        var partition = PartitionWith(corp);
        ContainerSelectionEditor.RecalculateCoverage(partition);
        Assert.That(sales.Included, Is.True, "arrangement check: the child starts covered");

        ContainerSelectionEditor.ToggleSelected(sales);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sales.Selected, Is.False);
            Assert.That(sales.Included, Is.False);
        }
    }

    [Test]
    public void ToggleSelected_SelectingTheLastSubtreeSibling_RollsTheSelectionUpToTheParent()
    {
        var sales = Container("Sales", selected: true);
        var support = Container("Support");
        var corp = Container("Corp", children: [sales, support]);
        var partition = PartitionWith(corp);

        ContainerSelectionEditor.ToggleSelected(support);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.Selected, Is.True, "every child selected as a whole subtree is the same statement as selecting the parent");
            Assert.That(sales.Selected, Is.False);
            Assert.That(support.Selected, Is.False);
            Assert.That(sales.Included, Is.True);
            Assert.That(support.Included, Is.True);
        }
    }

    [Test]
    public void ToggleSelected_SelectingTheLastSibling_DoesNotRollUpOverANarrowedSibling()
    {
        // Rolling up would replace the sibling's deliberate One Level narrowing with one Subtree selection on the
        // parent, silently widening scope to everything beneath the narrowed Container.
        var sales = Container("Sales", selected: true, scope: ConnectedSystemContainerScope.OneLevel);
        var support = Container("Support");
        var corp = Container("Corp", children: [sales, support]);
        var partition = PartitionWith(corp);

        ContainerSelectionEditor.ToggleSelected(support);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.Selected, Is.False);
            Assert.That(sales.Selected, Is.True);
            Assert.That(sales.Scope, Is.EqualTo(ConnectedSystemContainerScope.OneLevel));
            Assert.That(support.Selected, Is.True);
        }
    }

    [Test]
    public void ToggleSelected_SelectingAContainer_SelectsItsPartition()
    {
        var sales = Container("Sales");
        var corp = Container("Corp", children: [sales]);
        var partition = PartitionWith(corp);
        partition.Selected = false;

        ContainerSelectionEditor.ToggleSelected(sales);

        Assert.That(partition.Selected, Is.True, "a Container cannot be in scope while its partition is not");
    }

    #endregion

    #region Scope

    [Test]
    public void ToggleScope_OnASubtreeContainer_NarrowsItAndReleasesItsDescendants()
    {
        var sales = Container("Sales");
        var corp = Container("Corp", selected: true, children: [sales]);
        var partition = PartitionWith(corp);
        ContainerSelectionEditor.RecalculateCoverage(partition);

        ContainerSelectionEditor.ToggleScope(corp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.Scope, Is.EqualTo(ConnectedSystemContainerScope.OneLevel));
            Assert.That(sales.Included, Is.False, "narrowing releases the Containers beneath, which become selectable in their own right");
        }
    }

    [Test]
    public void ToggleScope_OnAOneLevelContainer_WidensItAndCoversItsDescendantsAgain()
    {
        var sales = Container("Sales");
        var corp = Container("Corp", selected: true, scope: ConnectedSystemContainerScope.OneLevel, children: [sales]);
        var partition = PartitionWith(corp);
        ContainerSelectionEditor.RecalculateCoverage(partition);

        ContainerSelectionEditor.ToggleScope(corp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.Scope, Is.EqualTo(ConnectedSystemContainerScope.Subtree));
            Assert.That(sales.Included, Is.True);
        }
    }

    [Test]
    public void ToggleScope_OnANestedContainer_RecalculatesFromThePartitionRatherThanTheContainer()
    {
        // A nested Container carries no Partition of its own; it reaches one through its ancestors. Getting this
        // wrong leaves the tree showing one scope while the import performs another.
        var region = Container("Region");
        var sales = Container("Sales", selected: true, children: [region]);
        var corp = Container("Corp", children: [sales]);
        var partition = PartitionWith(corp);
        ContainerSelectionEditor.RecalculateCoverage(partition);
        Assert.That(region.Included, Is.True, "arrangement check: the grandchild starts covered by Sales");

        ContainerSelectionEditor.ToggleScope(sales);

        Assert.That(region.Included, Is.False);
    }

    #endregion

    #region Coverage

    [Test]
    public void RecalculateCoverage_MarksOnlyDescendantsOfASubtreeSelection()
    {
        var deep = Container("Deep");
        var sales = Container("Sales", children: [deep]);
        var corp = Container("Corp", selected: true, children: [sales]);
        var groups = Container("Groups");
        var partition = PartitionWith(corp, groups);

        ContainerSelectionEditor.RecalculateCoverage(partition);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.Included, Is.False, "the selected Container itself is not covered by anything");
            Assert.That(sales.Included, Is.True);
            Assert.That(deep.Included, Is.True, "coverage reaches every level beneath a Subtree selection");
            Assert.That(groups.Included, Is.False, "an unrelated branch is untouched");
        }
    }

    #endregion

    #region Counting

    [Test]
    public void CountSelected_CountsSelectedContainersAtEveryDepth()
    {
        var region = Container("Region", selected: true);
        var sales = Container("Sales", scope: ConnectedSystemContainerScope.OneLevel, children: [region]);
        var corp = Container("Corp", selected: true, scope: ConnectedSystemContainerScope.OneLevel, children: [sales]);
        var partition = PartitionWith(corp, Container("Groups"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ContainerSelectionEditor.CountSelected(partition), Is.EqualTo(2));
            Assert.That(ContainerSelectionEditor.CountAll(partition), Is.EqualTo(4));
        }
    }

    [Test]
    public void ClearSelection_DeselectsEveryContainerAndClearsCoverage()
    {
        var sales = Container("Sales");
        var corp = Container("Corp", selected: true, children: [sales]);
        var partition = PartitionWith(corp);
        ContainerSelectionEditor.RecalculateCoverage(partition);

        ContainerSelectionEditor.ClearSelection(partition);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ContainerSelectionEditor.CountSelected(partition), Is.Zero);
            Assert.That(sales.Included, Is.False, "nothing is covered once nothing is selected");
        }
    }

    #endregion

    #region Filtering

    [Test]
    public void MatchesFilter_WithNoFilter_ShowsEveryContainer()
    {
        Assert.That(ContainerSelectionEditor.MatchesFilter(Container("Corp"), "  "), Is.True);
    }

    [Test]
    public void MatchesFilter_MatchesTheContainersOwnNameCaseInsensitively()
    {
        Assert.That(ContainerSelectionEditor.MatchesFilter(Container("Payroll"), "pay"), Is.True);
    }

    [Test]
    public void MatchesFilter_MatchesTheExternalId_SoAPastedDistinguishedNameFindsItsContainer()
    {
        var container = Container("Payroll");
        container.ExternalId = "OU=Payroll,OU=Finance,DC=example,DC=com";

        Assert.That(ContainerSelectionEditor.MatchesFilter(container, "OU=Finance"), Is.True);
    }

    [Test]
    public void MatchesFilter_ShowsAnAncestorOfAMatch_SoTheMatchIsNotOrphanedFromItsBranch()
    {
        var payroll = Container("Payroll");
        var finance = Container("Finance", children: [payroll]);

        Assert.That(ContainerSelectionEditor.MatchesFilter(finance, "Payroll"), Is.True);
    }

    [Test]
    public void MatchesFilter_HidesABranchWithNoMatchAnywhereInIt()
    {
        var groups = Container("Groups", children: [Container("Distribution")]);

        Assert.That(ContainerSelectionEditor.MatchesFilter(groups, "Payroll"), Is.False);
    }

    #endregion

    #region Helpers

    private static ConnectedSystemContainer Container(
        string name,
        bool selected = false,
        ConnectedSystemContainerScope scope = ConnectedSystemContainerScope.Subtree,
        IEnumerable<ConnectedSystemContainer>? children = null)
    {
        var container = new ConnectedSystemContainer
        {
            Id = _nextId++,
            Name = name,
            ExternalId = $"OU={name}",
            Selected = selected,
            Scope = scope
        };

        foreach (var child in children ?? [])
            container.AddChildContainer(child);

        return container;
    }

    private static ConnectedSystemPartition PartitionWith(params ConnectedSystemContainer[] rootContainers)
    {
        var partition = new ConnectedSystemPartition
        {
            Id = _nextId++,
            Name = "DC=example,DC=com",
            Selected = true,
            Containers = [.. rootContainers]
        };

        // Only root Containers carry the Partition back-reference, matching how the hierarchy is persisted.
        foreach (var rootContainer in rootContainers)
            rootContainer.Partition = partition;

        return partition;
    }

    private static ConnectedSystemContainer Root(ConnectedSystemPartition partition, string name) =>
        partition.Containers!.Single(c => c.Name == name);

    #endregion
}
