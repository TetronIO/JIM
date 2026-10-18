// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Service;

/// <summary>
/// Who is making a SCIM request, and to which Connected System's store: what the server needs beyond the request itself.
/// </summary>
/// <param name="ConnectedSystemId">The Connected System whose store the request is for.</param>
/// <param name="BaseUrl">The store's SCIM base URL, which resource locations (<c>meta.location</c>, <c>$ref</c>) are built
/// on.</param>
/// <param name="ApiKeyId">The API key that authenticated the request, for the journal (D17).</param>
/// <param name="ApiKeyName">That key's name when the request was made, kept in the journal if the key is later deleted.</param>
/// <param name="ClientAddress">The client's address, for the journal.</param>
public sealed record ScimRequestContext(
    int ConnectedSystemId,
    string BaseUrl,
    Guid? ApiKeyId = null,
    string? ApiKeyName = null,
    string? ClientAddress = null);
