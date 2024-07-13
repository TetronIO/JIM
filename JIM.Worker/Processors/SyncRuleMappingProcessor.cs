using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Worker.Processors;

public static class SyncRuleMappingProcessor
{
    public static void Process(ConnectedSystemObject connectedSystemObject, SyncRuleMapping syncRuleMapping, List<ConnectedSystemObjectType> connectedSystemObjectTypes)
    {
        if (connectedSystemObject.MetaverseObject == null)
        {
            Log.Error($"Process: CSO ({connectedSystemObject}) has no MVO!");
            return;
        }
        
        // use the Connected System Object Type from our reference list, as the CSO will not contain any attributes (to keep their size down).
        var connectedSystemObjectType = connectedSystemObjectTypes.Single(t => t.Id == connectedSystemObject.Type.Id);
        
        foreach (var source in syncRuleMapping.Sources.OrderBy(q => q.Order))
        {
            // goal:
            // select or generate a source value, to assign to a target attribute (CSO to MVO).
            // inbound sync rule mappings can have one or more sources. those sources can be of different types, i.e:
            // - direct attribute flow
            // - generated from functions
            // - generated from expressions
            // you can even combine multiple sources, including of different types, and they will be combined to produce a final value.

            if (source.ConnectedSystemAttribute != null)
            {
                // CSOs and MVOs have slightly different ways of representing multiple-values due to how
                // they need to be used/populated in their respective scenarios.
                // CSOs store all values under a single attribute value object.
                // MVOs store each value under their own attribute value object.
                // so we need to handle MVAs differently when populating the MVO attribute value.
                
                // process known attributes (potential updates)
                foreach (var attributeName in connectedSystemObjectType.Attributes.Select(a => a.Name))
                {
                    // are there matching attribute values on the CSO for this attribute?
                    var csoAttributeValues = connectedSystemObject.GetAttributeValues(attributeName);
                    if (csoAttributeValues.Count > 0)
                    {
                        // work out what data type this attribute is and get the attribute
                        var attribute = connectedSystemObjectType.Attributes.Single(a => a.Name.Equals(attributeName, StringComparison.CurrentCultureIgnoreCase));
                        foreach (var csoAttributeValue in csoAttributeValues)
                        {
                            // process attribute additions and removals...
                            switch (attribute.Type)
                            {
                                case AttributeDataType.Text:
                                    // find values on the MVO of type string that aren't on the CSO and remove them first
                                    // TODO: use the mapping in the sync rule to determine the flow!
                                    var mvoMissingStringAttributeValues = connectedSystemObject.MetaverseObject.AttributeValues.Where(mvoav => 
                                        mvoav.Attribute.Name == attributeName && 
                                        mvoav.StringValue != null && 
                                        !importedObjectAttribute.StringValues.Any(i => i.Equals(m.StringValue)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => mvoMissingStringAttributeValues.Any(msav => msav.Id == av.Id)));

                                    // find imported values of type string that aren't on the cso and add them
                                    var newStringValues = csoAttributeValue.StringValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == attributeName && av.StringValue != null && av.StringValue.Equals(sv)));
                                    foreach (var newStringValue in newStringValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = attribute, StringValue = newStringValue });
                                    break;

                                case AttributeDataType.Number:
                                    // find values on the cso of type int that aren't on the imported object and remove them first
                                    var missingIntAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == attributeName && av.IntValue != null && !importedObjectAttribute.IntValues.Any(i => i.Equals(av.IntValue)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingIntAttributeValues.Any(msav => msav.Id == av.Id)));

                                    // find imported values of type int that aren't on the cso and add them
                                    var newIntValues = importedObjectAttribute.IntValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == attributeName && av.IntValue != null && av.IntValue.Equals(sv)));
                                    foreach (var newIntValue in newIntValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = attribute, IntValue = newIntValue });
                                    break;

                                case AttributeDataType.DateTime:
                                    var existingCsoDateTimeAttributeValue = connectedSystemObject.AttributeValues.SingleOrDefault(av => av.Attribute.Name == attributeName);
                                    if (existingCsoDateTimeAttributeValue == null)
                                    {
                                        // set initial value
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = attribute, DateTimeValue = importedObjectAttribute.DateTimeValue });
                                    }
                                    else if (existingCsoDateTimeAttributeValue.DateTimeValue != importedObjectAttribute.DateTimeValue)
                                    {
                                        // update existing value by removing and adding
                                        connectedSystemObject.PendingAttributeValueRemovals.Add(existingCsoDateTimeAttributeValue);
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = attribute, DateTimeValue = importedObjectAttribute.DateTimeValue });
                                    }
                                    break;

                                case AttributeDataType.Binary:
                                    // find values on the cso of type byte array that aren't on the imported object and remove them first
                                    var missingByteArrayAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == attributeName && av.ByteValue != null && !importedObjectAttribute.ByteValues.Any(i => Utilities.Utilities.AreByteArraysTheSame(i, av.ByteValue)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingByteArrayAttributeValues.Any(msav => msav.Id == av.Id)));

                                    // find imported values of type byte array that aren't on the cso and add them
                                    var newByteArrayValues = importedObjectAttribute.ByteValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == attributeName && av.ByteValue != null && Utilities.Utilities.AreByteArraysTheSame(sv, av.ByteValue)));
                                    foreach (var newByteArrayValue in newByteArrayValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = attribute, ByteValue = newByteArrayValue });
                                    break;

                                case AttributeDataType.Reference:
                                    // find unresolved reference values on the cso that aren't on the imported object and remove them first
                                    var missingUnresolvedReferenceValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == attributeName && av.UnresolvedReferenceValue != null && !importedObjectAttribute.ReferenceValues.Any(i => i.Equals(av.UnresolvedReferenceValue, StringComparison.InvariantCultureIgnoreCase)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingUnresolvedReferenceValues.Any(msav => msav.Id == av.Id)));

                                    // find imported unresolved reference values that aren't on the cso and add them
                                    var newUnresolvedReferenceValues = importedObjectAttribute.ReferenceValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == attributeName && av.UnresolvedReferenceValue != null && av.UnresolvedReferenceValue.Equals(sv, StringComparison.InvariantCultureIgnoreCase)));
                                    foreach (var newUnresolvedReferenceValue in newUnresolvedReferenceValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = attribute, UnresolvedReferenceValue = newUnresolvedReferenceValue });
                                    break;

                                case AttributeDataType.Guid:
                                    // find values on the cso of type Guid that aren't on the imported object and remove them first
                                    var missingGuidAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == attributeName && av.GuidValue != null && !importedObjectAttribute.GuidValues.Any(i => i.Equals(av.GuidValue)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingGuidAttributeValues.Any(msav => msav.Id == av.Id)));

                                    // find imported values of type Guid that aren't on the cso and add them
                                    var newGuidValues = importedObjectAttribute.GuidValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == attributeName && av.GuidValue != null && av.GuidValue.Equals(sv)));
                                    foreach (var newGuidValue in newGuidValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = attribute, GuidValue = newGuidValue });
                                    break;

                                case AttributeDataType.Boolean:
                                    // there will be only a single value for a bool. is it the same or different?
                                    // if different, remove the old value, add the new one
                                    // observation: removing and adding SVA values is costlier than just updating a row. it also results in increased primary key usage, i.e. constantly generating new values
                                    // todo: consider having the ability to update values instead of replacing.
                                    var csoBooleanAttributeValue = connectedSystemObject.AttributeValues.SingleOrDefault(av => av.Attribute.Name == attributeName);
                                    if (csoBooleanAttributeValue == null)
                                    {
                                        // set initial value
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = attribute, BoolValue = importedObjectAttribute.BoolValue });
                                    }
                                    else if (csoBooleanAttributeValue.BoolValue != importedObjectAttribute.BoolValue)
                                    {
                                        // update existing value by removing and adding
                                        connectedSystemObject.PendingAttributeValueRemovals.Add(csoBooleanAttributeValue);
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = attribute, BoolValue = importedObjectAttribute.BoolValue });
                                    }
                                    break;
                                
                                case AttributeDataType.NotSet:
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }
                    }
                    else
                    {
                        // no CSO values for this attribute. delete all the MVO attribute values for this attribute
                        var mvoAttributeValuesToDelete = connectedSystemObject.MetaverseObject.AttributeValues.Where(q => q.Attribute.Name == attributeName);
                        connectedSystemObject.MetaverseObject.PendingAttributeValueRemovals.AddRange(mvoAttributeValuesToDelete);
                    }
                }
                
                
                
                
            }
            else if (source.Function != null)
                throw new NotImplementedException("Functions have not been implemented yet.");
            else if (source.MetaverseAttribute != null)
                throw new InvalidDataException("SyncRuleMappingSource.MetaverseAttribute being populated is not supported for synchronisation operations. " +
                                               "This operation is focused on import flow, so Connected System to Metaverse Object.");
            else
                throw new InvalidDataException("Expected ConnectedSystemAttribute or Function to be populated in a SyncRuleMappingSource object.")
        }
    }
}