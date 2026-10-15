// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Models.ExampleData;

/// <summary>
/// What the Example Data expression tester (#2055) found: either the sample inputs could not be held as the generated
/// object's values (each problem named), or the expression's outcome, which is what generating objects would give the
/// attribute from the same values.
/// </summary>
public sealed class ExampleDataExpressionTestResult
{
    /// <summary>
    /// The inputs the expression reads that the template generates on the same object, each typed by its Metaverse
    /// attribute.
    /// </summary>
    public IReadOnlyList<AttributeSampleDescriptor> Inputs { get; init; } = [];

    /// <summary>
    /// The attributes the expression reads that the template does not generate on the object, so they never have a
    /// value when objects are generated.
    /// </summary>
    public IReadOnlyList<string> InputsNotGenerated { get; init; } = [];

    /// <summary>
    /// Why the sample inputs could not be used, one message per problem. When any are present the expression was not
    /// evaluated.
    /// </summary>
    public IReadOnlyList<string> InputErrors { get; init; } = [];

    /// <summary>
    /// The expression's outcome. Null when <see cref="InputErrors"/> stopped the test.
    /// </summary>
    public ExampleDataExpressionEvaluation? Evaluation { get; init; }
}
