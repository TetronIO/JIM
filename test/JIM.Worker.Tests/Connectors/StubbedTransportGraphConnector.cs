// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Graph;
using JIM.Connectors.Http;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A <see cref="GraphConnector"/> whose token service and Graph are a stub handler, so the connectivity test and later
/// the import and export can be exercised without a tenant. Authentication is the real path, built from the settings,
/// so a test sees the token request the Connector would send.
/// </summary>
internal sealed class StubbedTransportGraphConnector : GraphConnector
{
    private readonly HttpMessageHandler _handler;

    public StubbedTransportGraphConnector(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    /// <summary>A transport where the token grants every permission and Graph answers every request with an empty page.</summary>
    public static StubHttpMessageHandler HealthyTransport(params string[] roles) => new(request =>
        GraphTestTokens.IsTokenRequest(request)
            ? GraphTestTokens.TokenResponse(roles.Length > 0 ? roles : GraphTestTokens.AllPermissions)
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"value":[]}""") });

    internal override Task<GraphHttpClient> CreateClientAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        var httpClient = new HttpClient(_handler, disposeHandler: false);
        var authentication = GraphHttpClientFactory.CreateAuthentication(settingValues, CredentialProtection, httpClient, out var owned);
        GraphCloud.TryResolve(settingValues.SingleOrDefault(s => s.Setting.Name == GraphConnectorConstants.SettingCloud)?.StringValue, out var cloud);

        var client = new GraphHttpClient(
            httpClient,
            cloud,
            authentication,
            new HttpRetryPolicy(0, TimeSpan.Zero, TimeSpan.FromMinutes(5)),
            new GraphRequestPacer(GraphRequestPacer.WriteQuotaPerSecond, delay: (_, _) => Task.CompletedTask),
            logger,
            owned,
            delay: (_, _) => Task.CompletedTask);

        return Task.FromResult(client);
    }
}
