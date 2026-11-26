// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Resources;
using JIM.Models.Localisation;

namespace JIM.Connectors;

/// <summary>
/// Serves the built-in Connectors' catalogues (<see cref="BuiltInConnectorCatalogues"/>) from the resource files
/// JIM.Connectors carries. A catalogue it does not know, such as an installed Connector's, is another source's.
/// </summary>
public sealed class BuiltInConnectorCatalogueSource : IMessageCatalogueSource
{
    private static readonly IReadOnlyDictionary<string, ResourceManager> Managers =
        BuiltInConnectorCatalogues.ResourceFiles.ToDictionary(
            entry => entry.Key,
            entry => new ResourceManager($"JIM.Connectors.Resources.{entry.Value}", typeof(BuiltInConnectorCatalogueSource).Assembly),
            StringComparer.Ordinal);

    /// <inheritdoc />
    public bool Serves(string catalogue) => Managers.ContainsKey(catalogue);

    /// <inheritdoc />
    public string? Template(string catalogue, string key, CultureInfo language) =>
        Managers.TryGetValue(catalogue, out var manager) ? manager.GetString(key, language) : null;
}
