using JIM.Connectors.LDAP;
using NUnit.Framework;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class LdapConnectorExportDeleteIdempotencyTests
{
    [Test]
    public void IsNoSuchObjectResult_StandardNoSuchObject_ReturnsTrue()
    {
        var result = LdapConnectorExport.IsNoSuchObjectResult(ResultCode.NoSuchObject, null);
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsNoSuchObjectResult_StandardNoSuchObject_WithMessage_ReturnsTrue()
    {
        var result = LdapConnectorExport.IsNoSuchObjectResult(ResultCode.NoSuchObject, "some error");
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsNoSuchObjectResult_SambaErrorCode0x2030_ReturnsTrue()
    {
        var result = LdapConnectorExport.IsNoSuchObjectResult(
            ResultCode.Other,
            "0000208D: NameErr: DSID-0C090CE2, problem 2001 (NO_OBJECT), data 0, best match of: 'DC=test,DC=local' 00002030: ");
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsNoSuchObjectResult_SambaDsidOnly_ReturnsTrue()
    {
        var result = LdapConnectorExport.IsNoSuchObjectResult(
            ResultCode.Other,
            "NameErr: DSID-0C090CE2, problem 2001");
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsNoSuchObjectResult_Success_ReturnsFalse()
    {
        var result = LdapConnectorExport.IsNoSuchObjectResult(ResultCode.Success, null);
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsNoSuchObjectResult_OtherError_ReturnsFalse()
    {
        var result = LdapConnectorExport.IsNoSuchObjectResult(
            ResultCode.InsufficientAccessRights,
            "Access denied");
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsNoSuchObjectResult_NullResultCode_WithSambaMessage_ReturnsTrue()
    {
        var result = LdapConnectorExport.IsNoSuchObjectResult(null, "00002030: object not found");
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsNoSuchObjectResult_NullResultCode_NullMessage_ReturnsFalse()
    {
        var result = LdapConnectorExport.IsNoSuchObjectResult(null, null);
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsNoSuchObjectResult_OtherResultCode_EmptyMessage_ReturnsFalse()
    {
        var result = LdapConnectorExport.IsNoSuchObjectResult(ResultCode.Other, "");
        Assert.That(result, Is.False);
    }
}
