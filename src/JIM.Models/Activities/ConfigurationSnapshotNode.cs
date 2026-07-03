// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// One node in a <see cref="ConfigurationSnapshot"/> tree: a scalar field, a nested object, or a collection of objects.
/// The same node shape is used for capture, diffing and rendering.
/// </summary>
public class ConfigurationSnapshotNode
{
    /// <summary>
    /// Stable machine key identifying this node among its siblings (typically the property or collection name).
    /// Used by the diff engine to match nodes across versions.
    /// </summary>
    public string Key { get; set; } = null!;

    /// <summary>
    /// A friendly, human-readable label for display. Falls back to <see cref="Key"/> when not set.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Whether this node is a scalar value, a nested object, or a collection of objects.
    /// </summary>
    public ConfigurationSnapshotNodeType NodeType { get; set; }

    /// <summary>
    /// The string-rendered value for a <see cref="ConfigurationSnapshotNodeType.Scalar"/> node; null for objects and
    /// collections, and null when the scalar itself has no value. When <see cref="IsSecret"/> is true, this holds a
    /// keyed hash, never the actual value.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// An optional human-friendly rendering of <see cref="Value"/> for display, captured at snapshot time so it reflects
    /// the value as it was at that version: a foreign-key reference reads as "Name (id)" and an enum is spaced into words.
    /// Null when the raw <see cref="Value"/> is already display-ready (plain text, numbers, booleans) or could not be
    /// resolved. Diffing always compares the raw <see cref="Value"/>, never this; this is presentation only.
    /// </summary>
    public string? DisplayValue { get; set; }

    /// <summary>
    /// True when this scalar represents a secret (e.g. an encrypted Connected System setting). <see cref="Value"/> then
    /// holds a keyed hash so a change can be detected and shown without disclosing the secret.
    /// </summary>
    public bool IsSecret { get; set; }

    /// <summary>
    /// For an item within an integer-keyed collection, the stable integer database identifier of the underlying entity,
    /// so the diff engine can match items across versions even when reordered. Null for nodes that are not collection
    /// items, and for Guid-keyed collection items (which use <see cref="ItemGuidId"/>).
    /// </summary>
    public int? ItemId { get; set; }

    /// <summary>
    /// For an item within a Guid-keyed collection (e.g. a Schedule Step), the stable Guid database identifier of the
    /// underlying entity, so the diff engine can match items across versions even when reordered. Null for integer-keyed
    /// items and for nodes that are not collection items. A Guid item id is essential where the item has no unique
    /// integer key: a Schedule Step's StepIndex is not unique (parallel steps share one), so only the Guid identifies it.
    /// </summary>
    public Guid? ItemGuidId { get; set; }

    /// <summary>
    /// Child nodes for Object and Collection nodes. Null for scalars.
    /// </summary>
    public List<ConfigurationSnapshotNode>? Children { get; set; }

    /// <summary>Creates a scalar node, optionally with a human-friendly <paramref name="displayValue"/> for presentation.</summary>
    public static ConfigurationSnapshotNode Scalar(string key, string? value, string? label = null, string? displayValue = null) =>
        new() { Key = key, Label = label, NodeType = ConfigurationSnapshotNodeType.Scalar, Value = value, DisplayValue = displayValue };

    /// <summary>Creates a redacted secret scalar node whose value is a keyed hash, never the secret itself.</summary>
    public static ConfigurationSnapshotNode Secret(string key, string? valueHash, string? label = null) =>
        new() { Key = key, Label = label, NodeType = ConfigurationSnapshotNodeType.Scalar, Value = valueHash, IsSecret = true };

    /// <summary>Creates a nested object node, optionally an integer-keyed collection item (<paramref name="itemId"/>).</summary>
    public static ConfigurationSnapshotNode ObjectNode(string key, List<ConfigurationSnapshotNode> children, string? label = null, int? itemId = null) =>
        new() { Key = key, Label = label, NodeType = ConfigurationSnapshotNodeType.Object, ItemId = itemId, Children = children };

    /// <summary>Creates a nested object node that is a Guid-keyed collection item (e.g. a Schedule Step).</summary>
    public static ConfigurationSnapshotNode ObjectNode(string key, List<ConfigurationSnapshotNode> children, string? label, Guid itemGuidId) =>
        new() { Key = key, Label = label, NodeType = ConfigurationSnapshotNodeType.Object, ItemGuidId = itemGuidId, Children = children };

    /// <summary>Creates a collection node whose children are the collection's item nodes.</summary>
    public static ConfigurationSnapshotNode CollectionNode(string key, List<ConfigurationSnapshotNode> items, string? label = null) =>
        new() { Key = key, Label = label, NodeType = ConfigurationSnapshotNodeType.Collection, Children = items };
}
