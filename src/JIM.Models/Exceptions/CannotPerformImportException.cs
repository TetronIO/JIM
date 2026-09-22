// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// An import refused to continue because the Connected System could not give it what it needs, and going on
/// would have imported an incomplete picture: for example a directory that stopped a search at its own limit,
/// leaving the rest of the container unread. Operational, so the Activity carries the message and no stack
/// trace, exactly like <see cref="CannotPerformDeltaImportException"/>.
/// </summary>
public class CannotPerformImportException : OperationalException
{
    public CannotPerformImportException(string message) : base(message) { }
}
