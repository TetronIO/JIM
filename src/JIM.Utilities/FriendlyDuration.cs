// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;

namespace JIM.Utilities;

/// <summary>
/// Writes a <see cref="TimeSpan"/> the way a person configured it. Lives here rather than in JIM.Web because the
/// values that need it are composed where they are decided: a Configuration Change Preview's delta strings are
/// built in JIM.Application, persisted, and read back by the portal, the REST API and PowerShell alike, so
/// formatting at the point of display would leave three surfaces to keep in step.
/// </summary>
public static class FriendlyDuration
{
    /// <summary>
    /// Renders a duration as written English ("45 minutes", "1 hour 30 minutes", "2 days"), stopping at the two
    /// largest non-zero units. Zero reads as "immediately"; a negative duration is written as its magnitude.
    /// </summary>
    public static string ToFriendlyDuration(this TimeSpan duration)
    {
        if (duration == TimeSpan.Zero)
            return "immediately";

        // A negative grace period should not exist (validation rejects one), but rendering "-1 hour -30 minutes"
        // would be a worse answer than its magnitude if one ever reached a screen.
        if (duration < TimeSpan.Zero)
            duration = duration.Negate();

        var units = new (int Value, string Singular)[]
        {
            ((int)duration.TotalDays, "day"),
            (duration.Hours, "hour"),
            (duration.Minutes, "minute"),
            (duration.Seconds, "second")
        };

        var written = new StringBuilder();
        var partsWritten = 0;

        // Two units is the point where the answer stops helping: "1 day 2 hours" is what someone wants to know,
        // and the trailing minutes and seconds at that scale are noise. Leading zero units are skipped so a
        // sub-day duration is not padded with "0 days".
        foreach (var (value, singular) in units.SkipWhile(u => u.Value == 0))
        {
            if (partsWritten == 2)
                break;

            // An interior zero (90 minutes exactly, so zero seconds) simply contributes nothing rather than
            // consuming one of the two slots.
            if (value == 0)
                continue;

            if (partsWritten > 0)
                written.Append(' ');

            written.Append(value).Append(' ').Append(singular);
            if (value != 1)
                written.Append('s');

            partsWritten++;
        }

        return written.ToString();
    }
}
