// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors;

/// <summary>
/// The message catalogues in JIM.Connectors (#3030): each is a resource file under <c>Resources/</c> whose text is shown
/// to a person through a <see cref="JIM.Models.Localisation.JimMessage"/>, declared once here so no call site writes the string.
/// </summary>
public static class ConnectorMessageCatalogues
{
    /// <summary>
    /// What the Connectors tell an administrator when their settings are saved or checked: connection tests,
    /// refusals and warnings (<c>ConnectorMessages.resx</c>).
    /// </summary>
    public const string Connectors = "JIM.Connectors:ConnectorMessages";
}
