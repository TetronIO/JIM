// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models.Api;

/// <summary>
/// Response returned when a Clear Connector Space request is queued.
/// </summary>
public class ConnectorSpaceClearResponse
{
    /// <summary>
    /// The Activity id for tracking the clear.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// The worker task id for the queued clear.
    /// </summary>
    public Guid TaskId { get; set; }

    /// <summary>
    /// Message describing the result.
    /// </summary>
    public string Message { get; set; } = null!;
}
