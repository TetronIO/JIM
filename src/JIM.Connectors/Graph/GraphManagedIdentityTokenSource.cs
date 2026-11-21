// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text.Json;
using JIM.Connectors.Http;

namespace JIM.Connectors.Graph;

/// <summary>
/// A federated credential's token from a user-assigned managed identity of the Azure resource JIM runs on: a virtual
/// machine through the instance metadata service, or App Service, Container Apps and Functions through the local
/// endpoint the platform names.
/// </summary>
/// <remarks>
/// <para>
/// Only a user-assigned managed identity can be an app registration's federated credential, so its client id is always
/// named. The token is asked for the cloud's token exchange audience, which is what the app's federated credential
/// expects.
/// </para>
/// <para>
/// These are the only plain HTTP addresses the Connector sends to. The instance metadata service's address is fixed and
/// link-local; a platform endpoint comes from the environment, so it is accepted only on this host.
/// </para>
/// </remarks>
internal sealed class GraphManagedIdentityTokenSource : IGraphFederatedTokenSource
{
    /// <summary>The instance metadata service's token endpoint, the same on every Azure virtual machine.</summary>
    private static readonly Uri InstanceMetadataService = new("http://169.254.169.254/metadata/identity/oauth2/token");

    private const string EndpointVariable = "IDENTITY_ENDPOINT";
    private const string HeaderVariable = "IDENTITY_HEADER";

    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private readonly string _audience;
    private readonly Func<string, string?> _environment;

    /// <param name="httpClient">Reaches the identity endpoint directly, never through a proxy.</param>
    /// <param name="clientId">The user-assigned managed identity's client id.</param>
    /// <param name="audience">The cloud's federated token audience.</param>
    /// <param name="environment">Reads an environment variable; replaced in tests.</param>
    public GraphManagedIdentityTokenSource(HttpClient httpClient, string clientId, string audience, Func<string, string?> environment)
    {
        _httpClient = httpClient;
        _clientId = clientId;
        _audience = audience;
        _environment = environment;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        using var request = BuildRequest();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new HttpAuthenticationException(
                $"The Azure managed identity endpoint could not be reached ({ex.Message}). A managed identity is only available to JIM running on an Azure resource " +
                "with the user-assigned managed identity attached; elsewhere, use a certificate or a token file instead.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpAuthenticationException(
                    $"The Azure managed identity endpoint refused a token (HTTP {(int)response.StatusCode}{Describe(body)}). Check the " +
                    $"{GraphConnectorConstants.SettingManagedIdentityClientId} is the client id of a user-assigned managed identity attached to the resource JIM runs on.");
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("access_token", out var token) && token.ValueKind == JsonValueKind.String && token.GetString() is { Length: > 0 } value)
                    return value;
            }
            catch (JsonException)
            {
                // Reported below.
            }

            throw new HttpAuthenticationException("The Azure managed identity endpoint answered without a token.");
        }
    }

    private HttpRequestMessage BuildRequest()
    {
        var query = $"resource={Uri.EscapeDataString(_audience)}&client_id={Uri.EscapeDataString(_clientId)}";
        var endpoint = _environment(EndpointVariable);
        var header = _environment(HeaderVariable);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(header))
        {
            var imds = new HttpRequestMessage(HttpMethod.Get, new Uri($"{InstanceMetadataService}?api-version=2018-02-01&{query}"));
            imds.Headers.Add("Metadata", "true");
            return imds;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var platform) || !IsThisHost(platform))
        {
            throw new HttpAuthenticationException(
                $"{EndpointVariable} names {endpoint}, which is not on this host. A managed identity's platform endpoint is always local, so JIM will not send to it.");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{platform.GetLeftPart(UriPartial.Path)}?api-version=2019-08-01&{query}"));
        request.Headers.Add("X-IDENTITY-HEADER", header);
        return request;
    }

    private static bool IsThisHost(Uri uri) =>
        uri.IsLoopback || IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);

    /// <summary>The endpoint's own error description, which names an identity that is not attached; it carries no secret.</summary>
    private static string Describe(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error_description", out var description) && description.ValueKind == JsonValueKind.String
                ? $": {description.GetString()}"
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
