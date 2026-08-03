// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM;

/// <summary>
/// A deserialised SCIM response together with the parts of the HTTP response the connector needs but
/// the body does not carry.
/// </summary>
/// <typeparam name="T">The response body's type.</typeparam>
/// <param name="Body">The deserialised body, or null where the provider returned none.</param>
/// <param name="ServerDate">
/// The provider's clock, from the <c>Date</c> response header. Delta import watermarks against this
/// rather than against a JIM clock, so that the two ends of the comparison come from the same machine.
/// Null where the provider sent no header.
/// </param>
/// <param name="ETag">
/// The resource's entity tag, from the <c>ETag</c> response header. Sent back as <c>If-Match</c> on a
/// later write so the provider can refuse it if the resource moved on in between.
/// </param>
public sealed record ScimResponse<T>(T? Body, DateTimeOffset? ServerDate, string? ETag = null);
