// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Operations;

namespace JIM.Application.Services;

/// <summary>
/// The memory thresholds JIM acts on, and the words it uses for them, in one place: the service health report warns
/// at the same share of memory the Worker's guard warns at, so the portal, the REST API, PowerShell and an Activity
/// never describe one situation two ways.
/// </summary>
public static class MemoryPressure
{
    /// <summary>
    /// At this share of memory in use a service is reported <see cref="ServiceHealthCondition.MemoryLow"/>, and a
    /// running task's Activity carries a warning.
    /// </summary>
    public const double WarningShare = 0.85;

    /// <summary>
    /// At this share, with the Worker holding most of what is in use, the Worker stops its running tasks before the
    /// operating system stops the Worker.
    /// </summary>
    public const double StopShare = 0.92;

    /// <summary>
    /// At this share, after the Worker has asked its tasks to stop and they have not, it records why and exits
    /// rather than wait to be killed without a word.
    /// </summary>
    public const double AbortShare = 0.97;

    /// <summary>
    /// The Worker acts on memory only when it holds more than this share of what is in use: when something else on
    /// the host is filling memory, stopping a synchronisation would not help, and the operating system would not
    /// pick the Worker to stop.
    /// </summary>
    public const double DominantShare = 0.5;

    /// <summary>Whether a reading shows memory in use at or above <see cref="WarningShare"/>.</summary>
    public static bool IsLow(MemoryReading reading) => reading.UsedFraction >= WarningShare;

    /// <summary>Whether the process holds more than <see cref="DominantShare"/> of the memory in use.</summary>
    public static bool ProcessIsDominant(MemoryReading reading) => reading.UsedBytes is > 0 and var used && reading.ProcessBytes > used * DominantShare;

    /// <summary>
    /// "57 GB of 62 GB in use, 41 GB of it by this service": the figures a person needs to judge the reading, with
    /// <paramref name="processName"/> naming whose share the last figure is. Empty when the total is unknown.
    /// </summary>
    public static string Describe(MemoryReading reading, string processName) =>
        reading is { UsedBytes: { } used, TotalBytes: { } total }
            ? $"{Bytes(used)} of {Bytes(total)} in use, {Bytes(reading.ProcessBytes)} of it by {processName}"
            : string.Empty;

    /// <summary>
    /// What a run that failed for want of memory says on its Activity: that it ran out, the figures as they stand now (the
    /// run's own memory has been released by the time it is recorded, so the figures say what was left beside it), and
    /// the remedy.
    /// </summary>
    public static string OutOfMemoryText(MemoryReading reading)
    {
        var figures = Describe(reading, "this service");
        return "This run ran out of memory and stopped" + (figures.Length > 0 ? $" (now {figures})" : string.Empty) +
               ". In a container with a memory limit, .NET stops at about 75% of the limit. Give JIM more memory, or run less at once, then run it again.";
    }

    /// <summary>A share as a whole percentage: "92%".</summary>
    public static string Percent(double share) => $"{(int)Math.Round(share * 100)}%";

    /// <summary>A size in decimal units, as people read memory: "57 GB", "4.2 GB", "850 MB".</summary>
    public static string Bytes(long bytes)
    {
        const double gb = 1_000_000_000d;
        const double mb = 1_000_000d;
        return bytes switch
        {
            >= 10_000_000_000 => $"{(bytes / gb).ToString("N0", CultureInfo.InvariantCulture)} GB",
            >= 1_000_000_000 => $"{(bytes / gb).ToString("0.#", CultureInfo.InvariantCulture)} GB",
            _ => $"{(bytes / mb).ToString("N0", CultureInfo.InvariantCulture)} MB"
        };
    }
}
