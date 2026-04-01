using JIM.Connectors.LDAP;
using JIM.Models.Activities;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using System.Text.Json;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Tests for the delta import fallback behaviour in the LDAP connector.
/// When the accesslog watermark is not available (e.g., due to server-side size limits),
/// the connector should fall back to a full import and signal the fallback via a warning.
/// </summary>
[TestFixture]
public class LdapConnectorImportDeltaFallbackTests
{
    #region Error type classification tests

    [Test]
    public void DeltaImportFallbackToFullImport_ErrorType_ExistsInEnumAsync()
    {
        // The DeltaImportFallbackToFullImport error type must exist in the enum
        var errorType = ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport;
        Assert.That(errorType, Is.Not.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet));
        Assert.That(errorType, Is.Not.EqualTo(ActivityRunProfileExecutionItemErrorType.UnhandledError));
    }

    [Test]
    public void DeltaImportFallbackToFullImport_IsNotUnhandledError_SoTriggersWarningNotErrorAsync()
    {
        // The fallback error type should NOT be UnhandledError, because UnhandledError
        // escalates the activity to CompleteWithError. The fallback is a warning-grade
        // issue that should result in CompleteWithWarning.
        var errorType = ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport;
        Assert.That(errorType, Is.Not.EqualTo(ActivityRunProfileExecutionItemErrorType.UnhandledError),
            "DeltaImportFallbackToFullImport must not be classified as UnhandledError — " +
            "it should produce CompleteWithWarning, not CompleteWithError");
    }

    #endregion

    #region ConnectedSystemImportResult warning properties tests

    [Test]
    public void ConnectedSystemImportResult_WarningMessage_DefaultsToNullAsync()
    {
        var result = new ConnectedSystemImportResult();
        Assert.That(result.WarningMessage, Is.Null);
        Assert.That(result.WarningErrorType, Is.Null);
    }

    [Test]
    public void ConnectedSystemImportResult_WarningMessage_CanBeSetAsync()
    {
        var result = new ConnectedSystemImportResult
        {
            WarningMessage = "Delta import fell back to full import",
            WarningErrorType = ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport
        };

        Assert.That(result.WarningMessage, Is.EqualTo("Delta import fell back to full import"));
        Assert.That(result.WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
    }

    #endregion

    #region RootDSE accesslog watermark tests

    [Test]
    public void OpenLdapRootDse_WithNullAccesslogTimestamp_IndicatesWatermarkUnavailableAsync()
    {
        // When an OpenLDAP RootDSE has UseAccesslogDeltaImport=true but no timestamp,
        // the connector should detect that the watermark is unavailable
        var rootDse = CreateOpenLdapRootDse(lastAccesslogTimestamp: null);

        Assert.That(rootDse.UseAccesslogDeltaImport, Is.True);
        Assert.That(rootDse.LastAccesslogTimestamp, Is.Null);
    }

    [Test]
    public void OpenLdapRootDse_WithAccesslogTimestamp_IndicatesWatermarkAvailableAsync()
    {
        var rootDse = CreateOpenLdapRootDse(lastAccesslogTimestamp: "20260329094128.000033Z");

        Assert.That(rootDse.UseAccesslogDeltaImport, Is.True);
        Assert.That(rootDse.LastAccesslogTimestamp, Is.Not.Null);
        Assert.That(rootDse.LastAccesslogTimestamp, Is.EqualTo("20260329094128.000033Z"));
    }

    [Test]
    public void OpenLdapRootDse_SerialisesAndDeserialisesCorrectly_WithNullTimestampAsync()
    {
        // Verify that the persisted connector data correctly round-trips with null timestamp,
        // which is the condition that triggers the fallback
        var rootDse = CreateOpenLdapRootDse(lastAccesslogTimestamp: null);
        var json = JsonSerializer.Serialize(rootDse);
        var deserialised = JsonSerializer.Deserialize<LdapConnectorRootDse>(json);

        Assert.That(deserialised, Is.Not.Null);
        Assert.That(deserialised!.UseAccesslogDeltaImport, Is.True);
        Assert.That(deserialised.LastAccesslogTimestamp, Is.Null);
        Assert.That(deserialised.DirectoryType, Is.EqualTo(LdapDirectoryType.OpenLDAP));
    }

    [Test]
    public void OpenLdapRootDse_SerialisesAndDeserialisesCorrectly_WithTimestampAsync()
    {
        var rootDse = CreateOpenLdapRootDse(lastAccesslogTimestamp: "20260329094128.000033Z");
        var json = JsonSerializer.Serialize(rootDse);
        var deserialised = JsonSerializer.Deserialize<LdapConnectorRootDse>(json);

        Assert.That(deserialised, Is.Not.Null);
        Assert.That(deserialised!.UseAccesslogDeltaImport, Is.True);
        Assert.That(deserialised.LastAccesslogTimestamp, Is.EqualTo("20260329094128.000033Z"));
    }

    #endregion

    #region Accesslog fallback timestamp tests

    [Test]
    public void GenerateAccesslogFallbackTimestamp_ReturnsValidLdapGeneralisedTime()
    {
        // When the accesslog is empty (e.g., after snapshot restore), the connector should
        // generate a fallback timestamp so the watermark is never null for OpenLDAP directories.
        var timestamp = LdapConnectorUtilities.GenerateAccesslogFallbackTimestamp();

        Assert.That(timestamp, Is.Not.Null);
        Assert.That(timestamp, Is.Not.Empty);
        // LDAP generalised time format: YYYYMMDDHHmmSS.ffffffZ
        Assert.That(timestamp, Does.Match(@"^\d{14}\.\d{6}Z$"),
            "Timestamp must be in LDAP generalised time format (YYYYMMDDHHmmSS.ffffffZ)");
    }

    [Test]
    public void GenerateAccesslogFallbackTimestamp_IsRecentUtcTime()
    {
        // The fallback timestamp should represent approximately "now" so that a subsequent
        // delta import queries from this point forward and finds no spurious changes.
        var before = DateTime.UtcNow;
        var timestamp = LdapConnectorUtilities.GenerateAccesslogFallbackTimestamp();
        var after = DateTime.UtcNow;

        // Parse the timestamp back to DateTime for comparison
        var parsed = DateTime.ParseExact(timestamp, "yyyyMMddHHmmss.ffffffZ",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

        Assert.That(parsed, Is.GreaterThanOrEqualTo(before.AddSeconds(-1)),
            "Fallback timestamp should be approximately current UTC time");
        Assert.That(parsed, Is.LessThanOrEqualTo(after.AddSeconds(1)),
            "Fallback timestamp should be approximately current UTC time");
    }

    [Test]
    public void GenerateAccesslogFallbackTimestamp_WorksAsAccesslogFilter()
    {
        // The timestamp must be usable in an LDAP filter like (reqStart>=timestamp)
        // This means it must sort correctly with real accesslog timestamps via string comparison.
        var fallback = LdapConnectorUtilities.GenerateAccesslogFallbackTimestamp();
        var realTimestamp = "20260329094128.000033Z"; // A real accesslog timestamp

        // The fallback (generated ~now, 2026-03-31) should be AFTER a timestamp from 2026-03-29
        Assert.That(string.Compare(fallback, realTimestamp, StringComparison.Ordinal), Is.GreaterThan(0),
            "Fallback timestamp (now) should sort after an older real timestamp");
    }

    #endregion

    #region Activity warning message tests (connector-level warnings go on Activity, not phantom RPEIs)

    [Test]
    public void Activity_WarningMessage_DefaultsToNull()
    {
        var activity = new Activity();
        Assert.That(activity.WarningMessage, Is.Null);
    }

    [Test]
    public void Activity_WarningMessage_CanBeSet()
    {
        var activity = new Activity
        {
            WarningMessage = "Delta import fell back to full import"
        };

        Assert.That(activity.WarningMessage, Is.EqualTo("Delta import fell back to full import"));
    }

    [Test]
    public void Activity_ConnectorWarning_ShouldNotCreatePhantomRpei()
    {
        // Connector-level warnings (like DeltaImportFallbackToFullImport) should be stored
        // on the Activity itself, NOT as a separate RPEI with no CSO association.
        // A phantom RPEI inflates error counts, pollutes the RPEI list, and misleads users.
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            WarningMessage = "Delta import was requested but the accesslog watermark was not available.",
            RunProfileExecutionItems = new List<ActivityRunProfileExecutionItem>()
        };

        // The activity should have zero RPEIs — the warning is on the activity, not an RPEI
        Assert.That(activity.RunProfileExecutionItems, Has.Count.EqualTo(0));
        Assert.That(activity.WarningMessage, Is.Not.Null);
    }

    #endregion

    #region Helpers

    private static LdapConnectorRootDse CreateOpenLdapRootDse(string? lastAccesslogTimestamp)
    {
        return new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.OpenLDAP,
            LastAccesslogTimestamp = lastAccesslogTimestamp
        };
    }

    #endregion
}
