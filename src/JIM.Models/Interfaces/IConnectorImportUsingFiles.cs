// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

public interface IConnectorImportUsingFiles
{
    /// <summary>
    /// Imports ConnectedSystemImportObjects from a file.
    /// It's up to you to specify where the source file is. 
    /// Recommend you have ConnectedSystemSettings that define delta-import, full-import and export file paths that map to the Connector Files Docker volume.
    /// You can map a network share on the Docker host and expose this to JIM using the Connector Files volume.
    /// </summary>
    /// <param name="connectedSystem">The Connected System to import objects from.</param>
    /// <param name="runProfile">Defines what type of import is being performed, i.e. delta import or full import.</param>
    /// <param name="logger">The object that enables log entries to be created.</param>
    /// <param name="cancellationToken">Enables the import to be stopped early, if required.</param>
    /// <param name="progressCallback">Optional callback for narrating your internal sub-phases (i.e. "Reading CSV file...", "Parsed 50,000 rows..."). JIM surfaces each message on the Activity, replacing the previous one, so operators can tell a healthy long-running import from a stuck one. The whole file is read in this one call, so this is the only progress an operator sees until it returns. The vocabulary is yours; emit on phase transitions rather than per row, and skip building messages entirely when this is null.</param>
    public Task<ConnectedSystemImportResult> ImportAsync(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile, ILogger logger, CancellationToken cancellationToken, Func<string, Task>? progressCallback = null);
}