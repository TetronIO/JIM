// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Models.Exceptions;

public class InvalidSettingValuesException : OperationalException, IJimMessageCarrier
{
    public InvalidSettingValuesException(string message) : base(message) { }

    /// <summary>
    /// Carries the underlying failure alongside the administrator-facing message, for settings whose
    /// value is parsed rather than merely read: a parser's own account of where a document broke is
    /// what locates the typo, and a message alone cannot express it.
    /// </summary>
    public InvalidSettingValuesException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>
    /// A refusal the portal shows in the viewer's language (#3030). <see cref="Exception.Message"/> is its British
    /// English, which the REST API and logs keep reading.
    /// </summary>
    public InvalidSettingValuesException(JimMessage detail) : this(detail, null)
    {
    }

    /// <summary>
    /// A refusal the portal shows in the viewer's language, carrying the underlying failure as well (#3030).
    /// </summary>
    public InvalidSettingValuesException(JimMessage detail, Exception? innerException) : base(detail.ToEnglish(), innerException)
    {
        Detail = detail;
    }

    /// <inheritdoc />
    public JimMessage? Detail { get; }
}
