// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.TestSupport;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A Connector setting whose feature is behind a flag (<see cref="ConnectorSetting.FeatureFlagKey"/>, #2021) is left off
/// the settings tab while that flag is off, so an In Development feature is never offered to an administrator.
/// </summary>
[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class ConnectedSystemSettingsTabFeatureFlagTests : JimComponentTestContext
{
    private const string FlaggedSetting = "Manage Exchange recipients";
    private readonly Mock<IRepository> _repository = new();

    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_repository.Object));
    }

    [Test]
    public void SettingsTab_FlagOff_LeavesTheFlaggedSettingOut()
    {
        _repository.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());

        var cut = RenderTab();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Delete Behaviour"));
            Assert.That(cut.Markup, Does.Not.Contain(FlaggedSetting));
        }
    }

    [Test]
    public void SettingsTab_FlagOn_ShowsTheFlaggedSetting()
    {
        _repository.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());

        var cut = RenderTab();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain(FlaggedSetting)));
    }

    private IRenderedComponent<ConnectedSystemSettingsTab> RenderTab()
    {
        var settings = new List<ConnectorDefinitionSetting>
        {
            new() { Id = 1, Name = "Delete Behaviour", Type = ConnectedSystemSettingType.String, Category = ConnectedSystemSettingCategory.Export },
            new()
            {
                Id = 2, Name = FlaggedSetting, Type = ConnectedSystemSettingType.CheckBox, Category = ConnectedSystemSettingCategory.Export,
                FeatureFlagKey = FeatureFlagCatalogue.ExchangeRecipients.Key
            }
        };
        // Unsaved, so the tab reads nothing for the system itself: only the flag decides what it shows.
        var connectedSystem = new ConnectedSystem
        {
            Name = "PANOPLY",
            SettingValues = settings.Select(s => new ConnectedSystemSettingValue { Id = s.Id, Setting = s }).ToList()
        };

        return Render<ConnectedSystemSettingsTab>(p => p
            .Add(c => c.ConnectedSystem, connectedSystem)
            .Add(c => c.SettingCategories, [ConnectedSystemSettingCategory.Export]));
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
