// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// What a configuration tester's input holds while it is edited (#2055): its values as text, whether it is explicitly
/// absent ("No value", distinct from empty text), and a value being typed for a multi-valued attribute.
/// </summary>
public sealed class TestInputState
{
    /// <summary>The values, as the text the tester reads as the attribute's type.</summary>
    public List<string> Values { get; private set; } = [];

    /// <summary>Whether the input has no value. An input starts with none.</summary>
    public bool NoValue { get; set; } = true;

    /// <summary>The value being typed for a multi-valued attribute, before it is added.</summary>
    public string? Pending { get; set; }

    /// <summary>
    /// The values the test reads: none when <see cref="NoValue"/> is set, whatever was typed before it was.
    /// </summary>
    public IReadOnlyList<object> TestValues => NoValue ? [] : Values.Cast<object>().ToList();

    /// <summary>
    /// Sets a single-valued input's value. Typing a value means the input has one; clearing the box leaves empty text
    /// until "No value" is set again.
    /// </summary>
    public void SetSingle(string? value)
    {
        Values = [value ?? string.Empty];
        NoValue = false;
    }

    /// <summary>Adds the pending value to a multi-valued input; nothing happens while it is empty.</summary>
    public void AddPending()
    {
        if (string.IsNullOrEmpty(Pending))
            return;

        Values.Add(Pending);
        Pending = null;
        NoValue = false;
    }

    /// <summary>Removes one of a multi-valued input's values; removing the last leaves the input with no value.</summary>
    public void Remove(string value)
    {
        Values.Remove(value);
        if (Values.Count == 0)
            NoValue = true;
    }

    /// <summary>Replaces the values with those loaded from an object; none loaded is no value.</summary>
    public void Load(IEnumerable<string> values)
    {
        Values = values.ToList();
        NoValue = Values.Count == 0;
        Pending = null;
    }
}
