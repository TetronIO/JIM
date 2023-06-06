namespace JIM.Models.Core
{
    public class MetaverseObjectChangeAttributeValue
    {
        #region accessors
        public Guid Id { get; set; }

        /// <summary>
        /// The parent for this object.
        /// Required for establishing an Entity Framework relationship.
        /// </summary>
        public MetaverseObjectChangeAttribute MetaverseObjectChangeAttribute { get; set; }

        public string? StringValue { get; set; }

        public DateTime? DateTimeValue { get; set; }

        public int? IntValue { get; set; }

        /// <summary>
        /// It would be inefficient, and not especially helpful to track the actual byte value changes, so just track the value lengths instead to show the change.
        /// </summary>
        public int? ByteValueLength { get; set; }

        public Guid? GuidValue { get; set; }

        public bool? BoolValue { get; set; }

        public MetaverseObject? ReferenceValue { get; set; }
        #endregion

        #region constructors
        public MetaverseObjectChangeAttributeValue()
        {
            // default constructor still required for EntityFramework
        }

        public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, string stringValue)
        {
            StringValue = stringValue;
            MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        }

        public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, int intValue)
        {
            IntValue = intValue;
            MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        }

        public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, DateTime dateTimeValue)
        {
            DateTimeValue = dateTimeValue;
            MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        }

        public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, Guid guidValue)
        {
            GuidValue = guidValue;
            MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        }

        public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, bool boolValue)
        {
            BoolValue = boolValue;
            MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        }

        public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, bool isByteValueLength, int byteValueLength)
        {
            // we use isByteValueLength to enable us to have a unique constructor signature for an int arg type
            if (isByteValueLength)
                ByteValueLength = byteValueLength;
            else
                throw new ArgumentException("Expected isByteValueLength == true");

            MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        }

        public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, MetaverseObject referenceValue)
        {
            ReferenceValue = referenceValue;
            MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        }
        #endregion
    }
}
