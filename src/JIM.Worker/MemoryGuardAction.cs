// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker;

/// <summary>What the Worker's memory guard decides from one reading.</summary>
internal enum MemoryGuardAction
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>Memory is low: record a warning on the running tasks' Activities.</summary>
    Warn,

    /// <summary>Memory is nearly exhausted and the Worker holds most of it: stop the running tasks.</summary>
    Stop,

    /// <summary>The stop has not taken effect: record why on the running tasks' Activities and exit.</summary>
    Abort
}
