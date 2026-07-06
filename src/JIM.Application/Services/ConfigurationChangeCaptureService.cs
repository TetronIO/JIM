// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using System.Text.Json;
using JIM.Models.Activities;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// The single implementation of configuration change capture, shared by every configuration type's server. It owns
/// the behaviours all capture paths must honour identically: recording the optional change reason independently of
/// the snapshot toggle, the ChangeTracking.ConfigurationChanges.Enabled check, the semantic no-change dedupe guard
/// (a save that changes nothing consumes no version), per-object version allocation, best-effort semantics (a
/// capture failure never fails the configuration operation that succeeded), and the unversioned deletion tombstone.
/// A server supplies only its redacted snapshot builder (via <see cref="ConfigurationSnapshotService"/>) and the
/// target's key; centralising the rest means a new configuration type cannot get these behaviours subtly wrong by
/// re-implementing them.
/// </summary>
public class ConfigurationChangeCaptureService
{
    private JimApplication Application { get; }

    internal ConfigurationChangeCaptureService(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// Captures a redacted, versioned configuration snapshot onto a configuration-change Activity for an
    /// integer-keyed configuration object. Call after the entity has been persisted (so its id and graph are
    /// current) and before the Activity is completed, so the snapshot fields are saved as part of the existing
    /// CompleteActivityAsync update.
    /// </summary>
    /// <param name="activity">The in-flight Activity the snapshot is recorded on.</param>
    /// <param name="changeReason">The optional reason for the change, recorded even when tracking is disabled.</param>
    /// <param name="targetType">The configuration object's target type; keys both the Activity target column and
    /// the per-object version sequence.</param>
    /// <param name="targetObjectId">The configuration object's database id.</param>
    /// <param name="buildSnapshotAsync">Builds the redacted snapshot given the server-held hash key. May return
    /// null to skip capture, e.g. when the object cannot be reloaded. Runs inside the best-effort block, so a
    /// repository failure here is logged rather than propagated.</param>
    /// <param name="targetDescription">Short target description for log messages, e.g. "Connected System 5".</param>
    public async Task CaptureChangeAsync(Activity activity, string? changeReason, ActivityTargetType targetType,
        int targetObjectId, Func<byte[], Task<ConfigurationSnapshot?>> buildSnapshotAsync, string targetDescription)
    {
        await CaptureChangeCoreAsync(activity, changeReason, buildSnapshotAsync,
            () => activity.SetConfigurationTargetId(targetType, targetObjectId),
            () => Application.Activities.GetLatestConfigurationChangeSnapshotAsync(targetType, targetObjectId),
            () => Application.Activities.GetNextConfigurationChangeVersionAsync(targetType, targetObjectId),
            targetDescription);
    }

    /// <summary>
    /// Guid-keyed counterpart of
    /// <see cref="CaptureChangeAsync(Activity,string?,ActivityTargetType,int,Func{byte[],Task{ConfigurationSnapshot?}},string)"/>,
    /// for configuration objects (e.g. a Schedule) whose database id is a Guid.
    /// </summary>
    public async Task CaptureChangeAsync(Activity activity, string? changeReason, ActivityTargetType targetType,
        Guid targetObjectId, Func<byte[], Task<ConfigurationSnapshot?>> buildSnapshotAsync, string targetDescription)
    {
        await CaptureChangeCoreAsync(activity, changeReason, buildSnapshotAsync,
            () => activity.SetConfigurationTargetId(targetType, targetObjectId),
            () => Application.Activities.GetLatestConfigurationChangeSnapshotAsync(targetType, targetObjectId),
            () => Application.Activities.GetNextConfigurationChangeVersionAsync(targetType, targetObjectId),
            targetDescription);
    }

    /// <summary>
    /// String-keyed counterpart of
    /// <see cref="CaptureChangeAsync(Activity,string?,ActivityTargetType,int,Func{byte[],Task{ConfigurationSnapshot?}},string)"/>,
    /// for configuration objects (e.g. a Service Setting) whose primary key is a string.
    /// </summary>
    public async Task CaptureChangeAsync(Activity activity, string? changeReason, ActivityTargetType targetType,
        string targetObjectKey, Func<byte[], Task<ConfigurationSnapshot?>> buildSnapshotAsync, string targetDescription)
    {
        await CaptureChangeCoreAsync(activity, changeReason, buildSnapshotAsync,
            () => activity.SetConfigurationTargetId(targetType, targetObjectKey),
            () => Application.Activities.GetLatestConfigurationChangeSnapshotAsync(targetType, targetObjectKey),
            () => Application.Activities.GetNextConfigurationChangeVersionAsync(targetType, targetObjectKey),
            targetDescription);
    }

    /// <summary>
    /// Captures an unversioned tombstone snapshot onto a delete Activity, before the object is removed. Matching
    /// the established Synchronisation Rule and Schedule deletion behaviour, this sets neither the Activity's
    /// target column nor a version: the object is deleted before the Activity completes, so the Activity is left
    /// unlinked and the snapshot is surfaced via the Activity itself rather than the object's history.
    /// </summary>
    public async Task CaptureDeletionAsync(Activity activity, string? changeReason,
        Func<byte[], Task<ConfigurationSnapshot?>> buildSnapshotAsync, string targetDescription)
    {
        ApplyChangeReason(activity, changeReason);

        try
        {
            if (!await Application.ServiceSettings.GetConfigurationChangeTrackingEnabledAsync())
                return;

            var hashKey = await Application.ServiceSettings.GetOrCreateConfigurationChangeHashKeyAsync();
            var snapshot = await buildSnapshotAsync(hashKey);
            if (snapshot == null)
                return;

            activity.ConfigurationChangeSnapshot = ConfigurationSnapshotService.Serialise(snapshot);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or FormatException or JsonException or DbException)
        {
            // Best-effort: a capture failure must never fail the deletion that is about to proceed; the miss is logged.
            Log.Warning(ex, "CaptureDeletionAsync: failed to capture deletion snapshot for {Target}; the deletion proceeded but its history snapshot was not recorded.", targetDescription);
        }
    }

    // The shared capture core. The key-shape-specific pieces (setting the Activity target column and the two
    // versioning lookups) are bound by the public overloads; everything else is identical across key shapes.
    private async Task CaptureChangeCoreAsync(Activity activity, string? changeReason,
        Func<byte[], Task<ConfigurationSnapshot?>> buildSnapshotAsync,
        Action setTargetId,
        Func<Task<string?>> getLatestSnapshotAsync,
        Func<Task<int>> getNextVersionAsync,
        string targetDescription)
    {
        // The reason is recorded independently of the snapshot toggle and has no external dependencies, so it is
        // set outside the best-effort block below.
        ApplyChangeReason(activity, changeReason);

        try
        {
            if (!await Application.ServiceSettings.GetConfigurationChangeTrackingEnabledAsync())
                return;

            var hashKey = await Application.ServiceSettings.GetOrCreateConfigurationChangeHashKeyAsync();
            var snapshot = await buildSnapshotAsync(hashKey);
            if (snapshot == null)
                return;

            // The target column is set before the dedupe guard so a skipped no-change save still deep-links the
            // Activity to the object it belongs to.
            setTargetId();

            // Idempotent capture guard: skip when nothing changed versus the latest stored snapshot, so no-change
            // saves (e.g. worker paths that persist after every import) do not consume versions and drown real
            // changes in noise. The comparison must be semantic (via the diff engine), not textual: snapshots are
            // stored in a jsonb column, and PostgreSQL normalises the text (key ordering, spacing) so the string
            // read back never equals a fresh serialisation.
            var latest = ConfigurationSnapshotService.Deserialise(await getLatestSnapshotAsync());
            if (latest != null && !Application.ConfigurationDiffs.Diff(latest, snapshot).HasChanges)
            {
                Log.Debug("CaptureChangeAsync: configuration of {Target} is unchanged from its latest snapshot; no new version recorded.", targetDescription);
                return;
            }

            activity.ConfigurationChangeVersion = await getNextVersionAsync();
            activity.ConfigurationChangeSnapshot = ConfigurationSnapshotService.Serialise(snapshot);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or FormatException or JsonException or DbException)
        {
            // Configuration change capture is best-effort secondary metadata recorded after the entity has already
            // been persisted; it must never fail or roll back the configuration operation that succeeded. On failure
            // the change history simply misses this snapshot. The failure is logged (never silent) so the gap is
            // diagnosable.
            Log.Warning(ex, "CaptureChangeAsync: failed to capture configuration snapshot for {Target}; the change was saved but its history snapshot was not recorded.", targetDescription);
        }
    }

    private static void ApplyChangeReason(Activity activity, string? changeReason)
    {
        if (!string.IsNullOrWhiteSpace(changeReason))
            activity.ChangeReason = changeReason.Trim();
    }
}
