using JIM.Models.Staging;

namespace JIM.Models.Interfaces
{
    public interface IConnectorImportUsingFiles
    {
        /// <summary>
        /// Imports ConnectedSystemImportObjects from a file.
        /// It's up to you to specify where the source file is. 
        /// Recommend you have ConnectedSystemSettings that define delta-import, full-import and export file paths that map to the Connector Files Docker volume.
        /// You can map a network share on the Docker host and expose this to JIM using the Connector Files volume.
        /// </summary>
        /// <param name="runProfile">Defines what type of import is being performed, i.e. delta import or full import.</param>
        public ConnectedSystemImportResult Import(IList<ConnectedSystemSetting> settings, ConnectedSystemRunProfile runProfile);
    }
}
