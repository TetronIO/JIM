// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>One row of a Text attribute's weighted values in the Template Attribute editor.</summary>
public class ExampleDataWeightedValueDraft
{
    public string Value { get; set; } = string.Empty;

    /// <summary>How often the value is picked relative to the others; the weights need not add up to anything.</summary>
    public float Weight { get; set; } = 1;
}
