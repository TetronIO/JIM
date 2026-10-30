// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.WSMan;

/// <summary>A WinRM server answered with a SOAP fault.</summary>
public sealed class WSManFaultException : Exception
{
    /// <summary>The WSManFault code a Receive returns when nothing arrived within its OperationTimeout.</summary>
    public const uint OperationTimeoutCode = 2150858793;

    public WSManFaultException(uint code, string subcode, string message)
        : base($"WinRM fault {code} ({subcode}): {message}")
    {
        Code = code;
        Subcode = subcode;
    }

    /// <summary>The WSManFault code (a Windows error number), or 0 if the fault carried none.</summary>
    public uint Code { get; }

    /// <summary>The SOAP subcode, for example <c>w:TimedOut</c> or <c>w:AccessDenied</c>.</summary>
    public string Subcode { get; }

    /// <summary>True when a Receive simply found no output in time; the caller asks again.</summary>
    public bool IsOperationTimeout => Code == OperationTimeoutCode;
}
