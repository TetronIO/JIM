using System;
namespace JIM.Models.Staging
{
	public class ConnectedSystemObjectChangeAttributeValue
	{
        public Guid Id { get; set; }

        /// <summary>
        /// The parent for this object.
        /// Required for establishing an Entity Framework relationship.
        /// </summary>
        public ConnectedSystemObjectChangeAttribute ConnectedSystemObjectChangeAttribute { get; set; }

        public string? StringValue { get; set; }

        public DateTime? DateTimeValue { get; set; }

        public int? IntValue { get; set; }

        /// <summary>
        /// It would be inefficient, and not especially helpful to track the actual byte value changes, so just track the value lengths instead to show the change.
        /// </summary>
        public int? ByteLengthValue { get; set; }

        public Guid? GuidValue { get; set; }

        public bool? BoolValue { get; set; }
    }
}

