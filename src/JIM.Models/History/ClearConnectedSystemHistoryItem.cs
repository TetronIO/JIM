using JIM.Models.Staging;

namespace JIM.Models.History
{
    public class ClearConnectedSystemHistoryItem : HistoryItem
    {
        public int ConnectedSystemId { get; set; }

        public ClearConnectedSystemHistoryItem()
        {
            // Entity Framework uses this constructor when retrieving objects from the database
        }

        public ClearConnectedSystemHistoryItem(int connectedSystemId)
        {
            ConnectedSystemId = connectedSystemId;
        }
    }
}
