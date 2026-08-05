// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exceptions;

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
}
