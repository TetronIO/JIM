// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using CsvHelper;
namespace JIM.Connectors.File;

/// <summary>
/// Enables us to encapsulate both reader-related objects that need disposing when no longer required.
/// </summary>
internal class FileConnectorReader : IDisposable
{
    internal StreamReader Reader { get; }

    internal CsvReader CsvReader { get; }

    /// <summary>
    /// The file's name without its folder, for telling an administrator where a row came from.
    /// </summary>
    internal string FileName { get; }

    internal FileConnectorReader(StreamReader reader, CsvReader csvReader, string fileName)
    {
        Reader = reader;
        CsvReader = csvReader;
        FileName = fileName;
    }

    public void Dispose()
    {
        Reader.Dispose();
        CsvReader.Dispose();
    }
}