namespace JIM.Models.Staging
{
    public class ConnectedSystemAttributeValue
    {
        public long Id { get; set; }

        /// <summary>
        /// The parent attribute for this attribute value object.
        /// </summary>
        public ConnectedSystemAttribute Attribute { get; set; }

        public string? StringValue { get; set; }

        public DateTime? DateTimeValue { get; set; }

        public int? IntValue { get; set; }

        public byte[]? ByteValue { get; set; }

        public Guid? GuidValue { get; set; }
    }
}
