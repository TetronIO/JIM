// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Synchronisation Rule editor's Details tab. Its provisioning switch names what the rule would create, and
/// that name is derived rather than fixed, so it is the one thing here worth pinning.
/// </summary>
[TestFixture]
public class SyncRuleDetailsTabTests : JimComponentTestContext
{
    private JimApplication _jim = null!;

    protected override void ConfigureAdditionalServices()
    {
        // The tab reaches for the application layer only when a Connected System is chosen on a new rule, which
        // these tests do not do; it still has to be resolvable for the component to construct.
        _jim = new JimApplication(new Mock<IRepository>().Object);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_jim));
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    /// <summary>
    /// Before an Object Type is chosen there is no name to use, and the placeholder standing in for one is
    /// already plural: pluralising it again produced "Provision objectses to the Connected System?" on every new
    /// Export rule.
    /// </summary>
    [Test]
    public void SyncRuleDetailsTab_WithNoConnectedSystemObjectTypeChosen_DoesNotPluraliseThePlaceholderTwice()
    {
        var syncRule = ProvisioningRule();
        // Declared non-nullable and initialised to null!, which is exactly the state a rule is in before an
        // Object Type has been chosen; the markup reads it through a null-conditional for that reason.
        syncRule.ConnectedSystemObjectType = null!;

        var cut = Render<SyncRuleDetailsTab>(p => p.Add(c => c.SyncRule, syncRule));

        Assert.That(cut.Markup, Does.Contain("Provision objects to the Connected System?"));
    }

    /// <summary>
    /// And where there is a name, it is still pluralised: the fix must not turn the label into a singular.
    /// </summary>
    [Test]
    public void SyncRuleDetailsTab_WithAConnectedSystemObjectTypeChosen_PluralisesItsName()
    {
        var cut = Render<SyncRuleDetailsTab>(p => p.Add(c => c.SyncRule, ProvisioningRule()));

        Assert.That(cut.Markup, Does.Contain("Provision users to the Connected System?"));
    }

    private static SyncRule ProvisioningRule() => new()
    {
        Id = 3,
        Name = "Provision Users",
        Direction = SyncRuleDirection.Export,
        ProvisionToConnectedSystem = true,
        ConnectedSystemId = 7,
        ConnectedSystem = new ConnectedSystem { Id = 7, Name = "Test File System" },
        ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "user" }
    };

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }
}
