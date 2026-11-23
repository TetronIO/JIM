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
/// Makes the framework exceptions a person is meant to read carry their message (#3030). The exception keeps its own
/// type, so a caller that catches (or a test that expects) <see cref="InvalidOperationException"/> or
/// <see cref="ArgumentException"/> is unaffected and <see cref="Exception.Message"/> is the British English as before;
/// the message rides in <see cref="Exception.Data"/> for the portal to render in the viewer's language
/// (<c>MessageDisplay.ToDisplayText</c>).
/// </summary>
public static class JimExceptions
{
    /// <summary>The key the message is held under in <see cref="Exception.Data"/>.</summary>
    public const string DataKey = "JimMessage";

    /// <summary>An <see cref="InvalidOperationException"/> a person is meant to read.</summary>
    public static InvalidOperationException InvalidOperation(JimMessage detail) =>
        new InvalidOperationException(detail.ToEnglish()).WithDetail(detail);

    /// <summary>
    /// An <see cref="ArgumentException"/> a person is meant to read. A parameter name appends "(Parameter 'x')" to
    /// <see cref="Exception.Message"/>, as it did before; the portal shows the message without it.
    /// </summary>
    public static ArgumentException Argument(JimMessage detail, string? paramName = null) =>
        (paramName == null ? new ArgumentException(detail.ToEnglish()) : new ArgumentException(detail.ToEnglish(), paramName)).WithDetail(detail);

    /// <summary>A <see cref="FileNotFoundException"/> a person is meant to read.</summary>
    public static FileNotFoundException FileNotFound(JimMessage detail) =>
        new FileNotFoundException(detail.ToEnglish()).WithDetail(detail);

    /// <summary>Attaches the message to an exception, and returns it.</summary>
    public static TException WithDetail<TException>(this TException exception, JimMessage detail) where TException : Exception
    {
        exception.Data[DataKey] = detail;
        return exception;
    }

    /// <summary>
    /// The message an exception carries, whether as an <see cref="IJimMessageException"/> or attached by
    /// <see cref="WithDetail{TException}"/>; null for an ordinary exception.
    /// </summary>
    public static JimMessage? DetailOf(this Exception exception) =>
        exception is IJimMessageException { Detail: { } own } ? own : exception.Data[DataKey] as JimMessage;
}
