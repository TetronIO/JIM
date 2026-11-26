// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace JIM.Models.Localisation;

/// <summary>
/// JIM's own catalogues: a resource file in a JIM assembly, named <c>"&lt;Assembly&gt;:&lt;File&gt;"</c>, such as
/// <c>JIM.Application:ActivityMessages</c> for <c>JIM.Application/Resources/ActivityMessages.resx</c> (#3030).
/// </summary>
public sealed class AssemblyResxCatalogueSource : IMessageCatalogueSource
{
    private readonly ConcurrentDictionary<string, ResourceManager?> _catalogues = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool Serves(string catalogue) => ManagerFor(catalogue) != null;

    /// <inheritdoc />
    public string? Template(string catalogue, string key, CultureInfo language) =>
        ManagerFor(catalogue)?.GetString(key, language);

    private ResourceManager? ManagerFor(string catalogue) => _catalogues.GetOrAdd(catalogue, Load);

    private static ResourceManager? Load(string catalogue)
    {
        // A Connector catalogue ("connector:jim.ldap/Messages") is another source's, and is never an assembly name.
        var separator = catalogue.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || ConnectorCatalogueNames.IsConnectorCatalogue(catalogue))
            return null;

        var assemblyName = catalogue[..separator];
        try
        {
            var assembly = Assembly.Load(assemblyName);
            return new ResourceManager($"{assemblyName}.Resources.{catalogue[(separator + 1)..]}", assembly);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}
