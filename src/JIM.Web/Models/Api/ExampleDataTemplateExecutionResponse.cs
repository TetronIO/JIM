// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models.Api;

/// <summary>
/// Response returned when a Data Generation Template execution is queued.
/// </summary>
public class ExampleDataTemplateExecutionResponse
{
    /// <summary>
    /// The Activity ID for tracking the execution. Follow it via GET /activities/{id} or the
    /// lightweight GET /activities/{id}/progress endpoint.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// The worker task ID for the queued execution.
    /// </summary>
    public Guid TaskId { get; set; }

    /// <summary>
    /// Message describing the result.
    /// </summary>
    public string Message { get; set; } = null!;
}
