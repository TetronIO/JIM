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

        if (syncRuleMapping.TargetMetaverseAttribute == null)
        {
            Log.Error("Process: sync rule mapping has no TargetMetaverseAttribute set!");
            return;
        }
        
        // use the Connected System Object Type from our reference list, as the CSO will not contain any attributes (to keep their size down).
        var connectedSystemObjectType = connectedSystemObjectTypes.Single(t => t.Id == connectedSystemObject.Type.Id);
        
        foreach (var source in syncRuleMapping.Sources.OrderBy(q => q.Order))
        {
            // goal:
            // generate or select a source value, to assign to a target attribute (CSO to MVO).
            // inbound sync rule mappings can have one or more sources and will compound if numerous. those sources can be of different types, i.e:
            // - direct attribute flow
            // - generated from functions
            // - generated from expressions
            
            // NOTE: attribute priority has not been implemented yet and will come in a later effort.
            // for now, all mappings will be applied, meaning if there are multiple mapping to a MVO attribute, the last to be processed will win.



            if (source.ConnectedSystemAttribute != null)
            {
                // process each known CSO attribute (potential updates). Unknown ones on a CSO will be ignored. The CSO type schema is king.
                foreach (var csotAttributeName in connectedSystemObjectType.Attributes.Select(a => a.Name))
                {
                    // what MVO attribute does this CSO attribute map to? source.MetaverseAttribute
                    // what MVO should we be updating? connectedSystemObject.MetaverseObject
                    
                    // are there matching attribute values on the CSO for this attribute?
                    // this might return multiple objects if the attribute is multivalued, i.e. a member attribute on a group.
                    var csoAttributeValues = connectedSystemObject.GetAttributeValues(csotAttributeName);
                    if (csoAttributeValues.Count > 0)
                    {
                        // work out what data type this attribute is and get the attribute
                        var csoAttribute = connectedSystemObjectType.Attributes.Single(a => a.Name.Equals(csotAttributeName, StringComparison.InvariantCultureIgnoreCase));
                        foreach (var csoAttributeValue in csoAttributeValues)
                        {
                            // process attribute additions and removals...
                            switch (csoAttribute.Type)
                            {
                                // TODO: move these into sub functions. maybe a single generic function?
                                
                                case AttributeDataType.Text:
                                    // find values on the MVO of type string that aren't on the CSO and remove them first
                                    var mvoMissingStringAttributeValue = connectedSystemObject.MetaverseObject.AttributeValues.Where(mvoav => 
                                        mvoav.Attribute.Id == syncRuleMapping.TargetMetaverseAttribute.Id && 
                                        mvoav.StringValue != 
                                        !importedObjectAttribute.StringValues.Any(i => i.Equals(m.StringValue)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => mvoMissingStringAttributeValues.Any(msav => msav.Id == av.Id)));

                                    // find imported values of type string that aren't on the cso and add them
                                    var newStringValues = csoAttributeValue.StringValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csotAttributeName && av.StringValue != null && av.StringValue.Equals(sv)));
                                    foreach (var newStringValue in newStringValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, StringValue = newStringValue });
                                    break;

                                case AttributeDataType.Number:
                                    // find values on the cso of type int that aren't on the imported object and remove them first
                                    var missingIntAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csotAttributeName && av.IntValue != null && !importedObjectAttribute.IntValues.Any(i => i.Equals(av.IntValue)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingIntAttributeValues.Any(msav => msav.Id == av.Id)));

                                    // find imported values of type int that aren't on the cso and add them
                                    var newIntValues = importedObjectAttribute.IntValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csotAttributeName && av.IntValue != null && av.IntValue.Equals(sv)));
                                    foreach (var newIntValue in newIntValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, IntValue = newIntValue });
                                    break;

                                case AttributeDataType.DateTime:
                                    var existingCsoDateTimeAttributeValue = connectedSystemObject.AttributeValues.SingleOrDefault(av => av.Attribute.Name == csotAttributeName);
                                    if (existingCsoDateTimeAttributeValue == null)
                                    {
                                        // set initial value
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, DateTimeValue = importedObjectAttribute.DateTimeValue });
                                    }
                                    else if (existingCsoDateTimeAttributeValue.DateTimeValue != importedObjectAttribute.DateTimeValue)
                                    {
                                        // update existing value by removing and adding
                                        connectedSystemObject.PendingAttributeValueRemovals.Add(existingCsoDateTimeAttributeValue);
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, DateTimeValue = importedObjectAttribute.DateTimeValue });
                                    }
                                    break;

                                case AttributeDataType.Binary:
                                    // find values on the cso of type byte array that aren't on the imported object and remove them first
                                    var missingByteArrayAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csotAttributeName && av.ByteValue != null && !importedObjectAttribute.ByteValues.Any(i => Utilities.Utilities.AreByteArraysTheSame(i, av.ByteValue)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingByteArrayAttributeValues.Any(msav => msav.Id == av.Id)));

                                    // find imported values of type byte array that aren't on the cso and add them
                                    var newByteArrayValues = importedObjectAttribute.ByteValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csotAttributeName && av.ByteValue != null && Utilities.Utilities.AreByteArraysTheSame(sv, av.ByteValue)));
                                    foreach (var newByteArrayValue in newByteArrayValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, ByteValue = newByteArrayValue });
                                    break;

                                case AttributeDataType.Reference:
                                    // find unresolved reference values on the cso that aren't on the imported object and remove them first
                                    var missingUnresolvedReferenceValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csotAttributeName && av.UnresolvedReferenceValue != null && !importedObjectAttribute.ReferenceValues.Any(i => i.Equals(av.UnresolvedReferenceValue, StringComparison.InvariantCultureIgnoreCase)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingUnresolvedReferenceValues.Any(msav => msav.Id == av.Id)));

                                    // find imported unresolved reference values that aren't on the cso and add them
                                    var newUnresolvedReferenceValues = importedObjectAttribute.ReferenceValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csotAttributeName && av.UnresolvedReferenceValue != null && av.UnresolvedReferenceValue.Equals(sv, StringComparison.InvariantCultureIgnoreCase)));
                                    foreach (var newUnresolvedReferenceValue in newUnresolvedReferenceValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, UnresolvedReferenceValue = newUnresolvedReferenceValue });
                                    break;

                                case AttributeDataType.Guid:
                                    // find values on the cso of type Guid that aren't on the imported object and remove them first
                                    var missingGuidAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csotAttributeName && av.GuidValue != null && !importedObjectAttribute.GuidValues.Any(i => i.Equals(av.GuidValue)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingGuidAttributeValues.Any(msav => msav.Id == av.Id)));

                                    // find imported values of type Guid that aren't on the cso and add them
                                    var newGuidValues = importedObjectAttribute.GuidValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csotAttributeName && av.GuidValue != null && av.GuidValue.Equals(sv)));
                                    foreach (var newGuidValue in newGuidValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, GuidValue = newGuidValue });
                                    break;

                                case AttributeDataType.Boolean:
                                    // there will be only a single value for a bool. is it the same or different?
                                    // if different, remove the old value, add the new one
                                    // observation: removing and adding SVA values is costlier than just updating a row. it also results in increased primary key usage, i.e. constantly generating new values
                                    // todo: consider having the ability to update values instead of replacing.
                                    var csoBooleanAttributeValue = connectedSystemObject.AttributeValues.SingleOrDefault(av => av.Attribute.Name == csotAttributeName);
                                    if (csoBooleanAttributeValue == null)
                                    {
                                        // set initial value
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, BoolValue = importedObjectAttribute.BoolValue });
                                    }
                                    else if (csoBooleanAttributeValue.BoolValue != importedObjectAttribute.BoolValue)
                                    {
                                        // update existing value by removing and adding
                                        connectedSystemObject.PendingAttributeValueRemovals.Add(csoBooleanAttributeValue);
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, BoolValue = importedObjectAttribute.BoolValue });
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
                        // there are no CSO values for this attribute. reflect this by deleting all MVO attribute values for this attribute.
                        var mvoAttributeValuesToDelete = connectedSystemObject.MetaverseObject.AttributeValues.Where(q => q.Attribute.Name == csotAttributeName);
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