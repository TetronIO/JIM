// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Security;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a Role for list views.
/// </summary>
public class RoleDto
{
    /// <summary>
    /// The Role's identifier.
    /// </summary>
    public int Id { get; set; }
    /// <summary>
    /// The Role's name.
    /// </summary>
    public string Name { get; set; } = null!;
    /// <summary>
    /// Whether JIM created the Role when it was set up. A built-in Role cannot be deleted.
    /// </summary>
    public bool BuiltIn { get; set; }
    /// <summary>
    /// When the Role was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }
    /// <summary>
    /// How many Metaverse Objects have been made members of the Role.
    /// </summary>
    public int StaticMemberCount { get; set; }

    /// <summary>
    /// Creates a DTO from a Role entity.
    /// </summary>
    public static RoleDto FromEntity(Role entity)
    {
        return new RoleDto
        {
            Id = entity.Id,
            Name = entity.Name,
            BuiltIn = entity.BuiltIn,
            Created = entity.Created,
            StaticMemberCount = entity.StaticMembers?.Count ?? 0
        };
    }
}
