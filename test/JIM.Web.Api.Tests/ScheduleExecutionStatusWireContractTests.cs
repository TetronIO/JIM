// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Text.Json;
using JIM.Models.Scheduling;
using JIM.Web;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Pins the serialised wire values of <see cref="ScheduleExecutionStatus"/> on the Schedule Execution DTOs.
/// <para>
/// <see cref="ApiJsonConfiguration"/> registers <c>JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)</c>,
/// so every member name of this enum IS the published REST contract: responses carry the name verbatim and requests are
/// rejected unless they send the name verbatim. Renaming a member is therefore a breaking API change for every client of
/// the Schedule Execution endpoints, and for the PowerShell module's <c>-Status</c> filter, which puts the same names on
/// the query string.
/// </para>
/// <para>
/// "Complete" in particular was deliberately chosen over "Completed" (#1196) so that Schedule Executions share the
/// Activity vocabulary (<c>ActivityStatus.Complete</c>); the Activity detail page shows both chips side by side, and the
/// two spellings read as a contradiction. That choice is what this fixture exists to hold still.
/// </para>
/// </summary>
[TestFixture]
public class ScheduleExecutionStatusWireContractTests
{
    private static JsonSerializerOptions ApiOptions()
    {
        var options = new JsonSerializerOptions();
        ApiJsonConfiguration.Configure(options);
        return options;
    }

    [TestCase(ScheduleExecutionStatus.Queued, "Queued")]
    [TestCase(ScheduleExecutionStatus.InProgress, "InProgress")]
    [TestCase(ScheduleExecutionStatus.Complete, "Complete")]
    [TestCase(ScheduleExecutionStatus.Failed, "Failed")]
    [TestCase(ScheduleExecutionStatus.Cancelled, "Cancelled")]
    [TestCase(ScheduleExecutionStatus.Paused, "Paused")]
    public void ScheduleExecutionDto_SerialisesStatusAsItsExactEnumMemberName(ScheduleExecutionStatus status, string expectedWireValue)
    {
        var dto = new ScheduleExecutionDto { Status = status };

        var json = JsonSerializer.Serialize(dto, ApiOptions());

        using var document = JsonDocument.Parse(json);
        var statusProperty = document.RootElement.GetProperty(nameof(ScheduleExecutionDto.Status));
        Assert.Multiple(() =>
        {
            Assert.That(statusProperty.ValueKind, Is.EqualTo(JsonValueKind.String));
            Assert.That(statusProperty.GetString(), Is.EqualTo(expectedWireValue));
        });
    }

    [Test]
    public void ScheduleExecutionStatus_SuccessMemberIsNamedComplete_MatchingTheActivityVocabulary()
    {
        // The whole point of the rename: the Activity detail page renders an ActivityStatus chip and a
        // ScheduleExecutionStatus chip beside each other, so the two enums must agree on the word for success.
        Assert.Multiple(() =>
        {
            Assert.That(Enum.GetNames<ScheduleExecutionStatus>(), Does.Contain("Complete"));
            Assert.That(Enum.GetNames<ScheduleExecutionStatus>(), Does.Not.Contain("Completed"));
        });
    }

    [Test]
    public void ScheduleExecutionStatus_RejectsThePreviousCompletedSpelling_WhenDeserialising()
    {
        // No compatibility alias was kept: "Completed" is gone from the wire, deliberately and breakingly (#1196).
        const string jsonWithOldValue = "{\"Status\":\"Completed\"}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScheduleExecutionDto>(jsonWithOldValue, ApiOptions()));
    }

    [Test]
    public void ScheduleExecutionStatus_AcceptsCompleteAsAStringValue_WhenDeserialising()
    {
        const string jsonWithNewValue = "{\"Status\":\"Complete\"}";

        var result = JsonSerializer.Deserialize<ScheduleExecutionDto>(jsonWithNewValue, ApiOptions());

        Assert.That(result!.Status, Is.EqualTo(ScheduleExecutionStatus.Complete));
    }

    [Test]
    public void ScheduleDto_SerialisesLastExecutionStatusAsItsExactEnumMemberName()
    {
        // The same enum is published a second time on the Schedules list endpoint, so it needs pinning here too.
        var dto = new ScheduleDto { LastExecutionStatus = ScheduleExecutionStatus.Complete };

        var json = JsonSerializer.Serialize(dto, ApiOptions());

        using var document = JsonDocument.Parse(json);
        var statusProperty = document.RootElement.GetProperty(nameof(ScheduleDto.LastExecutionStatus));
        Assert.That(statusProperty.GetString(), Is.EqualTo("Complete"));
    }
}
