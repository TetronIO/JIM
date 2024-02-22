using JIM.Models.Activities;
using JIM.Models.Core;
using System.ComponentModel.DataAnnotations.Schema;

namespace JIM.Models.Staging
{
    public class ConnectedSystemObject
    {
        #region accessors
        public Guid Id { get; set; }

        public DateTime Created { get; set; }

        public DateTime? LastUpdated { get; set; }

        public ConnectedSystemObjectType Type { get; set; } = null!;

        public ConnectedSystem ConnectedSystem { get; set; } = null!;
        public int ConnectedSystemId { get; set; }

        /// <summary>
        /// Backlink for EF navigation. Do not use.
        /// </summary>
        public ICollection<ActivityRunProfileExecutionItem> ActivityRunProfileExecutionItems { get; } = new List<ActivityRunProfileExecutionItem>();

        /// <summary>
        /// The attribute that uniquely identifies this object in the connected system.
        /// It should be immutable (not change for the lifetime of the object). 
        /// The connected system may author it and be made available to JIM after import, or you may specify it at provisioning time, depending on the needs of the connected system.
        /// This is a convenience accessor. It's defined as a property on one of the connected system object type attributes. i.e. ConnectedSystemObjectTypeAttribute.IsExternalId
        /// </summary>
        public int ExternalIdAttributeId { get; set; }

        /// <summary>
        /// The attribute that may also identify the object in an connected system.
        /// Whether or not this exists depends if the connected system supports secondary external ids or not. 
        /// For instance, an LDAP system will use the DN for references to other objects, even though this is not a good identifier as it's not immutable.
        /// </summary>
        public int? SecondaryExternalIdAttributeId { get; set; }

        public List<ConnectedSystemObjectAttributeValue> AttributeValues { get; set; }

        public ConnectedSystemObjectStatus Status { get; set; }

        /// <summary>
        /// If there's a link to a MetaverseObject here, then this is a connected object,
        /// </summary>
        public MetaverseObject? MetaverseObject { get; set; }

        /// <summary>
        /// How was this CSO joined to an MVO, if at all?
        /// </summary>
        public ConnectedSystemObjectJoinType JoinType { get; set; }

        /// <summary>
        /// When this Connector Space Object was joined to the Metaverse.
        /// </summary>
        public DateTime? DateJoined { get; set; }

        /// <summary>
        /// A list of the changes made to this connected system object.
        /// </summary>
        public List<ConnectedSystemObjectChange> Changes { get; set; } = null!;

        /// <summary>
        /// Only for use by JIM.Service to determine what attribute values need adding and and recording.
        /// </summary>
        [NotMapped]
        public List<ConnectedSystemObjectAttributeValue> PendingAttributeValueAdditions { get; set; } = new();

        /// <summary>
        /// Only for use by JIM.Service to determine what attribute values need removing and recording.
        /// </summary>
        [NotMapped]
        public List<ConnectedSystemObjectAttributeValue> PendingAttributeValueRemovals { get; set; } = new();

        [NotMapped]
        public ConnectedSystemObjectAttributeValue? ExternalIdAttributeValue 
        {  
            get
            {
                if (AttributeValues == null || AttributeValues.Count == 0)
                    return null;

                return AttributeValues.SingleOrDefault(q => q.Attribute.Id == ExternalIdAttributeId);
            } 
        }

        [NotMapped]
        public ConnectedSystemObjectAttributeValue? SecondaryExternalIdAttributeValue
        {
            get
            {
                if (AttributeValues == null || AttributeValues.Count == 0)
                    return null;

                return AttributeValues.SingleOrDefault(q => q.Attribute.Id == SecondaryExternalIdAttributeId);
            }
        }
        #endregion

        #region constructors
        public ConnectedSystemObject()
        {
            Created = DateTime.UtcNow;
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>();
            Status = ConnectedSystemObjectStatus.Normal;
            JoinType = ConnectedSystemObjectJoinType.NotJoined;
        }
        #endregion

        #region public methods
        public void UpdateSingleValuedAttribute<T>(ConnectedSystemObjectTypeAttribute connectedSystemAttribute, T newAttributeValue)
        {
            if (connectedSystemAttribute.AttributePlurality != AttributePlurality.SingleValued)
                throw new ArgumentException($"Attribute '{connectedSystemAttribute.Name}' is not a Single-Valued Attribute. Cannot update value. Use the Add/Remove Multi-Valued attribute methods instead.", nameof(connectedSystemAttribute));

            // the attribute might have pending changes already, so clear any previous pending changes as we can only accept the last change to a sva
            PendingAttributeValueAdditions.RemoveAll(q => q.Attribute.Id == connectedSystemAttribute.Id);
            PendingAttributeValueRemovals.RemoveAll(q => q.Attribute.Id == connectedSystemAttribute.Id);

            // create a new attribute value object for the addition
            var connectedSystemObjectAttributeValue = new ConnectedSystemObjectAttributeValue {
                Attribute = connectedSystemAttribute
            };

            // we need to cast the generic value back to object before we can cast to the specific attribute type next
            // and assign the correct attribute value.
            var newAttributeValueObject = newAttributeValue as object;
            if (typeof(T) == typeof(string))
                connectedSystemObjectAttributeValue.StringValue = newAttributeValueObject as string;
            else if (typeof(T) == typeof(int)) 
                connectedSystemObjectAttributeValue.IntValue = newAttributeValueObject as int?;
            else if (typeof(T) == typeof(DateTime))
                connectedSystemObjectAttributeValue.DateTimeValue = newAttributeValueObject as DateTime?;
            else if (typeof(T) == typeof(Guid))
                connectedSystemObjectAttributeValue.GuidValue = newAttributeValueObject as Guid?;
            else if (typeof(T) == typeof(bool))
                connectedSystemObjectAttributeValue.BoolValue = newAttributeValueObject as bool?;
            else if (typeof(T) == typeof(byte[]))
                connectedSystemObjectAttributeValue.ByteValue = newAttributeValueObject as byte[];
            else if (typeof(T) == typeof(ConnectedSystemObject))
                connectedSystemObjectAttributeValue.ReferenceValue = newAttributeValueObject as ConnectedSystemObject;
            else
                throw new ArgumentNullException(nameof(newAttributeValue), "New attribute value was not an accepted attribute value type!");

            // if all is good by this point, add the change attribute to the list of pending attribute changes
            PendingAttributeValueAdditions.Add(connectedSystemObjectAttributeValue);

            // add  removal for the existing value
            var existingAttributeValue = AttributeValues.SingleOrDefault(av => av.Attribute.Id == connectedSystemAttribute.Id);
            if (existingAttributeValue != null)
                PendingAttributeValueRemovals.Add(existingAttributeValue);
        }

        public void RemoveSingleValuedAttributeValue<T>(ConnectedSystemObjectTypeAttribute connectedSystemAttribute)
        {
            if (connectedSystemAttribute.AttributePlurality != AttributePlurality.SingleValued)
                throw new ArgumentException($"Attribute '{connectedSystemAttribute.Name}' is not a Single-Valued attribute (SVA). Cannot update value. Use the Add/Remove Multi-Valued attribute methods instead.", nameof(connectedSystemAttribute));

            var existingAttributeValue = AttributeValues.SingleOrDefault(av => av.Attribute.Id == connectedSystemAttribute.Id);
            if (existingAttributeValue != null)
                PendingAttributeValueRemovals.Add(existingAttributeValue);
        }

        public void AddMultiValuedAttributeValue<T>(ConnectedSystemObjectTypeAttribute connectedSystemAttribute, T attributeValueToAdd)
        {
            if (connectedSystemAttribute.AttributePlurality != AttributePlurality.MultiValued)
                throw new ArgumentException($"Attribute '{connectedSystemAttribute.Name}' is not a Multi-Valued attribute (MVA). Cannot add a value. Use the UpdateSingleValuedAttribute method instead.", nameof(connectedSystemAttribute));

            // create a new attribute value object for the addition
            var connectedSystemObjectAttributeValue = new ConnectedSystemObjectAttributeValue
            {
                Attribute = connectedSystemAttribute
            };

            // we need to cast the generic value back to object before we can cast to the specific attribute type next
            // and assign the correct attribute value.
            var newAttributeValueObject = attributeValueToAdd as object;
            if (typeof(T) == typeof(string))
                connectedSystemObjectAttributeValue.StringValue = newAttributeValueObject as string;
            else if (typeof(T) == typeof(int))
                connectedSystemObjectAttributeValue.IntValue = newAttributeValueObject as int?;
            else if (typeof(T) == typeof(DateTime))
                connectedSystemObjectAttributeValue.DateTimeValue = newAttributeValueObject as DateTime?;
            else if (typeof(T) == typeof(Guid))
                connectedSystemObjectAttributeValue.GuidValue = newAttributeValueObject as Guid?;
            else if (typeof(T) == typeof(bool))
                connectedSystemObjectAttributeValue.BoolValue = newAttributeValueObject as bool?;
            else if (typeof(T) == typeof(byte[]))
                connectedSystemObjectAttributeValue.ByteValue = newAttributeValueObject as byte[];
            else if (typeof(T) == typeof(ConnectedSystemObject))
                connectedSystemObjectAttributeValue.ReferenceValue = newAttributeValueObject as ConnectedSystemObject;
            else
                throw new ArgumentNullException(nameof(attributeValueToAdd), "New attribute value was not an accepted attribute value type!");

            // if all is good by this point, add the change attribute to the list of pending attribute additions
            PendingAttributeValueAdditions.Add(connectedSystemObjectAttributeValue);
        }

        public void RemoveMultiValuedAttributeValue(ConnectedSystemObjectAttributeValue attributeValueToRemove)
        {
            if (attributeValueToRemove.Attribute.AttributePlurality != AttributePlurality.MultiValued)
                throw new ArgumentException($"Attribute '{attributeValueToRemove.Attribute.Name}' is not a Multi-Valued attribute (MVA). Cannot remove a value. Use the RemoveSingleValuedAttributeValue method instead.", nameof(attributeValueToRemove));

            // add  removal for the existing value
            var existingAttributeValue = AttributeValues.SingleOrDefault(av => av.Id == attributeValueToRemove.Id);
            if (existingAttributeValue != null)
                PendingAttributeValueRemovals.Add(existingAttributeValue);
        }

        public void RemoveAllMultiValuedAttributeValues(ConnectedSystemObjectTypeAttribute connectedSystemAttribute)
        {
            foreach (var attributeValue in AttributeValues.Where(av => av.Attribute.Id == connectedSystemAttribute.Id))
                RemoveMultiValuedAttributeValue(attributeValue);
        }
        #endregion
    }
}
