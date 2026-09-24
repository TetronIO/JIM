// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Text.Json;
using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
using JIM.Web;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST shapes of Schedule failure handling (#1787): the new enums travel by member name, like every enum on the
/// API, so the names are the published contract; the request fields are nullable so "absent" can be told apart from a
/// value; and the execution step DTO reports where its effective behaviour came from.
/// </summary>
[TestFixture]
public class ScheduleFailureBehaviourDtoTests
{
    private static JsonSerializerOptions ApiOptions()
    {
        var options = new JsonSerializerOptions();
        ApiJsonConfiguration.Configure(options);
        return options;
    }

    [Test]
    public void ScheduleDto_SerialisesOnStepFailureByName()
    {
        var json = JsonSerializer.Serialize(new ScheduleDto { OnStepFailure = ScheduleFailureBehaviour.Continue }, ApiOptions());

        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty(nameof(ScheduleDto.OnStepFailure)).GetString(), Is.EqualTo("Continue"));
    }

    [Test]
    public void ScheduleStepDto_SerialisesOnFailureAndSourceByName()
    {
        var dto = new ScheduleStepDto
        {
            OnFailure = ScheduleStepFailureBehaviour.FollowSchedule,
            ContinueOnFailure = true,
            FailureBehaviourSource = ScheduleFailureBehaviourSource.Schedule
        };

        var json = JsonSerializer.Serialize(dto, ApiOptions());

        using var document = JsonDocument.Parse(json);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.RootElement.GetProperty(nameof(ScheduleStepDto.OnFailure)).GetString(), Is.EqualTo("FollowSchedule"));
            Assert.That(document.RootElement.GetProperty(nameof(ScheduleStepDto.ContinueOnFailure)).GetBoolean(), Is.True);
            Assert.That(document.RootElement.GetProperty(nameof(ScheduleStepDto.FailureBehaviourSource)).GetString(), Is.EqualTo("Schedule"));
        }
    }

    [Test]
    public void ScheduleStepRequest_WithNeitherFailureField_DeserialisesBothAsAbsent()
    {
        // Absent must stay distinguishable from false: the write rule keeps an existing step's setting when neither is sent.
        var request = JsonSerializer.Deserialize<ScheduleStepRequest>("{\"StepIndex\":0,\"StepType\":\"RunProfile\"}", ApiOptions());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request!.OnFailure, Is.Null);
            Assert.That(request.ContinueOnFailure, Is.Null);
        }
    }

    [Test]
    public void ScheduleStepRequest_ExistingClientPayload_StillBindsContinueOnFailure()
    {
        var request = JsonSerializer.Deserialize<ScheduleStepRequest>("{\"StepIndex\":0,\"StepType\":\"RunProfile\",\"ContinueOnFailure\":false}", ApiOptions());

        Assert.That(request!.ContinueOnFailure, Is.False);
    }

    [Test]
    public void ScheduleStepRequest_OnFailureByName_Binds()
    {
        var request = JsonSerializer.Deserialize<ScheduleStepRequest>("{\"StepIndex\":0,\"StepType\":\"RunProfile\",\"OnFailure\":\"Stop\"}", ApiOptions());

        Assert.That(request!.OnFailure, Is.EqualTo(ScheduleStepFailureBehaviour.Stop));
    }

    [Test]
    public void ScheduleStepRequest_OnFailureAsAnInteger_IsRejected()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ScheduleStepRequest>("{\"StepIndex\":0,\"StepType\":\"RunProfile\",\"OnFailure\":1}", ApiOptions()));
    }

    [Test]
    public void UpdateScheduleRequest_WithoutOnStepFailure_DeserialisesItAsAbsent()
    {
        var request = JsonSerializer.Deserialize<UpdateScheduleRequest>("{\"Name\":\"Nightly\",\"Steps\":[]}", ApiOptions());

        Assert.That(request!.OnStepFailure, Is.Null);
    }

    [Test]
    public void ScheduleExecutionStepDto_FromModel_CarriesEffectiveBehaviourAndItsSource()
    {
        var state = new ScheduleExecutionStepState
        {
            StepIndex = 2,
            Name = "Active Directory / Export",
            ContinueOnFailure = true,
            FailureBehaviourSource = ScheduleFailureBehaviourSource.Schedule
        };

        var dto = ScheduleExecutionStepDto.FromModel(state);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.ContinueOnFailure, Is.True);
            Assert.That(dto.FailureBehaviourSource, Is.EqualTo(ScheduleFailureBehaviourSource.Schedule));
        }
    }

    [Test]
    public void ScheduleDto_FromHeader_NeverRun_CarriesAnEmptyFailedStepIndicesList()
    {
        var dto = ScheduleDto.FromHeader(new ScheduleHeader { Name = "Never run" });

        Assert.That(dto.LastExecutionFailedStepIndices, Is.EqualTo(new List<int>()));
    }
}
