// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.SCIM;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Rejection classification for the SCIM Connector (Unique Value Generation, #242, release 4, decision 9): a 409
/// with <c>scimType</c> <c>uniqueness</c> is a value already in use, and the attribute is taken from the detail only
/// where the detail plainly names one.
/// </summary>
[TestFixture]
public class ScimUniquenessRejectionClassifierTests
{
    [TestCase("One or more of the attribute values are already in use or are reserved.", null, TestName = "Detail_Rfc7644Description_NamesNoAttribute")]
    [TestCase(null, null, TestName = "Detail_Absent_NamesNoAttribute")]
    [TestCase("The attribute 'userName' must be unique.", "userName", TestName = "Detail_AttributeKeywordWithQuotedName_IsThatName")]
    [TestCase("Uniqueness violated for attribute emails.value", "emails.value", TestName = "Detail_AttributeKeywordWithBareSubAttributePath_IsThatPath")]
    [TestCase("\"externalId\" must be unique", "externalId", TestName = "Detail_QuotedNameThenMustBeUnique_IsThatName")]
    [TestCase("'alice' is already in use", null, TestName = "Detail_QuotedValueThenInUse_NamesNoAttribute")]
    [TestCase("userName 'alice' is already in use", "userName", TestName = "Detail_NameThenQuotedValueThenInUse_IsThatName")]
    [TestCase("User 'alice' already exists", null, TestName = "Detail_ResourceTypeThenQuotedValue_NamesNoAttribute")]
    [TestCase("Conflict: duplicate resource", null, TestName = "Detail_NoAttributeWording_NamesNoAttribute")]
    [TestCase("The attribute 'userName' and the attribute 'externalId' must be unique.", null, TestName = "Detail_TwoAttributesNamed_NamesNoAttribute")]
    public void TryClassifyUniqueness_409Uniqueness_IsClassifiedWithTheAttributeTheDetailNamed(string? detail, string? expectedAttribute)
    {
        var classified = ScimExportErrorClassifier.TryClassifyUniqueness(409, "uniqueness", detail, out var attributeName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(classified, Is.True);
            Assert.That(attributeName, Is.EqualTo(expectedAttribute));
        }
    }

    [TestCase(409, null, TestName = "Conflict_WithoutScimType_IsNotClassified")]
    [TestCase(409, "mutability", TestName = "Conflict_OtherScimType_IsNotClassified")]
    [TestCase(400, "uniqueness", TestName = "BadRequest_WithUniquenessScimType_IsNotClassified")]
    [TestCase(null, "uniqueness", TestName = "NoStatus_IsNotClassified")]
    public void TryClassifyUniqueness_OtherRejection_IsNotClassified(int? statusCode, string? scimType)
    {
        var classified = ScimExportErrorClassifier.TryClassifyUniqueness(statusCode, scimType, "The attribute 'userName' must be unique.", out var attributeName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(classified, Is.False);
            Assert.That(attributeName, Is.Null);
        }
    }

    [Test]
    public void Classify_409Uniqueness_IsUniqueValueAlreadyInUse()
    {
        Assert.That(ScimExportErrorClassifier.Classify(409, "UNIQUENESS"), Is.EqualTo(ConnectedSystemExportErrorType.UniqueValueAlreadyInUse));
    }
}
