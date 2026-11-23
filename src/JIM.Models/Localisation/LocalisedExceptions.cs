// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Localisation;

/// <summary>
/// Creates the framework's own exceptions (<see cref="ArgumentException"/>, <see cref="NotSupportedException"/>,
/// <see cref="InvalidOperationException"/>) for a refusal a person reads, carrying the reason as a <see cref="JimMessage"/>
/// in <see cref="Exception.Data"/> so the portal can show it in the viewer's language (#3030). The exception is exactly the
/// type it always was, with the same British English <see cref="Exception.Message"/> (parameter name included), so the
/// REST API and every existing <c>catch</c> and exact-type assertion are unchanged.
/// </summary>
public static class LocalisedExceptions
{
    /// <summary>The <see cref="Exception.Data"/> key the message is held under.</summary>
    public const string DataKey = "JIM.Message";

    /// <summary>An <see cref="ArgumentException"/> whose reason is <paramref name="detail"/>.</summary>
    public static ArgumentException Argument(JimMessage detail, string? paramName = null) =>
        Attach(new ArgumentException(detail.ToEnglish(), paramName), detail);

    /// <summary>A <see cref="NotSupportedException"/> whose reason is <paramref name="detail"/>.</summary>
    public static NotSupportedException NotSupported(JimMessage detail) =>
        Attach(new NotSupportedException(detail.ToEnglish()), detail);

    /// <summary>An <see cref="InvalidOperationException"/> whose reason is <paramref name="detail"/>.</summary>
    public static InvalidOperationException InvalidOperation(JimMessage detail) =>
        Attach(new InvalidOperationException(detail.ToEnglish()), detail);

    /// <summary>Holds <paramref name="detail"/> on an exception, so <see cref="DetailOf"/> finds it.</summary>
    public static TException Attach<TException>(TException exception, JimMessage detail) where TException : Exception
    {
        exception.Data[DataKey] = detail;
        return exception;
    }

    /// <summary>
    /// The message an exception carries for the reader: its <see cref="IJimMessageCarrier.Detail"/>, or the one held by
    /// <see cref="Attach"/>; null for an exception with only English text.
    /// </summary>
    public static JimMessage? DetailOf(Exception exception) =>
        exception is IJimMessageCarrier { Detail: { } detail } ? detail : exception.Data[DataKey] as JimMessage;
}
