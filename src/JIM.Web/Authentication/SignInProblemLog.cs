// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Serilog;

namespace JIM.Web.Authentication;

/// <summary>
/// The sign-in problems JIM has met since it started (#2443), for the log, the page explaining a failed portal sign-in,
/// and <c>Get-JIMSignInDiagnostic</c>. Each distinct problem is held once, with how often and when it was seen.
/// <para>
/// It is bounded three ways, because what it holds comes from sign-in attempts: it keeps at most <see cref="Capacity"/>
/// problems, forgetting the one seen least recently; each value is cut to <see cref="MaxValueLength"/> characters with
/// control characters replaced; and a problem is written to the log at most once every <see cref="LogInterval"/>.
/// </para>
/// </summary>
public sealed class SignInProblemLog
{
    /// <summary>How many distinct problems are held.</summary>
    public const int Capacity = 20;

    /// <summary>The most characters a value is held to.</summary>
    public const int MaxValueLength = 512;

    /// <summary>How often the same problem is written to the log, however often it recurs.</summary>
    public static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(5);

    private const string Ellipsis = "...";

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<SignInProblem> _problems = [];

    /// <param name="time">The clock problems are timed on.</param>
    public SignInProblemLog(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Records a problem JIM has met, and writes it to the log unless it was written within <see cref="LogInterval"/>.
    /// </summary>
    /// <returns>The problem as now held.</returns>
    public SignInProblem Record(SignInProblemReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var expected = Bound(report.Expected);
        var received = Bound(report.Received);
        var now = _time.GetUtcNow();
        SignInProblem problem;
        bool log;

        lock (_lock)
        {
            var index = _problems.FindIndex(p => p.Source == report.Source
                                                 && p.Setting == report.Setting
                                                 && p.Expected == expected
                                                 && p.Received == received);
            if (index >= 0)
            {
                var held = _problems[index];
                log = now - held.LoggedAt >= LogInterval;
                problem = held with { LastSeen = now, Count = held.Count + 1, LoggedAt = log ? now : held.LoggedAt };
                _problems[index] = problem;
            }
            else
            {
                log = true;
                problem = new SignInProblem
                {
                    Id = Guid.NewGuid(),
                    Source = report.Source,
                    Setting = report.Setting,
                    Description = report.Description,
                    Expected = expected,
                    Received = received,
                    Fix = report.Fix,
                    FirstSeen = now,
                    LastSeen = now,
                    Count = 1,
                    LoggedAt = now
                };
                if (_problems.Count >= Capacity)
                    _problems.Remove(_problems.MinBy(p => p.LastSeen)!);
                _problems.Add(problem);
            }
        }

        if (log)
        {
            Log.Warning("Sign-in problem ({Source}, {Setting}): {Description} JIM expected {Expected}, and received {Received}. {Fix} Seen {Count} time(s) since JIM started.",
                problem.Source, problem.Setting, problem.Description, problem.Expected, problem.Received, problem.Fix, problem.Count);
        }

        return problem;
    }

    /// <summary>
    /// The problems held, the one seen most recently first.
    /// </summary>
    public IReadOnlyList<SignInProblem> GetRecent()
    {
        lock (_lock)
            return _problems.OrderByDescending(p => p.LastSeen).ToList();
    }

    /// <summary>
    /// The problem with this identifier, or null when JIM no longer holds it.
    /// </summary>
    public SignInProblem? Find(Guid id)
    {
        lock (_lock)
            return _problems.Find(p => p.Id == id);
    }

    private static string Bound(string value)
    {
        var kept = value.Length > MaxValueLength ? value[..(MaxValueLength - Ellipsis.Length)] + Ellipsis : value;
        return string.Concat(kept.Select(c => char.IsControl(c) ? ' ' : c));
    }
}
