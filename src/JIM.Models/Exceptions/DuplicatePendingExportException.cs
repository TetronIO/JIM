// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

public class DuplicatePendingExportException : OperationalException
{
    public DuplicatePendingExportException(string message) : base(message) { }
}
