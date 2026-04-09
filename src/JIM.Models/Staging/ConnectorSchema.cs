// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

public class ConnectorSchema
{
    public List<ConnectorSchemaObjectType> ObjectTypes { get; set; } = new();
}