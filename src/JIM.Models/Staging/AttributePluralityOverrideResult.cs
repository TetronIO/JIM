// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The outcome of an administrator saying whether a Connected System attribute holds one value or several.
/// </summary>
/// <remarks>
/// A result rather than an exception for the same reason as <see cref="AttributeTypeOverrideResult"/>: every refusal is
/// an administrator asking for something the Connector or the attribute's current use rules out, and the portal, the
/// REST API and PowerShell all need to say why.
/// </remarks>
public class AttributePluralityOverrideResult
{
    public bool Success { get; private init; }

    /// <summary>
    /// Why the override was refused. Null when it was applied.
    /// </summary>
    public string? ErrorMessage { get; private init; }

    public static AttributePluralityOverrideResult Applied()
    {
        return new AttributePluralityOverrideResult { Success = true };
    }

    public static AttributePluralityOverrideResult Refused(string errorMessage)
    {
        return new AttributePluralityOverrideResult { Success = false, ErrorMessage = errorMessage };
    }
}
