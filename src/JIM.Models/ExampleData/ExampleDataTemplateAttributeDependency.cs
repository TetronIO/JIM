// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
namespace JIM.Models.ExampleData;

public class ExampleDataTemplateAttributeDependency
{
    public int Id { get; set; }
    public MetaverseAttribute MetaverseAttribute { get; set; } = null!;
    public string StringValue { get; set; } = null!;
    public ComparisonType ComparisonType { get; set; }
}