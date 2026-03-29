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

    #region RPEI creation pattern tests

    [Test]
    public void ActivityRpei_WithFallbackWarning_HasCorrectPropertiesAsync()
    {
        // Verify that an RPEI created for the fallback warning has the correct structure
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            RunProfileExecutionItems = new List<ActivityRunProfileExecutionItem>()
        };

        var warningRpei = activity.PrepareRunProfileExecutionItem();
        warningRpei.ErrorType = ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport;
        warningRpei.ErrorMessage = "Delta import fell back to full import due to accesslog size limit.";
        activity.RunProfileExecutionItems.Add(warningRpei);

        Assert.That(activity.RunProfileExecutionItems, Has.Count.EqualTo(1));
        var rpei = activity.RunProfileExecutionItems.First();
        Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
        Assert.That(rpei.ErrorMessage, Does.Contain("accesslog"));
        Assert.That(rpei.ActivityId, Is.EqualTo(activity.Id));
        // No CSO associated — this is a connector-level warning, not an object-level error
        Assert.That(rpei.ConnectedSystemObjectId, Is.Null);
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
