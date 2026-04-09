// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A copy of a connector setting that will get persisted as a definition, that connected systems can reference when collecting values for connected system settings.
/// </summary>
public class ConnectorDefinitionSetting : ConnectorSetting
{
    public int Id { get; set; }

    /// <summary>
    /// Backwards navigation link for EF. Do not use.
    /// </summary>
    public List<ConnectedSystemSettingValue> Values { get; set; } = null!;
}