// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Security;

/// <summary>
/// What an API key may be used for. An unscoped key is an ordinary JIM API key, authorised by its roles; a scoped key
/// carries no roles and is accepted only on the one surface its scope names, for the one Connected System it names.
/// </summary>
public enum ApiKeyScope
{
    /// <summary>
    /// An ordinary API key: the REST API, authorised by the key's roles.
    /// </summary>
    None = 0,

    /// <summary>
    /// A SCIM client's key: JIM's SCIM 2.0 Service Provider endpoint for one Connected System, and nothing else.
    /// </summary>
    ScimServiceProvider = 1
}
