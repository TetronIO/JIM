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
        public ConnectedSystemObjectChangeAttribute ConnectedSystemObjectChangeAttribute { get; set; }

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

        #region constructors
        public ConnectedSystemObjectChangeAttributeValue()
        {
            // default constructor still required for EntityFramework
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, string stringValue)
        {
            StringValue = stringValue;
            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, int intValue)
        {
            IntValue = intValue;
            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, DateTime dateTimeValue)
        {
            DateTimeValue = dateTimeValue;
            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, Guid guidValue)
        {
            GuidValue = guidValue;
            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, bool boolValue)
        {
            BoolValue = boolValue;
            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, bool isByteValueLength, int byteValueLength)
        {
            // we use isByteValueLength to enable us to have a unique constructor signature for an int arg type
            if (isByteValueLength)
                ByteValueLength = byteValueLength;
            else
                throw new ArgumentException("Expected isByteValueLength == true");

            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        }

        public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ConnectedSystemObject referenceValue)
        {
            ReferenceValue = referenceValue;
            ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        }
        #endregion
    }
}

