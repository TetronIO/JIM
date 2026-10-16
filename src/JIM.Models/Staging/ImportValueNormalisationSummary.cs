// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What putting an import's values into the form JIM holds them did, counted across a run for its end-of-import summary
/// (#2153).
/// </summary>
public class ImportValueNormalisationSummary
{
    /// <summary>Dates that changed form: a sub-microsecond part dropped, or a time zone other than UTC.</summary>
    public int DatesCanonicalised { get; set; }

    /// <summary>Values dropped because the same attribute already carried exactly that value.</summary>
    public int DuplicatesCollapsed { get; set; }

    /// <summary>Dates reported because another of the attribute's values was less than a microsecond away.</summary>
    public int CollisionsReported { get; set; }

    /// <summary>True or false values reported because the attribute carried both.</summary>
    public int BooleanSurplusesReported { get; set; }
}
