// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.TestScimServiceProvider;
using Microsoft.Extensions.Configuration;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How the containerised test service provider is configured for Integration Scenario 015. The scenario's
/// multi-valued removal step (#2181) needs the custom User extension, so the provider publishes it unless told not to.
/// </summary>
[TestFixture]
public class ScimTestProviderSettingsTests
{
    private static MockScimProvider Configure(params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

        return ScimTestProviderSettings.From(configuration).Configure(new MockScimProvider());
    }

    [Test]
    public void Configure_NoCustomUserExtensionSwitch_PublishesTheExtension()
    {
        var provider = Configure();

        Assert.That(provider.Options.PublishesCustomUserExtension, Is.True);
    }

    [Test]
    public void Configure_CustomUserExtensionSwitchedOff_DoesNotPublishTheExtension()
    {
        var provider = Configure(("SCIM_PUBLISHES_CUSTOM_USER_EXTENSION", "false"));

        Assert.That(provider.Options.PublishesCustomUserExtension, Is.False);
    }
}
