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
        var csoType = connectedSystemObjectTypes.Single(t => t.Id == connectedSystemObject.TypeId);
        
        // working with a MVO reference directly to make this function easier to understand.
        var mvo = connectedSystemObject.MetaverseObject;
        
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
            
            if (source.ConnectedSystemAttributeId.HasValue)
            {
                // process the specific CSO attribute defined in this sync rule mapping source
                var csotAttribute = csoType.Attributes.SingleOrDefault(a => a.Id == source.ConnectedSystemAttributeId.Value);
                if (csotAttribute != null)
                {
                    // are there matching attribute values on the CSO for this attribute?
                    // this might return multiple objects if the attribute is multivalued, i.e. a member attribute on a group.
                    // use AttributeId instead of GetAttributeValues because Attribute navigation property may not be loaded
                    var csoAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.AttributeId == csotAttribute.Id).ToList();
                    if (csoAttributeValues.Count > 0)
                    {
                        foreach (var csoAttributeValue in csoAttributeValues)
                        {
                            // process attribute additions and removals...
                            switch (csotAttribute.Type)
                            {
                                case AttributeDataType.Text:
                                {
                                    // find values on the MVO of type string that aren't on the CSO and remove them.
                                    var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
                                        mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id &&
                                        !csoAttributeValues.Any(csoav => csoav.StringValue != null && csoav.StringValue.Equals(mvoav.StringValue)));
                                    mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

                                    // find values on the CSO of type string that aren't on the MVO according to the sync rule mapping.
                                    var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav =>
                                        csoav.AttributeId == source.ConnectedSystemAttributeId.Value &&
                                        !mvo.AttributeValues.Any(mvoav =>
                                            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id &&
                                            mvoav.StringValue != null && mvoav.StringValue.Equals(csoav.StringValue)));

                                    // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
                                    foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
                                    {
                                        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                                        {
                                            MetaverseObject = mvo,
                                            Attribute = syncRuleMapping.TargetMetaverseAttribute,
                                            AttributeId = syncRuleMapping.TargetMetaverseAttribute.Id,
                                            StringValue = newCsoNewAttributeValue.StringValue
                                        });
                                    }
                                    break;
                                }

                                case AttributeDataType.Number:
                                {
                                    // find values on the MVO of type int that aren't on the CSO and remove them
                                    var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
                                        mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id &&
                                        !csoAttributeValues.Any(csoav => csoav.IntValue != null && csoav.IntValue.Equals(mvoav.IntValue)));
                                    mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

                                    // find values on the CSO of type int that aren't on the MVO according to the sync rule mapping.
                                    var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav =>
                                        csoav.AttributeId == source.ConnectedSystemAttributeId.Value &&
                                        !mvo.AttributeValues.Any(mvoav =>
                                            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id &&
                                            mvoav.IntValue != null && mvoav.IntValue.Equals(csoav.IntValue)));

                                    // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
                                    foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
                                    {
                                        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                                        {
                                            MetaverseObject = mvo,
                                            Attribute = syncRuleMapping.TargetMetaverseAttribute,
                                            AttributeId = syncRuleMapping.TargetMetaverseAttribute.Id,
                                            IntValue = newCsoNewAttributeValue.IntValue
                                        });
                                    }
                                    break;
                                }

                                case AttributeDataType.DateTime:
                                {
                                    var csoValue = connectedSystemObject.AttributeValues.SingleOrDefault(csoav => csoav.AttributeId == source.ConnectedSystemAttributeId.Value);
                                    var mvoValue = mvo.AttributeValues.SingleOrDefault(mvoav => mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id);

                                    if (mvoValue != null && csoValue == null)
                                    {
                                        // there is a value on the MVO that isn't on the CSO. remove it.
                                        mvo.PendingAttributeValueRemovals.Add(mvoValue);
                                    }
                                    else if (csoValue != null && mvoValue == null)
                                    {
                                        // no MVO value set, but we have a CSO value, so set the MVO value.
                                        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                                        {
                                            MetaverseObject = mvo,
                                            Attribute = syncRuleMapping.TargetMetaverseAttribute,
                                            AttributeId = syncRuleMapping.TargetMetaverseAttribute.Id,
                                            DateTimeValue = csoValue.DateTimeValue
                                        });
                                    }
                                    else if (csoValue != null && mvoValue != null && mvoValue.DateTimeValue != csoAttributeValue.DateTimeValue)
                                    {
                                        // there are both MVO and CSO values, but they're different, update the MVO from the CSO.
                                        mvo.PendingAttributeValueRemovals.Add(mvoValue);
                                        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                                        {
                                            MetaverseObject = mvo,
                                            Attribute = syncRuleMapping.TargetMetaverseAttribute,
                                            AttributeId = syncRuleMapping.TargetMetaverseAttribute.Id,
                                            DateTimeValue = csoValue.DateTimeValue
                                        });
                                    }
                                    break;
                                }

                                case AttributeDataType.Binary:
                                {
                                    // find values on the MVO of type binary that aren't on the CSO and remove them
                                    var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
                                        mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id &&
                                        !csoAttributeValues.Any(csoav =>
                                            csoav.ByteValue != null && Utilities.Utilities.AreByteArraysTheSame(csoav.ByteValue, mvoav.ByteValue)));
                                    mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

                                    // find values on the CSO of type byte that aren't on the MVO according to the sync rule mapping.
                                    var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav =>
                                        csoav.AttributeId == source.ConnectedSystemAttributeId.Value &&
                                        !mvo.AttributeValues.Any(mvoav =>
                                            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id &&
                                            Utilities.Utilities.AreByteArraysTheSame(mvoav.ByteValue, csoav.ByteValue)));

                                    // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
                                    foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
                                    {
                                        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                                        {
                                            MetaverseObject = mvo,
                                            Attribute = syncRuleMapping.TargetMetaverseAttribute,
                                            AttributeId = syncRuleMapping.TargetMetaverseAttribute.Id,
                                            ByteValue = newCsoNewAttributeValue.ByteValue
                                        });
                                    }
                                    break;
                                }

                                case AttributeDataType.Reference:
                                {
                                    // find reference values on the MVO that aren't on the CSO and remove.
                                    var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
                                        mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id &&
                                        mvoav.ReferenceValue != null &&
                                        !csoAttributeValues.Any(csoav => csoav.ReferenceValue is { MetaverseObject: not null } && csoav.ReferenceValue.MetaverseObject.Id ==  mvoav.ReferenceValue.Id));
                                    mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

                                    // find values on the CSO of type reference that aren't on the MVO according to the sync rule mapping.
                                    var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav =>
                                        csoav.AttributeId == source.ConnectedSystemAttributeId.Value &&
                                        csoav.ReferenceValue is { MetaverseObject: not null } &&
                                        !mvo.AttributeValues.Any(mvoav =>
                                            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id &&
                                            mvoav.ReferenceValue != null && mvoav.ReferenceValue.Id.Equals(csoav.ReferenceValue.MetaverseObject.Id)));

                                    // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
                                    foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
                                    {
                                        if (newCsoNewAttributeValue.ReferenceValue?.MetaverseObject == null)
                                            continue;

                                        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                                        {
                                            MetaverseObject = mvo,
                                            Attribute = syncRuleMapping.TargetMetaverseAttribute,
                                            AttributeId = syncRuleMapping.TargetMetaverseAttribute.Id,
                                            ReferenceValue = newCsoNewAttributeValue.ReferenceValue.MetaverseObject
                                        });
                                    }
                                    break;
                                }

                                case AttributeDataType.Guid:
                                {
                                    // find values on the MVO of type guid that aren't on the CSO and remove them.
                                    var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
                                        mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id &&
                                        !csoAttributeValues.Any(csoav => csoav.GuidValue.HasValue && csoav.GuidValue.Equals(mvoav.GuidValue)));
                                    mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

                                    // find values on the CSO of type guid that aren't on the MVO according to the sync rule mapping.
                                    var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav =>
                                        csoav.AttributeId == source.ConnectedSystemAttributeId.Value &&
                                        !mvo.AttributeValues.Any(mvoav =>
                                            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id &&
                                            mvoav.GuidValue.HasValue && mvoav.GuidValue.Equals(csoav.GuidValue)));

                                    // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
                                    foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
                                    {
                                        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                                        {
                                            MetaverseObject = mvo,
                                            Attribute = syncRuleMapping.TargetMetaverseAttribute,
                                            AttributeId = syncRuleMapping.TargetMetaverseAttribute.Id,
                                            GuidValue = newCsoNewAttributeValue.GuidValue
                                        });
                                    }
                                    break;
                                }

                                case AttributeDataType.Boolean:
                                {
                                    var csoValue = connectedSystemObject.AttributeValues.SingleOrDefault(csoav => csoav.AttributeId == source.ConnectedSystemAttributeId.Value);
                                    var mvoValue = mvo.AttributeValues.SingleOrDefault(mvoav => mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id);

                                    if (mvoValue != null && csoValue == null)
                                    {
                                        // there is a value on the MVO that isn't on the CSO. remove it.
                                        mvo.PendingAttributeValueRemovals.Add(mvoValue);
                                    }
                                    else if (csoValue != null && mvoValue == null)
                                    {
                                        // no MVO value set, but we have a CSO value, so set the MVO value.
                                        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                                        {
                                            MetaverseObject = mvo,
                                            Attribute = syncRuleMapping.TargetMetaverseAttribute,
                                            AttributeId = syncRuleMapping.TargetMetaverseAttribute.Id,
                                            BoolValue = csoValue.BoolValue
                                        });
                                    }
                                    else if (csoValue != null && mvoValue != null && mvoValue.BoolValue != csoAttributeValue.BoolValue)
                                    {
                                        // there are both MVO and CSO values, but they're different, update the MVO from the CSO.
                                        mvo.PendingAttributeValueRemovals.Add(mvoValue);
                                        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                                        {
                                            MetaverseObject = mvo,
                                            Attribute = syncRuleMapping.TargetMetaverseAttribute,
                                            AttributeId = syncRuleMapping.TargetMetaverseAttribute.Id,
                                            BoolValue = csoValue.BoolValue
                                        });
                                    }
                                    break;
                                }
                                
                                case AttributeDataType.NotSet:
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }
                    }
                    else
                    {
                        // there are no CSO values for this attribute. reflect this by deleting all MVO attribute values for this attribute.
                        var mvoAttributeValuesToDelete = connectedSystemObject.MetaverseObject.AttributeValues.Where(q => q.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id);
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
}