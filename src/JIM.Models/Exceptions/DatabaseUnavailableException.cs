// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// The database server did not accept a connection within a service's start-up wait. Thrown after the wait has
/// logged its own Fatal line, so the service stops and its supervisor (the container runtime, or systemd) starts it
/// again, which begins a fresh wait.
/// </summary>
public class DatabaseUnavailableException : OperationalException
{
    public DatabaseUnavailableException(string message) : base(message) { }
}
