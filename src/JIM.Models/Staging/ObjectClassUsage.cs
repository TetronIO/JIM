// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What to sample when finding out which auxiliary classes a Connected System's entries actually carry.
/// </summary>
/// <remarks>
/// On an RFC 4512 directory an auxiliary class attaches to an entry, so the only way to learn which ones are in use
/// is to look at the entries themselves. That is a read of the whole population in the worst case, which is why it
/// is a worker task with a scope an administrator chooses rather than something schema discovery does.
/// </remarks>
public class ObjectClassUsageRequest
{
    /// <summary>
    /// The structural class whose entries are being sampled, named as the Connected System names it.
    /// </summary>
    public string ObjectTypeName { get; set; } = null!;

    /// <summary>
    /// Stop after this many entries. Null means read every entry of this class, which is the full scan scope.
    /// </summary>
    /// <remarks>
    /// A sample answers "which auxiliary classes are in use here" for a fraction of the cost, and is enough to
    /// configure a system with. It cannot answer "which entries lack one", so a count from a sample is a lower
    /// bound and is recorded as such.
    /// </remarks>
    public int? MaximumEntries { get; set; }

    /// <summary>
    /// How many entries to ask the Connected System for at a time.
    /// </summary>
    public int PageSize { get; set; } = 500;
}

/// <summary>
/// What a sample found: how many of the entries read carried each object class.
/// </summary>
/// <remarks>
/// Deliberately every class, not just the auxiliary ones. Which classes are auxiliary is JIM's knowledge, held as
/// classification tags on the Object Types, and a Connector that filtered on its own understanding of that would be
/// a second place for the two to disagree.
/// </remarks>
public class ObjectClassUsageResult
{
    /// <summary>
    /// Object class name to the number of entries read that carried it.
    /// </summary>
    public Dictionary<string, int> ObjectClassCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many entries were read.
    /// </summary>
    public int EntriesRead { get; set; }

    /// <summary>
    /// Whether the Connected System had more entries of this class than were read, because the sample limit was
    /// reached or the read was cancelled.
    /// </summary>
    /// <remarks>
    /// This is what makes the difference between "no entry carries posixAccount" and "no entry we looked at carried
    /// posixAccount", which are very different things to show an administrator.
    /// </remarks>
    public bool Partial { get; set; }
}
