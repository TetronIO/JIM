using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
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
    /// <param name="attributeName">The name of the attribute in the connected system. Must match what's in the persisted schema in JIM.</param>
    /// <param name="connectedSystemObjectTypesData">The mocked database table in Jim for the Connected System Objects.</param>
    /// <exception cref="NotSupportedException">Will be thrown for unsupported attribute data types./</exception>
    public static void ValidateImportAttributesForEquality(
        ConnectedSystemObject connectedSystemObject,
        ConnectedSystemImportObject connectedSystemImportObject,
        MockAttributeName attributeName,
        IEnumerable<ConnectedSystemObjectType> connectedSystemObjectTypesData)
    {
        Assert.That(connectedSystemObject, Is.Not.Null);
        Assert.That(connectedSystemObject.AttributeValues, Is.Not.Null);
        Assert.That(connectedSystemImportObject, Is.Not.Null);
        Assert.That(connectedSystemImportObject.Attributes, Is.Not.Null);

        var csoAttributeValues = connectedSystemObject.AttributeValues.Where(q => q.Attribute.Name == attributeName.ToString()).ToList();
        Assert.That(csoAttributeValues, Is.Not.Null);

        var csioAttribute = connectedSystemImportObject.Attributes.SingleOrDefault(q => q.Name == attributeName.ToString());
        Assert.That(csioAttribute, Is.Not.Null);

        // look up the attribute in the ConnectedSystemObjectTypesData list and validate that the Connected System Object that has been built, is compliant with the schema.
        var schemaObjectType = connectedSystemObjectTypesData.Single(q => q.Name.Equals(connectedSystemImportObject.ObjectType, StringComparison.InvariantCultureIgnoreCase));
        var schemaAttribute = schemaObjectType.Attributes.Single(q => q.Name.Equals(attributeName.ToString(), StringComparison.InvariantCultureIgnoreCase));

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
        return new MetaverseObject {
            Id = Guid.Parse("25441317-D01C-47DE-BA69-47EEEFD09DC4")
        };
    }

    public static List<ConnectedSystem> GetConnectedSystemData()
    {
        return new List<ConnectedSystem>
        {
            new()
            {
                Id = 1,
                Name = "Dummy System"
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
                Name = "Dummy Full Import",
                RunType = ConnectedSystemRunType.FullImport,
                ConnectedSystemId = 1
            }
        };
    }

    public static List<ConnectedSystemObjectType> GetConnectedSystemObjectTypeData()
    {
        return new List<ConnectedSystemObjectType>
        {
            new ()
            {
                Id = 1,
                Name = "User",
                ConnectedSystemId = 1,
                Selected = true,
                Attributes = new List<ConnectedSystemObjectTypeAttribute>
                {
                    new()
                    {
                        // mimicking a system identifier for the object in a connected system.
                        // this is intended to be unique for each object in the connected system.
                        // we use the term "External ID" for this in Jim.
                        Id = (int)MockAttributeName.HR_ID,
                        Name = MockAttributeName.HR_ID.ToString(),
                        Type = AttributeDataType.Guid,
                        IsExternalId = true,
                        Selected = true
                    },
                    new()
                    {
                        // mimicking the organisational unique and immutable identifier for a person in the organisation.
                        // should be unique, but any Senior Identity Engineer will most likely have stories to tell about HR re-issuing, or changing employee ids.
                        // intended to be used as the correlating attribute for Metaverse to Connected System object joins.
                        Id = (int)MockAttributeName.EMPLOYEE_ID,
                        Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                        Type = AttributeDataType.Number,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.DISPLAY_NAME,
                        Name = MockAttributeName.DISPLAY_NAME.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.EMAIL_ADDRESS,
                        Name = MockAttributeName.EMAIL_ADDRESS.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.START_DATE,
                        Name = MockAttributeName.START_DATE.ToString(),
                        Type = AttributeDataType.DateTime,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.ROLE,
                        Name = MockAttributeName.ROLE.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.MANAGER,
                        Name = MockAttributeName.MANAGER.ToString(),
                        Type = AttributeDataType.Reference,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.QUALIFICATIONS,
                        Name = MockAttributeName.QUALIFICATIONS.ToString(),
                        Type = AttributeDataType.Text,
                        AttributePlurality = AttributePlurality.MultiValued,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.PROFILE_PICTURE_BYTES,
                        Name = MockAttributeName.PROFILE_PICTURE_BYTES.ToString(),
                        Type = AttributeDataType.Binary,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.CONTRACTED_WEEKLY_HOURS,
                        Name = MockAttributeName.CONTRACTED_WEEKLY_HOURS.ToString(),
                        Type = AttributeDataType.Number,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.LOCATION_ID,
                        Name = MockAttributeName.LOCATION_ID.ToString(),
                        Type = AttributeDataType.Guid,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.END_DATE,
                        Name = MockAttributeName.END_DATE.ToString(),
                        Type = AttributeDataType.DateTime,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.LEAVER,
                        Name = MockAttributeName.LEAVER.ToString(),
                        Type = AttributeDataType.Boolean,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.COURSE_COUNT,
                        Name = MockAttributeName.COURSE_COUNT.ToString(),
                        Type = AttributeDataType.Number,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.COURSE_END_DATE,
                        Name = MockAttributeName.COURSE_END_DATE.ToString(),
                        Type = AttributeDataType.DateTime,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.CURRENT_COURSE_NAME,
                        Name = MockAttributeName.CURRENT_COURSE_NAME.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.CURRENT_COURSE_ID,
                        Name = MockAttributeName.CURRENT_COURSE_ID.ToString(),
                        Type = AttributeDataType.Guid,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.CURRENT_COURSE_ACTIVE,
                        Name = MockAttributeName.CURRENT_COURSE_ACTIVE.ToString(),
                        Type = AttributeDataType.Boolean,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.CURRENT_COURSE_TUTOR,
                        Name = MockAttributeName.CURRENT_COURSE_TUTOR.ToString(),
                        Type = AttributeDataType.Reference,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.PROXY_ADDRESSES,
                        Name = MockAttributeName.PROXY_ADDRESSES.ToString(),
                        Type = AttributeDataType.Text,
                        AttributePlurality = AttributePlurality.MultiValued,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.COMPLETED_COURSE_IDS,
                        Name = MockAttributeName.COMPLETED_COURSE_IDS.ToString(),
                        Type = AttributeDataType.Number,
                        AttributePlurality = AttributePlurality.MultiValued,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.PREVIOUS_LOCATION_IDS,
                        Name = MockAttributeName.PREVIOUS_LOCATION_IDS.ToString(),
                        Type = AttributeDataType.Guid,
                        AttributePlurality = AttributePlurality.MultiValued,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.CERTIFICATES,
                        Name = MockAttributeName.CERTIFICATES.ToString(),
                        Type = AttributeDataType.Binary,
                        AttributePlurality = AttributePlurality.MultiValued,
                        Selected = true
                    }
                }
            },
            new ()
            {
                Id = 2,
                Name = "Group",
                ConnectedSystemId = 1,
                Selected = true,
                Attributes = new List<ConnectedSystemObjectTypeAttribute>
                {
                    new()
                    {
                        // mimicking a system identifier for the object in a connected system.
                        // this is intended to be unique for each object in the connected system.
                        // we use the term "External ID" for this in Jim.
                        Id = (int)MockAttributeName.GROUP_UID,
                        Name = MockAttributeName.GROUP_UID.ToString(),
                        Type = AttributeDataType.Guid,
                        IsExternalId = true,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.DISPLAY_NAME,
                        Name = MockAttributeName.DISPLAY_NAME.ToString(),
                        Type = AttributeDataType.Text,
                        Selected = true
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.MEMBER,
                        Name = MockAttributeName.MEMBER.ToString(),
                        Type = AttributeDataType.Reference,
                        AttributePlurality = AttributePlurality.MultiValued,
                        Selected = true
                    }
                }
            }
        };
    }

    public static List<ConnectedSystemPartition> GetConnectedSystemPartitionData()
    {
        return new List<ConnectedSystemPartition>();
    }

    public static List<Activity> GetActivityData(ConnectedSystemRunType connectedSystemRunType)
    {
        return new List<Activity>
        {
            new ()
            {
                Id = Guid.NewGuid(),
                TargetName = $"Mock {connectedSystemRunType.ToString().SplitOnCapitalLetters()} Execution",
                Status = ActivityStatus.InProgress,
                ConnectedSystemRunType = connectedSystemRunType,
                InitiatedBy = GetInitiatedBy(),
                InitiatedByName = "Joe Bloggs"
            }
        };
    }
}