// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Scoping;

namespace JIM.Application.Expressions;

/// <summary>
/// One value, offered once to <see cref="CriterionComparer"/>, so an expression function compares a value exactly as a
/// criterion holding that one value would.
/// </summary>
internal struct OneValueCursor(ScopingValue value) : IScopingValueCursor
{
    private bool _taken;

    public bool TryGetNext(out ScopingValue next)
    {
        next = _taken ? default : value;
        var had = !_taken;
        _taken = true;
        return had;
    }
}
