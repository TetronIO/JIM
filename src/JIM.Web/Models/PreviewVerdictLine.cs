// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using MudBlazor;

namespace JIM.Web.Models;

/// <summary>
/// One sentence of a Configuration Change Preview's verdict, with the tone that ordered it: how a space too small for
/// the verdict's paragraph (the delete dialog's preview slot, #134) lists the same statements in the same order.
/// </summary>
/// <param name="Severity">The tone of the transition the sentence states.</param>
/// <param name="Sentence">"N objects would ...".</param>
public sealed record PreviewVerdictLine(Severity Severity, string Sentence);
