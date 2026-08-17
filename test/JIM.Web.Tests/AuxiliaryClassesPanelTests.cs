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
/// Covers the Auxiliary Classes panel on a Connected System Object Type's schema sub-tab.
/// </summary>
/// <remarks>
/// Two things are pinned here. First, which of its two faces the panel shows, because both are conditional on the
/// Connected System's own classification of the Object Type and getting it wrong offers an administrator a control
/// that cannot work: merging into an Active Directory type that resolves its own auxiliary classes, or a Structural
/// Carrier on a type that already is one. Second, that rendering reaches no further than the graph it was handed:
/// the tab renders one of these per Object Type, so a panel that fetched its own discovery run would query once per
/// type on every render (the same rule <see cref="ConnectedSystemSchemaAttributeGridTests"/> pins for the tab).
/// </remarks>
[TestFixture]
public class AuxiliaryClassesPanelTests : JimComponentTestContext
{
    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new UnusedJimApplicationFactory());
    }

    private sealed class UnusedJimApplicationFactory : IJimApplicationFactory
    {
        public JimApplication Create() =>
            throw new InvalidOperationException("The Auxiliary Classes panel reached the application layer while merely rendering, which it should not do.");
    }

    [Test]
    public void AuxiliaryClassesPanel_ObjectTypeThatDoesNotManageClassMembership_RendersNothing()
    {
        // An Active Directory Object Type carries no class membership attribute.
        var connectedSystem = ConnectedSystemWithAuxiliaryClass();
        var objectType = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "inetOrgPerson");
        objectType.Tags.RemoveAll(tag => tag.Key == ObjectTypeTags.Keys.ClassMembershipAttribute);

        var component = RenderPanel(connectedSystem, objectType);

        Assert.That(component.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void AuxiliaryClassesPanel_StructuralObjectType_OffersTheSchemasAuxiliaryClasses()
    {
        var connectedSystem = ConnectedSystemWithAuxiliaryClass();
        var objectType = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "inetOrgPerson");

        var component = RenderPanel(connectedSystem, objectType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("Auxiliary Classes"));
            Assert.That(component.Markup, Does.Contain("posixAccount"));
            Assert.That(component.Markup, Does.Not.Contain("Structural Carrier Class"));
        }
    }

    [Test]
    public void AuxiliaryClassesPanel_AuxiliaryObjectType_AsksForAStructuralCarrierInsteadOfOfferingMerges()
    {
        var connectedSystem = ConnectedSystemWithAuxiliaryClass();
        var objectType = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "posixAccount");
        objectType.Tags.Add(ClassMembershipTag());

        var component = RenderPanel(connectedSystem, objectType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("Structural Carrier Class"));
            Assert.That(component.Markup, Does.Not.Contain("Auxiliary Classes"));
        }
    }

    [Test]
    public void AuxiliaryClassesPanel_AuxiliaryObjectTypeWithNoCarrier_SaysProvisioningCannotHappen()
    {
        var connectedSystem = ConnectedSystemWithAuxiliaryClass();
        var objectType = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "posixAccount");
        objectType.Tags.Add(ClassMembershipTag());

        var component = RenderPanel(connectedSystem, objectType);

        Assert.That(component.Markup, Does.Contain("cannot create them"));
    }

    [Test]
    public void AuxiliaryClassesPanel_NoDiscoveryRun_SaysSoRatherThanShowingAnEmptyStatus()
    {
        var connectedSystem = ConnectedSystemWithAuxiliaryClass();
        var objectType = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "inetOrgPerson");

        var component = RenderPanel(connectedSystem, objectType);

        Assert.That(component.Markup, Does.Contain("Discovery has never been run"));
    }

    [Test]
    public void AuxiliaryClassesPanel_CancelledDiscoveryRun_MarksTheResultsPartial()
    {
        var connectedSystem = ConnectedSystemWithAuxiliaryClass();
        var objectType = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "inetOrgPerson");
        var run = new AuxiliaryClassDiscoveryRun
        {
            ConnectedSystemId = connectedSystem.Id,
            Scope = AuxiliaryClassDiscoveryScope.FullScan,
            Status = AuxiliaryClassDiscoveryStatus.Cancelled,
            Completed = new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc),
            EntriesRead = 412380
        };

        var component = RenderPanel(connectedSystem, objectType, run);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("Cancelled, partial"));
            Assert.That(component.Markup, Does.Contain("412,380"));
        }
    }

    [Test]
    public void AuxiliaryClassesPanel_ClassADiscoveryRunObserved_CarriesItsUsageAsASuggestion()
    {
        var connectedSystem = ConnectedSystemWithAuxiliaryClass();
        var objectType = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "inetOrgPerson");
        var run = new AuxiliaryClassDiscoveryRun
        {
            ConnectedSystemId = connectedSystem.Id,
            Scope = AuxiliaryClassDiscoveryScope.QuickSample,
            SampleSizePerObjectType = 5000,
            Status = AuxiliaryClassDiscoveryStatus.Complete,
            EntriesRead = 5000,
            Results =
            [
                new AuxiliaryClassDiscoveryResult
                {
                    StructuralObjectTypeId = objectType.Id,
                    AuxiliaryClassName = "posixAccount",
                    EntryCount = 1204
                }
            ]
        };

        var component = RenderPanel(connectedSystem, objectType, run);

        Assert.That(component.Markup, Does.Contain("in use on 1,204 entries"));
    }

    #region Helpers

    private IRenderedComponent<AuxiliaryClassesPanel> RenderPanel(
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType objectType,
        AuxiliaryClassDiscoveryRun? latestDiscoveryRun = null)
    {
        return Render<AuxiliaryClassesPanel>(parameters => parameters
            .Add(p => p.ConnectedSystem, connectedSystem)
            .Add(p => p.ObjectType, objectType)
            .Add(p => p.LatestDiscoveryRun, latestDiscoveryRun));
    }

    private static ConnectedSystemObjectTypeTag ClassMembershipTag() => new()
    {
        Key = ObjectTypeTags.Keys.ClassMembershipAttribute,
        Value = "objectClass"
    };

    private static ConnectedSystem ConnectedSystemWithAuxiliaryClass()
    {
        var inetOrgPerson = new ConnectedSystemObjectType { Id = 1, Name = "inetOrgPerson", Selected = true };
        inetOrgPerson.Tags.Add(new ConnectedSystemObjectTypeTag
        {
            Key = ObjectTypeTags.Keys.ClassKind,
            Value = ObjectTypeTags.Values.ClassKindStructural
        });
        inetOrgPerson.Tags.Add(ClassMembershipTag());

        var posixAccount = new ConnectedSystemObjectType { Id = 2, Name = "posixAccount", Selected = true };
        posixAccount.Tags.Add(new ConnectedSystemObjectTypeTag
        {
            Key = ObjectTypeTags.Keys.ClassKind,
            Value = ObjectTypeTags.Values.ClassKindAuxiliary
        });
        posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 10, Name = "uidNumber" });

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Corp LDAP",
            ObjectTypes = [inetOrgPerson, posixAccount]
        };
    }

    #endregion
}
