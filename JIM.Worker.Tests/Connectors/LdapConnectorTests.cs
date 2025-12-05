using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class LdapConnectorTests
{
    private LdapConnector _connector = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new LdapConnector();
    }

    [TearDown]
    public void TearDown()
    {
        _connector.Dispose();
    }

    #region IConnector property tests

    [Test]
    public void Name_ReturnsLdapConnectorName()
    {
        Assert.That(_connector.Name, Is.EqualTo("JIM LDAP Connector"));
    }

    [Test]
    public void Description_IsNotNullOrEmpty()
    {
        Assert.That(_connector.Description, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Url_IsNotNullOrEmpty()
    {
        Assert.That(_connector.Url, Is.Not.Null.And.Not.Empty);
    }

    #endregion

    #region IConnectorCapabilities tests

    [Test]
    public void SupportsFullImport_ReturnsTrue()
    {
        Assert.That(_connector.SupportsFullImport, Is.True);
    }

    [Test]
    public void SupportsDeltaImport_ReturnsFalse()
    {
        Assert.That(_connector.SupportsDeltaImport, Is.False);
    }

    [Test]
    public void SupportsExport_ReturnsFalse()
    {
        // Export is not yet implemented
        Assert.That(_connector.SupportsExport, Is.False);
    }

    [Test]
    public void SupportsPartitions_ReturnsTrue()
    {
        Assert.That(_connector.SupportsPartitions, Is.True);
    }

    [Test]
    public void SupportsPartitionContainers_ReturnsTrue()
    {
        Assert.That(_connector.SupportsPartitionContainers, Is.True);
    }

    [Test]
    public void SupportsSecondaryExternalId_ReturnsTrue()
    {
        Assert.That(_connector.SupportsSecondaryExternalId, Is.True);
    }

    [Test]
    public void SupportsAutoConfirmExport_ReturnsFalse()
    {
        Assert.That(_connector.SupportsAutoConfirmExport, Is.False);
    }

    #endregion

    #region GetSettings tests

    [Test]
    public void GetSettings_ReturnsNonEmptyList()
    {
        var settings = _connector.GetSettings();
        Assert.That(settings, Is.Not.Empty);
    }

    [Test]
    public void GetSettings_ContainsHostSetting()
    {
        var settings = _connector.GetSettings();
        var hostSetting = settings.FirstOrDefault(s => s.Name == "Host");

        Assert.That(hostSetting, Is.Not.Null);
        Assert.That(hostSetting!.Required, Is.True);
        Assert.That(hostSetting.Category, Is.EqualTo(ConnectedSystemSettingCategory.Connectivity));
        Assert.That(hostSetting.Type, Is.EqualTo(ConnectedSystemSettingType.String));
    }

    [Test]
    public void GetSettings_ContainsPortSetting()
    {
        var settings = _connector.GetSettings();
        var portSetting = settings.FirstOrDefault(s => s.Name == "Port");

        Assert.That(portSetting, Is.Not.Null);
        Assert.That(portSetting!.Required, Is.True);
        Assert.That(portSetting.DefaultIntValue, Is.EqualTo(389));
        Assert.That(portSetting.Type, Is.EqualTo(ConnectedSystemSettingType.Integer));
    }

    [Test]
    public void GetSettings_ContainsUsernameSetting()
    {
        var settings = _connector.GetSettings();
        var usernameSetting = settings.FirstOrDefault(s => s.Name == "Username");

        Assert.That(usernameSetting, Is.Not.Null);
        Assert.That(usernameSetting!.Required, Is.True);
        Assert.That(usernameSetting.Type, Is.EqualTo(ConnectedSystemSettingType.String));
    }

    [Test]
    public void GetSettings_ContainsPasswordSetting()
    {
        var settings = _connector.GetSettings();
        var passwordSetting = settings.FirstOrDefault(s => s.Name == "Password");

        Assert.That(passwordSetting, Is.Not.Null);
        Assert.That(passwordSetting!.Required, Is.True);
        Assert.That(passwordSetting.Type, Is.EqualTo(ConnectedSystemSettingType.StringEncrypted));
    }

    [Test]
    public void GetSettings_ContainsAuthTypeSetting()
    {
        var settings = _connector.GetSettings();
        var authTypeSetting = settings.FirstOrDefault(s => s.Name == "Authentication Type");

        Assert.That(authTypeSetting, Is.Not.Null);
        Assert.That(authTypeSetting!.Required, Is.True);
        Assert.That(authTypeSetting.Type, Is.EqualTo(ConnectedSystemSettingType.DropDown));
        Assert.That(authTypeSetting.DropDownValues, Does.Contain("Simple"));
        Assert.That(authTypeSetting.DropDownValues, Does.Contain("NTLM"));
    }

    [Test]
    public void GetSettings_ContainsConnectionTimeoutSetting()
    {
        var settings = _connector.GetSettings();
        var timeoutSetting = settings.FirstOrDefault(s => s.Name == "Connection Timeout");

        Assert.That(timeoutSetting, Is.Not.Null);
        Assert.That(timeoutSetting!.Required, Is.True);
        Assert.That(timeoutSetting.DefaultIntValue, Is.EqualTo(10));
        Assert.That(timeoutSetting.Type, Is.EqualTo(ConnectedSystemSettingType.Integer));
    }

    [Test]
    public void GetSettings_ContainsConnectivityCategorySettings()
    {
        var settings = _connector.GetSettings();
        var connectivitySettings = settings.Where(s => s.Category == ConnectedSystemSettingCategory.Connectivity).ToList();

        Assert.That(connectivitySettings, Is.Not.Empty);
        Assert.That(connectivitySettings.Count, Is.GreaterThan(5)); // Host, Port, Timeout, Username, Password, AuthType + headings/dividers
    }

    [Test]
    public void GetSettings_ContainsGeneralCategorySettings()
    {
        var settings = _connector.GetSettings();
        var generalSettings = settings.Where(s => s.Category == ConnectedSystemSettingCategory.General).ToList();

        Assert.That(generalSettings, Is.Not.Empty);
    }

    [Test]
    public void GetSettings_ContainsCreateContainersAsNeededSetting()
    {
        var settings = _connector.GetSettings();
        var createContainersSetting = settings.FirstOrDefault(s => s.Name == "Create containers as needed?");

        Assert.That(createContainersSetting, Is.Not.Null);
        Assert.That(createContainersSetting!.Type, Is.EqualTo(ConnectedSystemSettingType.CheckBox));
        Assert.That(createContainersSetting.DefaultCheckboxValue, Is.False);
    }

    #endregion

    #region IDisposable tests

    [Test]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var connector = new LdapConnector();

        // Should not throw
        connector.Dispose();
        connector.Dispose();
        connector.Dispose();
    }

    #endregion
}
