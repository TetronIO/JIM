// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.ExampleData.DTOs;

/// <summary>
/// Lightweight representation of a ExampleDataTemplate for list views.
/// </summary>
public class ExampleDataTemplateHeader
{
    /// <summary>The unique identifier of the Data Generation Template.</summary>
    public int Id { get; set; }

    /// <summary>The name of the Data Generation Template, for example "Demo Users".</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Whether the Data Generation Template ships with JIM, rather than being created by an administrator.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>When the Data Generation Template was created, in UTC.</summary>
    public DateTime Created { set; get; }

    /// <summary>How many Object Types the Data Generation Template generates objects for.</summary>
    public int ObjectTypeCount { get; set; }

    /// <summary>
    /// Creates a header from a ExampleDataTemplate entity.
    /// </summary>
    public static ExampleDataTemplateHeader FromEntity(ExampleDataTemplate entity)
    {
        return new ExampleDataTemplateHeader
        {
            Id = entity.Id,
            Name = entity.Name,
            BuiltIn = entity.BuiltIn,
            Created = entity.Created,
            ObjectTypeCount = entity.ObjectTypes?.Count ?? 0
        };
    }
}