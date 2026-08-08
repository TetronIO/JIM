// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using System.Data.Common;

namespace JIM.Connectors.Sql;

/// <summary>
/// Reads a result set's values in the CLR types JIM's attribute types are, rather than in whichever
/// types the driver inferred for the columns. Decimal is the only attribute type that needs this today,
/// and everything else is handed over exactly as the driver produced it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a Decimal column needs it.</b> ODP.NET chooses the CLR type a <c>NUMBER</c> column
/// materialises as from the column's declared precision and scale, not from the value. Measured against
/// Oracle Database Free 23ai with Oracle.ManagedDataAccess.Core 23.26.300, a scale of zero gives Int16,
/// Int32, Int64 or Decimal by precision, while a column with a scale gives Single up to precision 7,
/// Double from 8 to 15, and Decimal from 16 up. A Decimal attribute exists precisely so that an exact
/// decimal stays exact, and a value that has been through binary floating point on the way in is no
/// longer exact by construction, whatever it happens to round back to.
/// </para>
/// <para>
/// <b>What was and was not observed.</b> No value a NUMBER(7,s) or NUMBER(15,s) column can hold was
/// found to read back wrongly through the inferred type: every 7-significant-digit value was checked
/// exhaustively and the Double band sampled at millions of values, and ODP.NET's precision bands line up
/// exactly with what <see cref="Convert.ToDecimal(object?, IFormatProvider?)"/> preserves from each
/// floating-point type. That alignment is an undocumented property of one driver version rather than
/// anything JIM is promised, so the Connector reads the value the column holds instead of relying on it.
/// </para>
/// <para>
/// <b>How it reads exactly.</b> <see cref="DbDataReader.GetDecimal"/> asks the driver for the column as
/// a decimal, and both drivers JIM speaks to answer it from the value they hold rather than from the CLR
/// type they would otherwise have inferred; against the live container every <c>NUMBER</c> shape came
/// back exact, including the Single- and Double-typed ones, and it measured marginally faster than
/// reading and converting. It is an ADO.NET accessor rather than a driver one, so nothing
/// provider-specific is involved and the dialect seam needs no method for it.
/// <c>GetFieldValue&lt;decimal&gt;</c> is not an alternative: its base implementation casts whatever
/// <see cref="DbDataReader.GetValue"/> returned, so it throws for the very columns this class exists
/// for.
/// </para>
/// <para>
/// <b>Why a driver may refuse, and why that is not an error.</b> A genuinely approximate binary column
/// (Microsoft SQL Server's <c>float</c> and <c>real</c>, Oracle's <c>BINARY_FLOAT</c> and
/// <c>BINARY_DOUBLE</c>) is not a decimal, and both drivers raise an
/// <see cref="InvalidCastException"/> rather than convert one. Those types map to Decimal so that JIM
/// compares them as numbers rather than lexicographically, and the PRD documents that their round trip
/// is not bit-exact; they fall back to the driver's own value, which is all there ever was. The refusal
/// is a property of the column rather than of the row, so it is settled once per result set.
/// </para>
/// <para>
/// <b>A column the driver has already committed to as a decimal is never demoted.</b> Where
/// <see cref="DbDataReader.GetFieldType"/> says decimal, a failure is the value being wider than a CLR
/// decimal can hold rather than the accessor being unsupported, and it is reported as exactly that.
/// </para>
/// </remarks>
internal sealed class SqlValueReader
{
    /// <summary>
    /// What a CLR decimal can hold, and what to do about a column that holds more. Shared with the
    /// approximate-numeric conversion so both routes to the same limit read the same way.
    /// </summary>
    internal const string DecimalRangeAdvice =
        "A Decimal attribute is a 96-bit decimal, which holds 28 to 29 significant digits, and this value has more. " +
        "JIM will not round or truncate it, because a number silently shortened to fit is worse than one that is refused. " +
        "Narrow the column, stop selecting the attribute for synchronisation, or expose the source through a view that casts it to 28 significant digits or fewer.";

    private readonly DbDataReader _reader;
    private readonly string _objectTypeName;
    private readonly ReadStrategy[] _strategies;

    internal SqlValueReader(DbDataReader reader, string objectTypeName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrEmpty(objectTypeName);

        _reader = reader;
        _objectTypeName = objectTypeName;
        _strategies = new ReadStrategy[reader.FieldCount];
    }

    /// <summary>
    /// Reads one column of the row the reader is positioned on.
    /// </summary>
    /// <param name="ordinal">The column's ordinal in the result set.</param>
    /// <param name="columnName">The column's name, for the error a value beyond a decimal's range raises.</param>
    /// <param name="mappedType">The JIM attribute type the column maps to, or null where JIM has none for it (a change log's own sequence and change-type columns).</param>
    /// <exception cref="InvalidDataException">The column holds a number wider than a CLR decimal, which JIM will not shorten to fit.</exception>
    internal object Read(int ordinal, string columnName, AttributeDataType? mappedType)
    {
        if (mappedType != AttributeDataType.Decimal || _strategies[ordinal] == ReadStrategy.DriverValue)
            return _reader.GetValue(ordinal);

        try
        {
            var value = _reader.GetDecimal(ordinal);
            _strategies[ordinal] = ReadStrategy.ExactDecimal;
            return value;
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException)
        {
            // A column the driver already materialises as a decimal has not refused the accessor, so the
            // value is what could not be held. Deciding this on the field type rather than on the
            // exception type matters: ODP.NET reports both cases as an InvalidCastException.
            if (_strategies[ordinal] == ReadStrategy.ExactDecimal || _reader.GetFieldType(ordinal) == typeof(decimal))
                throw BeyondDecimalRange(_objectTypeName, columnName, ex);

            _strategies[ordinal] = ReadStrategy.DriverValue;
            return _reader.GetValue(ordinal);
        }
    }

    /// <summary>
    /// The error a value wider than a CLR decimal raises, naming what an administrator has to change.
    /// </summary>
    /// <remarks>
    /// A run failure rather than one object's error, and deliberately so: the driver refuses the value
    /// while the row is still being materialised, so there is no object to attach an error to yet, and
    /// the column may well be the anchor, in which case the row could not be identified either. Failing
    /// the run states the problem once instead of reporting it half a million times.
    /// </remarks>
    internal static InvalidDataException BeyondDecimalRange(string objectTypeName, string columnName, Exception innerException) =>
        new($"Object Type '{objectTypeName}' has a row whose column '{columnName}' holds a number JIM cannot hold exactly. {DecimalRangeAdvice}", innerException);

    /// <summary>
    /// How a Decimal-mapped column is read, settled the first time a value is read from it.
    /// </summary>
    private enum ReadStrategy
    {
        /// <summary>
        /// The driver has not been asked for this column as a decimal yet.
        /// </summary>
        Untried = 0,

        /// <summary>
        /// The driver answers this column as a decimal, so every value of it is read exactly.
        /// </summary>
        ExactDecimal,

        /// <summary>
        /// The driver refuses this column as a decimal, because it is an approximate binary type. Its
        /// values are taken as the driver produces them.
        /// </summary>
        DriverValue
    }
}
