// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim.Discovery;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// Resolving a provider's optional capabilities from its ServiceProviderConfig, and what happens when
/// it does not publish one.
/// </summary>
public class ScimProviderCapabilitiesTests
{
    [Test]
    public void From_FullyCapableProvider_ReportsEveryFeatureAndItsLimits()
    {
        var config = new ScimServiceProviderConfig
        {
            Patch = new ScimSupportedFeature { Supported = true },
            Bulk = new ScimBulkFeature { Supported = true, MaxOperations = 1000, MaxPayloadSize = 1048576 },
            Filter = new ScimFilterFeature { Supported = true, MaxResults = 200 },
            Sort = new ScimSupportedFeature { Supported = true },
            ETag = new ScimSupportedFeature { Supported = true },
            ChangePassword = new ScimSupportedFeature { Supported = true },
            AuthenticationSchemes = [new ScimAuthenticationScheme { Type = "oauthbearertoken" }]
        };

        var capabilities = ScimProviderCapabilities.From(config);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capabilities.DiscoveryAvailable, Is.True);
            Assert.That(capabilities.SupportsPatch, Is.True);
            Assert.That(capabilities.SupportsBulk, Is.True);
            Assert.That(capabilities.BulkMaxOperations, Is.EqualTo(1000));
            Assert.That(capabilities.BulkMaxPayloadSize, Is.EqualTo(1048576));
            Assert.That(capabilities.SupportsFilter, Is.True);
            Assert.That(capabilities.FilterMaxResults, Is.EqualTo(200));
            Assert.That(capabilities.SupportsETag, Is.True);
            Assert.That(capabilities.SupportsSort, Is.True);
            Assert.That(capabilities.SupportsChangePassword, Is.True);
            Assert.That(capabilities.AuthenticationSchemes, Is.EqualTo(new[] { "oauthbearertoken" }));
            Assert.That(capabilities.Warnings, Is.Empty);
        }
    }

    [Test]
    public void From_NoConfigDocument_FallsBackToTheProtocolFloorsAndWarns()
    {
        var capabilities = ScimProviderCapabilities.From(null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capabilities.DiscoveryAvailable, Is.False);
            Assert.That(capabilities.SupportsPatch, Is.False);
            Assert.That(capabilities.SupportsBulk, Is.False);
            Assert.That(capabilities.SupportsFilter, Is.False);
            Assert.That(capabilities.SupportsETag, Is.False);
            Assert.That(capabilities.Warnings, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void From_FeatureBlockAbsent_IsTreatedAsUnsupportedRatherThanAssumed()
    {
        // Assuming support the provider never claimed turns a discovery gap into failed exports.
        var capabilities = ScimProviderCapabilities.From(new ScimServiceProviderConfig());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capabilities.DiscoveryAvailable, Is.True);
            Assert.That(capabilities.SupportsPatch, Is.False);
            Assert.That(capabilities.SupportsBulk, Is.False);
            Assert.That(capabilities.SupportsETag, Is.False);
        }
    }

    [Test]
    public void From_FeatureBlockPresentButUnsupported_IsUnsupported()
    {
        var config = new ScimServiceProviderConfig
        {
            Patch = new ScimSupportedFeature { Supported = false },
            Bulk = new ScimBulkFeature { Supported = false, MaxOperations = 100 }
        };

        var capabilities = ScimProviderCapabilities.From(config);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capabilities.SupportsPatch, Is.False);
            Assert.That(capabilities.SupportsBulk, Is.False);
            // A limit published alongside "not supported" is meaningless and must not be acted on.
            Assert.That(capabilities.BulkMaxOperations, Is.Null);
        }
    }

    [Test]
    public void From_NoFilterSupport_WarnsThatDeltaImportWillDegradeToAFullScan()
    {
        var config = new ScimServiceProviderConfig { Patch = new ScimSupportedFeature { Supported = true } };

        var capabilities = ScimProviderCapabilities.From(config);

        Assert.That(capabilities.Warnings, Has.Exactly(1).Contains("filtering"));
    }

    [Test]
    public void From_NoPatchSupport_WarnsThatUpdatesWillOverwriteUnmanagedAttributes()
    {
        // A PUT replaces the whole resource, so anything JIM does not manage is lost. An administrator
        // needs to know that before the first export, not after it.
        var config = new ScimServiceProviderConfig { Filter = new ScimFilterFeature { Supported = true } };

        var capabilities = ScimProviderCapabilities.From(config);

        Assert.That(capabilities.Warnings, Has.Exactly(1).Contains("PUT"));
    }

    [Test]
    public void From_AuthenticationSchemeWithNoType_IsSkippedRatherThanRecordedAsBlank()
    {
        var config = new ScimServiceProviderConfig
        {
            AuthenticationSchemes =
            [
                new ScimAuthenticationScheme { Name = "Mystery" },
                new ScimAuthenticationScheme { Type = "httpbasic" }
            ]
        };

        var capabilities = ScimProviderCapabilities.From(config);

        Assert.That(capabilities.AuthenticationSchemes, Is.EqualTo(new[] { "httpbasic" }));
    }
}
