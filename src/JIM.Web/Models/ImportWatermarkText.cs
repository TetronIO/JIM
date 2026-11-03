// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Staging;

namespace JIM.Web.Models;

/// <summary>
/// How the portal writes a position a Connector describes in one of its import watermarks, so an Activity's read range
/// and a partition's current watermark read alike.
/// </summary>
public static class ImportWatermarkText
{
    /// <summary>
    /// One position: a number with its thousands separated, a point in time in local time, or the Connector's own text.
    /// </summary>
    public static string Value(ImportWatermarkPoint point) =>
        point.Number?.ToString("N0", CultureInfo.InvariantCulture)
        ?? point.Time?.ToLocalTime().ToFriendlyDate()
        ?? point.Text
        ?? string.Empty;

    /// <summary>
    /// Every position a description holds, each followed by what it counts in, as in "48,213 (Update Sequence Number)";
    /// a position scoped within the watermark (an Object Type, a source) is prefixed with its scope.
    /// </summary>
    public static string Positions(ImportWatermarkDescription description) =>
        string.Join("; ", description.Points.Select(point =>
            point.Scope == null ? $"{Value(point)} ({description.Kind})" : $"{point.Scope}: {Value(point)} ({description.Kind})"));
}
