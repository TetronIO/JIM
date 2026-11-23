// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Models.Utility;

/// <summary>
/// Describes the validity status of a JIM object.
/// </summary>
public class ValidityStatus
{
    /// <summary>
    /// Denotes whether or not the object is valid, i.e. can it be persisted and/or executed.
    /// </summary>
    public bool IsValid
    {
        get
        {
            // true if there's no items with a warning or above
            return !Items.Any(q => q.Level > ValidityStatusItemLevel.Warning);
        }
    }

    public List<ValidityStatusItem> Items { get; } = new();

    public void AddItem(ValidityStatusItemLevel level, string message) 
    { 
        Items.Add(new ValidityStatusItem(level, message));
    }
}

public class ValidityStatusItem
{
    public ValidityStatusItemLevel Level { get; set; }

    public string Message { get; set; }

    public ValidityStatusItem(ValidityStatusItemLevel level, string message) 
    {
        Level = level;
        Message = message;
    }

    /// <summary>A finding whose <see cref="Message"/> is <paramref name="detail"/>'s English (#3030).</summary>
    public ValidityStatusItem(ValidityStatusItemLevel level, JimMessage detail)
        : this(level, detail.ToEnglish())
    {
        Detail = detail;
    }

    /// <summary>
    /// <see cref="Message"/> as a message, so the portal can show it in the viewer's language (#3030); null for a
    /// finding built from text alone.
    /// </summary>
    public JimMessage? Detail { get; }
}

public enum ValidityStatusItemLevel
{
    NotSet = 0,
        
    /// <summary>
    /// The object will function, and can be persisted, but it's not in a recommended state.
    /// </summary>
    Warning = 1,
        
    /// <summary>
    /// The object will not function, and cannot be persisted in its current state.
    /// </summary>
    Error = 2
}