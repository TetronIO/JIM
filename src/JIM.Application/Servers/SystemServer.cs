// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Utility;
using JIM.Utilities;
using Serilog;
namespace JIM.Application.Servers;

/// <summary>
/// System-wide administrative operations that cut across the other servers.
/// Currently scoped to factory reset; future maintenance routines should join it here.
/// </summary>
public class SystemServer
{
    private JimApplication Application { get; }

    internal SystemServer(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// Wipes all customer data and configuration from the database, preserving the schema,
    /// EF Core migration history, and the rows seeded at first launch (built-in metaverse
    /// attributes and object types, built-in roles, built-in connector definitions, built-in
    /// example data sets and templates, built-in predefined searches, the singleton service
    /// settings record, and infrastructure API keys).
    /// </summary>
    /// <remarks>
    /// On completion the method always records a single Reset <see cref="Activity"/> attributed to
    /// the invoking principal (created after the wipe so it survives it), and advances the global
    /// authentication epoch (<see cref="ServiceSettings.SessionsValidFromUtc"/>) so that every
    /// existing portal session is invalidated on its next request. Both happen in both reset modes.
    /// </remarks>
    /// <param name="initiatorType">The type of security principal that initiated the reset.</param>
    /// <param name="initiatorId">The id of the initiating principal (MetaverseObject or ApiKey), if known.</param>
    /// <param name="initiatorName">The display name of the initiating principal, captured for the audit trail.</param>
    /// <param name="includeAdministrators">
    /// When <c>false</c> (the default), Metaverse Objects holding the built-in Administrator role are
    /// preserved so the operator is not locked out of the portal. When <c>true</c>, those administrator
    /// identities are removed as well, leaving a true brand-new install.
    /// </param>
    /// <param name="acknowledgeAdministratorLockout">
    /// Overrides the lockout guard. The guard refuses an administrator-inclusive wipe when no initial
    /// administrator is configured (<c>JIM_SSO_INITIAL_ADMIN</c>), because the portal would then be
    /// inaccessible afterwards. Set to <c>true</c> to proceed anyway (for example when access is retained
    /// via the infrastructure API key).
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when one or more activities are currently <c>InProgress</c> (racing a wipe against an
    /// ongoing sync run can corrupt customer data), or when the lockout guard refuses an
    /// administrator-inclusive wipe with no initial administrator configured.
    /// </exception>
    public async Task<SystemResetResult> ResetSystemAsync(
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName,
        bool includeAdministrators,
        bool acknowledgeAdministratorLockout = false)
    {
        Log.Information(
            "ResetSystemAsync: Factory reset requested by {User} (includeAdministrators={IncludeAdministrators})",
            LogSanitiser.Sanitise(initiatorName),
            includeAdministrators);

        // Refuse if anything is mid-flight. Synchronisation integrity is paramount: a wipe
        // committed while a sync run is writing to the same tables would leave the database
        // in an indeterminate state, with partial activity records, half-flushed pending
        // exports, and orphaned RPEIs.
        var inProgress = await Application.Repository.Activity.GetActivitiesAsync(
            page: 1,
            pageSize: 1,
            statusFilter: new[] { ActivityStatus.InProgress });

        if (inProgress.TotalResults > 0)
        {
            Log.Warning(
                "ResetSystemAsync: Refused — {Count} activity(ies) currently in progress",
                inProgress.TotalResults);
            throw new InvalidOperationException(
                $"Cannot reset the system while activities are in progress ({inProgress.TotalResults} found). " +
                "Wait for activities to finish, or cancel them, and try again.");
        }

        // Lockout guard: removing administrators with no initial administrator configured would leave
        // the portal inaccessible (only the infrastructure API key would remain). Refuse unless the
        // caller has explicitly acknowledged the risk.
        if (includeAdministrators && !acknowledgeAdministratorLockout)
        {
            var initialAdmin = Environment.GetEnvironmentVariable(Constants.Config.SsoInitialAdmin);
            if (string.IsNullOrEmpty(initialAdmin))
            {
                Log.Warning("ResetSystemAsync: Refused — administrator-inclusive wipe requested with no initial administrator configured");
                throw new InvalidOperationException(
                    "Refusing to remove administrators: no initial administrator is configured " +
                    $"({Constants.Config.SsoInitialAdmin}), so the portal would be inaccessible after the reset. " +
                    "Configure an initial administrator, or acknowledge the lockout risk to proceed.");
            }
        }

        var result = await Application.Repository.System.ResetSystemAsync(includeAdministrators);

        // Record the reset as an Activity AFTER the wipe so it survives it. This is the auditable
        // record of who initiated the reset; it is never optional.
        var activity = new Activity
        {
            TargetType = ActivityTargetType.System,
            TargetOperationType = ActivityTargetOperationType.Reset,
            TargetName = "Factory reset",
            Message = result.BuildResetMessage(includeAdministrators)
        };
        await Application.Activities.CreateActivityWithTriadAsync(activity, initiatorType, initiatorId, initiatorName);
        // Mark it complete immediately: the reset is a point-in-time event, and leaving it InProgress
        // would cause the in-progress guard to block any subsequent reset.
        await Application.Activities.CompleteActivityAsync(activity);

        // Advance the authentication epoch as the final step so every existing portal session is
        // invalidated on its next request. This de-authenticates everyone (no stale role claims or
        // Metaverse Object references survive the wipe). API keys are unaffected; they are validated
        // against the database per request.
        var serviceSettings = await Application.ServiceSettings.GetServiceSettingsAsync();
        if (serviceSettings != null)
        {
            serviceSettings.SessionsValidFromUtc = DateTime.UtcNow;
            await Application.ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
        }
        else
        {
            Log.Warning("ResetSystemAsync: ServiceSettings not found; could not advance the authentication epoch to invalidate existing sessions");
        }

        Log.Information(
            "ResetSystemAsync: Factory reset completed by {User}. Removed {CsCount} connected systems, {MvoCount} metaverse objects; {AdminsRetained} administrators retained",
            LogSanitiser.Sanitise(initiatorName),
            result.ConnectedSystemsRemoved,
            result.MetaverseObjectsRemoved,
            result.AdministratorsRetained);

        return result;
    }
}
