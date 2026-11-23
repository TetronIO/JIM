// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Localisation;

/// <summary>
/// An exception whose text a person reads and so can be rendered in their language (#3030). <see cref="Exception.Message"/>
/// stays the message's British English, which the REST API and logs read, so an exception keeps the type callers catch
/// (<see cref="ArgumentException"/>, <see cref="InvalidOperationException"/>) while the portal shows <see cref="Detail"/>.
/// </summary>
public interface IJimMessageException
{
    /// <summary>
    /// The message, for rendering in the reader's language; null for an exception that has no message of its own
    /// to translate (one thrown from a place not yet converted), whose <see cref="Exception.Message"/> is shown instead.
    /// </summary>
    JimMessage? Detail { get; }
}

/// <summary>
/// An <see cref="InvalidOperationException"/> a person is meant to read: a refusal such as "cannot delete a built-in X".
/// Callers that catch <see cref="InvalidOperationException"/> still do, and <see cref="Exception.Message"/> is unchanged.
/// </summary>
public class JimInvalidOperationException : InvalidOperationException, IJimMessageException
{
    public JimInvalidOperationException(JimMessage detail)
        : base(detail.ToEnglish())
    {
        Detail = detail;
    }

    public JimMessage Detail { get; }
}

/// <summary>
/// An <see cref="ArgumentException"/> a person is meant to read: a request refused for what it holds. Like the base
/// class, a parameter name appends "(Parameter 'x')" to <see cref="Exception.Message"/>, as it did before; the portal shows
/// <see cref="Detail"/> without it.
/// </summary>
public class JimArgumentException : ArgumentException, IJimMessageException
{
    public JimArgumentException(JimMessage detail)
        : base(detail.ToEnglish())
    {
        Detail = detail;
    }

    public JimArgumentException(JimMessage detail, string paramName)
        : base(detail.ToEnglish(), paramName)
    {
        Detail = detail;
    }

    public JimMessage Detail { get; }
}

/// <summary>A <see cref="KeyNotFoundException"/> a person is meant to read.</summary>
public class JimKeyNotFoundException : KeyNotFoundException, IJimMessageException
{
    public JimKeyNotFoundException(JimMessage detail)
        : base(detail.ToEnglish())
    {
        Detail = detail;
    }

    public JimMessage Detail { get; }
}

/// <summary>A <see cref="FileNotFoundException"/> a person is meant to read.</summary>
public class JimFileNotFoundException : FileNotFoundException, IJimMessageException
{
    public JimFileNotFoundException(JimMessage detail)
        : base(detail.ToEnglish())
    {
        Detail = detail;
    }

    public JimMessage Detail { get; }
}
