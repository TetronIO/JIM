// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Localisation;

public static partial class ApplicationMessageCatalogues
{
    /// <summary>
    /// What a stored Activity or Run Profile Execution Item says about a failure, warning or refusal (#3039,
    /// <c>ActivityMessages.resx</c>). Held here even for text the Worker and the Scheduler write, since the portal that
    /// renders it references this assembly and not theirs.
    /// </summary>
    public const string Activities = "JIM.Application:ActivityMessages";
}
