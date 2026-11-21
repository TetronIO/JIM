// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Graph;

/// <summary>One request inside a Microsoft Graph <c>$batch</c>.</summary>
/// <param name="Url">The request's path relative to the version, i.e. <c>/groups/{id}/members?$select=id</c>.</param>
/// <param name="Body">The JSON body for a write; null for a read.</param>
internal sealed record GraphBatchRequest(HttpMethod Method, string Url, object? Body = null);
