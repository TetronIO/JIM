using JIM.Models.Activities;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class HelpersOutcomeSummaryTests
{
    #region ParseOutcomeSummary

    [Test]
    public void ParseOutcomeSummary_NullInput_ReturnsEmptyList()
    {
        var result = Helpers.ParseOutcomeSummary(null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ParseOutcomeSummary_EmptyString_ReturnsEmptyList()
    {
        var result = Helpers.ParseOutcomeSummary("");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ParseOutcomeSummary_WhitespaceOnly_ReturnsEmptyList()
    {
        var result = Helpers.ParseOutcomeSummary("   ");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ParseOutcomeSummary_SingleOutcome_ParsesCorrectly()
    {
        var result = Helpers.ParseOutcomeSummary("Projected:1");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
        Assert.That(result[0].Count, Is.EqualTo(1));
    }

    [Test]
    public void ParseOutcomeSummary_MultipleOutcomes_ParsesAll()
    {
        var result = Helpers.ParseOutcomeSummary("Projected:1,AttributeFlow:12,PendingExportCreated:2");
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
        Assert.That(result[0].Count, Is.EqualTo(1));
        Assert.That(result[1].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow));
        Assert.That(result[1].Count, Is.EqualTo(12));
        Assert.That(result[2].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
        Assert.That(result[2].Count, Is.EqualTo(2));
    }

    [Test]
    public void ParseOutcomeSummary_InvalidTypeName_SkipsEntry()
    {
        var result = Helpers.ParseOutcomeSummary("InvalidType:5,Projected:1");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
    }

    [Test]
    public void ParseOutcomeSummary_InvalidCount_SkipsEntry()
    {
        var result = Helpers.ParseOutcomeSummary("Projected:abc,Joined:3");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Joined));
    }

    [Test]
    public void ParseOutcomeSummary_ZeroCount_SkipsEntry()
    {
        var result = Helpers.ParseOutcomeSummary("Projected:0,Joined:3");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Joined));
    }

    [Test]
    public void ParseOutcomeSummary_MalformedEntry_SkipsEntry()
    {
        var result = Helpers.ParseOutcomeSummary("NoColon,Projected:1");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
    }

    [Test]
    public void ParseOutcomeSummary_AllOutcomeTypes_ParsesAll()
    {
        var result = Helpers.ParseOutcomeSummary(
            "CsoAdded:1,CsoUpdated:2,CsoDeleted:3,ExportConfirmed:4,ExportFailed:5," +
            "Projected:6,Joined:7,AttributeFlow:8,Disconnected:9,DisconnectedOutOfScope:10," +
            "MvoDeleted:11,MvoDeletionScheduled:12,Provisioned:13,PendingExportCreated:14,Exported:15,Deprovisioned:16");
        Assert.That(result, Has.Count.EqualTo(16));
    }

    #endregion

    #region GetOutcomeTypeDisplayName

    [Test]
    public void GetOutcomeTypeDisplayName_AttributeFlow_ReturnsMvoAttributeFlow()
    {
        var result = Helpers.GetOutcomeTypeDisplayName(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        Assert.That(result, Is.EqualTo("MVO Attribute Flow"));
    }

    [Test]
    public void GetOutcomeTypeDisplayName_PendingExportCreated_ReturnsCsoPendingExport()
    {
        var result = Helpers.GetOutcomeTypeDisplayName(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);
        Assert.That(result, Is.EqualTo("CSO Pending Export"));
    }

    [Test]
    public void GetOutcomeTypeDisplayName_Projected_ReturnsMvoProjectedAsync()
    {
        var result = Helpers.GetOutcomeTypeDisplayName(ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        Assert.That(result, Is.EqualTo("MVO Projected"));
    }

    [Test]
    public void GetOutcomeTypeDisplayName_Provisioned_ReturnsCsoProvisionedAsync()
    {
        var result = Helpers.GetOutcomeTypeDisplayName(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned);
        Assert.That(result, Is.EqualTo("CSO Provisioned"));
    }

    #endregion

    #region GetOutcomeTypeMudBlazorColor

    [Test]
    public void GetOutcomeTypeMudBlazorColor_Projected_ReturnsPrimary()
    {
        var result = Helpers.GetOutcomeTypeMudBlazorColor(ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        Assert.That(result, Is.EqualTo(Color.Primary));
    }

    [Test]
    public void GetOutcomeTypeMudBlazorColor_CsoAdded_ReturnsSuccess()
    {
        var result = Helpers.GetOutcomeTypeMudBlazorColor(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded);
        Assert.That(result, Is.EqualTo(Color.Success));
    }

    [Test]
    public void GetOutcomeTypeMudBlazorColor_Exported_ReturnsInfo()
    {
        var result = Helpers.GetOutcomeTypeMudBlazorColor(ActivityRunProfileExecutionItemSyncOutcomeType.Exported);
        Assert.That(result, Is.EqualTo(Color.Info));
    }

    [Test]
    public void GetOutcomeTypeMudBlazorColor_MvoDeleted_ReturnsError()
    {
        var result = Helpers.GetOutcomeTypeMudBlazorColor(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        Assert.That(result, Is.EqualTo(Color.Error));
    }

    [Test]
    public void GetOutcomeTypeMudBlazorColor_MvoDeletionScheduled_ReturnsWarning()
    {
        var result = Helpers.GetOutcomeTypeMudBlazorColor(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled);
        Assert.That(result, Is.EqualTo(Color.Warning));
    }

    [Test]
    public void GetOutcomeTypeDisplayName_MvoDeletionScheduled_ReturnsMvoDeletionScheduled()
    {
        var result = Helpers.GetOutcomeTypeDisplayName(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled);
        Assert.That(result, Is.EqualTo("MVO Deletion Scheduled"));
    }

    [Test]
    public void GetOutcomeTypeIcon_MvoDeletionScheduled_ReturnsHourglassBottom()
    {
        var result = Helpers.GetOutcomeTypeIcon(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled);
        Assert.That(result, Is.EqualTo(Icons.Material.Filled.HourglassBottom));
    }

    #endregion
}
