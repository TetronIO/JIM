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
/// Covers the settings tab's rendering of a connector whose settings become relevant conditionally
/// (a <c>RequiredWhen</c> drop-down selecting between mutually exclusive authentication methods).
/// </summary>
/// <remarks>
/// This is a component test because the defect it guards is one: the fields are rendered in a loop and
/// the set of them changes when the controlling drop-down changes, so without an identity Blazor
/// re-parameterises the existing component instances positionally rather than creating and destroying
/// them. The validation state a field acquired while it was one setting then survives onto the setting
/// that took its place. No plain unit test can reach it, and every field in isolation renders perfectly.
/// </remarks>
[TestFixture]
public class ConnectedSystemSettingsTabTests : JimComponentTestContext
{
    private const string AuthenticationMethod = "Authentication Method";
    private const string OAuth = "OAuth 2.0 Client Credentials";
    private const string BearerToken = "Static Bearer Token";
    private const string TokenEndpointUrl = "Token Endpoint URL";

    public ConnectedSystemSettingsTabTests()
    {
        // Registered in the constructor rather than a SetUp: bUnit builds its service provider on the
        // first render and refuses registrations after that.
        Services.AddSingleton<IJimApplicationFactory>(new UnusedJimApplicationFactory());
    }

    /// <summary>
    /// The tab only reaches the application layer for a persisted Connected System, and every system in
    /// this fixture is unsaved, so this exists to satisfy the injection and throws if that ever changes
    /// rather than quietly returning something unusable.
    /// </summary>
    private sealed class UnusedJimApplicationFactory : IJimApplicationFactory
    {
        public JimApplication Create() =>
            throw new InvalidOperationException("The settings tab reached the application layer for an unsaved Connected System, which it should not do.");
    }

    /// <summary>
    /// A connector shaped like the SCIM one: a method drop-down, a setting required by only one of its
    /// values, and an always-relevant setting immediately after it to be mistaken for the first.
    /// </summary>
    private static ConnectedSystem ConnectedSystemWithConditionalSettings(string authenticationMethod)
    {
        var settings = new List<ConnectorDefinitionSetting>
        {
            new()
            {
                Id = 1,
                Name = AuthenticationMethod,
                Required = true,
                Type = ConnectedSystemSettingType.DropDown,
                Category = ConnectedSystemSettingCategory.Connectivity,
                DropDownValues = [OAuth, BearerToken]
            },
            new()
            {
                Id = 2,
                Name = TokenEndpointUrl,
                Type = ConnectedSystemSettingType.String,
                Category = ConnectedSystemSettingCategory.Connectivity,
                RequiredWhenSetting = AuthenticationMethod,
                RequiredWhenValue = OAuth
            },
            new()
            {
                Id = 3,
                Name = "OAuth Scope",
                Type = ConnectedSystemSettingType.String,
                Category = ConnectedSystemSettingCategory.Connectivity
            },
            new()
            {
                Id = 4,
                Name = "Bearer Token",
                Type = ConnectedSystemSettingType.String,
                Category = ConnectedSystemSettingCategory.Connectivity,
                RequiredWhenSetting = AuthenticationMethod,
                RequiredWhenValue = BearerToken
            }
        };

        var connectedSystem = new ConnectedSystem { Name = "SCIM", SettingValues = [] };
        foreach (var setting in settings)
        {
            connectedSystem.SettingValues.Add(new ConnectedSystemSettingValue
            {
                Id = setting.Id,
                Setting = setting,
                StringValue = setting.Name == AuthenticationMethod ? authenticationMethod : null
            });
        }

        return connectedSystem;
    }

    private IRenderedComponent<ConnectedSystemSettingsTab> RenderTab(ConnectedSystem connectedSystem)
    {
        return Render<ConnectedSystemSettingsTab>(p => p
            .Add(c => c.ConnectedSystem, connectedSystem)
            .Add(c => c.SettingCategories, [ConnectedSystemSettingCategory.Connectivity]));
    }

    [Test]
    public void SettingsTab_WhileTheControllingValueSelectsIt_ShowsTheConditionalSetting()
    {
        var cut = RenderTab(ConnectedSystemWithConditionalSettings(OAuth));

        Assert.That(cut.Markup, Does.Contain(TokenEndpointUrl));
    }

    [Test]
    public void SettingsTab_WhenTheControllingValueChanges_DoesNotCarryTheOldSettingsValidationOntoItsNeighbour()
    {
        // The failure this guards is not cosmetic. The stale "A Token Endpoint URL is required" error
        // stays in the form's validation state, so Save Settings is disabled for ever and the Connected
        // System cannot be configured with any authentication method but the default one.
        var connectedSystem = ConnectedSystemWithConditionalSettings(OAuth);
        var cut = RenderTab(connectedSystem);

        connectedSystem.SettingValues.Single(v => v.Setting.Name == AuthenticationMethod).StringValue = BearerToken;
        cut.Render();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain($"A {TokenEndpointUrl} is required"));
            Assert.That(cut.Markup, Does.Not.Contain(TokenEndpointUrl), "the setting is no longer relevant, so it should not be rendered at all");
        });
    }
}
