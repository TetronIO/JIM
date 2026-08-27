// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The outcome of changing which auxiliary classes an Object Type carries, or which structural class carries it.
/// </summary>
/// <remarks>
/// A result rather than an exception because every refusal here is an administrator naming something that does not
/// fit, and the surfaces that offer the choice (the portal, the REST API and PowerShell) all need to say why in the
/// Connected System's own vocabulary rather than report a fault.
/// </remarks>
public class AuxiliaryClassSelectionResult
{
    public bool Success { get; private init; }

    /// <summary>
    /// Why the change was refused. Null when it was applied.
    /// </summary>
    public string? ErrorMessage { get; private init; }

    public static AuxiliaryClassSelectionResult Applied()
    {
        return new AuxiliaryClassSelectionResult { Success = true };
    }

    public static AuxiliaryClassSelectionResult Refused(string errorMessage)
    {
        return new AuxiliaryClassSelectionResult { Success = false, ErrorMessage = errorMessage };
    }
}
