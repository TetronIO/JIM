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

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ServiceSettings).Returns(_serviceSettingsRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
        Services.AddSingleton(new Mock<ICredentialProtectionService>().Object);

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

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
