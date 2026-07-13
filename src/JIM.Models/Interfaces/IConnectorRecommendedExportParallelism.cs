// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Interfaces;

/// <summary>
/// Connectors that can recommend a directory/system-aware degree of export batch parallelism
/// implement this interface. JIM only consults it when the administrator has not explicitly
/// configured Max Export Parallelism on the Connected System; an explicit value always wins.
/// </summary>
public interface IConnectorRecommendedExportParallelism
{
    /// <summary>
    /// Returns a recommended degree of export batch parallelism for the given Connected System
    /// setting values, or null if the connector cannot offer a recommendation (for example, no
    /// schema import has run yet to detect the target system's type). Implementations MUST NOT
    /// open a network connection to compute this value; base the recommendation only on data
    /// already available in <paramref name="settingValues"/>.
    /// </summary>
    public int? GetRecommendedExportParallelism(List<ConnectedSystemSettingValue> settingValues);
}
