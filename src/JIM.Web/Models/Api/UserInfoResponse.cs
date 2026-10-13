// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Web.Models.Api;

/// <summary>
/// Who the caller is to JIM, as <c>GET /api/v1/userinfo</c> returns it: whether they have a JIM identity, and
/// which roles it holds. A caller who authenticated but has no identity is answered rather than refused, so a
/// client can explain what to do.
/// </summary>
public class UserInfoResponse
{
    /// <summary>
    /// Whether the caller has a JIM identity (a Metaverse Object) and so can use JIM.
    /// </summary>
    public bool Authorised { get; set; }

    /// <summary>
    /// Whether the caller holds the Administrator role.
    /// </summary>
    public bool IsAdministrator { get; set; }

    /// <summary>
    /// The caller's name: the one their identity provider gives them, or the API key's name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// How the caller authenticated: oauth or api_key.
    /// </summary>
    public string AuthMethod { get; set; } = string.Empty;

    /// <summary>
    /// The caller's Metaverse Object; null when they have no JIM identity.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// The names of the roles the caller holds; empty when they have no JIM identity.
    /// </summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>
    /// Why a caller with no JIM identity cannot use JIM, and what to do about it. Present only when
    /// Authorised is false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}
