// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
namespace JIM.Data.Repositories;

public interface IServiceSettingsRepository
{
    // ServiceSettings (singleton) methods
    public Task<ServiceSettings?> GetServiceSettingsAsync();
    public Task<bool> ServiceSettingsExistAsync();
    public Task CreateServiceSettingsAsync(ServiceSettings serviceSettings);
    public Task UpdateServiceSettingsAsync(ServiceSettings serviceSettings);

    // ServiceSetting (individual settings) methods
    public Task<ServiceSetting?> GetSettingAsync(string key);
    public Task<List<ServiceSetting>> GetAllSettingsAsync();
    public Task<List<ServiceSetting>> GetSettingsByCategoryAsync(ServiceSettingCategory category);
    public Task<List<ServiceSetting>> GetOverriddenSettingsAsync();
    public Task CreateSettingAsync(ServiceSetting setting);
    public Task UpdateSettingAsync(ServiceSetting setting);
    public Task<bool> SettingExistsAsync(string key);

    /// <summary>
    /// Returns the existing setting for the given key, or atomically creates it from <paramref name="setting"/> and
    /// returns it. Safe against a concurrent first-use race: if another caller inserts the same key first, the
    /// persisted winner is returned rather than throwing. Used for lazily-generated singletons such as the
    /// configuration-change hash key.
    /// </summary>
    public Task<ServiceSetting> GetOrCreateSettingAsync(ServiceSetting setting);
}