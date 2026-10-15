// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.ExampleData;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request DTO for testing an Example Data Template attribute's expression.
/// </summary>
public class TestExampleDataExpressionRequest
{
    /// <summary>
    /// The ID of the template attribute whose expression to test. Its object type's other template attributes are what
    /// the expression can read.
    /// </summary>
    public int TemplateAttributeId { get; set; }

    /// <summary>
    /// The expression to test in place of the attribute's saved one, so an edit can be tried before it is saved. When
    /// omitted, the attribute's own expression is tested.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// Sample values for the attributes the expression reads, keyed by Metaverse attribute name. Each is read as the
    /// type of the template attribute that generates it; null is no value and an array gives several values.
    /// </summary>
    public Dictionary<string, object?>? Attributes { get; set; }
}

/// <summary>
/// Response DTO for an Example Data Template attribute's expression test: what generating objects would give the
/// attribute from the same values.
/// </summary>
public class TestExampleDataExpressionResponse
{
    /// <summary>
    /// True when the sample values could be used and the expression gives the attribute a value or no value; false for
    /// an input error or an expression that would fail generation.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// The Metaverse attribute the template attribute generates.
    /// </summary>
    public string Attribute { get; set; } = null!;

    /// <summary>
    /// The expression tested.
    /// </summary>
    public string Expression { get; set; } = null!;

    /// <summary>
    /// What the attribute would be given: a value, no value, or an error. Not set when the sample values could not be
    /// used.
    /// </summary>
    public ExampleDataExpressionOutcome? Outcome { get; set; }

    /// <summary>
    /// Why the outcome is an error. Set only when <c>Outcome</c> is Error.
    /// </summary>
    public ExampleDataExpressionErrorReason? ErrorReason { get; set; }

    /// <summary>
    /// The value the attribute would be generated as, in JIM's text form. Set only when <c>Outcome</c> is Value.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// What generating objects would do, in one sentence. Not set when the sample values could not be used.
    /// </summary>
    public string? OutcomeDescription { get; set; }

    /// <summary>
    /// Why the test failed: the input errors, or the outcome description for an expression that would fail generation.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Why the sample values could not be used, one message each. When present the expression was not evaluated.
    /// </summary>
    public IReadOnlyList<string> InputErrors { get; set; } = [];

    /// <summary>
    /// The attributes the expression reads that the template generates on the same object, with their types and the
    /// values they were given.
    /// </summary>
    public List<AttributeSampleDto> Inputs { get; set; } = [];

    /// <summary>
    /// The attributes the expression reads that the template does not generate on the object, so they never have a
    /// value when objects are generated.
    /// </summary>
    public IReadOnlyList<string> InputsNotGenerated { get; set; } = [];
}
