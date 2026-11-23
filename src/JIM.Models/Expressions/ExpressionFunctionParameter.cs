// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Models.Expressions;

/// <summary>
/// One parameter of an <see cref="ExpressionFunction"/>.
/// </summary>
/// <param name="Name">Its name in the signature, such as <c>prefix</c>.</param>
/// <param name="Description">What to pass, in a sentence.</param>
/// <param name="IsOptional">Whether it may be left out.</param>
public sealed record ExpressionFunctionParameter(string Name, string Description, bool IsOptional = false)
{
    /// <summary>
    /// <see cref="Description"/> as a message, so the portal can show it in the viewer's language; the REST API reads
    /// <see cref="Description"/>, which is this message's British English.
    /// </summary>
    public JimMessage? DescriptionDetail { get; init; }
}
