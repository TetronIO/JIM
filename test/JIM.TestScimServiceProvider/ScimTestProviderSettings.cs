// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace JIM.TestScimServiceProvider;

/// <summary>
/// How a run of the test service provider is configured, from environment variables or command line.
/// <para>
/// Every switch here corresponds to something a real provider varies, so one scenario can drive the
/// connector's per-object path and its bulk path against identical data by changing nothing but this.
/// </para>
/// </summary>
public sealed class ScimTestProviderSettings
{
    public int Port { get; private init; } = 5300;

    /// <summary>
    /// The name the certificate is issued for, which must be the name JIM connects by. In the
    /// integration stack that is the container's service name on the Docker network.
    /// </summary>
    public string HostName { get; private init; } = "localhost";

    public int UserCount { get; private init; } = 25;

    public int GroupCount { get; private init; } = 2;

    public bool SupportsBulk { get; private init; } = true;

    /// <summary>Zero advertises bulk support without stating a limit, which many providers do.</summary>
    public int BulkMaxOperations { get; private init; }

    public bool SupportsPatch { get; private init; } = true;

    public bool PublishesSchemas { get; private init; } = true;

    /// <summary>Where the public certificate is written for the scenario to trust. Empty writes none.</summary>
    public string? CertificateExportPath { get; private init; }

    public static ScimTestProviderSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new ScimTestProviderSettings
        {
            Port = Integer(configuration, "SCIM_PORT", 5300),
            HostName = configuration["SCIM_HOSTNAME"] is { Length: > 0 } host ? host : "localhost",
            UserCount = Integer(configuration, "SCIM_USER_COUNT", 25),
            GroupCount = Integer(configuration, "SCIM_GROUP_COUNT", 2),
            SupportsBulk = Boolean(configuration, "SCIM_SUPPORTS_BULK", true),
            BulkMaxOperations = Integer(configuration, "SCIM_BULK_MAX_OPERATIONS", 0),
            SupportsPatch = Boolean(configuration, "SCIM_SUPPORTS_PATCH", true),
            PublishesSchemas = Boolean(configuration, "SCIM_PUBLISHES_SCHEMAS", true),
            CertificateExportPath = configuration["SCIM_CERTIFICATE_PATH"]
        };
    }

    /// <summary>
    /// Applies these settings to a provider and seeds it, returning the provider ready to serve.
    /// </summary>
    public MockScimProvider Configure(MockScimProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        provider.Options.SupportsBulk = SupportsBulk;
        provider.Options.BulkMaxOperations = BulkMaxOperations > 0 ? BulkMaxOperations : null;
        provider.Options.SupportsPatch = SupportsPatch;
        provider.Options.PublishesSchemas = PublishesSchemas;

        // The provider's clock drives meta.lastModified, and a delta import filters against it. Left at
        // its fixed default the seeded resources would predate any watermark JIM records, so a delta
        // import would correctly find nothing and prove nothing.
        provider.Options.ProviderClock = DateTimeOffset.UtcNow;

        for (var i = 1; i <= UserCount; i++)
        {
            var user = provider.AddUser($"user-{i}", $"user{i}");
            user.Attributes["name"] = new System.Text.Json.Nodes.JsonObject
            {
                ["givenName"] = "User",
                ["familyName"] = $"Number{i}"
            };
            user.Attributes["emails"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["value"] = $"user{i}@example.com", ["type"] = "work", ["primary"] = true });
        }

        for (var i = 1; i <= GroupCount; i++)
        {
            var group = provider.AddGroup($"group-{i}", $"Group {i}");

            // Membership is what makes a group worth importing: it proves reference values survive the
            // round trip, which a group with a display name alone would not.
            var members = new System.Text.Json.Nodes.JsonArray();
            for (var member = i; member <= UserCount; member += GroupCount)
                members.Add(new System.Text.Json.Nodes.JsonObject { ["value"] = $"user-{member}" });

            group.Attributes["members"] = members;
        }

        return provider;
    }

    private static int Integer(IConfiguration configuration, string key, int fallback)
    {
        return int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static bool Boolean(IConfiguration configuration, string key, bool fallback)
    {
        return bool.TryParse(configuration[key], out var value) ? value : fallback;
    }
}
