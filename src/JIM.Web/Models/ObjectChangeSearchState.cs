// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Specialized;
using System.Globalization;
using JIM.Models.History;

namespace JIM.Web.Models;

/// <summary>
/// The Object Changes page's filters, and their round trip through the page's URL so a search can be bookmarked and
/// shared. The display name search is the grid's own (its <c>q</c> parameter), so it is not held here.
/// </summary>
public class ObjectChangeSearchState
{
    private const string FromKey = "from";
    private const string ToKey = "to";
    private const string TypeKey = "type";
    private const string ModeKey = "mode";
    private const string ConditionKey = "c";
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly Dictionary<AttributeChangeOperator, string> OperatorCodes = new()
    {
        [AttributeChangeOperator.AnyChange] = "any",
        [AttributeChangeOperator.ChangedTo] = "to",
        [AttributeChangeOperator.ChangedFrom] = "from"
    };

    /// <summary>
    /// The first day of the range, in the reader's local time; changes from its start are included.
    /// </summary>
    public DateTime? From { get; set; }

    /// <summary>
    /// The last day of the range, in the reader's local time; changes up to its end are included.
    /// </summary>
    public DateTime? To { get; set; }

    public int? ObjectTypeId { get; set; }

    public AttributeConditionMatchMode MatchMode { get; set; } = AttributeConditionMatchMode.All;

    public List<ObjectChangeConditionRow> Conditions { get; set; } = [];

    private IEnumerable<ObjectChangeConditionRow> CompleteConditions => Conditions.Where(c => c.AttributeId.HasValue);

    public bool HasActiveFilters => From.HasValue || To.HasValue || ObjectTypeId.HasValue || CompleteConditions.Any();

    /// <summary>
    /// Reads the filters from a URL's query string. Anything malformed (a hand-edited or truncated link) is ignored
    /// rather than refused, so the page still opens.
    /// </summary>
    public static ObjectChangeSearchState FromQuery(NameValueCollection query)
    {
        var state = new ObjectChangeSearchState
        {
            From = ParseDate(query[FromKey]),
            To = ParseDate(query[ToKey]),
            ObjectTypeId = int.TryParse(query[TypeKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var typeId) ? typeId : null,
            MatchMode = string.Equals(query[ModeKey], "any", StringComparison.OrdinalIgnoreCase)
                ? AttributeConditionMatchMode.Any
                : AttributeConditionMatchMode.All
        };

        state.Conditions.AddRange((query.GetValues(ConditionKey) ?? [])
            .Select(ParseCondition)
            .Where(c => c != null)
            .Select(c => c!));

        return state;
    }

    /// <summary>
    /// Reads one condition, written as <c>attributeId:operator:value</c>, where the value is everything after the
    /// second colon and may itself hold colons. Null when it is malformed.
    /// </summary>
    private static ObjectChangeConditionRow? ParseCondition(string encoded)
    {
        var parts = encoded.Split(':', 3);
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attributeId))
            return null;

        var op = OperatorCodes.FirstOrDefault(o => o.Value == parts[1]);
        if (op.Value == null)
            return null;

        return new ObjectChangeConditionRow
        {
            AttributeId = attributeId,
            Operator = op.Key,
            Value = parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null
        };
    }

    /// <summary>
    /// Writes the filters into a query string, replacing any this page owns and leaving every other parameter alone.
    /// Defaults write nothing, so an unfiltered page keeps a clean URL.
    /// </summary>
    public void WriteTo(NameValueCollection query)
    {
        SetOrRemove(query, FromKey, From?.ToString(DateFormat, CultureInfo.InvariantCulture));
        SetOrRemove(query, ToKey, To?.ToString(DateFormat, CultureInfo.InvariantCulture));
        SetOrRemove(query, TypeKey, ObjectTypeId?.ToString(CultureInfo.InvariantCulture));
        SetOrRemove(query, ModeKey, MatchMode == AttributeConditionMatchMode.Any ? "any" : null);

        query.Remove(ConditionKey);
        foreach (var condition in CompleteConditions)
            query.Add(ConditionKey, $"{condition.AttributeId!.Value.ToString(CultureInfo.InvariantCulture)}:{OperatorCodes[condition.Operator]}:{condition.Value}");
    }

    /// <summary>
    /// The search to run: the complete conditions, the date range widened to cover both days whole and converted to
    /// UTC, and the grid's search text as the display name filter.
    /// </summary>
    public MetaverseObjectChangeSearchCriteria ToCriteria(string? displayNameSearch) => new()
    {
        FromDate = From.HasValue ? DateTime.SpecifyKind(From.Value.Date, DateTimeKind.Local).ToUniversalTime() : null,
        ToDate = To.HasValue ? DateTime.SpecifyKind(To.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local).ToUniversalTime() : null,
        ObjectTypeId = ObjectTypeId,
        DisplayNameSearch = string.IsNullOrWhiteSpace(displayNameSearch) ? null : displayNameSearch.Trim(),
        MatchMode = MatchMode,
        AttributeConditions = CompleteConditions
            .Select(c => new MetaverseObjectChangeAttributeCondition
            {
                AttributeId = c.AttributeId!.Value,
                Operator = c.Operator,
                Value = string.IsNullOrWhiteSpace(c.Value) ? null : c.Value
            })
            .ToList()
    };

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static void SetOrRemove(NameValueCollection query, string key, string? value)
    {
        if (value == null)
            query.Remove(key);
        else
            query[key] = value;
    }
}
