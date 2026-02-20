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
    public Task<ConnectedSystemImportResult> ImportAsync(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile, ILogger logger, CancellationToken cancellationToken);
}