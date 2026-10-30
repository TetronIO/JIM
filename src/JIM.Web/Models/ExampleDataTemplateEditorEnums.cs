// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Where a Text attribute's generated values come from, in the Template Attribute editor. Generation uses exactly one
/// of these per attribute (an expression first, then data sets with no pattern, a pattern, weighted values, numbers),
/// so the editor offers them as a single choice rather than as independent fields that could contradict each other.
/// </summary>
public enum ExampleDataTextSource
{
    /// <summary>A random value from one or more Example Data Sets.</summary>
    DataSets = 0,
    /// <summary>A pattern of literal text, attribute placeholders, system variables and data set placeholders.</summary>
    Pattern = 1,
    /// <summary>One of a fixed list of values, each picked in proportion to its weight.</summary>
    WeightedValues = 2,
    /// <summary>An expression evaluated over the object's other generated values.</summary>
    Expression = 3,
    /// <summary>A number, written as text.</summary>
    Numbers = 4
}

/// <summary>How generated numbers follow one another.</summary>
public enum ExampleDataNumberOrder
{
    /// <summary>Counting up from the minimum, one per object.</summary>
    Sequential = 0,
    /// <summary>A random number within the range.</summary>
    Random = 1
}
