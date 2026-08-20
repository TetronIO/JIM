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
    private int _nextId;

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
    public void ToggleSelected_OnAnIncludedContainer_LeavesItCoveredRatherThanSelectingIt()
    {
        // Ticking a covered Container would only restate what the ancestor above already says, so it does not
        // select it. Nor does it stop the coverage: Corp's search still returns Sales, and coverage is derived from
        // the selections rather than set by hand, so claiming otherwise would be a state the next recalculation
        // contradicts. Carving the Container out is what an administrator wants here, and Exclude is that action.
        var sales = Container("Sales");
        var corp = Container("Corp", selected: true, children: [sales]);
        var partition = PartitionWith(corp);
        ContainerSelectionEditor.RecalculateCoverage(partition);
        Assert.That(sales.Included, Is.True, "arrangement check: the child starts covered");

        ContainerSelectionEditor.ToggleSelected(sales);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sales.Selected, Is.False);
            Assert.That(sales.Included, Is.True);
            Assert.That(corp.Selected, Is.True, "the ancestor's own selection is untouched");
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
    public void CountExcluded_CountsExcludedContainersAtEveryDepth()
    {
        // The summary above the tree answers "what does this system import?", and a carve-out changes that answer.
        var partition = PartitionWith(Container("Corp", selected: true, children:
        [
            Container("Service Accounts", excluded: true, children: [Container("App1", excluded: true)]),
            Container("Sales")
        ]));

        Assert.That(ContainerSelectionEditor.CountExcluded(partition), Is.EqualTo(2));
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

    #region Excluding and re-including (#1255)

    [Test]
    public void ToggleExcluded_OnAContainerCoveredByASelectedAncestor_ExcludesIt()
    {
        var partition = PartitionWith(Container("Corp", selected: true, children: [Container("Service Accounts")]));
        ContainerSelectionEditor.RecalculateCoverage(partition);
        var serviceAccounts = Child(partition, "Corp", "Service Accounts");

        ContainerSelectionEditor.ToggleExcluded(serviceAccounts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serviceAccounts.Excluded, Is.True);
            Assert.That(serviceAccounts.Selected, Is.False);
            Assert.That(serviceAccounts.Included, Is.False, "an excluded Container is no longer covered by the ancestor it was carved out of");
        }
    }

    [Test]
    public void ToggleExcluded_OnAnExcludedContainer_ClearsTheExclusion()
    {
        var partition = PartitionWith(Container("Corp", selected: true, children: [Container("Service Accounts", excluded: true)]));
        var serviceAccounts = Child(partition, "Corp", "Service Accounts");

        ContainerSelectionEditor.ToggleExcluded(serviceAccounts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serviceAccounts.Excluded, Is.False);
            Assert.That(serviceAccounts.Included, Is.True, "clearing the exclusion hands the Container back to the ancestor covering it");
        }
    }

    [Test]
    public void ToggleExcluded_OnASelectedContainer_ClearsTheSelection()
    {
        // The two statements are mutually exclusive: a Container cannot say both "manage this" and "do not".
        var partition = PartitionWith(Container("Corp", selected: true));
        var corp = Root(partition, "Corp");

        ContainerSelectionEditor.ToggleExcluded(corp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.Excluded, Is.True);
            Assert.That(corp.Selected, Is.False);
        }
    }

    [Test]
    public void ToggleSelected_OnAnExcludedContainer_ClearsTheExclusion()
    {
        var partition = PartitionWith(Container("Corp", selected: true, children:
        [
            Container("Service Accounts", excluded: true),
            Container("People")
        ]));
        ContainerSelectionEditor.RecalculateCoverage(partition);
        var serviceAccounts = Child(partition, "Corp", "Service Accounts");

        ContainerSelectionEditor.ToggleSelected(serviceAccounts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serviceAccounts.Selected, Is.True);
            Assert.That(serviceAccounts.Excluded, Is.False);
        }
    }

    [Test]
    public void ToggleSelected_OnAContainerBeneathAnExcludedAncestor_SelectsIt()
    {
        // Re-inclusion: ticking a Container an exclusion has carved out is a meaningful statement, unlike ticking
        // one a selection already covers.
        var partition = PartitionWith(Container("Corp", selected: true, children:
        [
            Container("Service Accounts", excluded: true, children: [Container("App1"), Container("App2")]),
            Container("People")
        ]));
        ContainerSelectionEditor.RecalculateCoverage(partition);
        var app1 = Child(Child(partition, "Corp", "Service Accounts"), "App1");

        ContainerSelectionEditor.ToggleSelected(app1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(app1.Selected, Is.True);
            Assert.That(app1.ExcludedByAncestor, Is.False);
        }
    }

    [Test]
    public void ToggleSelected_CompletingTheSelectionInsideAnExcludedBranch_DoesNotRollUpOntoTheExclusion()
    {
        // Rolling up would replace the re-inclusions with a selection on the excluded Container itself, which both
        // breaks the one-statement-per-Container rule and silently undoes the exclusion.
        var partition = PartitionWith(Container("Corp", selected: true, children:
        [
            Container("Service Accounts", excluded: true, children: [Container("App1", selected: true), Container("App2")])
        ]));
        ContainerSelectionEditor.RecalculateCoverage(partition);
        var serviceAccounts = Child(partition, "Corp", "Service Accounts");
        var app2 = Child(serviceAccounts, "App2");

        ContainerSelectionEditor.ToggleSelected(app2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serviceAccounts.Excluded, Is.True);
            Assert.That(serviceAccounts.Selected, Is.False);
            Assert.That(Child(serviceAccounts, "App1").Selected, Is.True);
            Assert.That(app2.Selected, Is.True);
        }
    }

    [Test]
    public void ClearSelection_WithAnExcludedContainer_ClearsTheExclusionToo()
    {
        var partition = PartitionWith(Container("Corp", selected: true, children: [Container("Service Accounts", excluded: true)]));

        ContainerSelectionEditor.ClearSelection(partition);

        Assert.That(ContainerSelectionEditor.Flatten(partition).Any(c => c.Excluded), Is.False);
    }

    [Test]
    public void RecalculateCoverage_WithAnExcludedBranch_MarksItsDescendantsExcludedByAncestor()
    {
        var partition = PartitionWith(Container("Corp", selected: true, children:
        [
            Container("Service Accounts", excluded: true, children: [Container("App1")])
        ]));

        ContainerSelectionEditor.RecalculateCoverage(partition);

        var serviceAccounts = Child(partition, "Corp", "Service Accounts");
        var app1 = Child(serviceAccounts, "App1");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(serviceAccounts.Included, Is.False);
            Assert.That(serviceAccounts.ExcludedByAncestor, Is.False, "Service Accounts states its own exclusion; nothing above it did");
            Assert.That(app1.Included, Is.False, "the exclusion is nearer than Corp's selection, so Corp no longer covers App1");
            Assert.That(app1.ExcludedByAncestor, Is.True);
        }
    }

    [Test]
    public void RecalculateCoverage_WithAReInclusionBeneathAnExclusion_CoversWhatTheReInclusionReaches()
    {
        var partition = PartitionWith(Container("Corp", selected: true, children:
        [
            Container("Service Accounts", excluded: true, children:
            [
                Container("App1", selected: true, children: [Container("Staging")])
            ])
        ]));

        ContainerSelectionEditor.RecalculateCoverage(partition);

        var app1 = Child(Child(partition, "Corp", "Service Accounts"), "App1");
        var staging = Child(app1, "Staging");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(app1.ExcludedByAncestor, Is.False, "App1 states its own selection, so the exclusion above it no longer governs it");
            Assert.That(staging.Included, Is.True, "App1's selection is nearer than the exclusion above it");
            Assert.That(staging.ExcludedByAncestor, Is.False);
        }
    }

    [Test]
    public void RecalculateCoverage_WithAnExclusionBeneathAOneLevelSelection_LeavesTheBranchUntouched()
    {
        // A OneLevel selection reaches nothing beneath it, so there is nothing for an exclusion to carve out.
        var partition = PartitionWith(Container("Corp", selected: true, scope: ConnectedSystemContainerScope.OneLevel, children:
        [
            Container("Service Accounts", children: [Container("App1")])
        ]));

        ContainerSelectionEditor.RecalculateCoverage(partition);

        var serviceAccounts = Child(partition, "Corp", "Service Accounts");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(serviceAccounts.Included, Is.False);
            Assert.That(serviceAccounts.ExcludedByAncestor, Is.False);
        }
    }

    #endregion

    [Test]
    public void ToggleSelected_DeselectingAReInclusionInsideAnExclusion_HandsItBackToThatExclusion()
    {
        // Found by driving the portal: the row went blank rather than reading "Excluded by Service Accounts", which
        // says "nothing has been decided here" when the branch is in fact carved out. ToggleSelected maintained the
        // coverage flags by hand and knew only about selections, so nothing re-applied the exclusion above.
        var app1 = Container("App1", selected: true);
        var partition = PartitionWith(Container("Corp", selected: true, children:
        [
            Container("Service Accounts", excluded: true, children: [app1])
        ]));
        ContainerSelectionEditor.RecalculateCoverage(partition);

        ContainerSelectionEditor.ToggleSelected(app1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(app1.Selected, Is.False);
            Assert.That(app1.ExcludedByAncestor, Is.True, "the exclusion it was carved back out of governs it again");
            Assert.That(app1.Included, Is.False, "the exclusion is nearer than Corp's selection");
        }
    }

    [Test]
    public void ToggleSelected_SelectingAContainerInsideAnExclusion_CoversWhatItReaches()
    {
        var app1 = Container("App1", children: [Container("Staging")]);
        var partition = PartitionWith(Container("Corp", selected: true, children:
        [
            Container("Service Accounts", excluded: true, children: [app1])
        ]));
        ContainerSelectionEditor.RecalculateCoverage(partition);

        ContainerSelectionEditor.ToggleSelected(app1);

        var staging = Child(app1, "Staging");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(app1.Selected, Is.True);
            Assert.That(staging.Included, Is.True, "the re-inclusion is nearer to it than the exclusion above");
            Assert.That(staging.ExcludedByAncestor, Is.False);
        }
    }

    #region Naming the Container that decided a row (#1255)

    [Test]
    public void DecidingAncestor_ForACoveredContainer_IsTheSelectionThatCoversIt()
    {
        var partition = PartitionWith(Container("Corp", selected: true, children: [Container("Sales")]));
        ContainerSelectionEditor.RecalculateCoverage(partition);

        var deciding = ContainerSelectionEditor.DecidingAncestor(Child(partition, "Corp", "Sales"));

        Assert.That(deciding?.Name, Is.EqualTo("Corp"));
    }

    [Test]
    public void DecidingAncestor_ForAContainerInsideAnExclusion_IsTheExclusionRatherThanTheSelectionAboveIt()
    {
        // The row has to name what actually decided it. Naming Corp here would tell an administrator their objects
        // are imported when the exclusion beneath it is what governs them.
        var partition = PartitionWith(Container("Corp", selected: true, children:
        [
            Container("Service Accounts", excluded: true, children: [Container("App1")])
        ]));
        ContainerSelectionEditor.RecalculateCoverage(partition);

        var app1 = Child(Child(partition, "Corp", "Service Accounts"), "App1");

        Assert.That(ContainerSelectionEditor.DecidingAncestor(app1)?.Name, Is.EqualTo("Service Accounts"));
    }

    [Test]
    public void DecidingAncestor_SkipsAnAncestorWhoseStatementReachesOnlyItsOwnLevel()
    {
        // A OneLevel statement reaches the objects held directly in that Container and no Container beneath it, so
        // whatever reached it from above is still what governs its descendants.
        var partition = PartitionWith(Container("Corp", selected: true, children:
        [
            Container("Regions", selected: true, scope: ConnectedSystemContainerScope.OneLevel, children: [Container("EMEA")])
        ]));
        ContainerSelectionEditor.RecalculateCoverage(partition);

        var emea = Child(Child(partition, "Corp", "Regions"), "EMEA");

        Assert.That(ContainerSelectionEditor.DecidingAncestor(emea)?.Name, Is.EqualTo("Corp"));
    }

    [Test]
    public void DecidingAncestor_WhereNothingAboveStatesAnything_IsNull()
    {
        var partition = PartitionWith(Container("Corp", children: [Container("Sales")]));
        ContainerSelectionEditor.RecalculateCoverage(partition);

        Assert.That(ContainerSelectionEditor.DecidingAncestor(Child(partition, "Corp", "Sales")), Is.Null);
    }

    #endregion

    #region Helpers

    private ConnectedSystemContainer Container(
        string name,
        bool selected = false,
        bool excluded = false,
        ConnectedSystemContainerScope scope = ConnectedSystemContainerScope.Subtree,
        IEnumerable<ConnectedSystemContainer>? children = null)
    {
        var container = new ConnectedSystemContainer
        {
            Id = _nextId++,
            Name = name,
            ExternalId = $"OU={name}",
            Selected = selected,
            Excluded = excluded,
            Scope = scope
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

        // Only root Containers carry the Partition back-reference, matching how the hierarchy is persisted.
        foreach (var rootContainer in rootContainers)
            rootContainer.Partition = partition;

        return partition;
    }

    private static ConnectedSystemContainer Root(ConnectedSystemPartition partition, string name) =>
        partition.Containers!.Single(c => c.Name == name);

    private static ConnectedSystemContainer Child(ConnectedSystemPartition partition, string rootName, string childName) =>
        Child(Root(partition, rootName), childName);

    private static ConnectedSystemContainer Child(ConnectedSystemContainer container, string childName) =>
        container.ChildContainers.Single(c => c.Name == childName);

    #endregion
}
