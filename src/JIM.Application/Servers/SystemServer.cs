// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
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
    /// attributes and object types, built-in roles, built-in connector definitions,
    /// built-in example data sets and templates, built-in predefined searches, the
    /// singleton service settings record, and infrastructure API keys).
    /// </summary>
    /// <param name="invokingUserName">
    /// The display name (or service identifier) of the principal that initiated the reset.
    /// Captured in the audit log entry. Not validated — the caller is responsible for
    /// resolving the invoking identity before calling this method.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when one or more activities are currently <c>InProgress</c>. Racing a wipe
    /// against an ongoing sync run can corrupt customer data, so the reset refuses rather
    /// than risk it. The caller should wait for activities to finish (or be cancelled)
    /// and retry.
    /// </exception>
    public async Task<SystemResetResult> ResetSystemAsync(string invokingUserName)
    {
        Log.Information("ResetSystemAsync: Factory reset requested by {User}", LogSanitiser.Sanitise(invokingUserName));

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

        var result = await Application.Repository.System.ResetSystemAsync();

        Log.Information(
            "ResetSystemAsync: Factory reset completed by {User}. Removed {CsCount} connected systems, {MvoCount} metaverse objects",
            LogSanitiser.Sanitise(invokingUserName),
            result.ConnectedSystemsRemoved,
            result.MetaverseObjectsRemoved);

        return result;
    }
}
