// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Issue #985 (d): the LDAP connector's export batch-parallelism recommendation
/// (IConnectorRecommendedExportParallelism) must be deliberately conservative because the two
/// knobs MULTIPLY: each parallel batch pipeline gets its own connector instance, and each LDAP
/// connector instance runs its own Export Concurrency concurrent operations, so total in-flight
/// LDAP operations = parallelism x per-instance concurrency. The connector therefore recommends
/// a parallelism of 2 only when the Connected System's Export Concurrency signals a capable
/// directory (>= 8; the auto-tune only sets 16 for Active Directory and OpenLDAP), and no
/// recommendation otherwise (the resolver falls back to sequential).
/// </summary>
[TestFixture]
public class LdapExportParallelismRecommendationTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    /// <summary>
    /// The conservative parallelism recommendation for capable directories: 2 pipelines of
    /// (auto-tuned) 16 concurrent operations each = 32 in-flight operations, a mild default.
    /// </summary>
    private const int ExpectedCapableDirectoryRecommendation = 2;

    #region GetRecommendedExportParallelism — per directory type, after Export Concurrency auto-tune

    // LdapDirectoryType is internal, so it can't appear in a public [TestCase] method signature
    // (CS0051) — one test per directory type instead, mirroring LdapExportConcurrencyAutoTuneTests.

    [Test]
    public void GetRecommendedExportParallelism_ActiveDirectoryAfterAutoTune_Returns2()
        => AssertRecommendationAfterAutoTune(LdapDirectoryType.ActiveDirectory, ExpectedCapableDirectoryRecommendation);

    [Test]
    public void GetRecommendedExportParallelism_OpenLDAPAfterAutoTune_Returns2()
        => AssertRecommendationAfterAutoTune(LdapDirectoryType.OpenLDAP, ExpectedCapableDirectoryRecommendation);

    [Test]
    public void GetRecommendedExportParallelism_SambaADAfterAutoTune_ReturnsNull()
        => AssertRecommendationAfterAutoTune(LdapDirectoryType.SambaAD, null);

    [Test]
    public void GetRecommendedExportParallelism_GenericAfterAutoTune_ReturnsNull()
        => AssertRecommendationAfterAutoTune(LdapDirectoryType.Generic, null);

    /// <summary>
    /// Simulates a Connected System whose schema import has already auto-tuned Export
    /// Concurrency for the given directory type, then asserts the parallelism recommendation:
    /// 2 for capable directories (auto-tuned Export Concurrency of 16: Active Directory and
    /// OpenLDAP), null for the rest (auto-tune leaves them at the default of 4).
    /// </summary>
    private static void AssertRecommendationAfterAutoTune(LdapDirectoryType directoryType, int? expected)
    {
        var settings = CreateSettingsWithExportConcurrency(LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY);
        var rootDse = new LdapConnectorRootDse { DirectoryType = directoryType };
        LdapConnector.AutoTuneExportConcurrency(settings, rootDse, Logger);
        var connector = new LdapConnector();

        var recommendation = connector.GetRecommendedExportParallelism(settings);

        Assert.That(recommendation, Is.EqualTo(expected));
    }

    [Test]
    public void GetRecommendedExportParallelism_AdminSetConcurrencyAtThreshold_Returns2()
    {
        // An Export Concurrency of 8 or above (whether admin-set or auto-tuned) signals a
        // capable directory, so the conservative parallelism recommendation applies.
        var settings = CreateSettingsWithExportConcurrency(8);
        var connector = new LdapConnector();

        var recommendation = connector.GetRecommendedExportParallelism(settings);

        Assert.That(recommendation, Is.EqualTo(ExpectedCapableDirectoryRecommendation));
    }

    [Test]
    public void GetRecommendedExportParallelism_AdminSetConcurrencyBelowThreshold_ReturnsNull()
    {
        // Below the capable-directory threshold there is no recommendation; the resolver falls
        // back to sequential (1).
        var settings = CreateSettingsWithExportConcurrency(6);
        var connector = new LdapConnector();

        var recommendation = connector.GetRecommendedExportParallelism(settings);

        Assert.That(recommendation, Is.Null);
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
