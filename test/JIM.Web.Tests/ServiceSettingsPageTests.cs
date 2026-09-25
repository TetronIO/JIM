// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Web.Pages.Admin;
using JIM.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Service Settings list is reached two ways, and the second is easy to break silently. An Activity whose target
/// is a Service Setting links to <c>/admin/settings?search=&lt;name&gt;</c>, a parameter that predates the virtualised
/// grid, which owns its own search under <c>?q=</c>. If the page stops translating one into the other, the link still
/// resolves, the page still renders, and the setting the reader was sent to is simply somewhere in an unfiltered list:
/// a regression nothing fails over.
/// </summary>
[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class ServiceSettingsPageTests : JimComponentTestContext
{
    private Mock<IServiceSettingsRepository> _serviceSettingsRepository = null!;
    private Mock<IWebHostEnvironment> _hostEnvironment = null!;
    private NavigationManager _navigation = null!;

    [SetUp]
    public void SetUp()
    {
        _serviceSettingsRepository = new Mock<IServiceSettingsRepository>();
        _serviceSettingsRepository.Setup(r => r.GetAllSettingsAsync()).ReturnsAsync(
        [
            new ServiceSetting
            {
                Key = "SSO.Authority",
                DisplayName = "SSO Authority",
                Category = ServiceSettingCategory.SSO,
                ValueType = ServiceSettingValueType.String,
                Description = "Where JIM sends people to sign in."
            }
        ]);
        // Feature flags (#1781) are rows in this same list now, not a separate card; a lookup miss here is fine
        // for tests that do not care about them.
        _serviceSettingsRepository.Setup(r => r.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((ServiceSetting?)null);

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ServiceSettings).Returns(_serviceSettingsRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
        Services.AddSingleton(new Mock<ICredentialProtectionService>().Object);

        // Production by default: most of these tests are about the deep-link/grid behaviour, not the In
        // Development flag-visibility rule (#1781), so the environment is fixed rather than left unset. bUnit's
        // service provider locks after _navigation below resolves it, so a test needing Development calls
        // UseDevelopmentHost() to mutate this same mock rather than registering a new one.
        _hostEnvironment = new Mock<IWebHostEnvironment>();
        _hostEnvironment.Setup(e => e.EnvironmentName).Returns("Production");
        Services.AddSingleton(_hostEnvironment.Object);

        _navigation = Services.GetRequiredService<NavigationManager>();
    }

    [Test]
    public void Settings_DeepLinkedWithASearchParameter_HandsItToTheGridsOwnSearch()
    {
        _navigation.NavigateTo("/admin/settings?search=SSO%20Authority");

        Render<Settings>();

        Assert.That(_navigation.Uri, Does.Contain("q=SSO"),
            "the ?search= deep link must become the grid's ?q=, or the reader lands on an unfiltered list");
    }

    [Test]
    public void Settings_DeepLinkedWithASearchParameter_KeepsTheOtherQueryParametersWithIt()
    {
        // The grid writes its own sort and scroll position into the same query string, and a page that rebuilt the
        // URL from the search term alone would throw away whatever else the link carried.
        _navigation.NavigateTo("/admin/settings?search=SSO%20Authority&sort=setting&desc=true");

        Render<Settings>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_navigation.Uri, Does.Contain("sort=setting"));
            Assert.That(_navigation.Uri, Does.Contain("desc=true"));
            Assert.That(_navigation.Uri, Does.Not.Contain("search="),
                "the superseded parameter must be dropped, or a later render would translate it a second time");
        }
    }

    [Test]
    public void Settings_ReachedWithoutASearchParameter_LoadsTheSettingsRatherThanNavigating()
    {
        _navigation.NavigateTo("/admin/settings");

        var page = Render<Settings>();

        page.WaitForAssertion(() => Assert.That(page.Markup, Does.Contain("SSO Authority")));
        _serviceSettingsRepository.Verify(r => r.GetAllSettingsAsync(), Times.AtLeastOnce);
    }

    /// <summary>
    /// Feature flags (#1781) are ordinary Service Settings rows under the "Preview Features" category, rather
    /// than the removed Preview features card. The real <see cref="FeatureFlagCatalogue"/> carries one entry
    /// today, <see cref="FeatureFlagCatalogue.UniqueValueGeneration"/>, which is In Development tier: these
    /// tests prove it via real rendering (Preview-tier "always visible" is proven separately, against a
    /// synthetic definition, in <c>HelpersFeatureFlagVisibilityTests</c>, since the real catalogue has no
    /// Preview-tier entry to render).
    /// </summary>
    private static ServiceSetting InDevelopmentFlagSetting(bool overridden = false) => new()
    {
        Key = FeatureFlagCatalogue.UniqueValueGeneration.Key,
        DisplayName = FeatureFlagCatalogue.UniqueValueGeneration.DisplayName,
        Description = FeatureFlagCatalogue.UniqueValueGeneration.Description,
        Category = ServiceSettingCategory.FeatureFlags,
        ValueType = ServiceSettingValueType.Boolean,
        DefaultValue = "false",
        Value = overridden ? "true" : null,
        IsReadOnly = false
    };

    /// <summary>
    /// Switches the already-registered <see cref="IWebHostEnvironment"/> mock to Development before rendering.
    /// Mutating the mock's own setup, rather than registering a new mock, is what makes this callable from a
    /// test body: bUnit's service provider locks for further registration once <c>_navigation</c> resolves it
    /// in <see cref="SetUp"/>, which runs before every test.
    /// </summary>
    private void UseDevelopmentHost() => _hostEnvironment.Setup(e => e.EnvironmentName).Returns("Development");

    [Test]
    public void Settings_InDevelopmentFlagRow_HiddenOnProductionHost()
    {
        // SetUp already fixes the host environment to Production; the flag row must never render there.
        _serviceSettingsRepository.Setup(r => r.GetAllSettingsAsync()).ReturnsAsync(
        [
            new ServiceSetting
            {
                Key = "SSO.Authority",
                DisplayName = "SSO Authority",
                Category = ServiceSettingCategory.SSO,
                ValueType = ServiceSettingValueType.String,
                Description = "Where JIM sends people to sign in."
            },
            InDevelopmentFlagSetting()
        ]);

        _navigation.NavigateTo("/admin/settings");
        var page = Render<Settings>();

        page.WaitForAssertion(() => Assert.That(page.Markup, Does.Contain("SSO Authority")));
        Assert.That(page.Markup, Does.Not.Contain(FeatureFlagCatalogue.UniqueValueGeneration.DisplayName),
            "an In Development flag must never appear on a Production host");
    }

    [Test]
    public void Settings_InDevelopmentFlagRow_VisibleOnDevelopmentHost_UnderPreviewFeaturesWithItsChip()
    {
        UseDevelopmentHost();
        _serviceSettingsRepository.Setup(r => r.GetAllSettingsAsync()).ReturnsAsync([InDevelopmentFlagSetting()]);

        _navigation.NavigateTo("/admin/settings");
        var page = Render<Settings>();

        page.WaitForAssertion(() => Assert.That(page.Markup, Does.Contain(FeatureFlagCatalogue.UniqueValueGeneration.DisplayName)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Markup, Does.Contain("Preview Features"),
                "the Feature Flags category must display as 'Preview Features'");
            Assert.That(page.Markup, Does.Contain("In development"),
                "an In Development flag carries its own chip, distinct from a Preview one");
        }
    }

    [Test]
    public void Settings_OverriddenFlagRow_ReportsStatusAndActionsLikeAnyOtherBooleanSetting()
    {
        // Value/Status/Actions need no feature-flag-specific rendering: IsOverridden, IsReadOnly and ValueType
        // already drive them generically for every Service Setting, flags included.
        UseDevelopmentHost();
        _serviceSettingsRepository.Setup(r => r.GetAllSettingsAsync()).ReturnsAsync([InDevelopmentFlagSetting(overridden: true)]);

        _navigation.NavigateTo("/admin/settings");
        var page = Render<Settings>();

        page.WaitForAssertion(() => Assert.That(page.Markup, Does.Contain(FeatureFlagCatalogue.UniqueValueGeneration.DisplayName)));

        // MudTooltip does not render its Text into static markup (it is JS-driven, on hover), so the Actions
        // cell's button count is what actually distinguishes "has a revert action" from "does not": Edit,
        // Revert and Change history for an overridden, non-read-only setting, versus Edit and Change history
        // alone for one at its default.
        var actionsCell = page.Find("td[data-label='Actions']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Markup, Does.Contain("Modified"),
                "an overridden flag reports Modified status exactly like any other overridden setting");
            Assert.That(actionsCell.QuerySelectorAll("button"), Has.Count.EqualTo(3),
                "an overridden, non-read-only flag gets the same Edit/Revert/Change history actions as any other setting");
        }
    }

    [Test]
    public void Settings_ShowPreviewFeaturesOnlyFilter_LimitsTheTableToFeatureFlagRows()
    {
        UseDevelopmentHost();
        _serviceSettingsRepository.Setup(r => r.GetAllSettingsAsync()).ReturnsAsync(
        [
            new ServiceSetting
            {
                Key = "SSO.Authority",
                DisplayName = "SSO Authority",
                Category = ServiceSettingCategory.SSO,
                ValueType = ServiceSettingValueType.String,
                Description = "Where JIM sends people to sign in."
            },
            InDevelopmentFlagSetting()
        ]);

        _navigation.NavigateTo("/admin/settings");
        var page = Render<Settings>();

        page.WaitForAssertion(() => Assert.That(page.Markup, Does.Contain("SSO Authority")));
        Assert.That(page.Markup, Does.Contain(FeatureFlagCatalogue.UniqueValueGeneration.DisplayName));

        var checkboxLabel = page.FindAll("label")
            .Single(l => l.TextContent.Trim() == "Show preview features only");
        var checkboxInput = checkboxLabel.Closest(".mud-checkbox")!.QuerySelector("input[type=checkbox]")!;
        checkboxInput.Change(true);

        page.WaitForAssertion(() => Assert.That(page.Markup, Does.Not.Contain("SSO Authority"),
            "the filter must remove non-flag rows"));
        Assert.That(page.Markup, Does.Contain(FeatureFlagCatalogue.UniqueValueGeneration.DisplayName),
            "the filter must keep the Preview Features row it exists to show");
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
