// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.WSMan;

/// <summary>The WinRM signals PowerShell remoting sends to a command.</summary>
public enum WSManSignal
{
    /// <summary>The command is finished with; the server releases it.</summary>
    Terminate,

    /// <summary>Stop the running pipeline (PowerShell's Ctrl+C).</summary>
    PowerShellStop
}
