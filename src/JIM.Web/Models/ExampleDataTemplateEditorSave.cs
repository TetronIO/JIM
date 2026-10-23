// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Exceptions;

namespace JIM.Web.Models;

/// <summary>
/// How every portal edit to a Data Generation Template is saved (#1466): load the template fresh, apply one edit
/// from <see cref="ExampleDataTemplateGraphEdits"/>, and save the whole graph through
/// <c>ExampleDataServer.UpdateTemplateAsync</c>, which validates it, records an Activity and a configuration snapshot.
/// </summary>
public static class ExampleDataTemplateEditorSave
{
    /// <summary>The outcome of a save: why it was refused, or the warnings the server saved it with.</summary>
    public sealed record Result(string? Error, IReadOnlyList<string> Warnings)
    {
        public bool Succeeded => Error == null;
    }

    public static async Task<Result> ApplyAsync(JimApplication jim, int templateId, Action<ExampleDataTemplate> edit,
        MetaverseObject user, string? changeReason, bool replaceObjectTypes = true)
    {
        var template = await jim.ExampleData.GetTemplateAsync(templateId);
        if (template == null)
            return new Result("This Data Generation Template no longer exists.", []);

        // the REST API refuses the same; the portal hides its editing controls for these, so this guards a stale page.
        if (template.BuiltIn)
            return new Result("Built-in Data Generation Templates cannot be changed. Duplicate it to make a version you can change.", []);

        try
        {
            edit(template);
            await jim.ExampleData.UpdateTemplateAsync(template, ActivityInitiatorType.User, user.Id, user.NameOrId, changeReason, replaceObjectTypes);
        }
        catch (ExampleDataTemplateChangedException ex)
        {
            return new Result(ex.Message, []);
        }
        catch (ExampleDataTemplateException ex)
        {
            return new Result(ex.Message, []);
        }
        catch (ExampleDataTemplateAttributeException ex)
        {
            return new Result(ex.Message, []);
        }
        catch (InvalidOperationException ex)
        {
            // a name another template already holds, the last Object Type, or a reference that no longer exists.
            return new Result(ex.Message, []);
        }

        return new Result(null, template.SaveWarnings.ToList());
    }
}
