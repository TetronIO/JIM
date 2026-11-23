// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Localisation;

/// <summary>
/// A refusal or failure a person is meant to read: the application layer throws it with a <see cref="JimMessage"/>,
/// the portal shows that message in the viewer's language, and the REST API and logs read <see cref="Exception.Message"/>,
/// which is always its British English (#3030). Use it where an exception's text reaches a person; an unexpected fault
/// stays an ordinary exception.
/// </summary>
public class JimMessageException : Exception, IJimMessageException
{
    public JimMessageException(JimMessage detail)
        : base(detail.ToEnglish())
    {
        Detail = detail;
    }

    public JimMessageException(JimMessage detail, Exception innerException)
        : base(detail.ToEnglish(), innerException)
    {
        Detail = detail;
    }

    /// <summary>The message, for rendering in the reader's language.</summary>
    public JimMessage Detail { get; }
}
