// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Password Synchronisation settings on a Connected System's Passwords tab.
/// </summary>
[TestFixture]
public class ConnectedSystemPasswordSynchronisationTabTests : JimComponentTestContext
{
    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new UnusedJimApplicationFactory());
    }

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private sealed class UnusedJimApplicationFactory : IJimApplicationFactory
    {
        public JimApplication Create() =>
            throw new InvalidOperationException("The Passwords tab reached the application layer while merely rendering, which it should not do.");
    }

    /// <summary>
    /// With more than one selected Object Type there is no default, and the stored id is 0. The select used to
    /// show that 0 as though it were a choice; an unmade choice shows as an empty field.
    /// </summary>
    [Test]
    public void PasswordSynchronisationTab_WithNoObjectTypeChosen_ShowsAnEmptySelectNotZero()
    {
        var connectedSystem = ConnectedSystemWithSelectedObjectTypes("inetOrgPerson", "posixGroup");

        var cut = Render<ConnectedSystemPasswordSynchronisationTab>(p => p.Add(c => c.ConnectedSystem, connectedSystem));

        // MudSelect's input carries the bound value; an unmade choice used to reach it as "0".
        var select = cut.Find("[data-testid='jim-password-sync-object-type'] input");
        Assert.That(select.GetAttribute("value"), Is.Null.Or.Empty);
    }

    [Test]
    public void PasswordSynchronisationTab_WithOneSelectedObjectType_PreselectsIt()
    {
        var connectedSystem = ConnectedSystemWithSelectedObjectTypes("inetOrgPerson");

        var cut = Render<ConnectedSystemPasswordSynchronisationTab>(p => p.Add(c => c.ConnectedSystem, connectedSystem));

        var select = cut.Find("[data-testid='jim-password-sync-object-type'] input");
        Assert.That(select.GetAttribute("value"), Is.EqualTo("1"), "the sole selected Object Type's id");
    }

    private static ConnectedSystem ConnectedSystemWithSelectedObjectTypes(params string[] names)
    {
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Yellowstone",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "JIM LDAP Connector", SupportsPasswordSet = true },
            ObjectTypes = []
        };
        var id = 1;
        foreach (var name in names)
            connectedSystem.ObjectTypes.Add(new ConnectedSystemObjectType { Id = id++, Name = name, Selected = true, ConnectedSystemId = 1 });
        return connectedSystem;
    }
}
