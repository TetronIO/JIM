// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;
using JIM.Connectors.Graph;
using JIM.Connectors.Http;
using JIM.Models.Connectors;
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
    private readonly int _maxRetries;

    /// <param name="maxRetries">How many times a failed Graph request is retried; none by default.</param>
    public StubbedTransportGraphConnector(HttpMessageHandler handler, int maxRetries = 0)
    {
        _handler = handler;
        _maxRetries = maxRetries;
    }

    /// <summary>What a look at the server's certificate finds; null, by default, is a server that could not be reached.</summary>
    public ServerCertificateDiagnostic? ProbedDiagnostic { get; init; }

    /// <summary>Each host whose certificate was looked at, in order.</summary>
    public List<string> ProbedHosts { get; } = [];

    /// <summary>A transport where the token grants every permission and Graph answers every request with an empty page.</summary>
    public static StubHttpMessageHandler HealthyTransport(params string[] roles) => new(request =>
        GraphTestTokens.IsTokenRequest(request)
            ? GraphTestTokens.TokenResponse(roles.Length > 0 ? roles : GraphTestTokens.AllPermissions)
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"value":[]}""") });

    /// <summary>Each wait before asking Entra ID for a token again, in order; nothing actually waits.</summary>
    public List<TimeSpan> TokenWaits { get; } = [];

    internal override Task<GraphHttpClient> CreateClientAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger,
        GraphClientPurpose purpose = GraphClientPurpose.Run)
    {
        var httpClient = new HttpClient(_handler, disposeHandler: false);
        var authentication = GraphHttpClientFactory.CreateAuthentication(settingValues, CredentialProtection, httpClient, out var owned, purpose: purpose,
            logger: logger, delay: (wait, _) => { TokenWaits.Add(wait); return Task.CompletedTask; });
        GraphCloud.TryResolve(settingValues.SingleOrDefault(s => s.Setting.Name == GraphConnectorConstants.SettingCloud)?.StringValue, out var cloud);

        var client = new GraphHttpClient(
            httpClient,
            cloud,
            authentication,
            new HttpRetryPolicy(_maxRetries, TimeSpan.Zero, TimeSpan.FromMinutes(5)),
            new GraphRequestPacer(GraphRequestPacer.WriteQuotaPerSecond, delay: (_, _) => Task.CompletedTask),
            logger,
            owned,
            delay: (_, _) => Task.CompletedTask,
            describeCertificateFailure: failure => DescribeCertificateRejection(settingValues, failure, logger));

        return Task.FromResult(client);
    }

    internal override ServerCertificateDiagnostic? ProbeCertificate(SecureEndpoint endpoint, IReadOnlyCollection<X509Certificate2> trustedCertificates, ILogger logger)
    {
        ProbedHosts.Add(endpoint.Host);
        return ProbedDiagnostic;
    }
}
