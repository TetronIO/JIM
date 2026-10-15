// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Expressions;

namespace JIM.Web.Models.Api;

/// <summary>
/// One built-in function an expression can call, as the expression editor's Insert function list describes it.
/// </summary>
public class ExpressionFunctionDto
{
    /// <summary>
    /// The name an expression calls it by, such as StartsWith. Function names are exact: an expression must use this case.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The group it belongs to.
    /// </summary>
    public ExpressionFunctionCategory Category { get; set; }

    /// <summary>
    /// The group's name as the expression editor and the Expressions reference show it, such as "String Functions".
    /// </summary>
    public string CategoryName { get; set; } = null!;

    /// <summary>
    /// How it is called, with optional parameters in square brackets, such as StartsWith(value, prefix[, ignoreCase]).
    /// </summary>
    public string Signature { get; set; } = null!;

    /// <summary>
    /// Its parameters in order; optional ones last.
    /// </summary>
    public List<ExpressionFunctionParameterDto> Parameters { get; set; } = [];

    /// <summary>
    /// What it returns, in a few words, such as "true or false".
    /// </summary>
    public string Returns { get; set; } = null!;

    /// <summary>
    /// What it does. May contain Markdown.
    /// </summary>
    public string Description { get; set; } = null!;

    /// <summary>
    /// One call as an administrator would write it.
    /// </summary>
    public string Example { get; set; } = null!;

    /// <summary>
    /// False where it returns a different value each time it is evaluated, such as Now and RandomPassword. An
    /// Attribute Flow whose expression calls one is warned about, since its value changes on every synchronisation.
    /// </summary>
    public bool IsRepeatable { get; set; }

    /// <summary>
    /// Creates the API representation of a catalogue function.
    /// </summary>
    public static ExpressionFunctionDto FromFunction(ExpressionFunction function) => new()
    {
        Name = function.Name,
        Category = function.Category,
        CategoryName = ExpressionFunctionCatalogue.CategoryName(function.Category),
        Signature = function.Signature,
        Parameters = function.Parameters.Select(p => new ExpressionFunctionParameterDto
        {
            Name = p.Name,
            Description = p.Description,
            IsOptional = p.IsOptional
        }).ToList(),
        Returns = function.Returns,
        Description = function.Description,
        Example = function.Example,
        IsRepeatable = function.IsRepeatable
    };
}

/// <summary>
/// One parameter of an expression function.
/// </summary>
public class ExpressionFunctionParameterDto
{
    /// <summary>
    /// Its name in the signature, such as prefix.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// What to pass, in a sentence.
    /// </summary>
    public string Description { get; set; } = null!;

    /// <summary>
    /// Whether it may be left out.
    /// </summary>
    public bool IsOptional { get; set; }
}
