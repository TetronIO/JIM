// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Extensions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Run Profiles tab: its Preview action (#1530), and the "Allow Object Type Changes" setting (#2049).
/// <para>
/// The Preview action is offered on Full Synchronisation Run Profiles only: the preview walks the whole population as
/// a Full Synchronisation does, so it says nothing true about an import, an export or a Delta Synchronisation, which
/// process other things.
/// </para>
/// <para>
/// "Allow Object Type Changes" is offered on a Full Import only, and only where the Connector vouches that an External
/// ID names one object across every Object Type: anywhere else the import cannot tell an Object Type change from two
/// objects sharing a value, so the setting would do nothing. A Run Profile that already allows the change is the one
/// exception, because a value that changes what a run does must never be hidden from the person editing it.
/// </para>
/// </summary>
[TestFixture]
public class ConnectedSystemRunProfilesTabTests : JimComponentTestContext
{
    private const string PreviewMarker = "jim-run-profile-preview";
    private const string CreateButtonMarker = "jim-run-profile-create";
    private const string CreateSubmitMarker = "jim-run-profile-create-submit";
    private const string EditButtonMarker = "jim-run-profile-edit";
    private const string EditSubmitMarker = "jim-run-profile-edit-submit";
    private const string AllowObjectTypeChangesMarker = "jim-allow-object-type-changes";
    private const string NoEffectNoteMarker = "jim-allow-object-type-changes-no-effect";
    private const string AllowObjectTypeChangesChipMarker = "jim-run-profile-allows-object-type-changes";

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private ConnectedSystem? _serverConnectedSystem;

    protected override void ConfigureAdditionalServices()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        var mockMetaverseRepo = new Mock<IMetaverseRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        mockRepository.Setup(r => r.Activity).Returns(new Mock<IActivityRepository>().Object);
        mockRepository.Setup(r => r.Metaverse).Returns(mockMetaverseRepo.Object);

        // The server re-reads the Connected System to check the Run Profile is valid for its Connector; it gets the
        // same system the tab was rendered with.
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync(() => _serverConnectedSystem);

        var administratorId = Guid.NewGuid();
        AddAuthorization().SetAuthorized("Ada Lovelace").SetClaims(new Claim(Constants.BuiltInClaims.MetaverseObjectId, administratorId.ToString()));
        mockMetaverseRepo.Setup(r => r.GetMetaverseObjectAsync(administratorId))
            .ReturnsAsync(new MetaverseObject { Id = administratorId, CachedDisplayName = "Ada Lovelace" });

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(mockRepository.Object));
        Services.AddSingleton<IUserPreferenceService>(new Mock<IUserPreferenceService>().Object);
    }

    [TearDown]
    public void TearDown()
    {
        _serverConnectedSystem = null;
    }

    [Test]
    public void RunProfilesTab_PreviewAction_IsOfferedOnFullSynchronisationRunProfilesOnly()
    {
        ConnectedSystemRunProfile? requested = null;

        var cut = Render<ConnectedSystemRunProfilesTab>(p => p
            .Add(x => x.ConnectedSystem, System())
            .Add(x => x.OnPreviewRequested, profile => requested = profile));
        cut.WaitForState(() => cut.Markup.Contains("Delta Synchronisation"), TimeSpan.FromSeconds(2));

        var previews = cut.FindAll($"[data-testid='{PreviewMarker}']");
        Assert.That(previews, Has.Count.EqualTo(1), "one Full Synchronisation Run Profile among four");
        previews[0].Click();
        Assert.That(requested?.RunType, Is.EqualTo(ConnectedSystemRunType.FullSynchronisation));
    }

    // ─── Allow Object Type Changes: when it is offered ───

    [Test]
    public void CreateDialog_FullImportWhereTheConnectorVouchesForExternalIds_OffersAllowObjectTypeChanges()
    {
        var (provider, _) = OpenCreateDialog(DirectorySystem());

        ChooseRunType(provider, ConnectedSystemRunType.FullImport);

        provider.WaitForAssertion(() => Assert.That(FindAllowObjectTypeChanges(provider), Is.Not.Null));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FindAllowObjectTypeChanges(provider)!.Instance.GetState(x => x.Value), Is.False, "off unless the administrator turns it on");
            Assert.That(FindByMarker(provider, NoEffectNoteMarker), Is.Null, "the setting takes effect here");
        }
    }

    [Test]
    public void CreateDialog_FullImportWhereTheConnectorDoesNotVouchForExternalIds_DoesNotOfferAllowObjectTypeChanges()
    {
        var (provider, _) = OpenCreateDialog(TableSystem());

        ChooseRunType(provider, ConnectedSystemRunType.FullImport);

        // The Full Import block is on screen (its Verification Mode checkbox has rendered), so the setting's
        // absence is a decision rather than a render that has not happened yet.
        provider.WaitForAssertion(() => Assert.That(provider.FindComponents<MudCheckBox<bool>>(), Is.Not.Empty));
        Assert.That(FindAllowObjectTypeChanges(provider), Is.Null);
    }

    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.Export)]
    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    public void CreateDialog_RunTypeChangedAwayFromFullImport_WithdrawsAllowObjectTypeChanges(ConnectedSystemRunType runType)
    {
        var (provider, _) = OpenCreateDialog(DirectorySystem());
        ChooseRunType(provider, ConnectedSystemRunType.FullImport);
        provider.WaitForAssertion(() => Assert.That(FindAllowObjectTypeChanges(provider), Is.Not.Null));

        ChooseRunType(provider, runType);

        provider.WaitForAssertion(() => Assert.That(FindAllowObjectTypeChanges(provider), Is.Null));
    }

    [Test]
    public void EditDialog_FullImportWhereTheConnectorVouchesForExternalIds_OffersAllowObjectTypeChanges()
    {
        var provider = OpenEditDialog(DirectorySystem(FullImport(allowObjectTypeChanges: false)));

        provider.WaitForAssertion(() => Assert.That(FindAllowObjectTypeChanges(provider), Is.Not.Null));
        Assert.That(FindAllowObjectTypeChanges(provider)!.Instance.GetState(x => x.Value), Is.False);
    }

    [Test]
    public void EditDialog_FullImportWhereTheConnectorDoesNotVouchForExternalIds_DoesNotOfferAllowObjectTypeChanges()
    {
        var provider = OpenEditDialog(TableSystem(FullImport(allowObjectTypeChanges: false)));

        provider.WaitForAssertion(() => Assert.That(provider.FindComponents<MudCheckBox<bool>>(), Is.Not.Empty));
        Assert.That(FindAllowObjectTypeChanges(provider), Is.Null);
    }

    [Test]
    public void EditDialog_RunProfileAlreadyAllowingObjectTypeChanges_ShowsTheSettingWhereTheConnectorDoesNotVouch()
    {
        // Set through the API on a Connector that cannot honour it: it has no effect, but hiding it would leave the
        // administrator unable to see, or clear, a value the Run Profile holds.
        var provider = OpenEditDialog(TableSystem(FullImport(allowObjectTypeChanges: true)));

        provider.WaitForAssertion(() => Assert.That(FindAllowObjectTypeChanges(provider), Is.Not.Null));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FindAllowObjectTypeChanges(provider)!.Instance.GetState(x => x.Value), Is.True);
            Assert.That(FindByMarker(provider, NoEffectNoteMarker), Is.Not.Null, "says the setting does nothing on this Connector");
        }
    }

    [Test]
    public void EditDialog_SettingClearedWhereTheConnectorDoesNotVouch_StaysOnScreenUntilSaved()
    {
        var provider = OpenEditDialog(TableSystem(FullImport(allowObjectTypeChanges: true)));
        provider.WaitForAssertion(() => Assert.That(FindAllowObjectTypeChanges(provider), Is.Not.Null));

        SetAllowObjectTypeChanges(provider, false);

        // Visibility follows the saved value, not the box being edited, so unticking it does not make it vanish.
        provider.WaitForAssertion(() => Assert.That(FindAllowObjectTypeChanges(provider)!.Instance.GetState(x => x.Value), Is.False));
    }

    // ─── Allow Object Type Changes: what is saved ───

    [Test]
    public void CreateDialog_FullImportWithTheSettingTicked_SavesItOn()
    {
        var system = DirectorySystem();
        var (provider, _) = OpenCreateDialog(system);
        ChooseRunType(provider, ConnectedSystemRunType.FullImport);
        provider.WaitForAssertion(() => Assert.That(FindAllowObjectTypeChanges(provider), Is.Not.Null));
        SetAllowObjectTypeChanges(provider, true);

        Submit(provider, CreateSubmitMarker);

        _mockConnectedSystemRepo.Verify(r => r.CreateConnectedSystemRunProfileAsync(It.Is<ConnectedSystemRunProfile>(rp =>
            rp.RunType == ConnectedSystemRunType.FullImport && rp.AllowObjectTypeChanges)), Times.Once);
    }

    [Test]
    public void CreateDialog_SettingTickedThenRunTypeChangedAwayFromFullImport_SavesItOff()
    {
        // The box disappears with the Full Import block, but the value it held must not travel with the Run Profile:
        // the setting means nothing outside a Full Import, and the server refuses it there.
        var system = DirectorySystem();
        var (provider, _) = OpenCreateDialog(system);
        ChooseRunType(provider, ConnectedSystemRunType.FullImport);
        provider.WaitForAssertion(() => Assert.That(FindAllowObjectTypeChanges(provider), Is.Not.Null));
        SetAllowObjectTypeChanges(provider, true);
        ChooseRunType(provider, ConnectedSystemRunType.DeltaImport);
        provider.WaitForAssertion(() => Assert.That(FindAllowObjectTypeChanges(provider), Is.Null));

        Submit(provider, CreateSubmitMarker);

        _mockConnectedSystemRepo.Verify(r => r.CreateConnectedSystemRunProfileAsync(It.Is<ConnectedSystemRunProfile>(rp =>
            rp.RunType == ConnectedSystemRunType.DeltaImport && !rp.AllowObjectTypeChanges)), Times.Once);
    }

    [Test]
    public void EditDialog_SettingTicked_SavesItOn()
    {
        var runProfile = FullImport(allowObjectTypeChanges: false);
        var provider = OpenEditDialog(DirectorySystem(runProfile));
        provider.WaitForAssertion(() => Assert.That(FindAllowObjectTypeChanges(provider), Is.Not.Null));
        SetAllowObjectTypeChanges(provider, true);

        Submit(provider, EditSubmitMarker);

        _mockConnectedSystemRepo.Verify(r => r.UpdateConnectedSystemRunProfileAsync(It.Is<ConnectedSystemRunProfile>(rp =>
            rp.Id == runProfile.Id && rp.AllowObjectTypeChanges)), Times.Once);
    }

    [Test]
    public void EditDialog_SettingCleared_SavesItOff()
    {
        var runProfile = FullImport(allowObjectTypeChanges: true);
        var provider = OpenEditDialog(DirectorySystem(runProfile));
        provider.WaitForAssertion(() => Assert.That(FindAllowObjectTypeChanges(provider), Is.Not.Null));
        SetAllowObjectTypeChanges(provider, false);

        Submit(provider, EditSubmitMarker);

        _mockConnectedSystemRepo.Verify(r => r.UpdateConnectedSystemRunProfileAsync(It.Is<ConnectedSystemRunProfile>(rp =>
            rp.Id == runProfile.Id && !rp.AllowObjectTypeChanges)), Times.Once);
    }

    [Test]
    public void EditDialog_NonFullImportRunProfileHoldingTheSetting_SavesItOff()
    {
        // No valid path leaves a Delta Import holding the setting, but a save from the portal must never send it on
        // one: the server would refuse the whole update, leaving the Run Profile impossible to edit.
        var runProfile = new ConnectedSystemRunProfile
        {
            Id = 11, Name = "Delta Import", RunType = ConnectedSystemRunType.DeltaImport, ConnectedSystemId = 9, AllowObjectTypeChanges = true
        };
        var provider = OpenEditDialog(DirectorySystem(runProfile));
        provider.WaitForAssertion(() => Assert.That(FindByMarker(provider, EditSubmitMarker), Is.Not.Null));

        Submit(provider, EditSubmitMarker);

        _mockConnectedSystemRepo.Verify(r => r.UpdateConnectedSystemRunProfileAsync(It.Is<ConnectedSystemRunProfile>(rp =>
            rp.Id == runProfile.Id && !rp.AllowObjectTypeChanges)), Times.Once);
    }

    // ─── Allow Object Type Changes: the grid ───

    [Test]
    public void Grid_RunProfileAllowingObjectTypeChanges_IsMarked()
    {
        var allowing = FullImport(allowObjectTypeChanges: true);
        var refusing = new ConnectedSystemRunProfile { Id = 12, Name = "Full Import (refuse)", RunType = ConnectedSystemRunType.FullImport, ConnectedSystemId = 9 };

        var cut = Render<ConnectedSystemRunProfilesTab>(p => p.Add(x => x.ConnectedSystem, DirectorySystem(allowing, refusing)));
        cut.WaitForAssertion(() => Assert.That(cut.FindAll($"[data-testid='{EditButtonMarker}']"), Has.Count.EqualTo(2)));

        var marked = cut.FindComponents<MudChip<string>>().Where(c => HasMarker(c.Instance.UserAttributes, AllowObjectTypeChangesChipMarker)).ToList();
        Assert.That(marked, Has.Count.EqualTo(1), "only the Run Profile that allows Object Type changes is marked");
    }

    // ─── Helpers ───

    private (IRenderedComponent<MudDialogProvider> Provider, IRenderedComponent<ConnectedSystemRunProfilesTab> Tab) OpenCreateDialog(ConnectedSystem system)
    {
        _serverConnectedSystem = system;
        var provider = Render<MudDialogProvider>();
        var tab = Render<ConnectedSystemRunProfilesTab>(p => p.Add(x => x.ConnectedSystem, system));
        tab.WaitForElement($"[data-testid='{CreateButtonMarker}']").Click();
        provider.WaitForAssertion(() => Assert.That(provider.HasComponent<MudSelect<ConnectedSystemRunType>>(), Is.True));
        return (provider, tab);
    }

    private IRenderedComponent<MudDialogProvider> OpenEditDialog(ConnectedSystem system)
    {
        _serverConnectedSystem = system;
        var provider = Render<MudDialogProvider>();
        var tab = Render<ConnectedSystemRunProfilesTab>(p => p.Add(x => x.ConnectedSystem, system));
        tab.WaitForElement($"[data-testid='{EditButtonMarker}']").Click();
        provider.WaitForAssertion(() => Assert.That(FindByMarker(provider, EditSubmitMarker), Is.Not.Null));
        return provider;
    }

    /// <summary>
    /// Chooses a Run Type in the create dialog. The dialog is inline, so its content renders inside the provider; the
    /// select's callback re-renders the tab, which re-renders the dialog.
    /// </summary>
    private static void ChooseRunType(IRenderedComponent<MudDialogProvider> provider, ConnectedSystemRunType runType)
    {
        var select = provider.FindComponent<MudSelect<ConnectedSystemRunType>>();
        provider.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(runType)).GetAwaiter().GetResult();
    }

    private static void SetAllowObjectTypeChanges(IRenderedComponent<MudDialogProvider> provider, bool value)
    {
        var checkBox = FindAllowObjectTypeChanges(provider)!;
        provider.InvokeAsync(() => checkBox.Instance.ValueChanged.InvokeAsync(value)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Invokes a dialog's save button directly. The button is gated on the form's validity, which MudForm settles
    /// only once each required field has been touched in a browser; what these tests pin is what the save sends,
    /// not the gating, so they go straight to the button's handler.
    /// </summary>
    private static void Submit(IRenderedComponent<MudDialogProvider> provider, string marker)
    {
        var button = provider.FindComponents<MudButton>().Single(b => HasMarker(b.Instance.UserAttributes, marker));
        provider.InvokeAsync(() => button.Instance.OnClick.InvokeAsync(new MouseEventArgs())).GetAwaiter().GetResult();
    }

    private static IRenderedComponent<MudCheckBox<bool>>? FindAllowObjectTypeChanges(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindComponents<MudCheckBox<bool>>().SingleOrDefault(c => HasMarker(c.Instance.UserAttributes, AllowObjectTypeChangesMarker));

    private static AngleSharp.Dom.IElement? FindByMarker(IRenderedComponent<MudDialogProvider> provider, string marker) =>
        provider.FindAll($"[data-testid='{marker}']").FirstOrDefault();

    private static bool HasMarker(IDictionary<string, object?>? attributes, string marker) =>
        attributes != null && attributes.TryGetValue("data-testid", out var value) && Equals(value, marker);

    private static ConnectedSystemRunProfile FullImport(bool allowObjectTypeChanges) => new()
    {
        Id = 10, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, ConnectedSystemId = 9, AllowObjectTypeChanges = allowObjectTypeChanges
    };

    /// <summary>A directory, whose Connector vouches that an External ID names one object across every Object Type.</summary>
    private static ConnectedSystem DirectorySystem(params ConnectedSystemRunProfile[] runProfiles) =>
        SystemOf(9, "Panoply Directory", externalIdsUniqueAcrossObjectTypes: true, runProfiles);

    /// <summary>A database, whose Connector cannot vouch for that: a view can share its table's keys.</summary>
    private static ConnectedSystem TableSystem(params ConnectedSystemRunProfile[] runProfiles) =>
        SystemOf(9, "Glitterband Finance", externalIdsUniqueAcrossObjectTypes: false, runProfiles);

    private static ConnectedSystem SystemOf(int id, string name, bool externalIdsUniqueAcrossObjectTypes, ConnectedSystemRunProfile[] runProfiles) => new()
    {
        Id = id,
        Name = name,
        ConnectorDefinition = new ConnectorDefinition
        {
            Name = name,
            SupportsFullImport = true,
            SupportsDeltaImport = true,
            SupportsExport = true,
            ExternalIdsUniqueAcrossObjectTypes = externalIdsUniqueAcrossObjectTypes
        },
        RunProfiles = runProfiles.ToList()
    };

    private static ConnectedSystem System() => new()
    {
        Id = 7,
        Name = "Yellowstone HR",
        ConnectorDefinition = new ConnectorDefinition { Name = "CSV" },
        RunProfiles =
        [
            new ConnectedSystemRunProfile { Id = 1, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, ConnectedSystemId = 7 },
            new ConnectedSystemRunProfile { Id = 2, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation, ConnectedSystemId = 7 },
            new ConnectedSystemRunProfile { Id = 3, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation, ConnectedSystemId = 7 },
            new ConnectedSystemRunProfile { Id = 4, Name = "Export", RunType = ConnectedSystemRunType.Export, ConnectedSystemId = 7 }
        ]
    };

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
