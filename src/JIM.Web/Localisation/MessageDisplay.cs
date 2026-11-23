// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Web.Localisation;

/// <summary>
/// How the portal shows text the application layer produced (#3030): a <see cref="JimMessage"/> in the viewer's
/// language, and an exception's message in the viewer's language where it carries one.
/// </summary>
public static class MessageDisplay
{
    /// <summary>The message in the viewer's language; empty for none.</summary>
    public static string Show(this JimMessage? message) => message?.ToCurrentLanguage() ?? string.Empty;

    /// <summary>
    /// What to show a person for an exception: a <see cref="JimMessageException"/>'s message in their language, else the
    /// exception's own message (an unexpected fault, whose text is for support, not translation).
    /// </summary>
    public static string ToDisplayText(this Exception exception) => exception is JimMessageException localised
        ? localised.Detail.ToCurrentLanguage()
        : exception.Message;
}
