// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// Resolves the adapter for a configuration surface. Built once at startup from the adapters supplied to it; no
/// reflection scanning, so what is registered is visible in one place and cannot vary with assembly load order.
///
/// It refuses ambiguity rather than resolving it. A surface served by two adapters would resolve to whichever
/// registered last, and the resulting preview would be a confident answer produced by the wrong evaluator, with
/// nothing in a log to say so.
/// </summary>
public class ConfigurationChangePreviewAdapterRegistry
{
    private readonly Dictionary<ConfigurationChangePreviewSurface, IConfigurationChangePreviewAdapter> _adapters = [];

    public ConfigurationChangePreviewAdapterRegistry(IEnumerable<IConfigurationChangePreviewAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        foreach (var adapter in adapters)
        {
            if (adapter.Surface == ConfigurationChangePreviewSurface.NotSet)
            {
                throw new InvalidOperationException(
                    $"{adapter.GetType().Name} does not declare a preview surface. Set Surface to the surface it serves.");
            }

            if (!_adapters.TryAdd(adapter.Surface, adapter))
            {
                throw new InvalidOperationException(
                    $"Two preview adapters serve {adapter.Surface}: {_adapters[adapter.Surface].GetType().Name} and " +
                    $"{adapter.GetType().Name}. Exactly one adapter may serve a surface.");
            }
        }
    }

    /// <summary>
    /// Every surface an adapter is registered for. Surfaces absent from this list keep the save-time acknowledgement
    /// and offer no preview, which is the expected state for a surface whose adapter has not been written yet.
    /// </summary>
    public IReadOnlyCollection<ConfigurationChangePreviewSurface> RegisteredSurfaces => _adapters.Keys;

    /// <summary>
    /// Whether <paramref name="surface"/> can be previewed. Callers ask this to decide whether to offer a preview at
    /// all, so it answers rather than throwing.
    /// </summary>
    public bool HasAdapterFor(ConfigurationChangePreviewSurface surface) => _adapters.ContainsKey(surface);

    /// <summary>
    /// The adapter for <paramref name="surface"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No adapter serves the surface. Thrown, and naming the surface, because reaching here means something offered
    /// a preview it cannot produce: returning null would turn that into an empty result, which reads as "this change
    /// would do nothing".
    /// </exception>
    public IConfigurationChangePreviewAdapter Get(ConfigurationChangePreviewSurface surface) =>
        _adapters.TryGetValue(surface, out var adapter)
            ? adapter
            : throw new InvalidOperationException(
                $"No configuration change preview adapter is registered for {surface}. " +
                "Check with HasAdapterFor before offering a preview for a surface.");
}
