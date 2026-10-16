// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
namespace JIM.Models.Staging;

public class ConnectedSystemImportObjectAttribute
{
    public string Name { get; set; } = null!;

    public AttributeDataType Type { get; set; }

    public List<string> StringValues { get; set; } = new();

    /// <summary>
    /// References from Connected Systems are handled as strings. 
    /// JIM will then resolve them into hard references to other Connected System Objects as part of an Import operation.
    /// </summary>
    public List<string> ReferenceValues { get; set; } = new();

    public List<int> IntValues { get; set; } = new();

    public List<long> LongValues { get; set; } = new();

    public List<decimal> DecimalValues { get; set; } = new();

    // Allocated on first use, so an attribute of another type carries nothing new (#2153).
    private List<DateTime>? _dateTimeValues;
    private List<bool>? _boolValues;

    /// <summary>
    /// Every date and time value the Connected System holds for the attribute, as the source holds it. Do not
    /// normalise them: JIM puts every date into the form it holds (UTC, to the microsecond) itself, collapses exact
    /// duplicates, and reports any two values it cannot keep apart, rather than a Connector deciding what is lost.
    /// </summary>
    public List<DateTime> DateTimeValues
    {
        get => _dateTimeValues ??= [];
        set => _dateTimeValues = value;
    }

    /// <summary>
    /// Whether the attribute carries any date and time value. Reads without allocating, so prefer it to
    /// <c>DateTimeValues.Count</c> on a hot path.
    /// </summary>
    public bool HasDateTimeValues => _dateTimeValues is { Count: > 0 };

    public List<Guid> GuidValues { get; set; } = new();

    public List<byte[]> ByteValues { get; set; } = new();

    /// <summary>
    /// Every true or false value the Connected System holds for the attribute. A true or false attribute holds one
    /// value: hand JIM whatever the source holds all the same, and JIM collapses a repeated value and reports each value
    /// when both true and false arrive, rather than a Connector choosing one.
    /// </summary>
    public List<bool> BoolValues
    {
        get => _boolValues ??= [];
        set => _boolValues = value;
    }

    /// <summary>
    /// Whether the attribute carries any true or false value. Reads without allocating, so prefer it to
    /// <c>BoolValues.Count</c> on a hot path.
    /// </summary>
    public bool HasBoolValues => _boolValues is { Count: > 0 };

    public override string ToString()
    {
        return Name;
    }
}