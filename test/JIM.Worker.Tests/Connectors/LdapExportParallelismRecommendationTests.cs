// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector's export batch-parallelism recommendation
/// (IConnectorRecommendedExportParallelism; issues #985 (d) and #2897). The two knobs MULTIPLY:
/// each parallel batch pipeline gets its own connector instance, and each LDAP connector
/// instance runs its own Export Concurrency concurrent operations, so total in-flight LDAP
/// operations = parallelism x per-instance concurrency. The connector therefore recommends the
/// number of pipelines that keeps that total at a target of 32 (32 / Export Concurrency,
/// clamped to between 2 and 4), only when the Export Concurrency signals a capable directory
/// (8 or more), and makes no recommendation otherwise (the resolver falls back to sequential).
/// </summary>
[TestFixture]
public class LdapExportParallelismRecommendationTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    #region GetRecommendedExportParallelism: per directory type, after Export Concurrency auto-tune

    // LdapDirectoryType is internal, so it can't appear in a public [TestCase] method signature
    // (CS0051); one test per directory type instead, mirroring LdapExportConcurrencyAutoTuneTests.

    [Test]
    public void GetRecommendedExportParallelism_ActiveDirectoryAfterAutoTune_Returns2()
        => AssertRecommendationAfterAutoTune(LdapDirectoryType.ActiveDirectory, 2);

    [Test]
    public void GetRecommendedExportParallelism_OpenLDAPAfterAutoTune_Returns4()
        => AssertRecommendationAfterAutoTune(LdapDirectoryType.OpenLDAP, 4);

    [Test]
    public void GetRecommendedExportParallelism_DirectoryServer389AfterAutoTune_Returns2()
        => AssertRecommendationAfterAutoTune(LdapDirectoryType.DirectoryServer389, 2);

    [Test]
    public void GetRecommendedExportParallelism_SambaADAfterAutoTune_ReturnsNull()
        => AssertRecommendationAfterAutoTune(LdapDirectoryType.SambaAD, null);

    [Test]
    public void GetRecommendedExportParallelism_GenericAfterAutoTune_ReturnsNull()
        => AssertRecommendationAfterAutoTune(LdapDirectoryType.Generic, null);

    [Test]
    public void GetRecommendedExportParallelism_SambaRootDseDetectedAndAutoTuned_ReturnsNull()
    {
        // Samba AD advertises the Active Directory capability OID, so it is told apart from
        // Active Directory by its vendor name; were it not, it would be tuned to 16 and run
        // batches in parallel, which its LDAP implementation does not tolerate well.
        var directoryType = LdapConnectorUtilities.DetectDirectoryType(
            [LdapConnectorConstants.LDAP_CAP_ACTIVE_DIRECTORY_OID], "Samba Team");
        Assert.That(directoryType, Is.EqualTo(LdapDirectoryType.SambaAD));

        AssertRecommendationAfterAutoTune(directoryType, null);
    }

    /// <summary>
    /// Simulates a Connected System whose schema import has already auto-tuned Export
    /// Concurrency for the given directory type, then asserts the parallelism recommendation.
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

    #endregion

    #region GetRecommendedExportParallelism: by Export Concurrency

    // The recommendation keeps total operations in flight at 32: 32 / Export Concurrency,
    // clamped to between 2 and 4. Below 8 there is no recommendation. The value is the
    // administrator's or the auto-tune's alike; the connector cannot tell them apart.
    [TestCase(4, null)]
    [TestCase(7, null)]
    [TestCase(8, 4)]
    [TestCase(10, 3)]
    [TestCase(16, 2)]
    [TestCase(32, 2)]
    public void GetRecommendedExportParallelism_ByExportConcurrency_KeepsOperationsInFlightAtTarget(int exportConcurrency, int? expected)
    {
        var settings = CreateSettingsWithExportConcurrency(exportConcurrency);
        var connector = new LdapConnector();

        var recommendation = connector.GetRecommendedExportParallelism(settings);

        Assert.That(recommendation, Is.EqualTo(expected));
    }

    #endregion

    #region GetRecommendedExportParallelism: no recommendation available

    [Test]
    public void GetRecommendedExportParallelism_ExportConcurrencySettingMissing_ReturnsNull()
    {
        // No schema import has happened yet (e.g. a brand new Connected System); the connector
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
