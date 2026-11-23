// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Localisation;

/// <summary>
/// The message catalogues in JIM.Application (#3030): each is a resource file under <c>Resources/</c> whose text is shown
/// to a person through a <see cref="JIM.Models.Localisation.JimMessage"/>, declared once here so no call site writes the string.
/// </summary>
public static partial class ApplicationMessageCatalogues
{
    /// <summary>
    /// The refusals, results and explanations the Connected System server gives an administrator
    /// (<c>ConnectedSystemMessages.resx</c>).
    /// </summary>
    public const string ConnectedSystems = "JIM.Application:ConnectedSystemMessages";
}
