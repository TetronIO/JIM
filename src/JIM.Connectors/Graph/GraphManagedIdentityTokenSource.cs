// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Connectors.Http;

namespace JIM.Connectors.Graph;

/// <summary>
/// A federated credential's token from a user-assigned managed identity of the Azure virtual machine JIM runs on, through
/// the instance metadata service.
/// </summary>
/// <remarks>
/// <para>
/// Only a user-assigned managed identity can be an app registration's federated credential, so its client id is always
/// named. The token is asked for the cloud's token exchange audience, which is what the app's federated credential
/// expects.
/// </para>
/// <para>
/// The instance metadata service is the only plain HTTP address the Connector sends to: it is fixed and link-local, so
/// it is hard-coded rather than configurable.
/// </para>
/// </remarks>
internal sealed class GraphManagedIdentityTokenSource : IGraphFederatedTokenSource
{
    /// <summary>The instance metadata service's token endpoint, the same on every Azure virtual machine.</summary>
    private static readonly Uri InstanceMetadataService = new("http://169.254.169.254/metadata/identity/oauth2/token");

    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private readonly string _audience;

    /// <param name="httpClient">Reaches the instance metadata service directly, never through a proxy.</param>
    /// <param name="clientId">The user-assigned managed identity's client id.</param>
    /// <param name="audience">The cloud's federated token audience.</param>
    public GraphManagedIdentityTokenSource(HttpClient httpClient, string clientId, string audience)
    {
        _httpClient = httpClient;
        _clientId = clientId;
        _audience = audience;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri($"{InstanceMetadataService}?api-version=2018-02-01&resource={Uri.EscapeDataString(_audience)}&client_id={Uri.EscapeDataString(_clientId)}"));
        request.Headers.Add("Metadata", "true");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new HttpAuthenticationException(
                $"The Azure instance metadata service could not be reached ({ex.Message}). A managed identity is only available to JIM running on an Azure " +
                "virtual machine with the user-assigned managed identity attached; elsewhere, use a certificate instead.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpAuthenticationException(
                    $"The Azure instance metadata service refused a token (HTTP {(int)response.StatusCode}{Describe(body)}). Check the " +
                    $"{GraphConnectorConstants.SettingManagedIdentityClientId} is the client id of a user-assigned managed identity attached to the virtual machine JIM runs on.");
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

            throw new HttpAuthenticationException("The Azure instance metadata service answered without a token.");
        }
    }

    /// <summary>The service's own error description, which names an identity that is not attached; it carries no secret.</summary>
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
