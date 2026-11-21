// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Http;

namespace JIM.Connectors.Graph;

/// <summary>
/// A federated credential's token from a file a workload identity platform keeps current, such as Kubernetes with
/// Azure workload identity, which projects a service account token and names it in <c>AZURE_FEDERATED_TOKEN_FILE</c>.
/// </summary>
/// <remarks>The file is read for every token request, because the platform replaces it before the token in it expires.</remarks>
internal sealed class GraphTokenFileSource : IGraphFederatedTokenSource
{
    /// <summary>The variable Azure workload identity sets to the projected token's path.</summary>
    public const string TokenFileVariable = "AZURE_FEDERATED_TOKEN_FILE";

    private readonly string? _path;
    private readonly Func<string, string?> _environment;

    /// <param name="path">The Token File Path setting; when empty, the path <see cref="TokenFileVariable"/> names.</param>
    /// <param name="environment">Reads an environment variable; replaced in tests.</param>
    public GraphTokenFileSource(string? path, Func<string, string?> environment)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        _environment = environment;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var path = _path ?? _environment(TokenFileVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new HttpAuthenticationException(
                $"No token file is named: set {GraphConnectorConstants.SettingTokenFilePath}, or run JIM where the workload identity platform sets {TokenFileVariable}.");
        }

        string token;
        try
        {
            token = (await System.IO.File.ReadAllTextAsync(path, cancellationToken)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            throw new HttpAuthenticationException($"The federated credential's token file {path} could not be read: {ex.Message}", ex);
        }

        return token.Length > 0
            ? token
            : throw new HttpAuthenticationException($"The federated credential's token file {path} is empty.");
    }
}
