// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Utilities.Tests;

/// <summary>
/// Covers Advanced Mode: the text an administrator authors to state Container Scope, and the canonical text JIM
/// projects back from the Container rows.
/// </summary>
/// <remarks>
/// The text is an editor for the same <see cref="ConnectedSystemContainer.Selected"/> and
/// <see cref="ConnectedSystemContainer.Excluded"/> flags the tree edits, resolved against the discovered hierarchy
/// when it is applied. That is what keeps a rename from silently changing what a statement matches, which a stored
/// list of Distinguished Names re-resolved on every run could not.
///
/// Every rule here exists because the alternative is silence. A path naming no Container means the administrator
/// believes a branch is excluded when it is not, or included when it is not; both are import scope quietly
/// disagreeing with the configuration that was written down, which is the failure this feature exists to remove.
/// </remarks>
[TestFixture]
public class ContainerScopeTextTests
{
    private int _nextId;

    [SetUp]
    public void SetUp() => _nextId = 1;

    #region Parsing

    [Test]
    public void Parse_AnIncludeAndAnExclude_ReadsBothStatements()
    {
        var result = ContainerScopeText.Parse(
            """
            include OU=Corp,DC=example,DC=com
            exclude OU=Service Accounts,OU=Corp,DC=example,DC=com
            """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Statements, Has.Count.EqualTo(2));
            Assert.That(result.Statements[0].Kind, Is.EqualTo(ContainerScopeStatementKind.Include));
            Assert.That(result.Statements[0].Path, Is.EqualTo("OU=Corp,DC=example,DC=com"));
            Assert.That(result.Statements[0].Scope, Is.EqualTo(ConnectedSystemContainerScope.Subtree));
            Assert.That(result.Statements[1].Kind, Is.EqualTo(ContainerScopeStatementKind.Exclude));
            Assert.That(result.Statements[1].Path, Is.EqualTo("OU=Service Accounts,OU=Corp,DC=example,DC=com"));
        }
    }

    [Test]
    public void Parse_ThePlusAndMinusShorthand_MeansIncludeAndExclude()
    {
        var result = ContainerScopeText.Parse(
            """
            + OU=Corp,DC=example,DC=com
            - OU=Service Accounts,OU=Corp,DC=example,DC=com
            """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Statements[0].Kind, Is.EqualTo(ContainerScopeStatementKind.Include));
            Assert.That(result.Statements[1].Kind, Is.EqualTo(ContainerScopeStatementKind.Exclude));
        }
    }

    [Test]
    public void Parse_TheOneLevelModifier_NarrowsTheStatementToThatContainer()
    {
        var result = ContainerScopeText.Parse("include one-level OU=Corp,DC=example,DC=com");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Statements[0].Scope, Is.EqualTo(ConnectedSystemContainerScope.OneLevel));
            Assert.That(result.Statements[0].Path, Is.EqualTo("OU=Corp,DC=example,DC=com"),
                "the modifier is consumed, not left on the front of the path");
        }
    }

    [Test]
    public void Parse_AnAttributeNamedLikeTheModifier_IsReadAsAPathNotAModifier()
    {
        // "one-level" is a legal LDAP attribute type name, so the modifier is only a modifier when it stands alone.
        var result = ContainerScopeText.Parse("include one-level=Odd,DC=example,DC=com");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Statements[0].Scope, Is.EqualTo(ConnectedSystemContainerScope.Subtree));
            Assert.That(result.Statements[0].Path, Is.EqualTo("one-level=Odd,DC=example,DC=com"));
        }
    }

    [Test]
    public void Parse_BlankLinesAndWholeLineComments_AreIgnored()
    {
        var result = ContainerScopeText.Parse(
            """
            # everything JIM manages in the corporate tree

            include OU=Corp,DC=example,DC=com

              # indented comments count too
            """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Statements, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Parse_AHashInsideAPath_IsPartOfThePathNotAComment()
    {
        // Comments are whole-line only precisely because a Distinguished Name may carry an unescaped '#'.
        var result = ContainerScopeText.Parse("include OU=Team #1,DC=example,DC=com");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Statements[0].Path, Is.EqualTo("OU=Team #1,DC=example,DC=com"));
        }
    }

    [Test]
    public void Parse_AnUnknownDirective_IsReportedAgainstItsLine()
    {
        var result = ContainerScopeText.Parse(
            """
            include OU=Corp,DC=example,DC=com
            omit OU=Service Accounts,OU=Corp,DC=example,DC=com
            """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors, Has.Count.EqualTo(1));
            Assert.That(result.Errors[0].LineNumber, Is.EqualTo(2));
            Assert.That(result.Errors[0].Message, Does.Contain("omit"));
            Assert.That(result.Errors[0].Message, Does.Contain("include"),
                "an error about an unrecognised directive has to say what is recognised");
        }
    }

    [Test]
    public void Parse_ADirectiveWithNoPath_IsReportedAgainstItsLine()
    {
        var result = ContainerScopeText.Parse("exclude");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors[0].LineNumber, Is.EqualTo(1));
        }
    }

    [Test]
    public void Parse_EmptyText_IsValidAndStatesNothing()
    {
        var result = ContainerScopeText.Parse("   ");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Statements, Is.Empty);
        }
    }

    #endregion

    #region Applying to the hierarchy

    [Test]
    public void Apply_AnIncludeAndAnExclude_SetsTheFlagsTheTreeWouldHaveSet()
    {
        var partition = HierarchyWithServiceAccounts();

        var errors = ContainerScopeText.Apply(
            """
            include OU=Corp
            exclude OU=Service Accounts,OU=Corp
            """,
            [partition]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(Find(partition, "Corp").Selected, Is.True);
            Assert.That(Find(partition, "Service Accounts").Excluded, Is.True);
            Assert.That(Find(partition, "Service Accounts").Included, Is.False);
        }
    }

    [Test]
    public void Apply_ARestatementOfTheWholeSelection_ClearsWhatItNoLongerNames()
    {
        var partition = HierarchyWithServiceAccounts();
        Find(partition, "Sales").Selected = true;

        var errors = ContainerScopeText.Apply("include OU=Corp", [partition]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(Find(partition, "Sales").Selected, Is.False,
                "the text states the whole of Container Scope, so omitting a Container is how it is deselected");
            Assert.That(Find(partition, "Sales").Included, Is.True, "Corp's subtree still covers it");
        }
    }

    [Test]
    public void Apply_AReInclusionInsideAnExcludedBranch_KeepsBothStatements()
    {
        var partition = HierarchyWithServiceAccounts();

        var errors = ContainerScopeText.Apply(
            """
            include OU=Corp
            exclude OU=Service Accounts,OU=Corp
            include OU=App1,OU=Service Accounts,OU=Corp
            """,
            [partition]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(Find(partition, "Service Accounts").Excluded, Is.True);
            Assert.That(Find(partition, "App1").Selected, Is.True,
                "the nearest statement decides, so a selection inside a carved-out branch brings it back");
        }
    }

    [Test]
    public void Apply_TheOneLevelModifier_NarrowsTheContainersScope()
    {
        var partition = HierarchyWithServiceAccounts();

        var errors = ContainerScopeText.Apply("include one-level OU=Corp", [partition]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(Find(partition, "Corp").Scope, Is.EqualTo(ConnectedSystemContainerScope.OneLevel));
            Assert.That(Find(partition, "Sales").Included, Is.False,
                "One Level reaches the objects Corp holds directly and no Container beneath it");
        }
    }

    [Test]
    public void Apply_APathNamingNoContainer_FailsAndChangesNothing()
    {
        var partition = HierarchyWithServiceAccounts();

        var errors = ContainerScopeText.Apply(
            """
            include OU=Corp
            exclude OU=Contractors,OU=Corp
            """,
            [partition]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].LineNumber, Is.EqualTo(2));
            Assert.That(errors[0].Message, Does.Contain("OU=Contractors,OU=Corp"));
            Assert.That(Find(partition, "Corp").Selected, Is.False,
                "a text that cannot be applied in full is not applied at all; a half-applied scope is the silent " +
                "obsoletion this feature exists to prevent");
        }
    }

    [Test]
    public void Apply_AWhitespaceVariantOfAPath_ResolvesToTheSameContainer()
    {
        // RFC 4514 permits the optional space after a separator, and some directories emit it, so an administrator
        // pasting a Distinguished Name from elsewhere arrives with one.
        var partition = HierarchyWithServiceAccounts();

        var errors = ContainerScopeText.Apply("exclude OU=Service Accounts, OU=Corp", [partition]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(Find(partition, "Service Accounts").Excluded, Is.True);
        }
    }

    [Test]
    public void Apply_TheSameContainerStatedTwice_IsRefusedAsAContradiction()
    {
        var partition = HierarchyWithServiceAccounts();

        var errors = ContainerScopeText.Apply(
            """
            include OU=Corp
            exclude OU=Corp
            """,
            [partition]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].LineNumber, Is.EqualTo(2));
            Assert.That(errors[0].Message, Does.Contain("1"), "the error has to name the line it contradicts");
        }
    }

    [Test]
    public void Apply_AStatementAnAncestorAlreadyMakes_IsRefusedAsRedundant()
    {
        var partition = HierarchyWithServiceAccounts();

        var errors = ContainerScopeText.Apply(
            """
            include OU=Corp
            include OU=Sales,OU=Corp
            """,
            [partition]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].LineNumber, Is.EqualTo(2));
            Assert.That(errors[0].Message, Does.Contain("OU=Corp"),
                "the error has to name the statement that already says this");
        }
    }

    [Test]
    public void Apply_ASelectionBeneathAOneLevelAncestor_IsNotRedundant()
    {
        var partition = HierarchyWithServiceAccounts();

        var errors = ContainerScopeText.Apply(
            """
            include one-level OU=Corp
            include OU=Sales,OU=Corp
            """,
            [partition]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty, "a One Level statement reaches no Container beneath it, so it makes nothing redundant");
            Assert.That(Find(partition, "Sales").Selected, Is.True);
        }
    }

    [Test]
    public void Apply_NamingAContainer_SelectsThePartitionHoldingIt()
    {
        var partition = HierarchyWithServiceAccounts();
        partition.Selected = false;

        ContainerScopeText.Apply("include OU=Corp", [partition]);

        Assert.That(partition.Selected, Is.True, "a Container cannot be in scope while the partition around it is not");
    }

    [Test]
    public void Apply_APathWithAnEscapedSeparator_ResolvesDespiteTheOptionalWhitespace()
    {
        // The paste-from-elsewhere case where the Container's own name contains a comma: the whitespace after the
        // real separator is dropped, and the whitespace inside the name is kept.
        var partition = HierarchyWithAnEscapedSeparator();

        var errors = ContainerScopeText.Apply(@"include OU=Sales\, EMEA, OU=Corp", [partition]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(Find(partition, "Sales EMEA").Selected, Is.True);
        }
    }

    [Test]
    public void Apply_APathDifferingOnlyInsideAnEscapedName_ResolvesToNoContainer()
    {
        // The space after an escaped comma is part of the Container's name, so "OU=Sales\,EMEA" and
        // "OU=Sales\, EMEA" are two different Containers and neither may stand in for the other. This is the only
        // shape that can prove it: the authored path and the stored one go through the same normalisation, so an
        // error applied to both cancels out everywhere except where the two names genuinely differ.
        var partition = HierarchyWithAnEscapedSeparator();

        var errors = ContainerScopeText.Apply(@"include OU=Sales\,EMEA,OU=Corp", [partition]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Has.Count.EqualTo(1), "a Container that is not there is an error, not a near-enough match");
            Assert.That(Find(partition, "Sales EMEA").Selected, Is.False);
        }
    }

    [Test]
    public void Project_AContainerWithAnEscapedSeparator_WritesAPathThatResolvesBack()
    {
        var partition = HierarchyWithAnEscapedSeparator();
        Find(partition, "Sales EMEA").Selected = true;

        var text = ContainerScopeText.Project([partition]);
        var reapplied = HierarchyWithAnEscapedSeparator();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ContainerScopeText.Apply(text, [reapplied]), Is.Empty);
            Assert.That(Find(reapplied, "Sales EMEA").Selected, Is.True);
        }
    }

    [Test]
    public void Apply_ContainersInDifferentPartitions_StatesBothInOneText()
    {
        // The text covers a Connected System, not a partition, so a path is resolved against every partition's
        // hierarchy and each one it names is selected along with the Containers in it.
        var first = HierarchyWithServiceAccounts();
        var second = PartitionWithArchive();
        second.Selected = false;

        var errors = ContainerScopeText.Apply(
            """
            include OU=Corp
            include OU=Archive
            """,
            [first, second]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(Find(first, "Corp").Selected, Is.True);
            Assert.That(Find(second, "Archive").Selected, Is.True);
            Assert.That(second.Selected, Is.True, "naming a Container selects the partition holding it, in either partition");
        }
    }

    [Test]
    public void Apply_TextNamingOnlyTheOtherPartition_ClearsTheFirstOne()
    {
        // The whole-scope rule has to hold across partitions too: a Container omitted from the text states nothing,
        // wherever it lives, or an administrator restating one partition would silently keep another's selection.
        var first = HierarchyWithServiceAccounts();
        var second = PartitionWithArchive();
        Find(first, "Corp").Selected = true;

        ContainerScopeText.Apply("include OU=Archive", [first, second]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Find(first, "Corp").Selected, Is.False);
            Assert.That(Find(second, "Archive").Selected, Is.True);
        }
    }

    [Test]
    public void Apply_EmptyText_ClearsEveryStatement()
    {
        var partition = HierarchyWithServiceAccounts();
        Find(partition, "Corp").Selected = true;
        Find(partition, "Service Accounts").Excluded = true;

        var errors = ContainerScopeText.Apply(string.Empty, [partition]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(Find(partition, "Corp").Selected, Is.False);
            Assert.That(Find(partition, "Service Accounts").Excluded, Is.False);
        }
    }

    #endregion

    #region Projecting back to text

    [Test]
    public void Project_ASelectionWithAnExclusion_WritesOneStatementPerContainerInHierarchyOrder()
    {
        var partition = HierarchyWithServiceAccounts();
        Find(partition, "Corp").Selected = true;
        Find(partition, "Service Accounts").Excluded = true;
        Find(partition, "App1").Selected = true;

        var text = ContainerScopeText.Project([partition]);

        Assert.That(text, Is.EqualTo(
            """
            include OU=Corp
            exclude OU=Service Accounts,OU=Corp
            include OU=App1,OU=Service Accounts,OU=Corp
            """));
    }

    [Test]
    public void Project_AOneLevelContainer_WritesTheModifier()
    {
        var partition = HierarchyWithServiceAccounts();
        var corp = Find(partition, "Corp");
        corp.Selected = true;
        corp.Scope = ConnectedSystemContainerScope.OneLevel;

        Assert.That(ContainerScopeText.Project([partition]), Is.EqualTo("include one-level OU=Corp"));
    }

    [Test]
    public void Project_NothingSelected_WritesNothing()
    {
        Assert.That(ContainerScopeText.Project([HierarchyWithServiceAccounts()]), Is.Empty);
    }

    [Test]
    public void ProjectThenApply_RoundTripsWithoutChangingTheSelection()
    {
        // The two modes are lossless in both directions, which is what lets an administrator move between them
        // without being asked to confirm anything: with no wildcards, every text states exactly what the tree can.
        var original = HierarchyWithServiceAccounts();
        Find(original, "Corp").Selected = true;
        Find(original, "Corp").Scope = ConnectedSystemContainerScope.OneLevel;
        Find(original, "Service Accounts").Excluded = true;
        Find(original, "App1").Selected = true;

        var text = ContainerScopeText.Project([original]);
        var reapplied = HierarchyWithServiceAccounts();
        var errors = ContainerScopeText.Apply(text, [reapplied]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(ContainerScopeText.Project([reapplied]), Is.EqualTo(text));
            Assert.That(Find(reapplied, "Corp").Scope, Is.EqualTo(ConnectedSystemContainerScope.OneLevel));
            Assert.That(Find(reapplied, "Service Accounts").Excluded, Is.True);
            Assert.That(Find(reapplied, "App1").Selected, Is.True);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// DC=example,DC=com holding OU=Corp, with OU=Sales and OU=Service Accounts beneath it, and OU=App1 beneath
    /// that: the smallest hierarchy that can express a carve-out and a re-inclusion inside it.
    /// </summary>
    private ConnectedSystemPartition HierarchyWithServiceAccounts()
    {
        var app1 = Container("App1", "OU=App1,OU=Service Accounts,OU=Corp");
        var serviceAccounts = Container("Service Accounts", "OU=Service Accounts,OU=Corp", [app1]);
        var sales = Container("Sales", "OU=Sales,OU=Corp");
        var corp = Container("Corp", "OU=Corp", [sales, serviceAccounts]);

        var partition = new ConnectedSystemPartition
        {
            Id = _nextId++,
            Name = "DC=example,DC=com",
            Selected = true,
            Containers = [corp]
        };

        corp.Partition = partition;
        return partition;
    }

    /// <summary>
    /// A second partition, holding OU=Archive: enough to show that the text states a Connected System's scope
    /// rather than one partition's.
    /// </summary>
    private ConnectedSystemPartition PartitionWithArchive()
    {
        var archive = Container("Archive", "OU=Archive");

        var partition = new ConnectedSystemPartition
        {
            Id = _nextId++,
            Name = "DC=archive,DC=example,DC=com",
            Selected = true,
            Containers = [archive]
        };

        archive.Partition = partition;
        return partition;
    }

    /// <summary>
    /// A hierarchy holding a Container whose own name contains a comma, escaped as RFC 4514 requires.
    /// </summary>
    private ConnectedSystemPartition HierarchyWithAnEscapedSeparator()
    {
        var salesEmea = Container("Sales EMEA", @"OU=Sales\, EMEA,OU=Corp");
        var corp = Container("Corp", "OU=Corp", [salesEmea]);

        var partition = new ConnectedSystemPartition
        {
            Id = _nextId++,
            Name = "DC=example,DC=com",
            Selected = true,
            Containers = [corp]
        };

        corp.Partition = partition;
        return partition;
    }

    private ConnectedSystemContainer Container(
        string name,
        string externalId,
        IEnumerable<ConnectedSystemContainer>? children = null)
    {
        var container = new ConnectedSystemContainer
        {
            Id = _nextId++,
            Name = name,
            ExternalId = externalId
        };

        foreach (var child in children ?? [])
            container.AddChildContainer(child);

        return container;
    }

    private static ConnectedSystemContainer Find(ConnectedSystemPartition partition, string name) =>
        ContainerSelectionEditor.Flatten(partition).Single(c => c.Name == name);

    #endregion
}
