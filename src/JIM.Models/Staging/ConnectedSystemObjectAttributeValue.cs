using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectAttributeValue
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        /// <summary>
        /// The parent attribute for this attribute value object.
        /// </summary>
        public ConnectedSystemObjectTypeAttribute Attribute { get; set; }

        /// <summary>
        /// The parent connected system object for this attribute value object.
        /// </summary>
        public ConnectedSystemObject ConnectedSystemObject { get; set; }

        public string? StringValue { get; set; }

        public DateTime? DateTimeValue { get; set; }

        public int? IntValue { get; set; }

        public byte[]? ByteValue { get; set; }

        public Guid? GuidValue { get; set; }

        public bool? BoolValue { get; set; }

        public ConnectedSystemObject? ReferenceValue { get; set; }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(StringValue))
                return StringValue;

            if (DateTimeValue.HasValue)
                return DateTimeValue.Value.ToString();

            if (IntValue.HasValue)
                return IntValue.Value.ToString();

            if (ByteValue != null)
                return ByteValue.Length.ToString();

            if (GuidValue.HasValue)
                return GuidValue.Value.ToString();

            if (BoolValue.HasValue)
                return BoolValue.Value.ToString();

            if (ReferenceValue != null)
                return ReferenceValue.Id.ToString();

            return string.Empty;
        }
    }
}
