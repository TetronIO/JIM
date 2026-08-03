// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Interfaces;

/// <summary>
/// How a Connector tells JIM what it is doing while a Run Profile executes. Handed to every
/// interaction interface, and never null, so a Connector can narrate without ceremony.
/// </summary>
/// <remarks>
/// <para>
/// JIM's own object counts cannot move while a Connector call is in flight (nothing has been
/// returned yet), so this is the only thing distinguishing a healthy long phase from a stuck run.
/// </para>
/// <para>
/// Reporting is cosmetic and JIM treats it that way: emits are serialised (safe to call from
/// parallel internal work), a failure to record one is logged and swallowed rather than failing
/// the run, and blank messages are ignored. Cancellation still propagates, so an aborting run
/// keeps unwinding.
/// </para>
/// </remarks>
public interface IConnectorProgress
{
    /// <summary>
    /// Moves to one of the phases this Connector declared through
    /// <see cref="IConnectorPhases"/>, which shows as the step now running inside the JIM phase
    /// that called the Connector.
    /// </summary>
    /// <param name="phaseKey">
    /// The Connector's own phase key, exactly as declared. A key that was never declared still
    /// shows up, appended as an extra step, rather than being dropped.
    /// </param>
    /// <param name="message">
    /// Optional narration to show alongside the step, for detail the step's name cannot carry
    /// ("Fetching User objects from Employees (page 3)..."). Omit it to show the step's name.
    /// </param>
    Task EnterPhaseAsync(string phaseKey, string? message = null);

    /// <summary>
    /// Narrates progress within the phase currently running, without moving to another one. Use it
    /// for a phase that takes a while and has something to say the object counts cannot ("Reading
    /// the EMEA region...").
    /// </summary>
    /// <remarks>
    /// Emit on phase and page boundaries rather than per object: each emit is a small write, and a
    /// message nobody can read at speed is worse than one that changes every few seconds. How many
    /// objects have arrived, how fast and how long is left are rendered from the counts reported
    /// through <see cref="ReportObjectsReadAsync"/> and <see cref="ReportExpectedObjectCountAsync"/>,
    /// so a message repeating any of them says the same thing twice.
    /// </remarks>
    Task ReportAsync(string message);

    /// <summary>
    /// States how many objects this Connector will hand over in total for this run, once it knows.
    /// Report it and the Activity shows a real percentage and a time remaining from that moment;
    /// say nothing and the run counts up without either, which is all JIM can honestly show.
    /// </summary>
    /// <param name="objectCount">
    /// The whole run's expected object count, not the current page's. Report it again to correct
    /// it: a figure that turns out to be short is raised to what has actually arrived rather than
    /// letting a bar read past complete.
    /// </param>
    /// <remarks>
    /// Recommended wherever the Connected System can be asked cheaply: a file's rows can be counted
    /// before they are parsed, and a query's result set is often stated in its response. A
    /// Connector that would have to do its own work twice to answer should say nothing instead.
    /// </remarks>
    Task ReportExpectedObjectCountAsync(int objectCount);

    /// <summary>
    /// Reports how many objects this Connector has read so far within the call it is currently
    /// serving, so the Activity's counters move while a long call is in flight rather than only
    /// when it returns. JIM adds this to the objects earlier calls already delivered.
    /// </summary>
    /// <remarks>
    /// Worth reporting for a Connector that returns everything in one call, where otherwise nothing
    /// moves until the whole system has been read. A Connector that returns a page at a time gains
    /// little: JIM counts each page as it arrives.
    /// </remarks>
    Task ReportObjectsReadAsync(int objectCount);
}
