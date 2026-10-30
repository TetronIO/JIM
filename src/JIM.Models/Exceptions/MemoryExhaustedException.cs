// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// The Worker stopped a run because the memory it can draw on was nearly exhausted. Raised by the Worker's memory
/// guard rather than by the run, so that the Activity records why the run stopped and what to do about it, instead
/// of the run being killed by the operating system without a word.
/// </summary>
public class MemoryExhaustedException : OperationalException
{
    public MemoryExhaustedException(string message) : base(message) { }
}
