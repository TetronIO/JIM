// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Application.Utilities;
using Serilog;

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
            return await GetSettingValueAsync(Constants.SettingKeys.SyncPageSize, 1000);
        }

        /// <summary>
        /// Gets whether CSO change tracking is enabled.
        /// When enabled, ConnectedSystemObjectChange records are created for CSO create/update/delete operations.
        /// </summary>
        public async Task<bool> GetCsoChangeTrackingEnabledAsync()
        {
            return await GetSettingValueAsync(Constants.SettingKeys.ChangeTrackingCsoChangesEnabled, true);
        }

        /// <summary>
        /// Gets whether MVO change tracking is enabled.
        /// When enabled, MetaverseObjectChange records are created for MVO create/update/delete operations.
        /// </summary>
        public async Task<bool> GetMvoChangeTrackingEnabledAsync()
        {
            return await GetSettingValueAsync(Constants.SettingKeys.ChangeTrackingMvoChangesEnabled, true);
        }

        /// <summary>
        /// Gets whether configuration change tracking is enabled.
        /// When enabled, a redacted, versioned configuration snapshot is captured on the Activity for each
        /// configuration create/update/delete (Synchronisation Rules, Connected Systems).
        /// </summary>
        public async Task<bool> GetConfigurationChangeTrackingEnabledAsync()
        {
            return await GetSettingValueAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled, true);
        }

        /// <summary>
        /// Returns the server-held key used to compute keyed hashes of secret configuration values in change snapshots,
        /// generating and persisting it (encrypted at rest) on first use. The key never leaves the server; it lets a
        /// secret change be detected without the change history becoming an offline brute-force oracle for weak secrets.
        /// </summary>
        public async Task<byte[]> GetOrCreateConfigurationChangeHashKeyAsync()
        {
            var existing = await GetSettingValueAsync<string>(Constants.SettingKeys.ConfigurationChangeHashKey);
            if (!string.IsNullOrEmpty(existing))
                return DecodeHashKey(existing);

            // Generate a 256-bit key and persist it encrypted at rest. GetOrCreateSettingAsync is race-safe, so a
            // concurrent first use returns the persisted winner rather than throwing.
            var keyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var protectedValue = Application.CredentialProtection?.Protect(keyBase64) ?? keyBase64;

            var setting = new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                Description = "Server-held key used to detect changes to secret configuration values in change history without storing the values. Generated automatically; never displayed or edited.",
                Category = ServiceSettingCategory.Security,
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = protectedValue,
                IsReadOnly = true
            };

            var persisted = await Application.Repository.ServiceSettings.GetOrCreateSettingAsync(setting);

            // Decode the persisted winner, which may be ours or another instance's if we lost the first-use race.
            var effectiveValue = persisted.GetEffectiveValue();
            if (persisted.ValueType == ServiceSettingValueType.StringEncrypted && Application.CredentialProtection != null)
                effectiveValue = Application.CredentialProtection.Unprotect(effectiveValue);

            if (string.IsNullOrEmpty(effectiveValue))
                throw new InvalidOperationException("Failed to generate or retrieve the configuration change hash key.");

            return DecodeHashKey(effectiveValue);
        }

        /// <summary>
        /// Decodes the base64 configuration change hash key, translating the cryptic <see cref="FormatException"/> that
        /// results from feeding a still-encrypted value to the decoder into a clear diagnostic. This happens when the
        /// stored key is encrypted at rest but no credential protection service was available to decrypt it, so the
        /// ciphertext is returned in place of the key; the change-capture path swallows the failure as best-effort, so
        /// a clear message here is the difference between a diagnosable log line and an unexplained missing snapshot.
        /// </summary>
        private static byte[] DecodeHashKey(string value)
        {
            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    "The configuration change hash key could not be decoded from its stored value. This usually means " +
                    "the credential protection service was unavailable when the encrypted key was read, so its ciphertext " +
                    "was returned instead of the key. Ensure JimApplication.CredentialProtection is set wherever " +
                    "configuration change capture runs.", ex);
            }
        }

        /// <summary>
        /// Gets the sync outcome tracking level, which controls how much detail is recorded
        /// for sync outcome graphs on each RPEI.
        /// Default: Detailed (full causal chain with nested outcomes).
        /// </summary>
        public async Task<ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel> GetSyncOutcomeTrackingLevelAsync()
        {
            return await GetSettingValueAsync(
                Constants.SettingKeys.ChangeTrackingSyncOutcomesLevel,
                ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        }

        /// <summary>
        /// Gets the history retention period (how long to keep change history and activities).
        /// Default: 90 days.
        /// </summary>
        public async Task<TimeSpan> GetHistoryRetentionPeriodAsync()
        {
            var retentionPeriod = await GetSettingValueAsync(Constants.SettingKeys.HistoryRetentionPeriod, TimeSpan.FromDays(90));

            // Guard against zero or negative retention period which would delete all records
            if (retentionPeriod <= TimeSpan.Zero)
            {
                Log.Warning("History retention period is {RetentionPeriod}, which would delete all records. Using default of 90 days", retentionPeriod);
                return TimeSpan.FromDays(90);
            }

            return retentionPeriod;
        }

        /// <summary>
        /// Gets the configuration change retention period (how long configuration-change Activities, which carry the
        /// versioned configuration snapshots, are kept). Held separately from, and typically much longer than, the
        /// general history retention period, because these Activities are the configuration change history.
        /// Default: 3650 days (~10 years).
        /// </summary>
        public async Task<TimeSpan> GetConfigurationChangeRetentionPeriodAsync()
        {
            var retentionPeriod = await GetSettingValueAsync(Constants.SettingKeys.ConfigurationChangeRetentionPeriod, TimeSpan.FromDays(3650));

            // Guard against zero or negative retention period which would delete all configuration history
            if (retentionPeriod <= TimeSpan.Zero)
            {
                Log.Warning("Configuration change retention period is {RetentionPeriod}, which would delete all configuration change history. Using default of 3650 days", retentionPeriod);
                return TimeSpan.FromDays(3650);
            }

            return retentionPeriod;
        }

        /// <summary>
        /// Gets the cleanup batch size (maximum records to delete per housekeeping cycle).
        /// Default: 100 records.
        /// </summary>
        public async Task<int> GetHistoryCleanupBatchSizeAsync()
        {
            return await GetSettingValueAsync(Constants.SettingKeys.HistoryCleanupBatchSize, 100);
        }

        /// <summary>
        /// Gets the stale task timeout (how long before a processing task with no heartbeat is considered abandoned).
        /// Used by the scheduler as a safety net for crash recovery.
        /// Default: 5 minutes.
        /// </summary>
        public async Task<TimeSpan> GetStaleTaskTimeoutAsync()
        {
            var timeout = await GetSettingValueAsync(Constants.SettingKeys.StaleTaskTimeout, TimeSpan.FromMinutes(5));

            // Guard against zero or negative timeout which would immediately mark all processing tasks as stale
            if (timeout <= TimeSpan.Zero)
            {
                Log.Warning("Stale task timeout is {Timeout}, which would immediately recover all processing tasks. Using default of 5 minutes", timeout);
                return TimeSpan.FromMinutes(5);
            }

            return timeout;
        }

        /// <summary>
        /// Updates a service setting value and creates an Activity for audit purposes, carrying a redacted, versioned
        /// configuration snapshot (and the optional change reason) in the setting's change history.
        /// Encrypted string values are automatically encrypted before storage.
        /// </summary>
        public async Task UpdateSettingValueAsync(string key, string? newValue, MetaverseObject? initiatedBy, string? changeReason = null)
        {
            var (setting, activity) = await PrepareUpdateAsync(key, newValue);
            AuditHelper.SetUpdated(setting, initiatedBy);
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);
            await Application.Repository.ServiceSettings.UpdateSettingAsync(setting);
            await CaptureConfigurationChangeAsync(activity, setting, changeReason);
            await Application.Activities.CompleteActivityAsync(activity);
        }

        /// <summary>
        /// Updates a service setting value (API key initiated) and creates an Activity for audit purposes, carrying a
        /// redacted, versioned configuration snapshot (and the optional change reason) in the setting's change history.
        /// Encrypted string values are automatically encrypted before storage.
        /// </summary>
        public async Task UpdateSettingValueAsync(string key, string? newValue, ApiKey initiatedByApiKey, string? changeReason = null)
        {
            var (setting, activity) = await PrepareUpdateAsync(key, newValue);
            AuditHelper.SetUpdated(setting, initiatedByApiKey);
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
            await Application.Repository.ServiceSettings.UpdateSettingAsync(setting);
            await CaptureConfigurationChangeAsync(activity, setting, changeReason);
            await Application.Activities.CompleteActivityAsync(activity);
        }

        /// <summary>
        /// Reverts a service setting to its default value and creates an Activity for audit purposes, carrying a
        /// versioned configuration snapshot whose diff shows the override being removed.
        /// </summary>
        public async Task RevertSettingToDefaultAsync(string key, MetaverseObject? initiatedBy, string? changeReason = null)
        {
            var (setting, activity) = await PrepareRevertAsync(key);
            AuditHelper.SetUpdated(setting, initiatedBy);
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);
            await Application.Repository.ServiceSettings.UpdateSettingAsync(setting);
            await CaptureConfigurationChangeAsync(activity, setting, changeReason);
            await Application.Activities.CompleteActivityAsync(activity);
        }

        /// <summary>
        /// Reverts a service setting to its default value (API key initiated) and creates an Activity for audit
        /// purposes, carrying a versioned configuration snapshot whose diff shows the override being removed.
        /// </summary>
        public async Task RevertSettingToDefaultAsync(string key, ApiKey initiatedByApiKey, string? changeReason = null)
        {
            var (setting, activity) = await PrepareRevertAsync(key);
            AuditHelper.SetUpdated(setting, initiatedByApiKey);
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
            await Application.Repository.ServiceSettings.UpdateSettingAsync(setting);
            await CaptureConfigurationChangeAsync(activity, setting, changeReason);
            await Application.Activities.CompleteActivityAsync(activity);
        }

        /// <summary>
        /// Captures a redacted, versioned configuration snapshot of a Service Setting onto its audit Activity via the
        /// shared ConfigurationChangeCaptureService (which owns the toggle, dedupe-guard, versioning and best-effort
        /// behaviours). The just-persisted entity is snapshotted directly: the update paths mutate the instance they
        /// persist, so it is the persisted truth.
        /// </summary>
        private async Task CaptureConfigurationChangeAsync(Activity activity, ServiceSetting setting, string? changeReason)
        {
            await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
                ActivityTargetType.ServiceSetting, setting.Key,
                hashKey => Task.FromResult<ConfigurationSnapshot?>(Application.ConfigurationSnapshots.CreateSnapshot(setting, hashKey)),
                $"Service Setting {setting.Key}");
        }

        /// <summary>
        /// Records a System-attributed Create Activity and version-1 baseline snapshot for a built-in Service Setting
        /// created in the current seeding pass, grouped under the seeding pass's parent Activity, so its origin is
        /// visible in the change history and under System Initialisation (matching the other seeded configuration
        /// types). Built-in Service Settings are created by the per-startup <c>SyncServiceSettingsAsync</c> drift-sync
        /// rather than the one-time <c>SeedAsync</c> batch, so the caller records the baseline after the sync loop over
        /// the settings that were newly created this pass. Recording after the full loop guarantees the change-tracking
        /// toggle and hash-key settings (which the capture itself reads to decide whether and how to redact) already
        /// exist, so there is no bootstrap-ordering dependency even for the encrypted settings or the hash-key setting
        /// itself. Idempotency is the caller's responsibility: <see cref="SeedingServer"/> only calls this for settings
        /// that did not exist before the pass, so a restart re-baselines nothing. A missing setting is a safe no-op.
        /// </summary>
        internal async Task RecordSeededServiceSettingBaselineAsync(string key, string displayName, Guid parentActivityId)
        {
            var setting = await GetSettingAsync(key);
            if (setting == null)
                return;

            var activity = new Activity
            {
                TargetName = displayName,
                TargetType = ActivityTargetType.ServiceSetting,
                TargetOperationType = ActivityTargetOperationType.Create,
                ParentActivityId = parentActivityId,
                Message = $"Created built-in Service Setting '{displayName}'"
            };
            await Application.Activities.CreateSystemActivityAsync(activity);

            try
            {
                await CaptureConfigurationChangeAsync(activity, setting, "Built-in Service Setting created automatically by JIM.");
                await Application.Activities.CompleteActivityAsync(activity);
            }
            catch (Exception ex)
            {
                await Application.Activities.FailActivityWithErrorAsync(activity, ex);
                throw;
            }
        }

        /// <summary>
        /// Records a System-attributed Update Activity and versioned snapshot for a built-in Service Setting whose value
        /// JIM refreshed on startup from the deployment's environment configuration (the read-only settings updated by
        /// <see cref="SeedingServer.SyncServiceSettingsAsync"/>), so an operator changing e.g. the SSO authority or
        /// rotating a secret in the environment leaves an audit trail. Churn-free: it records nothing unless the
        /// setting's current snapshot actually differs from its latest stored snapshot, comparing encrypted values by
        /// their deterministic keyed hash so a mere re-encryption is not treated as a change; the seeding parent is
        /// materialised lazily (via <paramref name="getParentActivityAsync"/>) only when a change is recorded, so an
        /// unchanged restart creates no parent Activity. A missing setting, or disabled change tracking, is a safe no-op.
        /// </summary>
        internal async Task RecordSeededServiceSettingUpdateIfChangedAsync(string key, string displayName, Func<Task<Guid>> getParentActivityAsync)
        {
            var setting = await GetSettingAsync(key);
            if (setting == null)
                return;

            if (!await GetConfigurationChangeTrackingEnabledAsync())
                return;

            // Decide whether anything actually changed before creating an Activity. The capture guard below would also
            // skip the version for an unchanged setting, but the Activity row itself would still be created and clutter
            // the audit log on every restart, so the change is detected up front against the latest stored snapshot.
            var hashKey = await GetOrCreateConfigurationChangeHashKeyAsync();
            var current = Application.ConfigurationSnapshots.CreateSnapshot(setting, hashKey);
            var latest = JIM.Application.Services.ConfigurationSnapshotService.Deserialise(
                await Application.Repository.Activity.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ServiceSetting, key));
            if (latest != null && !Application.ConfigurationDiffs.Diff(latest, current).HasChanges)
                return;

            var activity = new Activity
            {
                TargetName = displayName,
                TargetType = ActivityTargetType.ServiceSetting,
                TargetOperationType = ActivityTargetOperationType.Update,
                ParentActivityId = await getParentActivityAsync(),
                Message = $"Updated built-in Service Setting '{displayName}' from environment configuration"
            };
            await Application.Activities.CreateSystemActivityAsync(activity);

            try
            {
                await CaptureConfigurationChangeAsync(activity, setting, "Built-in Service Setting updated from environment configuration.");
                await Application.Activities.CompleteActivityAsync(activity);
            }
            catch (Exception ex)
            {
                await Application.Activities.FailActivityWithErrorAsync(activity, ex);
                throw;
            }
        }

        private async Task<(ServiceSetting setting, Activity activity)> PrepareUpdateAsync(string key, string? newValue)
        {
            var setting = await GetSettingAsync(key);
            if (setting == null)
                throw new InvalidOperationException($"Setting with key '{key}' not found.");

            if (setting.IsReadOnly)
                throw new InvalidOperationException($"Setting '{setting.DisplayName}' is read-only and cannot be modified.");

            // Encrypt encrypted string values before storing
            if (setting.ValueType == ServiceSettingValueType.StringEncrypted &&
                !string.IsNullOrEmpty(newValue) &&
                Application.CredentialProtection != null)
            {
                newValue = Application.CredentialProtection.Protect(newValue);
            }

            setting.Value = newValue;

            var activity = new Activity
            {
                TargetName = setting.DisplayName,
                TargetType = ActivityTargetType.ServiceSetting,
                TargetOperationType = ActivityTargetOperationType.Update,
                Status = ActivityStatus.InProgress
            };

            return (setting, activity);
        }

        private async Task<(ServiceSetting setting, Activity activity)> PrepareRevertAsync(string key)
        {
            var setting = await GetSettingAsync(key);
            if (setting == null)
                throw new InvalidOperationException($"Setting with key '{key}' not found.");

            if (setting.IsReadOnly)
                throw new InvalidOperationException($"Setting '{setting.DisplayName}' is read-only and cannot be modified.");

            setting.Value = null; // null means use default

            var activity = new Activity
            {
                TargetName = setting.DisplayName,
                TargetType = ActivityTargetType.ServiceSetting,
                TargetOperationType = ActivityTargetOperationType.Revert,
                Status = ActivityStatus.InProgress
            };

            return (setting, activity);
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

                if (underlyingType == typeof(Guid))
                    return (T)(object)Guid.Parse(value);

                if (underlyingType.IsEnum)
                    return (T)Enum.Parse(underlyingType, value);

                return default;
            }
            catch (FormatException)
            {
                return default;
            }
            catch (OverflowException)
            {
                return default;
            }
            catch (ArgumentException)
            {
                return default;
            }
        }

        #endregion
    }
}