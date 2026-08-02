// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Scim.Discovery;
using JIM.Scim.Schema;

namespace JIM.Connectors.SCIM;

/// <summary>
/// How one import run will read the service provider: whether it asks only for what changed, and what
/// to tell the administrator when it cannot.
/// </summary>
/// <param name="Strategy">The change-detection strategy actually in force, never <see cref="ScimDeltaStrategy.Auto"/>.</param>
/// <param name="Filter">The SCIM filter to send with every page, or null for a full scan.</param>
/// <param name="WarningMessage">What to report on the Activity, where the run could not do what was asked of it.</param>
/// <param name="WarningErrorType">The classification for <paramref name="WarningMessage"/>.</param>
internal sealed record ScimImportPlan(
    ScimDeltaStrategy Strategy,
    string? Filter,
    string? WarningMessage,
    ActivityRunProfileExecutionItemErrorType? WarningErrorType)
{
    /// <summary>
    /// Decides how the run will read, from what was asked for, what the provider can do, and what the
    /// last completed import left behind.
    /// </summary>
    /// <param name="runType">The Run Profile's type. Only a Delta Import can filter.</param>
    /// <param name="configured">The administrator's Change Detection setting.</param>
    /// <param name="capabilities">What the provider advertised at discovery.</param>
    /// <param name="watermark">The instant the last completed import began reading, if there was one.</param>
    public static ScimImportPlan Create(
        ConnectedSystemRunType runType,
        ScimDeltaStrategy configured,
        ScimProviderCapabilities capabilities,
        DateTimeOffset? watermark)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        // A Full Import reads everything by definition, so there is nothing to decide.
        if (runType != ConnectedSystemRunType.DeltaImport)
            return FullScan();

        // A deliberate choice is not a shortfall, so it is not reported as one on every run.
        if (configured == ScimDeltaStrategy.FullScan)
            return FullScan();

        // Auto follows what the provider advertises. The explicit Last Modified Filter choice overrides
        // that, because providers do support filtering without advertising it.
        if (configured == ScimDeltaStrategy.Auto && !capabilities.SupportsFilter)
        {
            return FullScan(
                "Delta Import was requested, but the service provider does not advertise filtering, so JIM cannot ask it for only what changed. " +
                "Every resource was read instead. If the provider does support filtering, set Change Detection to 'Last Modified Filter' on the Connected System.");
        }

        // The first Delta Import after a Connected System is configured has nothing to filter against.
        // Reading everything establishes the watermark; failing the run would leave it unavailable for
        // ever, since only a completed import can record one.
        if (!watermark.HasValue)
        {
            return FullScan(
                "Delta Import was requested, but no watermark from a previous import was available, so JIM had nothing to ask for changes since. " +
                "Every resource was read instead, and a watermark has been recorded. Later Delta Imports will read only what changed.");
        }

        return new ScimImportPlan(ScimDeltaStrategy.LastModifiedFilter, BuildFilter(watermark.Value), null, null);
    }

    /// <summary>
    /// The plan a run switches to when the service provider rejects the filter it advertised support
    /// for. Advertising a capability and then refusing it is common enough that failing the run instead
    /// would make the connector unusable against those providers; reading everything still produces a
    /// correct import, and the warning tells the administrator to set Change Detection to Full Scan.
    /// </summary>
    public static ScimImportPlan FilterRejected()
    {
        return FullScan(
            "Delta Import was requested and the service provider advertises filtering, but it rejected the filter JIM sent. " +
            "Every resource was read instead. Set Change Detection to 'Full Scan' on the Connected System to stop JIM attempting the filter each run.");
    }

    private static ScimImportPlan FullScan(string? warningMessage = null)
    {
        return new ScimImportPlan(
            ScimDeltaStrategy.FullScan,
            null,
            warningMessage,
            warningMessage == null ? null : ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport);
    }

    /// <summary>
    /// Builds the RFC 7644 section 3.4.2.2 filter for everything changed since the watermark. The
    /// comparison value is a quoted UTC instant at second precision, which is the precision providers
    /// publish resource metadata at.
    /// </summary>
    private static string BuildFilter(DateTimeOffset watermark)
    {
        var instant = watermark.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return $"{ScimCommonAttributes.MetaLastModified} gt \"{instant}\"";
    }
}
