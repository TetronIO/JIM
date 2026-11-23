// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exceptions;
using JIM.Models.Localisation;

namespace JIM.Connectors.Sql;

/// <summary>
/// Thrown when the Object Types document cannot be used: it is not valid JSON, it says something the
/// Connector cannot act on, or it names a table, column or object type the database does not have.
/// <para>
/// The message is the whole of an administrator's feedback loop for a hand-written document, so every
/// one names the object type at fault and the field within it. Nothing is ever half-configured on the
/// strength of a document that only parses in part: a Connected System built from half a document would
/// import half its objects and report success.
/// </para>
/// </summary>
internal class SqlSchemaConfigurationException : InvalidSettingValuesException
{
    internal SqlSchemaConfigurationException(string message) : base(message)
    {
    }

    internal SqlSchemaConfigurationException(string message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// A problem whose words are held as a message, so the portal can show them in the viewer's language (#3030);
    /// <see cref="Exception.Message"/> is its British English, which the REST API, PowerShell and logs keep reading.
    /// </summary>
    internal SqlSchemaConfigurationException(JimMessage detail, Exception? innerException = null) : base(detail.ToEnglish(), innerException)
    {
        Detail = detail;
    }

    /// <summary>The problem as a message the portal can render in the viewer's language; null where only English text exists.</summary>
    internal JimMessage? Detail { get; }

    /// <summary>
    /// The document field the problem is in, where one can be named; lets an editor show the problem beside it.
    /// </summary>
    internal string? Field { get; init; }
}
