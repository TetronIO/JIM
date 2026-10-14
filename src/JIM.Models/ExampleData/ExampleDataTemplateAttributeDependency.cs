// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;
namespace JIM.Models.ExampleData;

/// <summary>
/// A condition on another attribute's generated value that must hold before a template attribute is generated, for
/// example "Employee End Date only when Employee Type equals Contractor".
/// </summary>
public class ExampleDataTemplateAttributeDependency
{
    public int Id { get; set; }

    public MetaverseAttribute MetaverseAttribute { get; set; } = null!;

    /// <summary>
    /// The value compared against, as text, read as the depended-on attribute's data type when the template runs
    /// (numbers with a full stop, dates in ISO 8601, Booleans as true or false). Empty for an operator that takes no value.
    /// </summary>
    public string StringValue { get; set; } = null!;

    /// <summary>
    /// The comparison, from the operator catalogue shared with scoping and Predefined Searches (#2053). Text compares
    /// case-sensitively.
    /// </summary>
    public SearchComparisonType ComparisonType { get; set; }
}
