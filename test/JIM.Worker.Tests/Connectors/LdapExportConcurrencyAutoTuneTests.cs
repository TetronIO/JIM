// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
    public void RecommendedExportConcurrency_OpenLDAP_Returns8()
    {
        // Issue #2897: 4 batches of 8 beat 2 batches of 16 against OpenLDAP at the same 32
        // operations in flight, so OpenLDAP is tuned to 8 and the parallelism recommendation
        // then runs 4 batches at once.
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        Assert.That(rootDse.RecommendedExportConcurrency, Is.EqualTo(8));
    }

    [Test]
    public void RecommendedExportConcurrency_DirectoryServer389_Returns16()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.DirectoryServer389 };
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
    public void AutoTuneExportConcurrency_OpenLDAP_DefaultValue_TunedTo8()
    {
        var settings = CreateSettingsWithExportConcurrency(LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY);
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };

        LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger);

        Assert.That(GetExportConcurrencyValue(settings), Is.EqualTo(8));
    }

    [Test]
    public void AutoTuneExportConcurrency_DirectoryServer389_DefaultValue_TunesTo16()
    {
        var settings = CreateSettingsWithExportConcurrency(LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY);
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.DirectoryServer389 };

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
    public void AutoTuneExportConcurrency_OpenLDAPAlreadyAt16_StaysAt16()
    {
        // An OpenLDAP Connected System auto-tuned to 16 before issue #2897 (or set to 16 by an
        // administrator) is no longer at the default, so a later schema import leaves it alone.
        var settings = CreateSettingsWithExportConcurrency(16);
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };

        LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger);

        Assert.That(GetExportConcurrencyValue(settings), Is.EqualTo(16));
    }

    [Test]
    public void AutoTuneExportConcurrency_SettingMissing_NoException()
    {
        var settings = new List<ConnectedSystemSettingValue>();
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };

        Assert.That(() => LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger), Throws.Nothing);
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
