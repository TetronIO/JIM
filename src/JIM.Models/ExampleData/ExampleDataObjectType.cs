// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
namespace JIM.Models.ExampleData;

public class ExampleDataObjectType
{
    public int Id { get; set; }
    public MetaverseObjectType MetaverseObjectType { get; set; } = null!;
    public List<ExampleDataTemplateAttribute> TemplateAttributes { get; } = new();
    public int ObjectsToCreate { get; set; }
}