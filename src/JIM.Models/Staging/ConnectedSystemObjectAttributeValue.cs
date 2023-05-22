namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectAttributeValue
    {
        public long Id { get; set; }

        /// <summary>
        /// The parent attribute for this attribute value object.
        /// </summary>
        public ConnectedSystemAttribute Attribute { get; set; }

        /// <summary>
        /// The parent connected system for this attribute value object.
        /// </summary>
        public ConnectedSystem ConnectedSystem { get; set; }

        public string? StringValue { get; set; }

        public DateTime? DateTimeValue { get; set; }

        public int? IntValue { get; set; }

        public byte[]? ByteValue { get; set; }

        public Guid? GuidValue { get; set; }

        public bool? BoolValue { get; set; }
    }
}
