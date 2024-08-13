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
            // why is this an error?
            Log.Error($"Process: CSO ({connectedSystemObject}) is not joined to an MVO!");
            return;
        }

        if (syncRuleMapping.TargetMetaverseAttribute == null)
        {
            Log.Error("Process: Sync Rule mapping has no TargetMetaverseAttribute set!");
            return;
        }
        
        // use the Connected System Object Type from our reference list, as the CSO will not contain any attributes (to keep their size down on retrieval).
        var csoType = connectedSystemObjectTypes.Single(t => t.Id == connectedSystemObject.Type.Id);
        
        foreach (var source in syncRuleMapping.Sources.OrderBy(q => q.Order))
        {
            // goal:
            // generate or select a source value to assign to a target MVO attribute.
            // inbound sync rule mappings can have one or more sources and will compound if numerous. those sources can be of different types, i.e:
            // - direct attribute flow
            // - generated from functions
            // - generated from expressions
            
            // NOTE: attribute priority has not been implemented yet and will come in a later effort.
            // for now, all mappings will be applied, meaning if there are multiple mapping to a MVO attribute, the last to be processed will win.
            
            if (source.ConnectedSystemAttribute != null)
            {
                // process each known CSO attribute (potential updates). Unknown ones on a CSO will be ignored. The CSO type schema is king.
                foreach (var csotAttributeName in csoType.Attributes.Select(a => a.Name))
                {
                    // what MVO attribute does this CSO attribute map to? source.MetaverseAttribute
                    // what MVO should we be updating? connectedSystemObject.MetaverseObject
                    
                    // are there matching attribute values on the CSO for this attribute?
                    // this might return multiple objects if the attribute is multivalued, i.e. a member attribute on a group.
                    var csoAttributeValues = connectedSystemObject.GetAttributeValues(csotAttributeName);
                    if (csoAttributeValues.Count > 0)
                    {
                        // work out what data type this attribute is and get the attribute
                        var csotAttribute = csoType.Attributes.Single(a => a.Name.Equals(csotAttributeName, StringComparison.InvariantCultureIgnoreCase));
                        foreach (var csoAttributeValue in csoAttributeValues)
                        {
                            // process attribute additions and removals...
                            switch (csotAttribute.Type)
                            {
                                // TODO: move these into sub functions. maybe a single generic function?
                                
                                case AttributeDataType.Text:
                                    // find values on the MVO of type string that aren't on the CSO and remove them
                                    var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav => 
                                        mvoav.Attribute.Id == syncRuleMapping.TargetMetaverseAttribute.Id && 
                                        !csoAttributeValues.Any(csoav => csoav.StringValue != null && csoav.StringValue.Equals(mvoav.StringValue)));
                                    mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

                                    // find values on the CSO of type string that aren't on the MVO according to the sync rule mapping.
                                    var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav => 
                                        csoav.Attribute.Id == source.ConnectedSystemAttribute.Id &&
                                        !mvo.AttributeValues.Any(mvoav => 
                                            mvoav.Attribute.Id == syncRuleMapping.TargetMetaverseAttribute.Id && 
                                            mvoav.StringValue != null && mvoav.StringValue.Equals(csoav.StringValue)));
                                    
                                    // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
                                    foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
                                    {
                                        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                                        {
                                            MetaverseObject = mvo,
                                            Attribute = syncRuleMapping.TargetMetaverseAttribute,
                                            StringValue = newCsoNewAttributeValue.StringValue
                                        });
                                    }
                                    break;

                                case AttributeDataType.Number:
                                    // find values on the cso of type int that aren't on the imported object and remove them first
                                    var missingIntAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csotAttributeName && av.IntValue != null && !importedObjectAttribute.IntValues.Any(i => i.Equals(av.IntValue)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingIntAttributeValues.Any(msav => msav.Id == av.Id)));

                                    // find imported values of type int that aren't on the cso and add them
                                    var newIntValues = importedObjectAttribute.IntValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csotAttributeName && av.IntValue != null && av.IntValue.Equals(sv)));
                                    foreach (var newIntValue in newIntValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csotAttribute, IntValue = newIntValue });
                                    break;

                                case AttributeDataType.DateTime:
                                    var existingCsoDateTimeAttributeValue = connectedSystemObject.AttributeValues.SingleOrDefault(av => av.Attribute.Name == csotAttributeName);
                                    if (existingCsoDateTimeAttributeValue == null)
                                    {
                                        // set initial value
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csotAttribute, DateTimeValue = importedObjectAttribute.DateTimeValue });
                                    }
                                    else if (existingCsoDateTimeAttributeValue.DateTimeValue != importedObjectAttribute.DateTimeValue)
                                    {
                                        // update existing value by removing and adding
                                        connectedSystemObject.PendingAttributeValueRemovals.Add(existingCsoDateTimeAttributeValue);
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csotAttribute, DateTimeValue = importedObjectAttribute.DateTimeValue });
                                    }
                                    break;

                                case AttributeDataType.Binary:
                                    // find values on the cso of type byte array that aren't on the imported object and remove them first
                                    var missingByteArrayAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csotAttributeName && av.ByteValue != null && !importedObjectAttribute.ByteValues.Any(i => Utilities.Utilities.AreByteArraysTheSame(i, av.ByteValue)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingByteArrayAttributeValues.Any(msav => msav.Id == av.Id)));

                                    // find imported values of type byte array that aren't on the cso and add them
                                    var newByteArrayValues = importedObjectAttribute.ByteValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csotAttributeName && av.ByteValue != null && Utilities.Utilities.AreByteArraysTheSame(sv, av.ByteValue)));
                                    foreach (var newByteArrayValue in newByteArrayValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csotAttribute, ByteValue = newByteArrayValue });
                                    break;

                                case AttributeDataType.Reference:
                                    // find unresolved reference values on the cso that aren't on the imported object and remove them first
                                    var missingUnresolvedReferenceValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csotAttributeName && av.UnresolvedReferenceValue != null && !importedObjectAttribute.ReferenceValues.Any(i => i.Equals(av.UnresolvedReferenceValue, StringComparison.InvariantCultureIgnoreCase)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingUnresolvedReferenceValues.Any(msav => msav.Id == av.Id)));

                                    // find imported unresolved reference values that aren't on the cso and add them
                                    var newUnresolvedReferenceValues = importedObjectAttribute.ReferenceValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csotAttributeName && av.UnresolvedReferenceValue != null && av.UnresolvedReferenceValue.Equals(sv, StringComparison.InvariantCultureIgnoreCase)));
                                    foreach (var newUnresolvedReferenceValue in newUnresolvedReferenceValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csotAttribute, UnresolvedReferenceValue = newUnresolvedReferenceValue });
                                    break;

                                case AttributeDataType.Guid:
                                    // find values on the cso of type Guid that aren't on the imported object and remove them first
                                    var missingGuidAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csotAttributeName && av.GuidValue != null && !importedObjectAttribute.GuidValues.Any(i => i.Equals(av.GuidValue)));
                                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingGuidAttributeValues.Any(msav => msav.Id == av.Id)));

                                    // find imported values of type Guid that aren't on the cso and add them
                                    var newGuidValues = importedObjectAttribute.GuidValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csotAttributeName && av.GuidValue != null && av.GuidValue.Equals(sv)));
                                    foreach (var newGuidValue in newGuidValues)
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csotAttribute, GuidValue = newGuidValue });
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
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csotAttribute, BoolValue = importedObjectAttribute.BoolValue });
                                    }
                                    else if (csoBooleanAttributeValue.BoolValue != importedObjectAttribute.BoolValue)
                                    {
                                        // update existing value by removing and adding
                                        connectedSystemObject.PendingAttributeValueRemovals.Add(csoBooleanAttributeValue);
                                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csotAttribute, BoolValue = importedObjectAttribute.BoolValue });
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
                throw new InvalidDataException("Expected ConnectedSystemAttribute or Function to be populated in a SyncRuleMappingSource object.");
        }
    }

    private static void ProcessAttributeChanges(ConnectedSystemObject connectedSystemObject, SyncRuleMapping syncRuleMapping, SyncRuleMappingSource syncRuleMappingSource, ConnectedSystemObjectTypeAttribute csotAttribute, List<ConnectedSystemObjectAttributeValue> csoAttributeValues)
    {
        // already checked for nulls higher up the call stack, but it doesn't hurt to be super sure.
        if (syncRuleMapping.TargetMetaverseAttribute == null)
        {
            Log.Error("syncRuleMapping.TargetMetaverseAttribute is null.");
            return;
        }
        if (syncRuleMappingSource.ConnectedSystemAttribute == null)
        {
            Log.Error("syncRuleMappingSource.ConnectedSystemAttribute is null.");
            return;
        }
        if (connectedSystemObject.MetaverseObject == null)
        {
            Log.Error("connectedSystemObject.MetaverseObject is null.");
            return;
        }
        
        // working with a MVO reference directly to make this function easier to understand.
        var mvo = connectedSystemObject.MetaverseObject;
        
        switch (csotAttribute.Type)
        {
            case AttributeDataType.Text:
            {
                // find values on the MVO of type string that aren't on the CSO and remove them
                var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
                    mvoav.Attribute.Id == syncRuleMapping.TargetMetaverseAttribute.Id &&
                    !csoAttributeValues.Any(csoav => csoav.StringValue != null && csoav.StringValue.Equals(mvoav.StringValue)));
                mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

                // find values on the CSO of type string that aren't on the MVO according to the sync rule mapping.
                var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav =>
                    csoav.Attribute.Id == syncRuleMappingSource.ConnectedSystemAttribute.Id &&
                    !mvo.AttributeValues.Any(mvoav =>
                        mvoav.Attribute.Id == syncRuleMapping.TargetMetaverseAttribute.Id &&
                        mvoav.StringValue != null && mvoav.StringValue.Equals(csoav.StringValue)));

                // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
                foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
                {
                    mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                    {
                        MetaverseObject = mvo,
                        Attribute = syncRuleMapping.TargetMetaverseAttribute,
                        StringValue = newCsoNewAttributeValue.StringValue
                    });
                }
            }
            break;

            case AttributeDataType.Number:
            {
                // find values on the MVO of type int that aren't on the CSO and remove them
                var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
                    mvoav.Attribute.Id == syncRuleMapping.TargetMetaverseAttribute.Id &&
                    !csoAttributeValues.Any(csoav => csoav.IntValue != null && csoav.IntValue.Equals(mvoav.IntValue)));
                mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

                // find values on the CSO of type int that aren't on the MVO according to the sync rule mapping.
                var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav =>
                    csoav.Attribute.Id == syncRuleMappingSource.ConnectedSystemAttribute.Id &&
                    !mvo.AttributeValues.Any(mvoav =>
                        mvoav.Attribute.Id == syncRuleMapping.TargetMetaverseAttribute.Id &&
                        mvoav.IntValue != null && mvoav.IntValue.Equals(csoav.IntValue)));

                // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
                foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
                {
                    mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                    {
                        MetaverseObject = mvo,
                        Attribute = syncRuleMapping.TargetMetaverseAttribute,
                        IntValue = newCsoNewAttributeValue.IntValue
                    });
                }
            }
            break;

            case AttributeDataType.DateTime:
            {
                // not sure about this, think DateTime can only be SVA.
                
                
                // find values on the MVO of type DateTime that aren't on the CSO and remove them
                var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
                    mvoav.Attribute.Id == syncRuleMapping.TargetMetaverseAttribute.Id &&
                    !csoAttributeValues.Any(csoav => csoav.DateTimeValue != null && csoav.DateTimeValue.Equals(mvoav.DateTimeValue)));
                mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

                // find values on the CSO of type DateTime that aren't on the MVO according to the sync rule mapping.
                var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav =>
                    csoav.Attribute.Id == syncRuleMappingSource.ConnectedSystemAttribute.Id &&
                    !mvo.AttributeValues.Any(mvoav =>
                        mvoav.Attribute.Id == syncRuleMapping.TargetMetaverseAttribute.Id &&
                        mvoav.DateTimeValue != null && mvoav.DateTimeValue.Equals(csoav.DateTimeValue)));

                // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
                foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
                {
                    mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                    {
                        MetaverseObject = mvo,
                        Attribute = syncRuleMapping.TargetMetaverseAttribute,
                        DateTimeValue = newCsoNewAttributeValue.DateTimeValue
                    });
                }
            }
            break;

            case AttributeDataType.Guid:
            {
            }
            break;
        }
    }
}