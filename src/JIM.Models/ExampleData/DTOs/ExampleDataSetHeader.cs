// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.ExampleData.DTOs;

/// <summary>
/// Lightweight representation of an ExampleDataSet for list views.
/// </summary>
public class ExampleDataSetHeader
{
    /// <summary>The unique identifier of the Example Data Set.</summary>
    public int Id { get; set; }

    /// <summary>The name of the Example Data Set, for example "Firstnames Female".</summary>
    public string Name { get; set; } = null!;

    /// <summary>Whether the Example Data Set ships with JIM, rather than being created by an administrator.</summary>
    public bool BuiltIn { get; set; }

    /// <summary>When the Example Data Set was created, in UTC.</summary>
    public DateTime Created { set; get; }

    /// <summary>The .NET culture code the values are in, for example "en-GB".</summary>
    public string Culture { get; set; } = null!;

    /// <summary>How many values the Example Data Set holds.</summary>
    public int ValueCount { get; set; }

    /// <summary>
    /// Creates a header from an ExampleDataSet entity.
    /// </summary>
    public static ExampleDataSetHeader FromEntity(ExampleDataSet entity)
    {
        return new ExampleDataSetHeader
        {
            Id = entity.Id,
            Name = entity.Name,
            BuiltIn = entity.BuiltIn,
            Created = entity.Created,
            Culture = entity.Culture,
            ValueCount = entity.Values?.Count ?? 0
        };
    }
}