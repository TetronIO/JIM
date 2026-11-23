// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Localisation;

/// <summary>
/// The message catalogues in JIM.Application (#3030): each is a resource file under <c>Resources/</c> whose text is
/// shown to a person through a <see cref="JIM.Models.Localisation.JimMessage"/>, declared once here so no call site
/// writes the string. Each area adds its own catalogues in a file of its own.
/// </summary>
public static partial class ApplicationMessageCatalogues
{
    /// <summary>
    /// What expressions tell an administrator: the function reference, the Attribute Flow tester's sentences, the
    /// problems an expression is refused for and the errors evaluating one raises (<c>ExpressionMessages.resx</c>).
    /// </summary>
    public const string Expressions = "JIM.Application:ExpressionMessages";
}
