// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;
using JIM.Models.Localisation;

namespace JIM.Models.Preview;

/// <summary>
/// Something structurally wrong, contradictory, or worth knowing about a proposed configuration, found before any
/// population is evaluated. Stage 1 runs synchronously and near-instantly, so an administrator learns their proposal
/// is invalid immediately rather than after waiting for a preview that could never have been meaningful.
/// </summary>
/// <param name="Severity">
/// How much it matters. Only <see cref="PreviewValidationSeverity.Blocking"/> prevents the change being applied.
/// </param>
/// <param name="Message">
/// What is wrong, in the terms an administrator would use. Never contains attribute values or anything else that
/// counts as personal data; a finding is about the configuration, not about the objects it would affect.
/// </param>
/// <param name="PropertyName">
/// The configuration property concerned, where the finding is about one, so the editor can point at the field
/// rather than leaving the administrator to find it.
/// </param>
/// <param name="MetaverseAttributeName">
/// The Metaverse Attribute the finding is about, where it is about one. The name also appears quoted inside
/// <paramref name="Message"/> so plain-text consumers (REST, PowerShell) read a complete sentence; the portal
/// uses this field to render the quoted name as the standard Metaverse attribute chip instead.
/// </param>
public record PreviewValidationFinding(
    PreviewValidationSeverity Severity,
    string Message,
    string? PropertyName = null,
    string? MetaverseAttributeName = null)
{
    /// <summary>
    /// The finding as a <see cref="JimMessage"/>, for the portal to show in the viewer's language (#3030); null for a
    /// finding recorded as English only. Never serialised with the finding: the REST API keeps <see cref="Message"/>
    /// only, and the portal's stored copy carries the detail beside it (<see cref="StoredPreviewValidationFinding"/>).
    /// </summary>
    [JsonIgnore]
    public JimMessage? Detail { get; init; }

    /// <summary>
    /// A finding worded as a message. <see cref="Message"/> is the message's British English, so everything that
    /// reads the English (the REST API, PowerShell, logs) sees what it always did.
    /// </summary>
    public static PreviewValidationFinding Of(PreviewValidationSeverity severity, JimMessage detail,
        string? propertyName = null, string? metaverseAttributeName = null) =>
        new(severity, detail.ToEnglish(), propertyName, metaverseAttributeName) { Detail = detail };
}
