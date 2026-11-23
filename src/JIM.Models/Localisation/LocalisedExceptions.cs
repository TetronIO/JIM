// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Localisation;

/// <summary>
/// An <see cref="ArgumentException"/> whose reason a person reads (a refusal the application layer explains), carrying
/// the reason as a <see cref="JimMessage"/> so the portal can show it in the viewer's language (#3030). Its
/// <see cref="Exception.Message"/> is the same British English the exception always had, parameter name included, so the
/// REST API and every existing <c>catch (ArgumentException)</c> are unchanged.
/// </summary>
public class JimArgumentException : ArgumentException, IJimMessageCarrier
{
    public JimArgumentException(JimMessage detail, string? paramName = null)
        : base(detail.ToEnglish(), paramName)
    {
        Detail = detail;
    }

    /// <inheritdoc />
    public JimMessage? Detail { get; }
}

/// <summary>
/// A <see cref="NotSupportedException"/> whose reason a person reads, carrying it as a <see cref="JimMessage"/> (#3030).
/// <see cref="Exception.Message"/> stays the British English.
/// </summary>
public class JimNotSupportedException : NotSupportedException, IJimMessageCarrier
{
    public JimNotSupportedException(JimMessage detail)
        : base(detail.ToEnglish())
    {
        Detail = detail;
    }

    /// <inheritdoc />
    public JimMessage? Detail { get; }
}

/// <summary>
/// An <see cref="InvalidOperationException"/> whose reason a person reads, carrying it as a <see cref="JimMessage"/>
/// (#3030). <see cref="Exception.Message"/> stays the British English.
/// </summary>
public class JimInvalidOperationException : InvalidOperationException, IJimMessageCarrier
{
    public JimInvalidOperationException(JimMessage detail)
        : base(detail.ToEnglish())
    {
        Detail = detail;
    }

    /// <inheritdoc />
    public JimMessage? Detail { get; }
}
