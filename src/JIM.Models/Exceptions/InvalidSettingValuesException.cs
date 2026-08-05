// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

public class InvalidSettingValuesException : OperationalException
{
    public InvalidSettingValuesException(string message) : base(message) { }

    /// <summary>
    /// Carries the underlying failure alongside the administrator-facing message, for settings whose
    /// value is parsed rather than merely read: a parser's own account of where a document broke is
    /// what locates the typo, and a message alone cannot express it.
    /// </summary>
    public InvalidSettingValuesException(string message, Exception? innerException) : base(message, innerException) { }
}