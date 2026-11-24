// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Models.Preview;

/// <summary>
/// A <see cref="PreviewValidationFinding"/> as the preview stores it (#3030): the finding's own fields, unchanged, plus
/// its message so a finding saved once can be read in any language later. A separate type so the REST API, which
/// returns <see cref="PreviewValidationFinding"/>, never gains a property; a document stored before messages existed
/// reads as a finding with no detail.
/// </summary>
public sealed class StoredPreviewValidationFinding
{
    public PreviewValidationSeverity Severity { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? PropertyName { get; set; }

    public string? MetaverseAttributeName { get; set; }

    /// <summary>The message as JSON (<see cref="MessageJson"/>), or null for a finding with English only.</summary>
    public string? DetailJson { get; set; }

    public static StoredPreviewValidationFinding From(PreviewValidationFinding finding) => new()
    {
        Severity = finding.Severity,
        Message = finding.Message,
        PropertyName = finding.PropertyName,
        MetaverseAttributeName = finding.MetaverseAttributeName,
        DetailJson = MessageJson.Serialise(finding.Detail)
    };

    public PreviewValidationFinding ToFinding() =>
        new(Severity, Message, PropertyName, MetaverseAttributeName) { Detail = MessageJson.Deserialise(DetailJson) };
}
