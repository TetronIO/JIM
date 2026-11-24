// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Localisation;

/// <summary>
/// The message catalogues in JIM.Models (#3030): each is a resource file under <c>Resources/</c> whose text is shown
/// to a person through a <see cref="JimMessage"/>, declared once here so no call site writes the string.
/// </summary>
public static class MessageCatalogues
{
    /// <summary>What is wrong with a Connected System's setting values (<c>SettingValidationMessages.resx</c>).</summary>
    public const string SettingValidation = "JIM.Models:SettingValidationMessages";

    /// <summary>How a Scoping Criterion and a relative date read on a chip (<c>ScopingCriteriaMessages.resx</c>).</summary>
    public const string ScopingCriteria = "JIM.Models:ScopingCriteriaMessages";

    public const string SyncRule = "JIM.Models:SyncRuleMessages";

    /// <summary>What a run says when an expression, a scoping criterion or an export rule stops an object (<c>SyncMessages.resx</c>).</summary>
    public const string Sync = "JIM.Models:SyncMessages";

    /// <summary>How a Metaverse Attribute's references read in an Activity and a confirmation dialog (<c>MetaverseReferenceMessages.resx</c>).</summary>
    public const string MetaverseReferences = "JIM.Models:MetaverseReferenceMessages";
}
