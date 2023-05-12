namespace JIM.Models.Staging
{
    public class ConnectedSystemImportResult
    {
        /// <summary>
        /// The objects imported from the connected system, i.e. users, groups, etc.
        /// </summary>
        public List<ConnectedSystemImportObject> ImportObjects { get; set; }

        /// <summary>
        /// Write any information to  this property that will be needed on the next import run to determine where to return results from.
        /// i.e. for an LDAP-based system this might store LDAP Cookies for numerous LDAP queries that have to be performed that would be passed in to the next query.
        /// This data is not persisted between synchronisation runs.
        /// Note: JIM will keep calling ImportAsync() until there are no more paginations tokens, as it understands this scenario to mean there is no more data to retrieve.
        /// </summary>
        public List<ConnectedSystemPaginationToken> PaginationTokens { get; set; }
        
        /// <summary>
        /// Write any information to this property that you want to be made available on subsequent synchronisation runs.
        /// i.e. for an LDAP system you might write the last known change number here so that you can perform delta imports in the future.
        /// JIM will pass this data to Connectors on each synchronisation run.
        /// </summary>
        public string? PersistedConnectorData { get; set; }

        public ConnectedSystemImportResult()
        {
            ImportObjects = new List<ConnectedSystemImportObject>();
            PaginationTokens = new List<ConnectedSystemPaginationToken>();
        }
    }
}
