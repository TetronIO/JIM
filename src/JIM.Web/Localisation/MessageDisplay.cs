// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
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
    /// A stored message in the viewer's language, or the English stored beside it: where the record has no message (one
    /// written before messages existed, or by a writer not yet converted), and where its message cannot be rendered (its
    /// Connector's catalogue is gone, a key was renamed since, or a translation is malformed; #3103), so a reader never
    /// sees a key: <c>MessageDisplay.Show(activity.ErrorMessageDetail, activity.ErrorMessage)</c>. Null when there is
    /// neither.
    /// </summary>
    public static string? Show(JimMessage? message, string? english)
    {
        if (message == null)
            return english;
        if (MessageRenderer.TryRender(message, CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture, out var text))
            return text;
        return english ?? message.ToCurrentLanguage();
    }

    /// <summary>
    /// What to show a person for an exception: a <see cref="JimMessageException"/>'s message in their language, else the
    /// exception's own message (an unexpected fault, whose text is for support, not translation).
    /// </summary>
    public static string ToDisplayText(this Exception exception) => LocalisedExceptions.DetailOf(exception) is { } detail
        ? detail.ToCurrentLanguage()
        : exception.Message;
}
