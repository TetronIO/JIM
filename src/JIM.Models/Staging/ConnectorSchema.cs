// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

public class ConnectorSchema
{
    public List<ConnectorSchemaObjectType> ObjectTypes { get; set; } = new();

    /// <summary>
    /// Discovery shortfalls the Connector worked around rather than failed on, i.e. a system that does not
    /// publish its schema, or an attribute definition that could not be fully interpreted. Warnings surface on
    /// the schema import's Activity and refresh result so an administrator can tell a system gap from a JIM one;
    /// an empty list means discovery was complete.
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}