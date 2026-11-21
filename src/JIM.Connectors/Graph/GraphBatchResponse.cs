// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text.Json;

namespace JIM.Connectors.Graph;

/// <summary>
/// The answer to one request in a <c>$batch</c>: its status and body. A request Graph returned no answer for has a
/// status of 0 and is a failure, never assumed applied.
/// </summary>
internal sealed record GraphBatchResponse(int Status, JsonElement? Body, string? Error = null)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    public HttpStatusCode StatusCode => (HttpStatusCode)Status;
}
