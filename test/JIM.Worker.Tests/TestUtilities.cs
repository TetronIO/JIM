using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Utilities;
using JIM.Worker.Tests.Models;

namespace JIM.Worker.Tests;

public static class TestUtilities
{
    /// <summary>
    /// Asserts that a Connected System Object attribute value is the same as what was received from a Connector on import via a Connected System Import Object.
    /// Essentially, to test that the sync logic has correctly persisted the connector changes to the Connected System in Jim. 
    /// </summary>
    /// <param name="connectedSystemObject">The persisted version of the object in Jim that correlates with the object in the connected system.</param>
    /// <param name="connectedSystemImportObject">The Connected System Import Object that a Connector will return after importing data from a Connected System.</param>
    /// <param name="sourceSystemAttributeNames">The name of the attribute in the connected system. Must match what's in the persisted schema in JIM.</param>
    /// <param name="connectedSystemObjectTypesData">The mocked database table in Jim for the Connected System Objects.</param>
    /// <exception cref="NotSupportedException">Will be thrown for unsupported attribute data types./</exception>
    public static void ValidateImportAttributesForEquality(
        ConnectedSystemObject connectedSystemObject,
        ConnectedSystemImportObject connectedSystemImportObject,
        MockSourceSystemAttributeNames sourceSystemAttributeNames,
        IEnumerable<ConnectedSystemObjectType> connectedSystemObjectTypesData)
    {
        Assert.That(connectedSystemObject, Is.Not.Null);
        Assert.That(connectedSystemObject.AttributeValues, Is.Not.Null);
        Assert.That(connectedSystemImportObject, Is.Not.Null);
        Assert.That(connectedSystemImportObject.Attributes, Is.Not.Null);

        var csoAttributeValues = connectedSystemObject.AttributeValues.Where(q => q.Attribute.Name == sourceSystemAttributeNames.ToString()).ToList();
        Assert.That(csoAttributeValues, Is.Not.Null);

        var csioAttribute = connectedSystemImportObject.Attributes.SingleOrDefault(q => q.Name == sourceSystemAttributeNames.ToString());
        Assert.That(csioAttribute, Is.Not.Null);

        // look up the attribute in the ConnectedSystemObjectTypesData list and validate that the Connected System Object that has been built, is compliant with the schema.
        var schemaObjectType = connectedSystemObjectTypesData.Single(q => q.Name.Equals(connectedSystemImportObject.ObjectType, StringComparison.InvariantCultureIgnoreCase));
        var schemaAttribute = schemaObjectType.Attributes.Single(q => q.Name.Equals(sourceSystemAttributeNames.ToString(), StringComparison.InvariantCultureIgnoreCase));

        // make sure schema attributes that are single valued, only have a single value
        if (schemaAttribute.AttributePlurality == AttributePlurality.SingleValued)
            Assert.That(csoAttributeValues, Has.Count.LessThanOrEqualTo(1), $"Single-valued attributes can only have 0 or 1 value. There are {csoAttributeValues.Count}.");

        switch (schemaAttribute.Type)
        {
            case AttributeDataType.Boolean:
                Assert.That(csoAttributeValues[0].BoolValue, Is.EqualTo(csioAttribute.BoolValue));
                break;
            case AttributeDataType.Guid:
                // checking that the counts are the same, and that the cso values exist in the Connected System Import Object value, and visa verse (i.e. are the two collections the same)
                Assert.That(csoAttributeValues, Has.Count.EqualTo(csioAttribute.GuidValues.Count));
                foreach (var csoGuidValue in csoAttributeValues)
                    Assert.That(csioAttribute.GuidValues.Any(q => q == csoGuidValue.GuidValue));
                foreach (var csioGuidValue in csioAttribute.GuidValues)
                    Assert.That(csoAttributeValues.Any(q => q.GuidValue == csioGuidValue));
                break;
            case AttributeDataType.Number:
                // checking that the counts are the same, and that the cso values exist in the csio value, and visa verse (i.e. are the two collections the same)
                Assert.That(csoAttributeValues, Has.Count.EqualTo(csioAttribute.IntValues.Count));
                foreach (var csoIntValue in csoAttributeValues)
                    Assert.That(csioAttribute.IntValues.Any(q => q == csoIntValue.IntValue));
                foreach (var csioIntValue in csioAttribute.IntValues)
                    Assert.That(csoAttributeValues.Any(q => q.IntValue == csioIntValue));
                break;
            case AttributeDataType.Text:
                // checking that the counts are the same, and that the cso values exist in the Connected System Import Object value, and visa verse (i.e. are the two collections the same).
                Assert.That(csoAttributeValues, Has.Count.EqualTo(csioAttribute.StringValues.Count));
                foreach (var csoStringValue in csoAttributeValues)
                    Assert.That(csioAttribute.StringValues.Any(q => q == csoStringValue.StringValue));
                foreach (var csioStringValue in csioAttribute.StringValues)
                    Assert.That(csoAttributeValues.Any(q => q.StringValue == csioStringValue));
                break;
            case AttributeDataType.DateTime:
                Assert.That(csoAttributeValues[0].DateTimeValue, Is.EqualTo(csioAttribute.DateTimeValue));
                break;
            case AttributeDataType.Binary:
                // this is quite crude, and could be improved.
                // checking that the counts are the same, and that the cso values exist in the Connected System Import Object value, and visa verse (i.e. are the two collections the same).
                Assert.That(csoAttributeValues, Has.Count.EqualTo(csioAttribute.ByteValues.Count));
                foreach (var csoByteValue in csoAttributeValues)
                    Assert.That(csioAttribute.ByteValues.Any(q => q == csoByteValue.ByteValue));
                foreach (var csioByteValue in csioAttribute.ByteValues)
                    Assert.That(csoAttributeValues.Any(q => q.ByteValue?.Length == csioByteValue.Length));
                break;
            case AttributeDataType.Reference:
            case AttributeDataType.NotSet:
            default:
                throw new NotSupportedException(
                    $"AttributeDataType of {schemaAttribute.Type} is supported by this method.");
        }
    }

    public static void SetEnvironmentVariables()
    {
        // environment variables needed by JIM, even though they won't be used
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseHostname, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseName, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseUsername, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabasePassword, "dummy");
    }

    public static MetaverseObject GetInitiatedBy()
    {
        var user = new MetaverseObject
        {
            Id = Guid.Parse("25441317-D01C-47DE-BA69-47EEEFD09DC4"),
            Type = new MetaverseObjectType { Id = 1, Name = "User" }
        };
        // Add Display Name attribute for proper activity tracking
        user.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = user,
            Attribute = new MetaverseAttribute { Id = 1, Name = Constants.BuiltInAttributes.DisplayName },
            StringValue = "Test User"
        });
        return user;
    }

    public static List<ConnectedSystem> GetConnectedSystemData()
    {
        return new List<ConnectedSystem>
        {
            new()
            {
                Id = 1,
                Name = "Dummy Source System",
                // Use SyncRule mode since tests add matching rules to SyncRules
                ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
            },
            new()
            {
                Id = 2,
                Name = "Dummy Target System",
                // Use SyncRule mode since tests add matching rules to SyncRules
                ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
            }
        };
    }

    public static List<ConnectedSystemRunProfile> GetConnectedSystemRunProfileData()
    {
        return new List<ConnectedSystemRunProfile>
        {
            new()
            {
                Id = 1,
                Name = "Dummy Source System Full Import",
                RunType = ConnectedSystemRunType.FullImport,
                ConnectedSystemId = 1
            },
            new()
            {
                Id = 2,
                Name = "Dummy Source System Full Sync",
                RunType = ConnectedSystemRunType.FullSynchronisation,
                ConnectedSystemId = 1
            },
            new()
            {
                Id = 3,
                Name = "Dummy Target System Full Import",
                RunType = ConnectedSystemRunType.FullImport,
                ConnectedSystemId = 2
            },
            new()
            {
                Id = 4,
                Name = "Dummy Target System Full Sync",
                RunType = ConnectedSystemRunType.FullSynchronisation,
                ConnectedSystemId = 2
            },
            new()
            {
                Id = 5,
                Name = "Dummy Target System Export",
                RunType = ConnectedSystemRunType.Export,
                ConnectedSystemId = 2
            },
            new()
            {
                Id = 6,
                Name = "Dummy Source System Delta Sync",
                RunType = ConnectedSystemRunType.DeltaSynchronisation,
                ConnectedSystemId = 1
            },
            new()
            {
                Id = 7,
                Name = "Dummy Target System Delta Sync",
                RunType = ConnectedSystemRunType.DeltaSynchronisation,
                ConnectedSystemId = 2
            }
        };
    }

    public static List<ConnectedSystemObject> GetConnectedSystemObjectData()
    {
        var csTypes = GetConnectedSystemObjectTypeData();
        var csUserType = csTypes.Single(t => t.Name == "SOURCE_USER");
        
        var csos = new List<ConnectedSystemObject>();
        var cso1 = new ConnectedSystemObject
        {
            Id = Guid.Parse("36B5F294-B602-4508-A2C4-1082C9D80B64"),
            ConnectedSystemId = 1, // mock hr system
            Type = csUserType,
            TypeId = csUserType.Id 
        };
        cso1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.HR_ID,
                Name = MockSourceSystemAttributeNames.HR_ID.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.HR_ID,
            GuidValue = Guid.Parse("A98D00CB-FB7F-48BE-A093-DF79E193836E")
        });
        cso1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.EMPLOYEE_ID,
                Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.EMPLOYEE_ID,
            StringValue = "E123"
        });
        cso1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER,
                Name = MockSourceSystemAttributeNames.EMPLOYEE_NUMBER.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER,
            IntValue = 123
        });
        cso1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
                Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
            StringValue = "Joe Bloggs"
        });
        cso1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.START_DATE,
                Name = MockSourceSystemAttributeNames.START_DATE.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.START_DATE,
            DateTimeValue = DateTime.Parse("2021-09-01")
        });
        cso1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.EMPLOYEE_TYPE,
                Name = MockSourceSystemAttributeNames.EMPLOYEE_TYPE.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.EMPLOYEE_TYPE,
            StringValue = "FTE"
        });
        cso1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.LEAVER,
                Name = MockSourceSystemAttributeNames.LEAVER.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.LEAVER,
            BoolValue = false
        });
        cso1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.ROLE,
                Name = MockSourceSystemAttributeNames.ROLE.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.ROLE,
            StringValue = "Manager"
        });
        cso1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
                Name = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
            StringValue = "Excel 101"
        });
        cso1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
                Name = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
            StringValue = "Outlook 101"
        });
        cso1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
                Name = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
            StringValue = "Workplace Safety 101"
        });
        
        var cso2 = new ConnectedSystemObject
        {
            Id = Guid.Parse("EDF6952E-FCF6-4D5B-8BDE-5D901D886E3D"),
            ConnectedSystemId = 1, // mock hr system
            Type = csUserType,
            TypeId = csUserType.Id
        };
        cso2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.HR_ID,
                Name = MockSourceSystemAttributeNames.HR_ID.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.HR_ID,
            GuidValue = Guid.Parse("E1A7D0DF-6C87-4EE7-ADDD-9BA084093A4B")
        });
        cso2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.EMPLOYEE_ID,
                Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.EMPLOYEE_ID,
            StringValue = "E124"
        });
        cso2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
                Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
            StringValue = "Jane Wright"
        });
        cso2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.START_DATE,
                Name = MockSourceSystemAttributeNames.START_DATE.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.START_DATE,
            DateTimeValue = DateTime.Parse("2022-03-05")
        });
        cso2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.EMPLOYEE_TYPE,
                Name = MockSourceSystemAttributeNames.EMPLOYEE_TYPE.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.EMPLOYEE_TYPE,
            StringValue = "FTE"
        });
        cso2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.LEAVER,
                Name = MockSourceSystemAttributeNames.LEAVER.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.LEAVER,
            BoolValue = false
        });
        cso2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.ROLE,
                Name = MockSourceSystemAttributeNames.ROLE.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.ROLE,
            StringValue = "System Admin"
        });
        cso2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
                Name = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
            StringValue = "System Admin 101"
        });
        cso2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
                Name = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
            StringValue = "Remote Desktop 101"
        });
        cso2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
                Name = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
            StringValue = "Workplace Safety 101"
        });
        cso2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute()
            {
                Id = (int)MockSourceSystemAttributeNames.MANAGER,
                Name = MockSourceSystemAttributeNames.MANAGER.ToString()
            },
            AttributeId = (int)MockSourceSystemAttributeNames.MANAGER,
            ReferenceValue = cso1,
            ReferenceValueId = cso1.Id
        });
        
        csos.Add(cso1);
        csos.Add(cso2);
        return csos;
    }

    public static List<ConnectedSystemObjectType> GetConnectedSystemObjectTypeData()
    {
        return new List<ConnectedSystemObjectType>
        {
            new ()
            {
                Id = 1,
                Name = "SOURCE_USER",
                ConnectedSystemId = 1,
                Selected = true,
                Attributes = new List<ConnectedSystemObjectTypeAttribute>
                {
                    new()
                    {
                        // mimicking a system identifier for the object in a connected system.
                        // this is intended to be unique for each object in the connected system.
                        // we use the term "External ID" for this in Jim.
                        Id = (int)MockSourceSystemAttributeNames.HR_ID,
                        Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                        Type = AttributeDataType.Guid,
                        IsExternalId = true,
                        Selected = true
                    },
                    new()
                    {
                        // mimicking an organisational unique and immutable identifier for a person in the organisation.
                        // should be unique, but any Senior Identity Engineer will most likely have stories to tell about HR re-issuing, or changing employee ids.
                        // intended to be used as a correlating attribute for Metaverse to Connected System object joins.
                        Id = (int)MockSourceSystemAttributeNames.EMPLOYEE_ID,
                        Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        // mimicking another organisational unique and immutable identifier for a person in the organisation.
                        // should be unique, but any Senior Identity Engineer will most likely have stories to tell about HR re-issuing, or changing employee numbers.
                        // intended to be used as a correlating attribute for Metaverse to Connected System object joins.
                        Id = (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER,
                        Name = MockSourceSystemAttributeNames.EMPLOYEE_NUMBER.ToString(),
                        Type = AttributeDataType.Number,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.EMPLOYEE_TYPE,
                        Name = MockSourceSystemAttributeNames.EMPLOYEE_TYPE.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
                        Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.EMAIL_ADDRESS,
                        Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.START_DATE,
                        Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                        Type = AttributeDataType.DateTime,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.ROLE,
                        Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.MANAGER,
                        Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                        Type = AttributeDataType.Reference,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.QUALIFICATIONS,
                        Name = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString(),
                        Type = AttributeDataType.Text,
                        AttributePlurality = AttributePlurality.MultiValued,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES,
                        Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                        Type = AttributeDataType.Binary,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS,
                        Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                        Type = AttributeDataType.Number,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.LOCATION_ID,
                        Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                        Type = AttributeDataType.Guid,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.END_DATE,
                        Name = MockSourceSystemAttributeNames.END_DATE.ToString(),
                        Type = AttributeDataType.DateTime,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.LEAVER,
                        Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                        Type = AttributeDataType.Boolean,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.COURSE_COUNT,
                        Name = MockSourceSystemAttributeNames.COURSE_COUNT.ToString(),
                        Type = AttributeDataType.Number,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.COURSE_END_DATE,
                        Name = MockSourceSystemAttributeNames.COURSE_END_DATE.ToString(),
                        Type = AttributeDataType.DateTime,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.CURRENT_COURSE_NAME,
                        Name = MockSourceSystemAttributeNames.CURRENT_COURSE_NAME.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.CURRENT_COURSE_ID,
                        Name = MockSourceSystemAttributeNames.CURRENT_COURSE_ID.ToString(),
                        Type = AttributeDataType.Guid,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.CURRENT_COURSE_ACTIVE,
                        Name = MockSourceSystemAttributeNames.CURRENT_COURSE_ACTIVE.ToString(),
                        Type = AttributeDataType.Boolean,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.CURRENT_COURSE_TUTOR,
                        Name = MockSourceSystemAttributeNames.CURRENT_COURSE_TUTOR.ToString(),
                        Type = AttributeDataType.Reference,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.PROXY_ADDRESSES,
                        Name = MockSourceSystemAttributeNames.PROXY_ADDRESSES.ToString(),
                        Type = AttributeDataType.Text,
                        AttributePlurality = AttributePlurality.MultiValued,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.COMPLETED_COURSE_IDS,
                        Name = MockSourceSystemAttributeNames.COMPLETED_COURSE_IDS.ToString(),
                        Type = AttributeDataType.Number,
                        AttributePlurality = AttributePlurality.MultiValued,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.PREVIOUS_LOCATION_IDS,
                        Name = MockSourceSystemAttributeNames.PREVIOUS_LOCATION_IDS.ToString(),
                        Type = AttributeDataType.Guid,
                        AttributePlurality = AttributePlurality.MultiValued,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.CERTIFICATES,
                        Name = MockSourceSystemAttributeNames.CERTIFICATES.ToString(),
                        Type = AttributeDataType.Binary,
                        AttributePlurality = AttributePlurality.MultiValued,
                        Selected = true
                    }
                }
            },
            new ()
            {
                Id = 2,
                Name = "SOURCE_GROUP",
                ConnectedSystemId = 1,
                Selected = true,
                Attributes = new List<ConnectedSystemObjectTypeAttribute>
                {
                    new()
                    {
                        // mimicking a system identifier for the object in a connected system.
                        // this is intended to be unique for each object in the connected system.
                        // we use the term "External ID" for this in Jim.
                        Id = (int)MockSourceSystemAttributeNames.GROUP_UID,
                        Name = MockSourceSystemAttributeNames.GROUP_UID.ToString(),
                        Type = AttributeDataType.Guid,
                        IsExternalId = true,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
                        Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockSourceSystemAttributeNames.MEMBER,
                        Name = MockSourceSystemAttributeNames.MEMBER.ToString(),
                        Type = AttributeDataType.Reference,
                        AttributePlurality = AttributePlurality.MultiValued,
                        Selected = true
                    }
                }
            },
            new ()
            {
                Id = 3,
                Name = "TARGET_USER",
                ConnectedSystemId = 2,
                Selected = true,
                Attributes = new List<ConnectedSystemObjectTypeAttribute>
                {
                    new()
                    {
                        // mimicking a system identifier for the object in a connected system.
                        // this is intended to be unique for each object in the connected system.
                        // we use the term "External ID" for this in Jim.
                        Id = (int)MockTargetSystemAttributeNames.ObjectGuid,
                        Name = MockTargetSystemAttributeNames.ObjectGuid.ToString(),
                        Type = AttributeDataType.Guid,
                        IsExternalId = true,
                        Selected = true
                    },
                    new()
                    {
                        // mimicking the organisational unique and immutable identifier for a person in the organisation.
                        // should be unique, but any Senior Identity Engineer will most likely have stories to tell about HR re-issuing, or changing employee ids.
                        // intended to be used as the correlating attribute for Metaverse to Connected System object joins.
                        Id = (int)MockTargetSystemAttributeNames.EmployeeId,
                        Name = MockTargetSystemAttributeNames.EmployeeId.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockTargetSystemAttributeNames.SamAccountName,
                        Name = MockTargetSystemAttributeNames.SamAccountName.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockTargetSystemAttributeNames.UserPrincipalName,
                        Name = MockTargetSystemAttributeNames.UserPrincipalName.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockTargetSystemAttributeNames.Mail,
                        Name = MockTargetSystemAttributeNames.Mail.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockTargetSystemAttributeNames.DisplayName,
                        Name = MockTargetSystemAttributeNames.DisplayName.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockTargetSystemAttributeNames.Manager,
                        Name = MockTargetSystemAttributeNames.Manager.ToString(),
                        Type = AttributeDataType.Reference,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockTargetSystemAttributeNames.JobTitle,
                        Name = MockTargetSystemAttributeNames.JobTitle.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockTargetSystemAttributeNames.UserAccountControl,
                        Name = MockTargetSystemAttributeNames.UserAccountControl.ToString(),
                        Type = AttributeDataType.Number,
                        Selected = true
                    }
                }
            },
        };
    }

    /// <summary>
    /// Returns test user and group metaverse object types.
    /// </summary>
    public static List<MetaverseObjectType> GetMetaverseObjectTypeData()
    {
        return new List<MetaverseObjectType>
        {
            new ()
            {
                Id = 1,
                Name = "User",
                BuiltIn = true,
                Attributes = new List<MetaverseAttribute>
                {
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.HrId,
                        Name = "HR ID",
                        Type = AttributeDataType.Guid,
                        AttributePlurality = AttributePlurality.SingleValued,
                        BuiltIn = false
                    },
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.EmployeeId,
                        Name = Constants.BuiltInAttributes.EmployeeId,
                        Type = AttributeDataType.Text,
                        AttributePlurality = AttributePlurality.SingleValued,
                        BuiltIn = true
                    },
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.EmployeeNumber,
                        Name = Constants.BuiltInAttributes.EmployeeNumber,
                        Type = AttributeDataType.Number,
                        AttributePlurality = AttributePlurality.SingleValued,
                        BuiltIn = true
                    },
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.DisplayName,
                        Name = Constants.BuiltInAttributes.DisplayName,
                        Type = AttributeDataType.Text,
                        AttributePlurality = AttributePlurality.SingleValued,
                        BuiltIn = true
                    },
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.Email,
                        Name = Constants.BuiltInAttributes.Email,
                        Type = AttributeDataType.Text,
                        AttributePlurality = AttributePlurality.SingleValued,
                        BuiltIn = true
                    },
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.EmployeeStartDate,
                        Name = Constants.BuiltInAttributes.EmployeeStartDate,
                        Type = AttributeDataType.DateTime,
                        AttributePlurality = AttributePlurality.SingleValued,
                        BuiltIn = true
                    },
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.EmployeeEndDate,
                        Name = Constants.BuiltInAttributes.EmployeeEndDate,
                        Type = AttributeDataType.DateTime,
                        AttributePlurality = AttributePlurality.SingleValued,
                        BuiltIn = true
                    },
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.Manager,
                        Name = Constants.BuiltInAttributes.Manager,
                        Type = AttributeDataType.Reference,
                        AttributePlurality = AttributePlurality.SingleValued,
                        BuiltIn = true
                    },
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.LocationId,
                        Name = "Location Id",
                        Type = AttributeDataType.Guid,
                        AttributePlurality = AttributePlurality.SingleValued,
                        BuiltIn = false
                    },
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.Photo,
                        Name = Constants.BuiltInAttributes.Photo,
                        Type = AttributeDataType.Binary,
                        AttributePlurality = AttributePlurality.SingleValued,
                        BuiltIn = true
                    }
                }
            },
            new ()
            {
                Id = 2,
                Name = "Group",
                BuiltIn = true,
                Attributes = new List<MetaverseAttribute>
                {
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.AccountName,
                        Name = Constants.BuiltInAttributes.AccountName,
                        Type = AttributeDataType.Text,
                        AttributePlurality = AttributePlurality.SingleValued,
                        BuiltIn = true
                    },
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.DisplayName,
                        Name = Constants.BuiltInAttributes.DisplayName,
                        Type = AttributeDataType.Text,
                        AttributePlurality = AttributePlurality.SingleValued,
                        BuiltIn = true
                    },
                    new()
                    {
                        Id = (int)MockMetaverseAttributeName.Member,
                        Name = Constants.BuiltInAttributes.StaticMembers,
                        Type = AttributeDataType.Reference,
                        AttributePlurality = AttributePlurality.MultiValued,
                        BuiltIn = true
                    }
                }
            }
        };
    }

    public static List<MetaverseObject> GetMetaverseObjectData()
    {
        var mvTypes = GetMetaverseObjectTypeData();
        var mvUserType = mvTypes.Single(t => t.Name == "User");
        
        var mvo1 = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvUserType
        };
        
        mvo1.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = (int)MockMetaverseAttributeName.HrId,
            Attribute = mvUserType.Attributes.Single(a=>a.Id == (int)MockMetaverseAttributeName.HrId),
            GuidValue = Guid.Parse("A98D00CB-FB7F-48BE-A093-DF79E193836E")
        });
        
        mvo1.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = (int)MockMetaverseAttributeName.EmployeeId,
            Attribute = mvUserType.Attributes.Single(a=>a.Id == (int)MockMetaverseAttributeName.EmployeeId),
            StringValue = "E123"
        });
        
        mvo1.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = (int)MockMetaverseAttributeName.EmployeeNumber,
            Attribute = mvUserType.Attributes.Single(a=>a.Id == (int)MockMetaverseAttributeName.EmployeeNumber),
            IntValue = 123
        });
        
        mvo1.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = (int)MockMetaverseAttributeName.DisplayName,
            Attribute = mvUserType.Attributes.Single(a=>a.Id == (int)MockMetaverseAttributeName.DisplayName),
            StringValue = "joe bloggs"
        });
        
        return new List<MetaverseObject> { mvo1 };
    }

    public static List<ConnectedSystemPartition> GetConnectedSystemPartitionData()
    {
        return new List<ConnectedSystemPartition>();
    }

    /// <summary>
    /// Returns stub test user and group, inbound and outbound sync rules for individual unit tests to customise for specific scenarios.
    /// </summary>
    public static List<SyncRule> GetSyncRuleData()
    {
        var mvTypes = GetMetaverseObjectTypeData();
        var mvUserType = mvTypes.Single(t => t.Name == "User");
        var mvGroupType = mvTypes.Single(t => t.Name == "Group");

        var csTypes = GetConnectedSystemObjectTypeData();
        var csUserType = csTypes.Single(t => t.Name == "SOURCE_USER");
        var csGroupType = csTypes.Single(t => t.Name == "SOURCE_GROUP");
        
        return new List<SyncRule>
        {
            new()
            {
                Id = 1,
                ConnectedSystemId = 1,
                Name = "Dummy User Import Sync Rule 1",
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystemObjectTypeId = csUserType.Id,
                ConnectedSystemObjectType = csUserType,
                MetaverseObjectTypeId = mvUserType.Id,
                MetaverseObjectType = mvUserType
            },
            new()
            {
                Id = 2,
                ConnectedSystemId = 1,
                Name = "Dummy User Export Sync Rule 1",
                Direction = SyncRuleDirection.Export,
                Enabled = true,
                ConnectedSystemObjectTypeId = csUserType.Id,
                ConnectedSystemObjectType = csUserType,
                MetaverseObjectTypeId = mvUserType.Id,
                MetaverseObjectType = mvUserType
            },
            new()
            {
                Id = 3,
                ConnectedSystemId = 1,
                Name = "Dummy Group Import Sync Rule 1",
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystemObjectTypeId = csGroupType.Id,
                ConnectedSystemObjectType = csGroupType,
                MetaverseObjectTypeId = mvGroupType.Id,
                MetaverseObjectType = mvGroupType
            },
            new()
            {
                Id = 4,
                ConnectedSystemId = 1,
                Name = "Dummy Group Export Sync Rule 1",
                Direction = SyncRuleDirection.Export,
                Enabled = true,
                ConnectedSystemObjectTypeId = csGroupType.Id,
                ConnectedSystemObjectType = csGroupType,
                MetaverseObjectTypeId = mvGroupType.Id,
                MetaverseObjectType = mvGroupType
            }
        };
    }

    public static List<Activity> GetActivityData(ConnectedSystemRunType connectedSystemRunType, int runProfileId)
    {
        var testUser = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "User" }
        };
        return new List<Activity>
        {
            new ()
            {
                Id = Guid.NewGuid(),
                TargetName = $"Mock {connectedSystemRunType.ToString().SplitOnCapitalLetters()} Execution",
                Status = ActivityStatus.InProgress,
                ConnectedSystemRunProfileId = runProfileId,
                ConnectedSystemRunType = connectedSystemRunType,
                InitiatedByType = ActivityInitiatorType.User,
                InitiatedById = testUser.Id,
                InitiatedByMetaverseObject = testUser,
                InitiatedByName = "Joe Bloggs"
            }
        };
    }

    /// <summary>
    /// Returns the service settings required for sync processors.
    /// </summary>
    public static List<ServiceSetting> GetServiceSettingsData()
    {
        return new List<ServiceSetting>
        {
            new()
            {
                Key = "Sync.PageSize",
                DisplayName = "Sync Page Size",
                Category = ServiceSettingCategory.Synchronisation,
                ValueType = ServiceSettingValueType.Integer,
                DefaultValue = "1000",
                Value = null
            }
        };
    }

    /// <summary>
    /// Creates a test WorkerTask with the specified activity and initiator for use with SyncImportTaskProcessor.
    /// </summary>
    public static SynchronisationWorkerTask CreateTestWorkerTask(Activity activity, MetaverseObject? initiatedBy)
    {
        var workerTask = new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            Activity = activity,
            InitiatedByType = initiatedBy != null ? ActivityInitiatorType.User : ActivityInitiatorType.NotSet,
            InitiatedByMetaverseObject = initiatedBy,
            InitiatedById = initiatedBy?.Id,
            InitiatedByName = initiatedBy?.DisplayName
        };
        return workerTask;
    }
}