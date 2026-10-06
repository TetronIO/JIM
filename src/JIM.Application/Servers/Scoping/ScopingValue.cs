// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// One attribute value as scoping reads it, whichever side it came from. The typed slots mirror those on Metaverse
/// and Connected System attribute values; the criterion's attribute type decides which one is compared.
/// </summary>
internal readonly record struct ScopingValue(
    string? StringValue,
    int? IntValue,
    long? LongValue,
    decimal? DecimalValue,
    DateTime? DateTimeValue,
    bool? BoolValue,
    Guid? GuidValue);
