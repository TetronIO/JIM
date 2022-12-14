namespace JIM.Models.Staging
{
    public class ConnectedSystemImportResult
    {
        /// <summary>
        /// Are there more results to import from the connected system?
        /// JIM allows Connectors to retrieve objects from connected systems in batches/pages to improve performance.
        /// i.e. you could retrieve 100 objects a time, setting MoreToCome to true whilst more results remain, and then when there are no more, set it to false so JIM knows to stop asking the Connector for more results.
        /// </summary>
        public bool MoreToCome { get; set; }

        public List<ConnectedSystemImportObject> ImportObjects { get; set; }

        public ConnectedSystemImportResult()
        {
            ImportObjects = new List<ConnectedSystemImportObject>();
        }
    }
}
