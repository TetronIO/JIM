// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.SCIM;
using JIM.Connectors.SCIM.Authentication;
using JIM.Models.Staging;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A <see cref="ScimConnector"/> whose transport is replaced by a stub handler, so schema discovery and
/// the live connectivity test can be exercised without a service provider or a network.
/// </summary>
internal sealed class StubbedTransportScimConnector : ScimConnector
{
    private readonly HttpMessageHandler _handler;
    private readonly int _maximumRetries;

    /// <param name="maximumRetries">
    /// Zero by default, so a test asserting on requests sees exactly the ones the connector chose to
    /// make. Raise it for the throttling and transient-failure cases, where retrying is the behaviour
    /// under test; the delay seam below keeps those instant.
    /// </param>
    public StubbedTransportScimConnector(HttpMessageHandler handler, int maximumRetries = 0)
    {
        _handler = handler;
        _maximumRetries = maximumRetries;
    }

    internal override Task<ScimHttpClient> CreateClientAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        var client = new ScimHttpClient(
            new HttpClient(_handler),
            new Uri("https://provider.example.com/scim/v2"),
            new ScimStaticBearerTokenAuthentication("token"),
            // The delay seam below makes every wait instant, so a realistic ceiling costs nothing and
            // keeps a provider's Retry-After from being rejected for exceeding it.
            new ScimRetryPolicy(_maximumRetries, baseDelay: TimeSpan.Zero, maxDelay: TimeSpan.FromMinutes(5)),
            logger,
            delay: (_, _) => Task.CompletedTask);

        return Task.FromResult(client);
    }
}
