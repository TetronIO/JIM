// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// One built-in expression function, described as data (#2053, PRD Phase 2 requirement 6). The function catalogue is
/// the one description of every function: the documentation's function reference is tested against it, the expression
/// editor suggests and explains from it, and the non-repeatable function check reads it.
/// </summary>
/// <param name="Name">The name an expression calls it by, such as <c>StartsWith</c>.</param>
/// <param name="Category">The reference section and editor group it belongs to.</param>
/// <param name="Parameters">Its parameters in order; optional ones last.</param>
/// <param name="Returns">What it returns, in a few words, such as "true or false".</param>
/// <param name="ResultType">The type of each value it returns, which saving checks against the target (#2055 Phase 3);
/// Unknown only where it returns one of its arguments.</param>
/// <param name="Description">What it does, as the reference states it. May contain Markdown links.</param>
/// <param name="Example">One call as an administrator would write it.</param>
/// <param name="IsRepeatable">False where it returns a different value each time it is evaluated (<c>Now</c>,
/// <c>RandomPassword</c>), so a derived Attribute Flow calling it is warned about.</param>
public sealed record ExpressionFunction(
    string Name,
    ExpressionFunctionCategory Category,
    IReadOnlyList<ExpressionFunctionParameter> Parameters,
    string Returns,
    ExpressionResultType ResultType,
    string Description,
    string Example,
    bool IsRepeatable = true)
{
    /// <summary>
    /// The call as the reference writes it: optional parameters in square brackets, such as
    /// <c>StartsWith(value, prefix[, ignoreCase])</c>.
    /// </summary>
    public string Signature
    {
        get
        {
            var required = Parameters.Where(p => !p.IsOptional).Select(p => p.Name);
            var optional = Parameters.Where(p => p.IsOptional).Select(p => $"[, {p.Name}]");
            return $"{Name}({string.Join(", ", required)}{string.Concat(optional)})";
        }
    }
}
