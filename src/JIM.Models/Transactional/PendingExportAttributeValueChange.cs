using JIM.Models.Staging;

namespace JIM.Models.Transactional
{
    public class PendingExportAttributeValueChange
    {
        public int Id { get; set; }
        public ConnectedSystemAttribute Attribute { get; set; }
        public string? StringValue { get; set; }
        public DateTime DateTimeValue { get; set; }
        public int IntValue { get; set; }
        public byte[] ByteValue { get; set; }

        public PendingExportAttributeChangeType ChangeType { get; set; }

        public PendingExportAttributeValueChange()
        {
            ByteValue = Array.Empty<byte>();
        }
    }
}
