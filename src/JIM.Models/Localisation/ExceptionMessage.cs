// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Localisation;

/// <summary>
/// Carries a <see cref="JimMessage"/> on an exception of any type (#3030), for a refusal that callers catch by its exact
/// type (an <see cref="ArgumentException"/> the REST API answers 400 for, or the inner exception a test or a log reads)
/// and so cannot become a <see cref="JimMessageException"/>. The exception's own <see cref="Exception.Message"/> stays
/// the message's British English, so nothing that reads it changes; the portal asks <see cref="DetailOf"/> for the message
/// to show in the viewer's language.
/// </summary>
public static class ExceptionMessage
{
    private const string DataKey = "JIM.Message";

    /// <summary>Attaches <paramref name="detail"/> to <paramref name="exception"/>, which is returned for throwing.</summary>
    public static TException Attach<TException>(TException exception, JimMessage detail) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(detail);

        exception.Data[DataKey] = detail;
        return exception;
    }

    /// <summary>The message attached to <paramref name="exception"/>, or null where it carries none.</summary>
    public static JimMessage? DetailOf(Exception exception) =>
        exception is JimMessageException localised ? localised.Detail
        : exception.Data.Contains(DataKey) ? exception.Data[DataKey] as JimMessage
        : null;
}
