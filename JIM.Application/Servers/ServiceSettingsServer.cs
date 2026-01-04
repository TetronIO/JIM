using JIM.Models.Activities;
using JIM.Models.Core;

namespace JIM.Application.Servers
{
    public class ServiceSettingsServer
    {
        private JimApplication Application { get; }

        internal ServiceSettingsServer(JimApplication application)
        {
            Application = application;
        }

        #region ServiceSettings (singleton) methods

        internal async Task<bool> ServiceSettingsExistAsync()
        {
            return await Application.Repository.ServiceSettings.ServiceSettingsExistAsync();
        }

        public async Task<ServiceSettings?> GetServiceSettingsAsync()
        {
            return await Application.Repository.ServiceSettings.GetServiceSettingsAsync();
        }

        internal async Task CreateServiceSettingsAsync(ServiceSettings serviceSettings)
        {
            await Application.Repository.ServiceSettings.CreateServiceSettingsAsync(serviceSettings);
        }

        public async Task UpdateServiceSettingsAsync(ServiceSettings serviceSettings)
        {
            await Application.Repository.ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
        }

        #endregion

        #region ServiceSetting (individual settings) methods

        public async Task<ServiceSetting?> GetSettingAsync(string key)
        {
            return await Application.Repository.ServiceSettings.GetSettingAsync(key);
        }

        public async Task<List<ServiceSetting>> GetAllSettingsAsync()
        {
            return await Application.Repository.ServiceSettings.GetAllSettingsAsync();
        }

        public async Task<List<ServiceSetting>> GetSettingsByCategoryAsync(ServiceSettingCategory category)
        {
            return await Application.Repository.ServiceSettings.GetSettingsByCategoryAsync(category);
        }

        public async Task<List<ServiceSetting>> GetOverriddenSettingsAsync()
        {
            return await Application.Repository.ServiceSettings.GetOverriddenSettingsAsync();
        }

        /// <summary>
        /// Gets the typed value of a setting, converting from string storage.
        /// Encrypted string values are automatically decrypted.
        /// </summary>
        public async Task<T?> GetSettingValueAsync<T>(string key, T? defaultValue = default)
        {
            var setting = await GetSettingAsync(key);
            if (setting == null)
                return defaultValue;

            var effectiveValue = setting.GetEffectiveValue();
            if (string.IsNullOrEmpty(effectiveValue))
                return defaultValue;

            // Decrypt encrypted string values before returning
            if (setting.ValueType == ServiceSettingValueType.StringEncrypted && Application.CredentialProtection != null)
            {
                effectiveValue = Application.CredentialProtection.Unprotect(effectiveValue);
                if (string.IsNullOrEmpty(effectiveValue))
                    return defaultValue;
            }

            return ConvertSettingValue<T>(effectiveValue!, setting.ValueType);
        }

        /// <summary>
        /// Gets the partition validation mode setting.
        /// </summary>
        public async Task<PartitionValidationMode> GetPartitionValidationModeAsync()
        {
            return await GetSettingValueAsync(
                Constants.SettingKeys.PartitionValidationMode,
                PartitionValidationMode.Error);
        }

        /// <summary>
        /// Gets the verbose no-change recording setting.
        /// When enabled, creates detailed Activity execution items for exports where CSO already has current values.
        /// </summary>
        public async Task<bool> GetVerboseNoChangeRecordingAsync()
        {
            return await GetSettingValueAsync(Constants.SettingKeys.VerboseNoChangeRecording, false);
        }

        /// <summary>
        /// Gets the sync page size setting.
        /// Controls the number of CSOs processed per database page during sync operations.
        /// </summary>
        public async Task<int> GetSyncPageSizeAsync()
        {
            return await GetSettingValueAsync(Constants.SettingKeys.SyncPageSize, 500);
        }

        /// <summary>
        /// Updates a service setting value and creates an Activity for audit purposes.
        /// Encrypted string values are automatically encrypted before storage.
        /// </summary>
        public async Task UpdateSettingValueAsync(string key, string? newValue, MetaverseObject? initiatedBy)
        {
            var setting = await GetSettingAsync(key);
            if (setting == null)
                throw new InvalidOperationException($"Setting with key '{key}' not found.");

            if (setting.IsReadOnly)
                throw new InvalidOperationException($"Setting '{setting.DisplayName}' is read-only and cannot be modified.");

            var oldValue = setting.Value;

            // Encrypt encrypted string values before storing
            if (setting.ValueType == ServiceSettingValueType.StringEncrypted &&
                !string.IsNullOrEmpty(newValue) &&
                Application.CredentialProtection != null)
            {
                newValue = Application.CredentialProtection.Protect(newValue);
            }

            setting.Value = newValue;
            setting.LastModified = DateTime.UtcNow;
            setting.LastModifiedBy = initiatedBy?.ToString() ?? "System";

            // Create activity for audit trail
            var activity = new Activity
            {
                TargetName = setting.DisplayName,
                TargetType = ActivityTargetType.ServiceSetting,
                TargetOperationType = ActivityTargetOperationType.Update,
                Status = ActivityStatus.InProgress
            };
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);

            await Application.Repository.ServiceSettings.UpdateSettingAsync(setting);

            await Application.Activities.CompleteActivityAsync(activity);
        }

        /// <summary>
        /// Reverts a service setting to its default value and creates an Activity for audit purposes.
        /// </summary>
        public async Task RevertSettingToDefaultAsync(string key, MetaverseObject? initiatedBy)
        {
            var setting = await GetSettingAsync(key);
            if (setting == null)
                throw new InvalidOperationException($"Setting with key '{key}' not found.");

            if (setting.IsReadOnly)
                throw new InvalidOperationException($"Setting '{setting.DisplayName}' is read-only and cannot be modified.");

            setting.Value = null; // null means use default
            setting.LastModified = DateTime.UtcNow;
            setting.LastModifiedBy = initiatedBy?.ToString() ?? "System";

            // Create activity for audit trail
            var activity = new Activity
            {
                TargetName = setting.DisplayName,
                TargetType = ActivityTargetType.ServiceSetting,
                TargetOperationType = ActivityTargetOperationType.Revert,
                Status = ActivityStatus.InProgress
            };
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);

            await Application.Repository.ServiceSettings.UpdateSettingAsync(setting);

            await Application.Activities.CompleteActivityAsync(activity);
        }

        internal async Task CreateSettingAsync(ServiceSetting setting)
        {
            await Application.Repository.ServiceSettings.CreateSettingAsync(setting);
        }

        internal async Task<bool> SettingExistsAsync(string key)
        {
            return await Application.Repository.ServiceSettings.SettingExistsAsync(key);
        }

        /// <summary>
        /// Creates or updates a setting (used during seeding).
        /// </summary>
        internal async Task CreateOrUpdateSettingAsync(ServiceSetting setting)
        {
            if (await SettingExistsAsync(setting.Key))
            {
                // Only update read-only settings (from environment) during seeding
                // Don't overwrite user-modified values
                var existingSetting = await GetSettingAsync(setting.Key);
                if (existingSetting != null && existingSetting.IsReadOnly)
                {
                    existingSetting.DefaultValue = setting.DefaultValue;
                    existingSetting.Value = setting.Value;
                    await Application.Repository.ServiceSettings.UpdateSettingAsync(existingSetting);
                }
            }
            else
            {
                await CreateSettingAsync(setting);
            }
        }

        private static T? ConvertSettingValue<T>(string value, ServiceSettingValueType valueType)
        {
            var targetType = typeof(T);
            var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            try
            {
                if (underlyingType == typeof(string))
                    return (T)(object)value;

                if (underlyingType == typeof(bool))
                    return (T)(object)bool.Parse(value);

                if (underlyingType == typeof(int))
                    return (T)(object)int.Parse(value);

                if (underlyingType == typeof(TimeSpan))
                    return (T)(object)TimeSpan.Parse(value);

                if (underlyingType.IsEnum)
                    return (T)Enum.Parse(underlyingType, value);

                return default;
            }
            catch
            {
                return default;
            }
        }

        #endregion
    }
}