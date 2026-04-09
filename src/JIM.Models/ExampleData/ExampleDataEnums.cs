// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.ExampleData;

/// <summary>
/// Defines the type of comparison to make.
/// Used with ExampleDataTemplateDependency objects.
/// </summary>
public enum ComparisonType
{
    Equals = 0,
    NotEquals = 1,
    LessThan = 2,
    GreaterThan = 3,
    GreaterThanOrEqual = 4,
    LessThanOrEqual = 5,
    Like = 6
}