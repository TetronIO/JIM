// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// Lightweight API representation of a Metaverse Object that is a member of a Role.
/// </summary>
public class RoleMemberDto
{
    /// <summary>
    /// The unique identifier (GUID) of the Metaverse Object.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The display name of the Metaverse Object.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The object type ID.
    /// </summary>
    public int TypeId { get; set; }

    /// <summary>
    /// The object type name.
    /// </summary>
    public string TypeName { get; set; } = null!;

    /// <summary>
    /// Creates a DTO from a MetaverseObject entity.
    /// </summary>
    public static RoleMemberDto FromEntity(MetaverseObject entity)
    {
        return new RoleMemberDto
        {
            Id = entity.Id,
            DisplayName = entity.DisplayName,
            TypeId = entity.Type?.Id ?? 0,
            TypeName = entity.Type?.Name ?? string.Empty
        };
    }
}
