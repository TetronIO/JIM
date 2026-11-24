// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Activities;
using JIM.Models.Localisation;
using JIM.Web.Localisation;

namespace JIM.Web.Causality;

/// <summary>
/// Shows a synchronisation outcome's detail in the viewer's language where JIM worded it (#3094).
/// </summary>
/// <remarks>
/// A sync outcome stores its detail as English in <c>DetailMessage</c>, a column that is also a channel for other
/// things (a target Connected System's id, an attribute and its value) and that the REST API returns as it is, so a
/// message cannot be stored beside it without a migration. The sentence for a value Collision Remediation corrected is
/// written from a message (<see cref="ActivityMessage.CollisionRemediated"/>), so it is read back into that message:
/// the English template is matched against the stored text, the four values it was built from are taken out, and the
/// message is rebuilt and shown in the viewer's language. The rebuilt message must render exactly the stored English,
/// or the stored text is shown as it is, so a sentence written by another release, or one a value made ambiguous, is
/// never misrepresented. A later migration adding a detail column would replace this.
/// </remarks>
public static class OutcomeDetailLocaliser
{
    private static readonly Lazy<Regex?> CollisionRemediatedPattern = new(BuildCollisionRemediatedPattern);

    /// <summary>
    /// The outcome's detail as the viewer should read it: the stored English for every outcome but a corrected generated
    /// value, whose sentence is shown in the viewer's language.
    /// </summary>
    /// <param name="outcomeType">The outcome's type.</param>
    /// <param name="storedDetail">The detail as stored, in British English.</param>
    public static string? Localise(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType, string? storedDetail)
    {
        if (string.IsNullOrEmpty(storedDetail) || outcomeType != ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated)
            return storedDetail;

        Match? match;
        try
        {
            match = CollisionRemediatedPattern.Value?.Match(storedDetail);
        }
        catch (RegexMatchTimeoutException)
        {
            return storedDetail;
        }

        if (match is not { Success: true })
            return storedDetail;

        var message = ActivityMessage.CollisionRemediated(
            match.Groups["a0"].Value, match.Groups["a1"].Value, match.Groups["a2"].Value, match.Groups["a3"].Value);

        return message.ToEnglish() == storedDetail ? message.Show() : storedDetail;
    }

    private static Regex? BuildCollisionRemediatedPattern()
    {
        var template = MessageRenderer.CatalogueFor(ApplicationMessageCatalogues.Activities)
            ?.GetString(nameof(ActivityMessages.CollisionRemediated), MessageRenderer.English);
        if (template == null)
            return null;

        var pattern = Regex.Escape(template);
        for (var i = 0; i < 4; i++)
            pattern = pattern.Replace($"\\{{{i}}}", $"(?<a{i}>.+?)", StringComparison.Ordinal);

        return new Regex($"^{pattern}$", RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
    }
}
