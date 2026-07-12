// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Issue #985 (d): the LDAP connector's export batch-parallelism recommendation
/// (IConnectorRecommendedExportParallelism) must stay coherent with the directory-type-aware
/// Export Concurrency auto-tune (LdapExportConcurrencyAutoTuneTests). Export Concurrency is
/// auto-tuned per directory type at schema import time from
/// LdapConnectorRootDse.RecommendedExportConcurrency, so its current setting value is the best
/// available connection-free, directory-aware signal for a parallelism recommendation.
/// </summary>
[TestFixture]
public class LdapExportParallelismRecommendationTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    #region GetRecommendedExportParallelism — mirrors RecommendedExportConcurrency per directory type

    // LdapDirectoryType is internal, so it can't appear in a public [TestCase] method signature
    // (CS0051) — one test per directory type instead, mirroring LdapExportConcurrencyAutoTuneTests.

    [Test]
    public void GetRecommendedExportParallelism_ActiveDirectoryAfterAutoTune_MatchesRecommendedExportConcurrency()
        => AssertRecommendationMatchesAutoTune(LdapDirectoryType.ActiveDirectory);

    [Test]
    public void GetRecommendedExportParallelism_OpenLDAPAfterAutoTune_MatchesRecommendedExportConcurrency()
        => AssertRecommendationMatchesAutoTune(LdapDirectoryType.OpenLDAP);

    [Test]
    public void GetRecommendedExportParallelism_SambaADAfterAutoTune_MatchesRecommendedExportConcurrency()
        => AssertRecommendationMatchesAutoTune(LdapDirectoryType.SambaAD);

    [Test]
    public void GetRecommendedExportParallelism_GenericAfterAutoTune_MatchesRecommendedExportConcurrency()
        => AssertRecommendationMatchesAutoTune(LdapDirectoryType.Generic);

    /// <summary>
    /// Simulates a Connected System whose schema import has already auto-tuned Export
    /// Concurrency for the given directory type, then asserts the parallelism recommendation
    /// matches LdapConnectorRootDse.RecommendedExportConcurrency for that same directory type.
    /// </summary>
    private static void AssertRecommendationMatchesAutoTune(LdapDirectoryType directoryType)
    {
        var settings = CreateSettingsWithExportConcurrency(LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY);
        var rootDse = new LdapConnectorRootDse { DirectoryType = directoryType };
        LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger);
        var connector = new LdapConnector();

        var recommendation = connector.GetRecommendedExportParallelism(settings);

        Assert.That(recommendation, Is.EqualTo(rootDse.RecommendedExportConcurrency));
    }

    [Test]
    public void GetRecommendedExportParallelism_AdminOverriddenExportConcurrency_MirrorsOverride()
    {
        // An administrator's explicit Export Concurrency choice is respected by the auto-tune
        // guard, so the parallelism recommendation mirrors that override too, not the directory
        // type's textbook value.
        var settings = CreateSettingsWithExportConcurrency(8);
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger);
        var connector = new LdapConnector();

        var recommendation = connector.GetRecommendedExportParallelism(settings);

        Assert.That(recommendation, Is.EqualTo(8));
    }

    #endregion

    #region GetRecommendedExportParallelism — no recommendation available

    [Test]
    public void GetRecommendedExportParallelism_ExportConcurrencySettingMissing_ReturnsNull()
    {
        // No schema import has happened yet (e.g. a brand new Connected System) — the connector
        // must not open a connection here, and has no cached/derivable value to offer.
        var connector = new LdapConnector();

        var recommendation = connector.GetRecommendedExportParallelism([]);

        Assert.That(recommendation, Is.Null);
    }

    [Test]
    public void GetRecommendedExportParallelism_ExportConcurrencyIntValueNull_ReturnsNull()
    {
        var settings = CreateSettingsWithExportConcurrency(null);
        var connector = new LdapConnector();

        var recommendation = connector.GetRecommendedExportParallelism(settings);

        Assert.That(recommendation, Is.Null);
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

    #endregion
}
