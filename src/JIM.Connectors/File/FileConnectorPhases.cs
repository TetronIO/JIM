// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.File;

/// <summary>
/// The phases the File Connector performs inside the JIM phase that calls it, and the labels an
/// administrator sees for them (#454). Declared through <see cref="JIM.Models.Interfaces.IConnectorPhases"/>
/// so the steps show up before they run, and entered by key as the work progresses.
/// </summary>
internal static class FileConnectorPhases
{
    /// <summary>Reading and parsing the source file. One phase because it is one pass over the file.</summary>
    internal const string Read = "read";

    internal const string ReadName = "Reading the file";

    /// <summary>Loading the existing export file, whose contents the pending changes are merged into.</summary>
    internal const string LoadExistingFile = "load-existing-file";

    internal const string LoadExistingFileName = "Loading existing export file";

    /// <summary>Merging the pending changes into the loaded contents, in memory.</summary>
    internal const string Merge = "merge";

    internal const string MergeName = "Merging changes into file";

    /// <summary>Writing the merged contents back out as the export file.</summary>
    internal const string Write = "write";

    internal const string WriteName = "Writing the output file";
}
