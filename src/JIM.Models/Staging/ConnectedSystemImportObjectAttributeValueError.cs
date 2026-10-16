// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A value a Connector found on an object in the Connected System but could not read as the attribute's type, such as
/// a date that does not exist or a number with letters in it (#2066). Reported with
/// <see cref="ConnectedSystemImportObject.AddAttributeValueError"/>, one per unreadable value.
/// </summary>
/// <remarks>
/// It tells JIM that the attribute holds a value it could not read, which is not the same as holding none: JIM leaves the
/// attribute as it already held it rather than removing the stored value, and reports what came in on the Activity.
/// </remarks>
public class ConnectedSystemImportObjectAttributeValueError
{
    /// <summary>
    /// The name of the attribute the value was found on, as the Connected System's schema names it.
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// The value as the Connector received it, before any conversion; null where the Connector could not render it as text.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Why the value could not be read, in a sentence an administrator can act on.
    /// </summary>
    public string Reason { get; set; } = null!;

    /// <summary>
    /// Who found the value could not be imported: the Connector, reading it, or JIM, holding it (#2153).
    /// </summary>
    public ImportAttributeValueErrorSource Source { get; set; } = ImportAttributeValueErrorSource.Connector;
}
