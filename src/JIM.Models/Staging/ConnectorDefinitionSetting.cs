// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A copy of a connector setting that will get persisted as a definition, that Connected Systems can reference when collecting values for Connected System settings.
/// </summary>
public class ConnectorDefinitionSetting : ConnectorSetting
{
    public int Id { get; set; }

    /// <summary>
    /// Where the setting comes among its Connector's settings: its position in the list the Connector declares, from 0.
    /// The Settings tab shows settings in this order. Kept current at every start, so a setting added to a Connector
    /// after installation shows where the Connector puts it rather than at the end of its section.
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// Backwards navigation link for EF. Do not use.
    /// </summary>
    public List<ConnectedSystemSettingValue> Values { get; set; } = null!;
}