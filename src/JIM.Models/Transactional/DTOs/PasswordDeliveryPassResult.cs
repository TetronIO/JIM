// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What one delivery pass achieved across every Connected System it visited (#1119).
/// <para>
/// The per-system counts of <see cref="PasswordDeliveryRunResult"/> rolled up, plus the problems that stopped a
/// system being delivered to at all. Problems are collected rather than thrown for the same reason the per-system
/// pass reports rather than throws: one unreachable directory must not abandon the systems the pass has not
/// reached yet.
/// </para>
/// </summary>
public class PasswordDeliveryPassResult
{
    /// <summary>Connected Systems the pass actually attempted a delivery pass over.</summary>
    public int ConnectedSystemsVisited { get; private set; }

    /// <summary>Changes delivered and removed from the queue.</summary>
    public int DeliveredCount { get; private set; }

    /// <summary>Changes that failed in a way another attempt may resolve, and are scheduled to be retried.</summary>
    public int RetryingCount { get; private set; }

    /// <summary>Changes JIM has stopped trying, each awaiting an administrator.</summary>
    public int ParkedCount { get; private set; }

    /// <summary>Changes that outlived their time to live and were expired rather than attempted.</summary>
    public int ExpiredCount { get; private set; }

    /// <summary>
    /// Systems the pass could not deliver to at all, one line each, named so an administrator reading the
    /// Activity knows where to look. Never contains a password or anything derived from one.
    /// </summary>
    public List<string> Problems { get; } = [];

    /// <summary>
    /// Whether this pass has anything worth telling an administrator about.
    /// </summary>
    public bool HasSomethingToReport =>
        DeliveredCount > 0 || RetryingCount > 0 || ParkedCount > 0 || ExpiredCount > 0 || Problems.Count > 0;

    /// <summary>
    /// Rolls one Connected System's pass into the total, recording anything that stopped it delivering.
    /// </summary>
    /// <param name="connectedSystemName">The Connected System's name, for any problem recorded against it.</param>
    /// <param name="result">What that system's pass achieved.</param>
    public void Add(string connectedSystemName, PasswordDeliveryRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        ConnectedSystemsVisited++;
        DeliveredCount += result.DeliveredCount;
        RetryingCount += result.RetryingCount;
        ParkedCount += result.ParkedCount;
        ExpiredCount += result.ExpiredCount;

        if (result.CouldNotOpenPasswordConnection)
            Problems.Add($"{connectedSystemName}: the password channel could not be opened, so nothing was attempted.");

        if (result.ConnectorCannotSetPasswords)
            Problems.Add($"{connectedSystemName}: this system's Connector cannot set passwords, so queued password changes are waiting.");

        if (result.PasswordChannelNotSecure)
            Problems.Add($"{connectedSystemName}: this system requires a secure transport for passwords and the connection is not encrypted, so nothing was sent.");

        if (result.ConnectorCouldNotBeResolved)
            Problems.Add($"{connectedSystemName}: its Connector could not be resolved, so queued password changes are waiting.");
    }

    /// <summary>
    /// Records a Connected System the pass could not even begin on, so it is visible rather than merely logged.
    /// </summary>
    public void AddProblem(string problem)
    {
        Problems.Add(problem);
    }

    /// <summary>
    /// A one-line summary for the Activity, or null where the pass achieved nothing and has nothing to say.
    /// Returning null rather than "nothing to do" is deliberate: an Activity message that says nothing happened
    /// reads as an outcome, and the housekeeping pass runs whether or not there is work.
    /// </summary>
    public string? Describe()
    {
        if (!HasSomethingToReport)
            return null;

        var parts = new List<string>();

        if (DeliveredCount > 0)
            parts.Add($"{DeliveredCount} password change(s) delivered");
        if (RetryingCount > 0)
            parts.Add($"{RetryingCount} will be retried");
        if (ParkedCount > 0)
            parts.Add($"{ParkedCount} parked for review");
        if (ExpiredCount > 0)
            parts.Add($"{ExpiredCount} expired before they could be delivered");

        var summary = parts.Count > 0
            ? string.Join(", ", parts) + "."
            : string.Empty;

        if (Problems.Count == 0)
            return summary;

        var problems = string.Join(" ", Problems);
        return summary.Length > 0 ? $"{summary} {problems}" : problems;
    }
}
