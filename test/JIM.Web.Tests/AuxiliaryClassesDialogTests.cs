// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the dialog that offers auxiliary classes for merging into a structural Connected System Object Type.
/// </summary>
/// <remarks>
/// The dialog replaced an inline list of every auxiliary class in the schema, which on a real directory ran to
/// eighty rows and pushed Attribute Selection off the screen. Three things are pinned: that it opens on the short
/// list (what is suggested) with the whole schema one click away; that a row can be opened to show the attributes
/// merging the class would bring, since a count alone cannot inform the choice; and that nothing is saved until
/// Apply, so an administrator can change their mind about several classes and confirm once.
/// </remarks>
[TestFixture]
public class AuxiliaryClassesDialogTests : JimComponentTestContext
{
    private const string DialogMarker = "jim-aux-dialog";
    private const string RowMarker = "jim-aux-row";
    private const string AttributesToggleMarker = "jim-aux-attrs-toggle";
    private const string AttributesMarker = "jim-aux-attrs";
    private const string ApplyMarker = "jim-aux-apply";

    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new UnusedJimApplicationFactory());
    }

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private sealed class UnusedJimApplicationFactory : IJimApplicationFactory
    {
        public JimApplication Create() =>
            throw new InvalidOperationException("The Auxiliary Classes dialog reached the application layer while merely rendering, which it should not do.");
    }

    [Test]
    public void AuxiliaryClassesDialog_WhenSomethingIsSuggested_OpensOnTheSuggestedClasses()
    {
        var provider = OpenDialog(ConnectedSystemWithAuxiliaryClasses());

        var rows = provider.FindAll($"[data-testid='{RowMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows.Select(r => r.GetAttribute("data-class")), Is.EquivalentTo(new[] { "posixAccount" }));
            Assert.That(provider.Markup, Does.Not.Contain("sambaSamAccount"));
        }
    }

    [Test]
    public void AuxiliaryClassesDialog_TheAllFilter_ListsEveryClassInTheSchema()
    {
        var provider = OpenDialog(ConnectedSystemWithAuxiliaryClasses());

        provider.Find("[data-testid='jim-aux-filter-all']").Click();

        var rows = provider.FindAll($"[data-testid='{RowMarker}']");
        Assert.That(rows.Select(r => r.GetAttribute("data-class")),
            Is.EquivalentTo(new[] { "posixAccount", "sambaSamAccount", "shadowAccount" }));
    }

    /// <summary>
    /// A schema whose auxiliary classes nothing suggests would open on an empty list, which reads as "there are
    /// none". It opens on everything instead.
    /// </summary>
    [Test]
    public void AuxiliaryClassesDialog_WhenNothingIsSuggested_OpensOnEveryClass()
    {
        var connectedSystem = ConnectedSystemWithAuxiliaryClasses();
        var structural = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "inetOrgPerson");
        structural.Tags.RemoveAll(tag => tag.Key == ObjectTypeTags.Keys.PermittedAuxiliaryClass);

        var provider = OpenDialog(connectedSystem);

        Assert.That(provider.FindAll($"[data-testid='{RowMarker}']"), Has.Count.EqualTo(3));
    }

    [Test]
    public void AuxiliaryClassesDialog_OpeningARow_ListsTheAttributesMergingItWouldContribute()
    {
        var provider = OpenDialog(ConnectedSystemWithAuxiliaryClasses());

        provider.Find($"[data-testid='{RowMarker}'][data-class='posixAccount'] [data-testid='{AttributesToggleMarker}']").Click();

        var attributes = provider.Find($"[data-testid='{AttributesMarker}']").TextContent;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attributes, Does.Contain("uidNumber"));
            Assert.That(attributes, Does.Contain("gidNumber"));
            Assert.That(attributes, Does.Contain("userPassword"));
        }
    }

    /// <summary>
    /// A credential attribute can never be selected however the class is merged, so listing it without saying
    /// so would promise an attribute the administrator cannot have.
    /// </summary>
    [Test]
    public void AuxiliaryClassesDialog_ACredentialAttribute_IsMarkedAsNeverSelectable()
    {
        var provider = OpenDialog(ConnectedSystemWithAuxiliaryClasses());

        provider.Find($"[data-testid='{RowMarker}'][data-class='posixAccount'] [data-testid='{AttributesToggleMarker}']").Click();

        var credential = provider.Find($"[data-testid='{AttributesMarker}'] [data-attribute='userPassword']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(credential.TextContent, Does.Contain("credential"));
            Assert.That(provider.Find($"[data-testid='{AttributesMarker}'] [data-attribute='uidNumber']").TextContent, Does.Not.Contain("credential"));
        }
    }

    [Test]
    public void AuxiliaryClassesDialog_ApplyIsDisabled_UntilAMergeChanges()
    {
        var provider = OpenDialog(ConnectedSystemWithAuxiliaryClasses());

        var applyBefore = provider.Find($"[data-testid='{ApplyMarker}']");
        Assert.That(applyBefore.HasAttribute("disabled"), Is.True, "nothing has changed yet");

        provider.Find($"[data-testid='{RowMarker}'][data-class='posixAccount'] input[type='checkbox']").Change(true);

        Assert.That(provider.Find($"[data-testid='{ApplyMarker}']").HasAttribute("disabled"), Is.False);
    }

    [Test]
    public void AuxiliaryClassesDialog_AClassADiscoveryRunObserved_CarriesItsUsageAsASuggestion()
    {
        var connectedSystem = ConnectedSystemWithAuxiliaryClasses();
        var structural = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "inetOrgPerson");
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
                    StructuralObjectTypeId = structural.Id,
                    AuxiliaryClassName = "shadowAccount",
                    EntryCount = 1204
                }
            ]
        };

        var provider = OpenDialog(connectedSystem, run);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Contain("in use on 1,204 entries"));
            Assert.That(provider.FindAll($"[data-testid='{RowMarker}']").Select(r => r.GetAttribute("data-class")),
                Is.EquivalentTo(new[] { "posixAccount", "shadowAccount" }), "an observed class is suggested");
        }
    }

    #region Helpers

    private IRenderedComponent<MudDialogProvider> OpenDialog(ConnectedSystem connectedSystem, AuxiliaryClassDiscoveryRun? run = null)
    {
        var objectType = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "inetOrgPerson");
        var offers = AuxiliaryClassOfferBuilder.Build(objectType, connectedSystem.ObjectTypes!, run);

        var parameters = new DialogParameters<AuxiliaryClassesDialog>
        {
            { x => x.ConnectedSystem, connectedSystem },
            { x => x.ObjectType, objectType },
            { x => x.Offers, offers },
            { x => x.LatestDiscoveryRun, run }
        };

        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        provider.InvokeAsync(() => dialogService.ShowAsync<AuxiliaryClassesDialog>("Auxiliary classes", parameters));
        provider.WaitForElement($"[data-testid='{DialogMarker}']");
        return provider;
    }

    /// <summary>
    /// A structural class with three auxiliary classes on offer: one the directory permits (suggested), and two
    /// nothing suggests. The permitted one carries a credential attribute among its contributions.
    /// </summary>
    private static ConnectedSystem ConnectedSystemWithAuxiliaryClasses()
    {
        var inetOrgPerson = new ConnectedSystemObjectType { Id = 1, Name = "inetOrgPerson", Selected = true };
        inetOrgPerson.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural });
        inetOrgPerson.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassMembershipAttribute, Value = "objectClass" });
        inetOrgPerson.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.PermittedAuxiliaryClass, Value = "posixAccount" });

        var posixAccount = AuxiliaryClass(2, "posixAccount");
        posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 10, Name = "uidNumber", Type = AttributeDataType.Number, Required = true });
        posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "gidNumber", Type = AttributeDataType.Number, Required = true });
        posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "userPassword", Type = AttributeDataType.Text });

        var shadowAccount = AuxiliaryClass(3, "shadowAccount");
        shadowAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 20, Name = "shadowExpire", Type = AttributeDataType.Number });

        var sambaSamAccount = AuxiliaryClass(4, "sambaSamAccount");
        sambaSamAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 30, Name = "sambaSID", Type = AttributeDataType.Text });

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Corp LDAP",
            ObjectTypes = [inetOrgPerson, posixAccount, shadowAccount, sambaSamAccount]
        };
    }

    private static ConnectedSystemObjectType AuxiliaryClass(int id, string name)
    {
        var objectType = new ConnectedSystemObjectType { Id = id, Name = name };
        objectType.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindAuxiliary });
        return objectType;
    }

    #endregion
}
