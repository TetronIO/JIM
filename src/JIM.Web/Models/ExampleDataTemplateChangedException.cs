// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// Thrown when a portal edit names a row of a Data Generation Template that the freshly loaded template no longer has:
/// every save replaces the template's rows, so the template was saved elsewhere since the page loaded it.
/// </summary>
public class ExampleDataTemplateChangedException : Exception
{
    public ExampleDataTemplateChangedException()
        : base(DisplayText.TemplateChangedSincePageLoaded)
    {
    }
}
