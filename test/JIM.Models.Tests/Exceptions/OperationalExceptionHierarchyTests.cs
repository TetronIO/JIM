using System;
using JIM.Models.Exceptions;
using NUnit.Framework;

namespace JIM.Models.Tests.Exceptions;

/// <summary>
/// Tests to verify that all custom exception types correctly inherit from OperationalException,
/// ensuring they are treated as expected, user-actionable errors (no stack trace persisted).
/// </summary>
[TestFixture]
public class OperationalExceptionHierarchyTests
{
    [Test]
    public void OperationalException_IsException()
    {
        var exception = new OperationalException("test");
        Assert.That(exception, Is.InstanceOf<Exception>());
    }

    [TestCase(typeof(CannotPerformDeltaImportException))]
    [TestCase(typeof(CsvParsingException))]
    [TestCase(typeof(InvalidSettingValuesException))]
    [TestCase(typeof(MissingExternalIdAttributeException))]
    [TestCase(typeof(ExternalIdAttributeValueMissingException))]
    [TestCase(typeof(ExternalIdAttributeNotSingleValuedException))]
    [TestCase(typeof(MultipleMatchesException))]
    [TestCase(typeof(DuplicateAttributesException))]
    [TestCase(typeof(DuplicatePendingExportException))]
    [TestCase(typeof(DataGenerationTemplateException))]
    [TestCase(typeof(DataGenerationTemplateAttributeException))]
    [TestCase(typeof(LdapCommunicationException))]
    public void CustomException_IsOperationalException(Type exceptionType)
    {
        Assert.That(typeof(OperationalException).IsAssignableFrom(exceptionType),
            $"{exceptionType.Name} should inherit from OperationalException");
    }

    [Test]
    public void LdapCommunicationException_PreservesInnerException()
    {
        var inner = new InvalidOperationException("inner error");
        var exception = new LdapCommunicationException("LDAP failed", inner);

        Assert.That(exception.InnerException, Is.SameAs(inner));
        Assert.That(exception.Message, Is.EqualTo("LDAP failed"));
    }

    [Test]
    public void OperationalException_PreservesInnerException()
    {
        var inner = new Exception("inner");
        var exception = new OperationalException("outer", inner);

        Assert.That(exception.InnerException, Is.SameAs(inner));
        Assert.That(exception.Message, Is.EqualTo("outer"));
    }
}
