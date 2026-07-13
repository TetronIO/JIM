// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Worker.Processors;

/// <summary>
/// Resolves the degree of parallelism used for the Export batch-processing loop
/// (ExportExecutionOptions.MaxParallelism). Extracted from SyncExportTaskProcessor so the
/// resolution order can be unit tested without a full processor harness (issue #985d).
/// </summary>
internal static class ExportParallelismResolver
{
    /// <summary>
    /// Fallback used when neither an explicit Connected System setting nor a connector
    /// recommendation is available. Matches the pre-#985d default (sequential export).
    /// </summary>
    internal const int FallbackParallelism = 1;

    /// <summary>
    /// Lower bound applied to a connector's recommended parallelism.
    /// </summary>
    internal const int MinRecommendedParallelism = 1;

    /// <summary>
    /// Upper bound applied to a connector's recommended parallelism, so a connector cannot
    /// recommend an unbounded degree of concurrency against the target system.
    /// </summary>
    internal const int MaxRecommendedParallelism = 16;

    /// <summary>
    /// Resolves the export batch parallelism to use, and logs which source supplied it.
    /// Resolution order: an explicit Max Export Parallelism value on the Connected System always
    /// wins (respects the administrator's choice, same philosophy as the LDAP connector's Export
    /// Concurrency auto-tune guard); otherwise a connector recommendation via
    /// <see cref="IConnectorRecommendedExportParallelism"/> is used, clamped to
    /// [<see cref="MinRecommendedParallelism"/>, <see cref="MaxRecommendedParallelism"/>];
    /// otherwise <see cref="FallbackParallelism"/>.
    /// </summary>
    /// <param name="explicitMaxExportParallelism">The Connected System's MaxExportParallelism setting, or null if not explicitly configured.</param>
    /// <param name="connector">The connector instance for the Connected System, consulted for a recommendation if it implements <see cref="IConnectorRecommendedExportParallelism"/>.</param>
    /// <param name="settingValues">The Connected System's setting values, passed to the connector's recommendation method.</param>
    /// <param name="connectedSystemName">Name of the Connected System, for the source log line.</param>
    public static int Resolve(
        int? explicitMaxExportParallelism,
        IConnector connector,
        List<ConnectedSystemSettingValue> settingValues,
        string connectedSystemName)
    {
        if (explicitMaxExportParallelism.HasValue)
        {
            Log.Information(
                "ExportParallelismResolver: Using explicitly configured Max Export Parallelism {Value} for {SystemName}",
                explicitMaxExportParallelism.Value, connectedSystemName);
            return explicitMaxExportParallelism.Value;
        }

        if (connector is IConnectorRecommendedExportParallelism recommender)
        {
            var recommended = recommender.GetRecommendedExportParallelism(settingValues);
            if (recommended.HasValue)
            {
                var clamped = Math.Clamp(recommended.Value, MinRecommendedParallelism, MaxRecommendedParallelism);
                Log.Information(
                    "ExportParallelismResolver: Using connector-recommended Max Export Parallelism {Value} (clamped from {Recommended}) for {SystemName}",
                    clamped, recommended.Value, connectedSystemName);
                return clamped;
            }
        }

        Log.Information(
            "ExportParallelismResolver: No explicit Max Export Parallelism configured and no connector recommendation available; defaulting to {Value} for {SystemName}",
            FallbackParallelism, connectedSystemName);
        return FallbackParallelism;
    }
}
