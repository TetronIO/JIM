// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Exceptions;
using JIM.Models.Core;
using JIM.Models.Security;

namespace JIM.Application.Servers;

/// <summary>
/// JIM's homegrown feature-flag mechanism (#1781), built on the existing Service Settings store: each flag in
/// <see cref="FeatureFlagCatalogue"/> is one <see cref="ServiceSetting"/> row in the
/// <see cref="ServiceSettingCategory.FeatureFlags"/> category. Flag changes are written through
/// <see cref="ServiceSettingsServer"/>'s ordinary setting-update path, so they get the same audit Activity and
/// configuration change capture as any other Service Setting, keyed by the flag's key.
/// <para>
/// Consumers gate at entry points (a portal control, a REST endpoint, a server-side configuration write), never
/// deep in the sync engine; see <see cref="EnsureEnabledAsync"/>. See the "Feature Flags" section of
/// <c>engineering/DEVELOPER_GUIDE.md</c> for the full lifecycle, including the two tiers and what happens to
/// existing configuration when a flag is off.
/// </para>
/// </summary>
public class FeatureFlagServer
{
    private JimApplication Application { get; }

    /// <summary>
    /// Per-instance cache of resolved flag states, keyed by flag key. A <see cref="JimApplication"/> is one unit
    /// of work (see src/CLAUDE.md), so caching here is safe: nothing outlives the request or task that owns it,
    /// and a write made through this same instance updates the cache immediately rather than waiting for the
    /// instance to be recreated.
    /// </summary>
    private readonly Dictionary<string, bool> _cache = new();

    internal FeatureFlagServer(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// Whether the named feature flag is currently enabled. Cheap: cached per <see cref="JimApplication"/>
    /// instance, and reads the underlying Service Setting on a cache miss.
    /// </summary>
    /// <exception cref="FeatureFlagNotFoundException">The key does not match any catalogue entry.</exception>
    public async Task<bool> IsEnabledAsync(string key)
    {
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var definition = GetDefinitionOrThrow(key);
        var enabled = await Application.ServiceSettings.GetSettingValueAsync(definition.Key, false);
        _cache[key] = enabled;
        return enabled;
    }

    /// <summary>
    /// Gates an entry point on a feature flag. Throws <see cref="FeatureDisabledException"/>, naming the feature,
    /// when the flag is off. Call this at entry points only (a portal action handler, a REST controller action, a
    /// server-side configuration write) and never deep inside the sync engine, so a single, readable check governs
    /// whether the feature runs at all.
    /// </summary>
    /// <exception cref="FeatureFlagNotFoundException">The key does not match any catalogue entry.</exception>
    /// <exception cref="FeatureDisabledException">The flag is currently disabled.</exception>
    public async Task EnsureEnabledAsync(string key)
    {
        if (await IsEnabledAsync(key))
            return;

        throw new FeatureDisabledException(GetDefinitionOrThrow(key));
    }

    /// <summary>
    /// Every feature flag's definition and current state, for the Service Settings page's "Preview features" card
    /// and the REST API. <paramref name="includeInDevelopment"/> is true only for a development host (the portal
    /// checks <c>IWebHostEnvironment.IsDevelopment()</c>) and for the integration harness; In Development flags
    /// are never surfaced in Production.
    /// </summary>
    public async Task<List<FeatureFlagState>> GetFeatureFlagsAsync(bool includeInDevelopment)
    {
        var states = new List<FeatureFlagState>();
        foreach (var definition in FeatureFlagCatalogue.All)
        {
            if (!includeInDevelopment && definition.Tier == FeatureFlagTier.InDevelopment)
                continue;

            states.Add(await GetStateAsync(definition));
        }

        return states;
    }

    /// <summary>
    /// Switches a feature flag on or off, attributed to an interactive user, recording an audit Activity and
    /// configuration change snapshot via the same path an ordinary Service Setting change uses.
    /// </summary>
    /// <param name="allowInDevelopment">
    /// Must be true to enable an <see cref="FeatureFlagTier.InDevelopment"/> flag. Without it, enabling one
    /// throws; disabling a flag never needs this, whatever its tier.
    /// </param>
    /// <exception cref="FeatureFlagNotFoundException">The key does not match any catalogue entry.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="enabled"/> is true, the flag is In Development, and <paramref name="allowInDevelopment"/>
    /// was not set.
    /// </exception>
    public async Task<FeatureFlagState> SetFeatureFlagAsync(string key, bool enabled, MetaverseObject? initiatedBy,
        bool allowInDevelopment = false, string? changeReason = null)
    {
        var definition = GetDefinitionOrThrow(key);
        RequireAllowedTier(definition, enabled, allowInDevelopment);

        await Application.ServiceSettings.UpdateSettingValueAsync(definition.Key, ToStoredValue(enabled), initiatedBy, changeReason);
        _cache[definition.Key] = enabled;
        return await GetStateAsync(definition);
    }

    /// <summary>
    /// Switches a feature flag on or off, attributed to an API key. See the interactive-user overload for the
    /// full behaviour.
    /// </summary>
    /// <exception cref="FeatureFlagNotFoundException">The key does not match any catalogue entry.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="enabled"/> is true, the flag is In Development, and <paramref name="allowInDevelopment"/>
    /// was not set.
    /// </exception>
    public async Task<FeatureFlagState> SetFeatureFlagAsync(string key, bool enabled, ApiKey initiatedByApiKey,
        bool allowInDevelopment = false, string? changeReason = null)
    {
        var definition = GetDefinitionOrThrow(key);
        RequireAllowedTier(definition, enabled, allowInDevelopment);

        await Application.ServiceSettings.UpdateSettingValueAsync(definition.Key, ToStoredValue(enabled), initiatedByApiKey, changeReason);
        _cache[definition.Key] = enabled;
        return await GetStateAsync(definition);
    }

    private static void RequireAllowedTier(FeatureFlagDefinition definition, bool enabled, bool allowInDevelopment)
    {
        if (enabled && definition.Tier == FeatureFlagTier.InDevelopment && !allowInDevelopment)
        {
            throw new InvalidOperationException(
                $"'{definition.DisplayName}' is an in-development feature (tracked for removal in issue #{definition.TrackingIssueNumber}). " +
                "It can only be enabled outside Development by passing allowInDevelopment.");
        }
    }

    private static string ToStoredValue(bool enabled) => enabled ? "true" : "false";

    private async Task<FeatureFlagState> GetStateAsync(FeatureFlagDefinition definition)
    {
        var setting = await Application.ServiceSettings.GetSettingAsync(definition.Key);
        var enabled = setting != null && bool.TryParse(setting.GetEffectiveValue(), out var parsed) && parsed;
        _cache[definition.Key] = enabled;

        return new FeatureFlagState
        {
            Definition = definition,
            Enabled = enabled,
            LastUpdated = setting?.LastUpdated,
            LastUpdatedByName = setting?.LastUpdatedByName
        };
    }

    private static FeatureFlagDefinition GetDefinitionOrThrow(string key) =>
        FeatureFlagCatalogue.All.FirstOrDefault(d => d.Key == key) ?? throw new FeatureFlagNotFoundException(key);
}
