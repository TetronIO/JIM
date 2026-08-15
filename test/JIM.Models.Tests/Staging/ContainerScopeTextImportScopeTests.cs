// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// What an import actually asks after a Container Scope has been stated as text (#1255 Advanced Mode).
/// </summary>
/// <remarks>
/// The Advanced Mode tests in <c>JIM.Utilities.Tests</c> assert on the flags the text sets, which leaves a seam: an
/// administrator does not care what <see cref="ConnectedSystemContainer.Selected"/> reads, they care which objects
/// the next Full Import returns. These join the two halves by putting the text through
/// <see cref="ContainerScopeText.Apply"/> and then asking <see cref="ConnectedSystemScope"/> the one membership
/// question import, export and preview all ask, so the chain from authored text to import scope is covered in one
/// place rather than inferred from two.
/// </remarks>
[TestFixture]
public class ContainerScopeTextImportScopeTests
{
    private const string Alice = "CN=Alice,OU=Corp,DC=example,DC=local";
    private const string SalesBob = "CN=Bob,OU=Sales,OU=Corp,DC=example,DC=local";
    private const string ServiceAccount = "CN=svc-backup,OU=Service Accounts,OU=Corp,DC=example,DC=local";
    private const string App1Account = "CN=svc-app1,OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=local";
    private const string Partner = "CN=Pat,OU=Partners,DC=example,DC=local";

    [Test]
    public void AnIncludeStatedAsText_PutsThatBranchInImportScope()
    {
        var connectedSystem = SystemWithContainers();

        var errors = ContainerScopeText.Apply("include OU=Corp,DC=example,DC=local", Partitions(connectedSystem));
        var scope = ScopeFromCurrentSelection(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(scope.Contains(1, Alice), Is.True);
            Assert.That(scope.Contains(1, SalesBob), Is.True, "a Subtree statement reaches everything beneath it");
            Assert.That(scope.Contains(1, Partner), Is.False, "the text names OU=Corp and nothing else");
        }
    }

    [Test]
    public void AnExcludeStatedAsText_TakesThatBranchOutOfImportScope()
    {
        var connectedSystem = SystemWithContainers();

        ContainerScopeText.Apply(
            """
            include OU=Corp,DC=example,DC=local
            exclude OU=Service Accounts,OU=Corp,DC=example,DC=local
            """,
            Partitions(connectedSystem));
        var scope = ScopeFromCurrentSelection(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Contains(1, Alice), Is.True);
            Assert.That(scope.Contains(1, ServiceAccount), Is.False);
            Assert.That(scope.Contains(1, App1Account), Is.False, "an exclusion reaches every Container beneath it too");
        }
    }

    [Test]
    public void AReInclusionStatedAsText_BringsItsBranchBackIntoImportScope()
    {
        // The case the tree expresses by ticking a Container inside a carve-out: whichever statement is nearest to
        // an object decides its fate, and the text has to reach the same answer.
        var connectedSystem = SystemWithContainers();

        ContainerScopeText.Apply(
            """
            include OU=Corp,DC=example,DC=local
            exclude OU=Service Accounts,OU=Corp,DC=example,DC=local
            include OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=local
            """,
            Partitions(connectedSystem));
        var scope = ScopeFromCurrentSelection(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Contains(1, ServiceAccount), Is.False, "the carve-out still stands where nothing overrides it");
            Assert.That(scope.Contains(1, App1Account), Is.True);
        }
    }

    [Test]
    public void OneLevelStatedAsText_LeavesTheContainersBeneathItOutOfImportScope()
    {
        var connectedSystem = SystemWithContainers();

        ContainerScopeText.Apply("include one-level OU=Corp,DC=example,DC=local", Partitions(connectedSystem));
        var scope = ScopeFromCurrentSelection(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Contains(1, Alice), Is.True, "objects held directly in OU=Corp are in scope");
            Assert.That(scope.Contains(1, SalesBob), Is.False, "One Level reaches no Container beneath it");
        }
    }

    [Test]
    public void TextThatNamesNothing_LeavesNothingInImportScope()
    {
        // Clearing the text is how a Connected System is taken out of management entirely, and it has to be a
        // determined "no" rather than an undetermined answer, because that is what a preview counts.
        var connectedSystem = SystemWithContainers();
        ContainerScopeText.Apply("include OU=Corp,DC=example,DC=local", Partitions(connectedSystem));

        ContainerScopeText.Apply(string.Empty, Partitions(connectedSystem));
        var scope = ScopeFromCurrentSelection(connectedSystem);

        Assert.That(scope.Contains(1, Alice), Is.False);
    }

    [Test]
    public void TextRefused_LeavesImportScopeExactlyAsItWas()
    {
        // The point of applying all-or-nothing: a refused text must not have moved a single object in or out of
        // scope, since a scope applied halfway obsoletes objects nobody asked to obsolete.
        var connectedSystem = SystemWithContainers();
        ContainerScopeText.Apply("include OU=Corp,DC=example,DC=local", Partitions(connectedSystem));

        var errors = ContainerScopeText.Apply(
            """
            include OU=Partners,DC=example,DC=local
            exclude OU=Contractors,DC=example,DC=local
            """,
            Partitions(connectedSystem));
        var scope = ScopeFromCurrentSelection(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(scope.Contains(1, Alice), Is.True, "the selection the refused text would have replaced still stands");
            Assert.That(scope.Contains(1, Partner), Is.False, "and the refused text's own first line was not applied either");
        }
    }

    #region Helpers

    private static IReadOnlyList<ConnectedSystemPartition> Partitions(ConnectedSystem connectedSystem) =>
        [.. connectedSystem.Partitions!];

    /// <summary>
    /// The scope as the import would build it: from the Connected System's own flags, which is what the text has
    /// just written, rather than from a proposal built by the test.
    /// </summary>
    private static ConnectedSystemScope ScopeFromCurrentSelection(ConnectedSystem connectedSystem)
    {
        var containers = (connectedSystem.Partitions ?? [])
            .SelectMany(ContainerSelectionEditor.Flatten)
            .ToList();

        return ConnectedSystemScope.From(
            connectedSystem,
            new ConnectedSystemScopeSelectionProposal(
                (connectedSystem.Partitions ?? []).Where(p => p.Selected).Select(p => p.Id).ToList(),
                containers.Where(c => c.Selected).Select(c => c.Id).ToList(),
                containers.Where(c => c.Excluded).Select(c => c.Id).ToList()),
            DistinguishedNameContainment.Instance);
    }

    /// <summary>
    /// DC=example,DC=local holding OU=Corp (with OU=Sales, and OU=Service Accounts holding OU=App1) and a sibling
    /// OU=Partners: enough to express a carve-out, a re-inclusion inside it, and something outside the selection.
    /// </summary>
    private static ConnectedSystem SystemWithContainers()
    {
        var app1 = Container(13, "OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=local");
        var serviceAccounts = Container(14, "OU=Service Accounts,OU=Corp,DC=example,DC=local");
        serviceAccounts.AddChildContainer(app1);

        var sales = Container(11, "OU=Sales,OU=Corp,DC=example,DC=local");
        var corp = Container(10, "OU=Corp,DC=example,DC=local");
        corp.AddChildContainer(sales);
        corp.AddChildContainer(serviceAccounts);

        var partners = Container(12, "OU=Partners,DC=example,DC=local");

        var partition = new ConnectedSystemPartition
        {
            Id = 1,
            Name = "example.local",
            ExternalId = "DC=example,DC=local",
            Selected = true,
            Containers = [corp, partners]
        };
        corp.Partition = partition;
        partners.Partition = partition;

        return new ConnectedSystem
        {
            Name = "Test Directory",
            ConnectorDefinition = new ConnectorDefinition { Name = "Test Connector", SupportsPartitionContainers = true },
            Partitions = [partition]
        };
    }

    private static ConnectedSystemContainer Container(int id, string externalId) =>
        new() { Id = id, Name = externalId.Split(',')[0][3..], ExternalId = externalId };

    #endregion
}
