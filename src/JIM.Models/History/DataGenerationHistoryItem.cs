namespace JIM.Models.History
{
    public class DataGenerationHistoryItem : HistoryItem
    {
        public int DataGenerationTemplateId { get; set; }

        public DataGenerationHistoryItem()
        {
            // Entity Framework uses this constructor when retrieving objects from the database
        }

        public DataGenerationHistoryItem(int dataGenerationTemplateId)
        {
            DataGenerationTemplateId = dataGenerationTemplateId;
        }
    }
}
