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
    public void SupportsDeltaImport_ReturnsTrue()
    {
        Assert.That(_connector.SupportsDeltaImport, Is.True);
    }

    [Test]
    public void SupportsExport_ReturnsTrue()
    {
        Assert.That(_connector.SupportsExport, Is.True);
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

    [Test]
    public void GetSettings_ContainsSearchTimeoutSetting()
    {
        var settings = _connector.GetSettings();
        var searchTimeoutSetting = settings.FirstOrDefault(s => s.Name == "Search Timeout");

        Assert.That(searchTimeoutSetting, Is.Not.Null);
        Assert.That(searchTimeoutSetting!.Type, Is.EqualTo(ConnectedSystemSettingType.Integer));
        Assert.That(searchTimeoutSetting.DefaultIntValue, Is.EqualTo(300)); // 5 minutes default
        Assert.That(searchTimeoutSetting.Required, Is.False);
        Assert.That(searchTimeoutSetting.Category, Is.EqualTo(ConnectedSystemSettingCategory.General));
    }

    #endregion

    #region LDAPS settings tests

    [Test]
    public void GetSettings_ContainsUseSecureConnectionSetting()
    {
        var settings = _connector.GetSettings();
        var secureConnectionSetting = settings.FirstOrDefault(s => s.Name == "Use Secure Connection (LDAPS)?");

        Assert.That(secureConnectionSetting, Is.Not.Null);
        Assert.That(secureConnectionSetting!.Type, Is.EqualTo(ConnectedSystemSettingType.CheckBox));
        Assert.That(secureConnectionSetting.DefaultCheckboxValue, Is.False);
        Assert.That(secureConnectionSetting.Category, Is.EqualTo(ConnectedSystemSettingCategory.Connectivity));
    }

    [Test]
    public void GetSettings_ContainsCertificateValidationSetting()
    {
        var settings = _connector.GetSettings();
        var certValidationSetting = settings.FirstOrDefault(s => s.Name == "Certificate Validation");

        Assert.That(certValidationSetting, Is.Not.Null);
        Assert.That(certValidationSetting!.Type, Is.EqualTo(ConnectedSystemSettingType.DropDown));
        Assert.That(certValidationSetting.DropDownValues, Does.Contain("Full Validation"));
        Assert.That(certValidationSetting.DropDownValues, Does.Contain("Skip Validation (Not Recommended)"));
        Assert.That(certValidationSetting.DropDownValues!.Count, Is.EqualTo(2));
        Assert.That(certValidationSetting.Category, Is.EqualTo(ConnectedSystemSettingCategory.Connectivity));
    }

    #endregion

    #region Retry settings tests

    [Test]
    public void GetSettings_ContainsMaxRetriesSetting()
    {
        var settings = _connector.GetSettings();
        var maxRetriesSetting = settings.FirstOrDefault(s => s.Name == "Maximum Retries");

        Assert.That(maxRetriesSetting, Is.Not.Null);
        Assert.That(maxRetriesSetting!.Type, Is.EqualTo(ConnectedSystemSettingType.Integer));
        Assert.That(maxRetriesSetting.DefaultIntValue, Is.EqualTo(3));
        Assert.That(maxRetriesSetting.Required, Is.False);
        Assert.That(maxRetriesSetting.Category, Is.EqualTo(ConnectedSystemSettingCategory.General));
    }

    [Test]
    public void GetSettings_ContainsRetryDelaySetting()
    {
        var settings = _connector.GetSettings();
        var retryDelaySetting = settings.FirstOrDefault(s => s.Name == "Retry Delay (ms)");

        Assert.That(retryDelaySetting, Is.Not.Null);
        Assert.That(retryDelaySetting!.Type, Is.EqualTo(ConnectedSystemSettingType.Integer));
        Assert.That(retryDelaySetting.DefaultIntValue, Is.EqualTo(1000));
        Assert.That(retryDelaySetting.Required, Is.False);
        Assert.That(retryDelaySetting.Category, Is.EqualTo(ConnectedSystemSettingCategory.General));
    }

    #endregion

    #region Export settings tests

    [Test]
    public void GetSettings_ContainsDeleteBehaviourSetting()
    {
        var settings = _connector.GetSettings();
        var deleteBehaviourSetting = settings.FirstOrDefault(s => s.Name == "Delete Behaviour");

        Assert.That(deleteBehaviourSetting, Is.Not.Null);
        Assert.That(deleteBehaviourSetting!.Type, Is.EqualTo(ConnectedSystemSettingType.DropDown));
        Assert.That(deleteBehaviourSetting.DropDownValues, Does.Contain("Delete"));
        Assert.That(deleteBehaviourSetting.DropDownValues, Does.Contain("Disable"));
        Assert.That(deleteBehaviourSetting.Category, Is.EqualTo(ConnectedSystemSettingCategory.Export));
    }

    [Test]
    public void GetSettings_ContainsDisableAttributeSetting()
    {
        var settings = _connector.GetSettings();
        var disableAttributeSetting = settings.FirstOrDefault(s => s.Name == "Disable Attribute");

        Assert.That(disableAttributeSetting, Is.Not.Null);
        Assert.That(disableAttributeSetting!.Type, Is.EqualTo(ConnectedSystemSettingType.String));
        Assert.That(disableAttributeSetting.DefaultStringValue, Is.EqualTo("userAccountControl"));
        Assert.That(disableAttributeSetting.Category, Is.EqualTo(ConnectedSystemSettingCategory.Export));
    }

    [Test]
    public void GetSettings_ContainsExportCategorySettings()
    {
        var settings = _connector.GetSettings();
        var exportSettings = settings.Where(s => s.Category == ConnectedSystemSettingCategory.Export).ToList();

        Assert.That(exportSettings, Is.Not.Empty);
        Assert.That(exportSettings.Count, Is.GreaterThanOrEqualTo(3)); // Heading + Delete Behaviour + Disable Attribute
    }

    #endregion

    #region IConnectorExportUsingCalls tests

    [Test]
    public void Export_WithoutOpenExportConnection_ThrowsInvalidOperationException()
    {
        var pendingExports = new List<JIM.Models.Transactional.PendingExport>();

        var exception = Assert.Throws<InvalidOperationException>(() => _connector.Export(pendingExports));
        Assert.That(exception.Message, Does.Contain("OpenExportConnection"));
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
