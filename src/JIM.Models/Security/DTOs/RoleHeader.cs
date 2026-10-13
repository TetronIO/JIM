// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Security.DTOs;

/// <summary>
/// Lightweight representation of a Role for list views. The static member count is
/// projected in SQL to avoid materialising the full member list per role.
/// </summary>
public class RoleHeader
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
}
