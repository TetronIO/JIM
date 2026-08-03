// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Records what a Connector narrated, so a test can assert on the steps it entered and the messages
/// it wrote (#454). The production reporter guards and serialises emits; this one deliberately does
/// neither, so a test sees exactly what the Connector did.
/// </summary>
public class RecordingConnectorProgress : IConnectorProgress
{
    private readonly Func<string, Task>? _onReport;

    public RecordingConnectorProgress(Func<string, Task>? onReport = null)
    {
        _onReport = onReport;
    }

    /// <summary>
    /// Every phase key entered, in order.
    /// </summary>
    public List<string> PhaseKeys { get; } = [];

    /// <summary>
    /// Every message the Connector produced, in order, whether narrated on its own or alongside a
    /// phase change.
    /// </summary>
    public List<string> Messages { get; } = [];

    /// <summary>
    /// Each phase change as it happened, with the message that came with it (null where the
    /// Connector left the step's own name to speak for itself).
    /// </summary>
    public List<(string PhaseKey, string? Message)> Transitions { get; } = [];

    public Task EnterPhaseAsync(string phaseKey, string? message = null)
    {
        PhaseKeys.Add(phaseKey);
        Transitions.Add((phaseKey, message));
        return message == null ? Task.CompletedTask : ReportAsync(message);
    }

    /// <summary>
    /// Every expected object count the Connector stated, in order.
    /// </summary>
    public List<int> ExpectedObjectCounts { get; } = [];

    /// <summary>
    /// Every running count of objects produced that the Connector reported, in order.
    /// </summary>
    public List<int> ObjectsProduced { get; } = [];

    public Task ReportAsync(string message)
    {
        Messages.Add(message);
        return _onReport?.Invoke(message) ?? Task.CompletedTask;
    }

    public Task ReportExpectedObjectCountAsync(int objectCount)
    {
        ExpectedObjectCounts.Add(objectCount);
        return Task.CompletedTask;
    }

    public Task ReportObjectsProducedAsync(int objectCount)
    {
        ObjectsProduced.Add(objectCount);
        return Task.CompletedTask;
    }
}
