namespace JIM.Models.Staging
{
    public class ConnectedSystemAttributeValue
    {
        public long Id { get; set; }

        public ConnectedSystemAttribute Attribute { get; set; }

        public string? StringValue { get; set; }

        public DateTime DateTimeValue { get; set; }

        public int IntValue { get; set; }

        public byte[] ByteValue { get; set; }

        public ConnectedSystemAttributeValue()
        {
            ByteValue = Array.Empty<byte>();
        }
    }
}
