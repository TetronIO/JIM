// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
namespace JIM.Models.Staging;

/// <summary>
/// One attribute of an object a Connector returned, with its values in the list for their type.
/// </summary>
/// <remarks>
/// An attribute uses one of its nine value lists, and a Full Import holds millions of attributes at once, so each list is
/// allocated only when first read or written (#2153 for dates and true or false values, #2521 for the rest): a text
/// attribute carries one list rather than nine. Reading a list's property allocates it, so code that only asks whether an
/// attribute holds values of a type, as the import does for every attribute, reads the matching <c>Has</c> property
/// instead, which allocates nothing. Add to a list through its property as before.
/// </remarks>
public class ConnectedSystemImportObjectAttribute
{
    public string Name { get; set; } = null!;

    public AttributeDataType Type { get; set; }

    // Allocated on first use, so an attribute carries only the list for the type it holds (#2153, #2521).
    private List<string>? _stringValues;
    private List<string>? _referenceValues;
    private List<int>? _intValues;
    private List<long>? _longValues;
    private List<decimal>? _decimalValues;
    private List<DateTime>? _dateTimeValues;
    private List<Guid>? _guidValues;
    private List<byte[]>? _byteValues;
    private List<bool>? _boolValues;

    public List<string> StringValues
    {
        get => _stringValues ??= [];
        set => _stringValues = value;
    }

    /// <summary>
    /// Whether the attribute carries any text value. Reads without allocating, so prefer it to
    /// <c>StringValues.Count</c> on a hot path.
    /// </summary>
    public bool HasStringValues => _stringValues is { Count: > 0 };

    /// <summary>
    /// References from Connected Systems are handled as strings. 
    /// JIM will then resolve them into hard references to other Connected System Objects as part of an Import operation.
    /// </summary>
    public List<string> ReferenceValues
    {
        get => _referenceValues ??= [];
        set => _referenceValues = value;
    }

    /// <summary>
    /// Whether the attribute carries any reference value. Reads without allocating, so prefer it to
    /// <c>ReferenceValues.Count</c> on a hot path.
    /// </summary>
    public bool HasReferenceValues => _referenceValues is { Count: > 0 };

    public List<int> IntValues
    {
        get => _intValues ??= [];
        set => _intValues = value;
    }

    /// <summary>
    /// Whether the attribute carries any number value. Reads without allocating, so prefer it to
    /// <c>IntValues.Count</c> on a hot path.
    /// </summary>
    public bool HasIntValues => _intValues is { Count: > 0 };

    public List<long> LongValues
    {
        get => _longValues ??= [];
        set => _longValues = value;
    }

    /// <summary>
    /// Whether the attribute carries any long number value. Reads without allocating, so prefer it to
    /// <c>LongValues.Count</c> on a hot path.
    /// </summary>
    public bool HasLongValues => _longValues is { Count: > 0 };

    public List<decimal> DecimalValues
    {
        get => _decimalValues ??= [];
        set => _decimalValues = value;
    }

    /// <summary>
    /// Whether the attribute carries any decimal value. Reads without allocating, so prefer it to
    /// <c>DecimalValues.Count</c> on a hot path.
    /// </summary>
    public bool HasDecimalValues => _decimalValues is { Count: > 0 };

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

    public List<Guid> GuidValues
    {
        get => _guidValues ??= [];
        set => _guidValues = value;
    }

    /// <summary>
    /// Whether the attribute carries any GUID value. Reads without allocating, so prefer it to
    /// <c>GuidValues.Count</c> on a hot path.
    /// </summary>
    public bool HasGuidValues => _guidValues is { Count: > 0 };

    public List<byte[]> ByteValues
    {
        get => _byteValues ??= [];
        set => _byteValues = value;
    }

    /// <summary>
    /// Whether the attribute carries any binary value. Reads without allocating, so prefer it to
    /// <c>ByteValues.Count</c> on a hot path.
    /// </summary>
    public bool HasByteValues => _byteValues is { Count: > 0 };

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
