using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class LdapExportConcurrencyAutoTuneTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    #region RecommendedExportConcurrency computed property

    [Test]
    public void RecommendedExportConcurrency_ActiveDirectory_Returns16()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        Assert.That(rootDse.RecommendedExportConcurrency, Is.EqualTo(16));
    }

    [Test]
    public void RecommendedExportConcurrency_OpenLDAP_Returns16()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        Assert.That(rootDse.RecommendedExportConcurrency, Is.EqualTo(16));
    }

    [Test]
    public void RecommendedExportConcurrency_SambaAD_ReturnsDefault()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.SambaAD };
        Assert.That(rootDse.RecommendedExportConcurrency, Is.EqualTo(LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY));
    }

    [Test]
    public void RecommendedExportConcurrency_Generic_ReturnsDefault()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.Generic };
        Assert.That(rootDse.RecommendedExportConcurrency, Is.EqualTo(LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY));
    }

    #endregion

    #region AutoTuneExportConcurrency

    [Test]
    public void AutoTuneExportConcurrency_ActiveDirectory_DefaultValue_TunedTo16()
    {
        var settings = CreateSettingsWithExportConcurrency(LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY);
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };

        LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger);

        Assert.That(GetExportConcurrencyValue(settings), Is.EqualTo(16));
    }

    [Test]
    public void AutoTuneExportConcurrency_OpenLDAP_DefaultValue_TunedTo16()
    {
        var settings = CreateSettingsWithExportConcurrency(LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY);
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };

        LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger);

        Assert.That(GetExportConcurrencyValue(settings), Is.EqualTo(16));
    }

    [Test]
    public void AutoTuneExportConcurrency_SambaAD_DefaultValue_StaysAtDefault()
    {
        var settings = CreateSettingsWithExportConcurrency(LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY);
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.SambaAD };

        LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger);

        Assert.That(GetExportConcurrencyValue(settings), Is.EqualTo(LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY));
    }

    [Test]
    public void AutoTuneExportConcurrency_Generic_DefaultValue_StaysAtDefault()
    {
        var settings = CreateSettingsWithExportConcurrency(LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY);
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.Generic };

        LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger);

        Assert.That(GetExportConcurrencyValue(settings), Is.EqualTo(LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY));
    }

    [Test]
    public void AutoTuneExportConcurrency_AdminSetTo8_ActiveDirectory_StaysAt8()
    {
        var settings = CreateSettingsWithExportConcurrency(8);
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };

        LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger);

        Assert.That(GetExportConcurrencyValue(settings), Is.EqualTo(8));
    }

    [Test]
    public void AutoTuneExportConcurrency_AdminSetTo16_SambaAD_StaysAt16()
    {
        var settings = CreateSettingsWithExportConcurrency(16);
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.SambaAD };

        LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger);

        Assert.That(GetExportConcurrencyValue(settings), Is.EqualTo(16));
    }

    [Test]
    public void AutoTuneExportConcurrency_SettingMissing_NoException()
    {
        var settings = new List<ConnectedSystemSettingValue>();
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };

        Assert.DoesNotThrow(() => LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger));
    }

    [Test]
    public void AutoTuneExportConcurrency_IntValueNull_TreatedAsDefault_TunedAccordingly()
    {
        var settings = CreateSettingsWithExportConcurrency(null);
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };

        LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger);

        Assert.That(GetExportConcurrencyValue(settings), Is.EqualTo(16));
    }

    #endregion

    #region Helpers

    private static List<ConnectedSystemSettingValue> CreateSettingsWithExportConcurrency(int? value)
    {
        return
        [
            new ConnectedSystemSettingValue
            {
                Setting = new ConnectorDefinitionSetting { Name = "Export Concurrency" },
                IntValue = value
            }
        ];
    }

    private static int? GetExportConcurrencyValue(List<ConnectedSystemSettingValue> settings)
    {
        return settings.First(s => s.Setting.Name == "Export Concurrency").IntValue;
    }

    #endregion
}
