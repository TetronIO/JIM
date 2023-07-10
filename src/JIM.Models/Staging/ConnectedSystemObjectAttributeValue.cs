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
        /// The normal parent connected system object for this attribute value object.
        /// Might be null if being referenced by a Connected System Object Change object and the CSO has been deleted.
        /// </summary>
        public ConnectedSystemObject? ConnectedSystemObject { get; set; }

        public string? StringValue { get; set; }

        public DateTime? DateTimeValue { get; set; }

        public int? IntValue { get; set; }

        public byte[]? ByteValue { get; set; }

        public Guid? GuidValue { get; set; }

        public bool? BoolValue { get; set; }

        public ConnectedSystemObject? ReferenceValue { get; set; }
    }
}
