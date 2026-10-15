// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The outcome of an administrator overriding the data type of a Connected System attribute.
/// </summary>
/// <remarks>
/// A result rather than an exception because every refusal is an administrator asking for something the Connector or
/// the attribute's current use rules out, and the portal, the REST API and PowerShell all need to say why.
/// </remarks>
public class AttributeTypeOverrideResult
{
    public bool Success { get; private init; }

    /// <summary>
    /// Why the override was refused. Null when it was applied.
    /// </summary>
    public string? ErrorMessage { get; private init; }

    /// <summary>
    /// True when applying the override made a multi-valued attribute single-valued, because the chosen type holds one
    /// value (a Boolean).
    /// </summary>
    public bool MadeSingleValued { get; private init; }

    public static AttributeTypeOverrideResult Applied(bool madeSingleValued)
    {
        return new AttributeTypeOverrideResult { Success = true, MadeSingleValued = madeSingleValued };
    }

    public static AttributeTypeOverrideResult Refused(string errorMessage)
    {
        return new AttributeTypeOverrideResult { Success = false, ErrorMessage = errorMessage };
    }
}
