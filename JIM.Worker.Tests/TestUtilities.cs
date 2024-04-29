using JIM.Models.Core;
using JIM.Models.Staging;
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
                // checking that the counts are the same, and that the cso values exist in the Connected System Import Object value, and visa verse (i.e. are the two collections the same).
                Assert.That(csoAttributeValues, Has.Count.EqualTo(csioAttribute.DateTimeValues.Count));
                foreach (var csoDateTimeValue in csoAttributeValues)
                    Assert.That(csioAttribute.DateTimeValues.Any(q => q == csoDateTimeValue.DateTimeValue));
                foreach (var csioDateTimeValue in csioAttribute.DateTimeValues)
                    Assert.That(csoAttributeValues.Any(q => q.DateTimeValue == csioDateTimeValue));
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
}