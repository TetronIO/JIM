// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;

namespace JIM.Web.Localisation;

/// <summary>
/// How the portal names a piece of configuration that reads an attribute (#3030): in the viewer's language, falling back
/// to the English description where the reader carries no message.
/// </summary>
public static class AttributeReaderDisplay
{
    /// <summary>The reader's description in the viewer's language.</summary>
    public static string ShowDescription(this AttributeReader reader) =>
        MessageDisplay.Show(reader.DescriptionDetail, reader.Description) ?? string.Empty;
}
