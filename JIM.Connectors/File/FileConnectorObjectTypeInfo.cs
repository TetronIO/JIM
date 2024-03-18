namespace JIM.Connectors.File
{
    /// <summary>
    /// Encapsulates all the information needed to determine the Object Type of a row in a file.
    /// Reduces the need to work with setting values to work out what a row object type is.
    /// </summary>
    internal class FileConnectorObjectTypeInfo
    {
        internal FileConnectorObjectTypeSpecifier Specifier { get; set; }

        internal string? PredefinedObjectType { get; set; }

        internal string? ObjectTypeColumnName { get; set; }
    }

    internal enum FileConnectorObjectTypeSpecifier
    {
        PredefinedObjectType,
        ColumnBasedObjectType
    }
}
