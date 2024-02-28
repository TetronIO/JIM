using JIM.Models.Enums;

namespace JIM.Models.Staging
{
	public class ConnectedSystemObjectChangeAttributeValue
	{
        #region accessors
        public Guid Id { get; set; }

        /// <summary>
        /// The parent for this object.
        /// Required for establishing an Entity Framework relationship.
        /// </summary>
        public ConnectedSystemObjectChangeAttribute ConnectedSystemObjectChangeAttribute { get; set; } = null!;

        /// <summary>
        /// Was the value being added, or removed?
        /// </summary>
        public ValueChangeType ValueChangeType { get; set; } = ValueChangeType.NotSet;

        public string? StringValue { get; set; }

        public DateTime? DateTimeValue { get; set; }

        public int? IntValue { get; set; }

        /// <summary>
        /// It would be inefficient, and not especially helpful to track the actual byte value changes, so just track the value lengths instead to show the change.
        /// </summary>
        public int? ByteValueLength { get; set; }

        public Guid? GuidValue { get; set; }

        public bool? BoolValue { get; set; }

        public ConnectedSystemObject? ReferenceValue { get; set; }
        #endregion

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(StringValue))
                return StringValue;

            if (DateTimeValue.HasValue)
                return DateTimeValue.Value.ToString();

            if (IntValue.HasValue)
                return IntValue.Value.ToString();

            if (ByteValueLength.HasValue)
                return ByteValueLength.Value.ToString();

            if (GuidValue.HasValue)
                return GuidValue.Value.ToString();

            if (BoolValue.HasValue)
                return BoolValue.Value.ToString();

            if (ReferenceValue != null)
                return ReferenceValue.Id.ToString();

            return string.Empty;
        }

        #region constructors
        public ConnectedSystemObjectChangeAttributeValue()
        {
            // default constructor still required for EntityFramework
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, string stringValue)
        {
            StringValue = stringValue;
            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
            ValueChangeType = valueChangeType;
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, int intValue)
        {
            IntValue = intValue;
            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
            ValueChangeType = valueChangeType;
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, DateTime dateTimeValue)
        {
            DateTimeValue = dateTimeValue;
            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
            ValueChangeType = valueChangeType;
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, Guid guidValue)
        {
            GuidValue = guidValue;
            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
            ValueChangeType = valueChangeType;
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, bool boolValue)
        {
            BoolValue = boolValue;
            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
            ValueChangeType = valueChangeType;
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, bool isByteValueLength, int byteValueLength)
        {
            // we use isByteValueLength to enable us to have a unique constructor signature for an int arg type
            if (isByteValueLength)
                ByteValueLength = byteValueLength;
            else
                throw new ArgumentException("Expected isByteValueLength == true");

            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
            ValueChangeType = valueChangeType;
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, ConnectedSystemObject referenceValue)
        {
            ReferenceValue = referenceValue;
            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
            ValueChangeType = valueChangeType;
        }
        #endregion
    }
}

