// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Security.DTOs;

/// <summary>
/// Lightweight representation of a Role for list views. The static member count is
/// projected in SQL to avoid materialising the full member list per role.
/// </summary>
public class RoleHeader
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public bool BuiltIn { get; set; }

    public DateTime Created { get; set; }

    public int StaticMemberCount { get; set; }
}
