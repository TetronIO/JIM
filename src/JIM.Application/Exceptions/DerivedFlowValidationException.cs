// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown when saving an import Attribute Flow would leave the Metaverse-Derived Attribute Flow graph invalid (#1750):
/// a dependency cycle, an <c>mv["..."]</c> name that is not an attribute of the Metaverse Object Type, or a Reference
/// input or target. An <see cref="ArgumentException"/>, so REST maps it to 400 and the portal and PowerShell surface
/// the message exactly as they do every other save-time validation failure (FR 12).
/// </summary>
public class DerivedFlowValidationException : ArgumentException
{
    /// <summary>
    /// Every reason the save was refused; <see cref="Exception.Message"/> joins them.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    public DerivedFlowValidationException(IReadOnlyList<string> errors)
        : base(string.Join(" ", errors))
    {
        Errors = errors;
    }
}
