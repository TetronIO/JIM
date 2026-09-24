// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Core;

namespace JIM.TestSupport;

/// <summary>
/// A minimal, in-memory <see cref="IServiceSettingsRepository"/> fake, so a test can construct a
/// <c>JimApplication</c> with feature flags on (or any other Service Setting seeded) without mocking the whole
/// settings/Activity/change-capture path via Moq. Not a general-purpose double: it has no concurrency handling
/// beyond a single dictionary and ignores <see cref="ServiceSettings"/> (the singleton row), because nothing that
/// constructs this needs it. See test/CLAUDE.md &gt; "Tests run with flags on".
/// </summary>
public class InMemoryServiceSettingsRepository : IServiceSettingsRepository
{
    private readonly Dictionary<string, ServiceSetting> _settings = new();

    /// <summary>
    /// Builds a repository seeded with every entry in <c>FeatureFlagCatalogue.All</c>, each enabled. The
    /// shorthand a gating test reaches for: <c>new JimApplication(new InMemoryRepository { ServiceSettings =
    /// InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled() })</c>-shaped setup, or via
    /// <c>Mock&lt;IRepository&gt;.Setup(r =&gt; r.ServiceSettings).Returns(...)</c>.
    /// </summary>
    public static InMemoryServiceSettingsRepository WithAllFeatureFlagsEnabled()
    {
        var repository = new InMemoryServiceSettingsRepository();
        foreach (var flag in FeatureFlagCatalogue.All)
        {
            repository._settings[flag.Key] = new ServiceSetting
            {
                Key = flag.Key,
                DisplayName = flag.DisplayName,
                Description = flag.Description,
                Category = ServiceSettingCategory.FeatureFlags,
                ValueType = ServiceSettingValueType.Boolean,
                DefaultValue = "false",
                Value = "true",
                IsReadOnly = false
            };
        }

        return repository;
    }

    public Task<ServiceSettings?> GetServiceSettingsAsync() => Task.FromResult<ServiceSettings?>(null);

    public Task<bool> ServiceSettingsExistAsync() => Task.FromResult(false);

    public Task CreateServiceSettingsAsync(ServiceSettings serviceSettings) => Task.CompletedTask;

    public Task UpdateServiceSettingsAsync(ServiceSettings serviceSettings) => Task.CompletedTask;

    public Task<ServiceSetting?> GetSettingAsync(string key) =>
        Task.FromResult(_settings.GetValueOrDefault(key));

    public Task<List<ServiceSetting>> GetAllSettingsAsync() =>
        Task.FromResult(_settings.Values.ToList());

    public Task<List<ServiceSetting>> GetSettingsByCategoryAsync(ServiceSettingCategory category) =>
        Task.FromResult(_settings.Values.Where(s => s.Category == category).ToList());

    public Task<List<ServiceSetting>> GetOverriddenSettingsAsync() =>
        Task.FromResult(_settings.Values.Where(s => s.Value != null && s.Value != s.DefaultValue).ToList());

    public Task CreateSettingAsync(ServiceSetting setting)
    {
        _settings[setting.Key] = setting;
        return Task.CompletedTask;
    }

    public Task UpdateSettingAsync(ServiceSetting setting)
    {
        _settings[setting.Key] = setting;
        return Task.CompletedTask;
    }

    public Task<bool> SettingExistsAsync(string key) => Task.FromResult(_settings.ContainsKey(key));

    public Task DeleteSettingAsync(string key)
    {
        _settings.Remove(key);
        return Task.CompletedTask;
    }

    public Task<ServiceSetting> GetOrCreateSettingAsync(ServiceSetting setting)
    {
        if (_settings.TryGetValue(setting.Key, out var existing))
            return Task.FromResult(existing);

        _settings[setting.Key] = setting;
        return Task.FromResult(setting);
    }
}
