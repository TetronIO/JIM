// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;

namespace JIM.Models.Activities;

/// <summary>
/// What a Connector returned for an imported object that JIM rejected before a Connected System Object existed for it
/// (#874), as recorded on the execution item's <see cref="ActivityRunProfileExecutionItem.ImportedObjectSnapshotJson"/>.
/// Such an item has no change record to show, so without this the administrator would see only the error message.
/// </summary>
/// <remarks>
/// <para>
/// Credential material is never recorded: an attribute on the credential denylist, or whose name merely looks like it
/// may carry a credential, keeps its name but not its values (<see cref="ImportedObjectSnapshotAttribute.Withheld"/>).
/// The name check is the broad one, so harmless attributes such as <c>pwdLastSet</c> are withheld too; a value
/// wrongly withheld costs a little diagnostic detail, whereas one wrongly kept puts a password at rest.
/// </para>
/// <para>
/// Bounded twice over, so one large or malformed object cannot bloat the Activity. Each attribute keeps at most
/// <see cref="MaxValuesPerAttribute"/> values of at most <see cref="MaxValueLength"/> characters, and the whole snapshot
/// stays within <see cref="MaxSnapshotBytes"/>: past that, the largest attributes are dropped first (named in
/// <see cref="OmittedAttributeNames"/>), and attributes named in the error are the last to lose values or their place.
/// A run records at most <see cref="MaxSnapshotsPerRun"/> snapshots; the items after that carry
/// <see cref="RunLimitReached"/> instead.
/// </para>
/// </remarks>
public class ImportedObjectSnapshot
{
    /// <summary>The most values kept for one attribute; <see cref="ImportedObjectSnapshotAttribute.OmittedValueCount"/> counts the rest.</summary>
    public const int MaxValuesPerAttribute = 50;

    /// <summary>The longest a value is kept at; a longer one is cut short and marked so.</summary>
    public const int MaxValueLength = 1000;

    /// <summary>The largest a snapshot is stored at, in bytes of UTF-8 JSON.</summary>
    public const int MaxSnapshotBytes = 32 * 1024;

    /// <summary>The most dropped attributes named in <see cref="OmittedAttributeNames"/>; <see cref="OmittedAttributeCount"/> counts them all.</summary>
    public const int MaxOmittedAttributeNames = 100;

    /// <summary>
    /// The most snapshots one run records. When every object fails it is almost always for one shared cause, so the
    /// first thousand show it as well as a hundred thousand would, at a hundredth of the storage.
    /// </summary>
    public const int MaxSnapshotsPerRun = 1000;

    private const int MaxNameLength = 256;

    // Room left for the attributes not named in the error when those named in it have to be cut down to fit.
    private const int ProtectedHeadroomBytes = 4 * 1024;

    // Set once the list of dropped names itself had to go to fit; the count still says how many attributes were dropped.
    private bool _omittedNamesDropped;

    // Follows the established stored-JSON conventions (ImportedAttributeValueErrors, MvoDeletionPolicySnapshot):
    // camelCase properties, string enum values, nulls omitted.
    private static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    /// <summary>The Object Type the Connector named, as it named it; null where it named none.</summary>
    public string? ObjectType { get; set; }

    /// <summary>The kind of change the Connector reported.</summary>
    public ObjectChangeType? ChangeType { get; set; }

    /// <summary>The attributes kept, in the order the Connector returned them.</summary>
    public List<ImportedObjectSnapshotAttribute> Attributes { get; set; } = [];

    /// <summary>How many attributes were dropped to keep the snapshot within <see cref="MaxSnapshotBytes"/>; null when none were.</summary>
    public int? OmittedAttributeCount { get; set; }

    /// <summary>
    /// The names of the dropped attributes, in the Connector's order, up to <see cref="MaxOmittedAttributeNames"/> of
    /// them; null when none were dropped.
    /// </summary>
    public List<string>? OmittedAttributeNames { get; set; }

    /// <summary>
    /// True when the object was not recorded because its run had already recorded <see cref="MaxSnapshotsPerRun"/>
    /// snapshots; null otherwise.
    /// </summary>
    public bool? RunLimitReached { get; set; }

    /// <summary>
    /// Records what a Connector returned for an object, withholding credential values and keeping within the limits.
    /// </summary>
    /// <param name="importObject">The object as the Connector returned it.</param>
    /// <param name="protectedAttributeNames">Attributes named in the item's error, which are the last to be dropped
    /// for size, compared ignoring case.</param>
    public static ImportedObjectSnapshot Record(ConnectedSystemImportObject importObject, IReadOnlyCollection<string>? protectedAttributeNames = null)
    {
        var protectedNames = new HashSet<string>(protectedAttributeNames ?? [], StringComparer.OrdinalIgnoreCase);
        var working = importObject.Attributes
            .Select((attribute, index) => WorkingAttribute.From(attribute, index, protectedNames.Contains(attribute.Name)))
            .ToList();

        var snapshot = new ImportedObjectSnapshot
        {
            ObjectType = Shorten(importObject.ObjectType, MaxValueLength),
            ChangeType = importObject.ChangeType
        };

        var omitted = new List<WorkingAttribute>();
        var baseBytes = Encoding.UTF8.GetByteCount(snapshot.ToJson());

        // Work on estimates first, which cost nothing per step, then confirm against the real document and keep trimming
        // in the rare case the estimate fell short. Every step makes the snapshot smaller, so both loops end.
        while (baseBytes + working.Sum(w => w.EstimatedBytes) + EstimateOmittedBytes(omitted) > MaxSnapshotBytes
               && TrimOneStep(working, omitted, snapshot, baseBytes))
        {
        }

        snapshot.Apply(working, omitted);
        while (Encoding.UTF8.GetByteCount(snapshot.ToJson()) > MaxSnapshotBytes && TrimOneStep(working, omitted, snapshot, baseBytes))
            snapshot.Apply(working, omitted);

        return snapshot;
    }

    /// <summary>
    /// The snapshot recorded for an object after its run reached <see cref="MaxSnapshotsPerRun"/>: no attributes, and
    /// the reason why.
    /// </summary>
    public static ImportedObjectSnapshot CreateRunLimitReached() => new() { RunLimitReached = true };

    /// <summary>Serialises the snapshot to the JSON document stored on the execution item.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerialiserOptions);

    /// <summary>
    /// Reads back a snapshot written by <see cref="ToJson"/>. Returns null when the value is absent or no longer parses,
    /// so a rendering caller shows the item without it rather than failing.
    /// </summary>
    public static ImportedObjectSnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ImportedObjectSnapshot>(json, SerialiserOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Apply(List<WorkingAttribute> working, List<WorkingAttribute> omitted)
    {
        Attributes = working.OrderBy(w => w.Index).Select(w => w.Attribute).ToList();
        if (omitted.Count == 0)
        {
            OmittedAttributeCount = null;
            OmittedAttributeNames = null;
            return;
        }

        OmittedAttributeCount = omitted.Count;
        OmittedAttributeNames = _omittedNamesDropped ? null : OmittedNames(omitted);
    }

    /// <summary>
    /// Makes the snapshot smaller by one step, in order of what costs the administrator least. Attributes named in the
    /// error that would not fit on their own (less some room for the rest) lose values first, since dropping the others
    /// could not make them fit; then the largest attribute not named in the error goes; then a value from the attribute
    /// holding the most; then the largest attribute named in the error; then the list of dropped names. Returns false
    /// when there is nothing left to drop.
    /// </summary>
    private static bool TrimOneStep(List<WorkingAttribute> working, List<WorkingAttribute> omitted, ImportedObjectSnapshot snapshot, int baseBytes)
    {
        if (baseBytes + working.Where(w => w.IsProtected).Sum(w => w.EstimatedBytes) > MaxSnapshotBytes - ProtectedHeadroomBytes)
        {
            var largestProtected = working.Where(w => w.IsProtected && w.Attribute.Values.Count > 1).MaxBy(w => w.EstimatedBytes);
            if (largestProtected != null)
            {
                largestProtected.DropLastValue();
                return true;
            }
        }

        var largestUnprotected = working.Where(w => !w.IsProtected).MaxBy(w => w.EstimatedBytes);
        if (largestUnprotected != null)
        {
            working.Remove(largestUnprotected);
            omitted.Add(largestUnprotected);
            return true;
        }

        var holdingMostValues = working.Where(w => w.Attribute.Values.Count > 1).MaxBy(w => w.EstimatedBytes);
        if (holdingMostValues != null)
        {
            holdingMostValues.DropLastValue();
            return true;
        }

        var largest = working.MaxBy(w => w.EstimatedBytes);
        if (largest != null)
        {
            working.Remove(largest);
            omitted.Add(largest);
            return true;
        }

        if (!snapshot._omittedNamesDropped && omitted.Count > 0)
        {
            snapshot._omittedNamesDropped = true;
            snapshot.OmittedAttributeNames = null;
            return true;
        }

        return false;
    }

    private static List<string> OmittedNames(List<WorkingAttribute> omitted) =>
        omitted.OrderBy(w => w.Index).Take(MaxOmittedAttributeNames).Select(w => w.Attribute.Name).ToList();

    private static int EstimateOmittedBytes(List<WorkingAttribute> omitted)
    {
        if (omitted.Count == 0)
            return 0;

        // "omittedAttributeCount":N,"omittedAttributeNames":[...]
        return 64 + omitted.OrderBy(w => w.Index).Take(MaxOmittedAttributeNames).Sum(w => EncodedBytes(w.Attribute.Name) + 3);
    }

    private static int EncodedBytes(string value) => JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length;

    private static string? Shorten(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length > maxLength ? $"{value[..maxLength]}..." : value;

    private static bool IsCredential(string name) =>
        CredentialAttributes.IsCredentialAttribute(name) || CredentialAttributes.HasCredentialLikeName(name);

    private static IEnumerable<string> ValuesAsText(ConnectedSystemImportObjectAttribute attribute)
    {
        // Read through the Has properties, so recording an attribute allocates no list it does not hold.
        if (attribute.HasStringValues)
            foreach (var value in attribute.StringValues) yield return value;
        if (attribute.HasReferenceValues)
            foreach (var value in attribute.ReferenceValues) yield return value;
        if (attribute.HasIntValues)
            foreach (var value in attribute.IntValues) yield return value.ToString(CultureInfo.InvariantCulture);
        if (attribute.HasLongValues)
            foreach (var value in attribute.LongValues) yield return value.ToString(CultureInfo.InvariantCulture);
        if (attribute.HasDecimalValues)
            foreach (var value in attribute.DecimalValues) yield return value.ToString(CultureInfo.InvariantCulture);
        if (attribute.HasDateTimeValues)
            foreach (var value in attribute.DateTimeValues) yield return value.ToString("o", CultureInfo.InvariantCulture);
        if (attribute.HasGuidValues)
            foreach (var value in attribute.GuidValues) yield return value.ToString("D");
        if (attribute.HasByteValues)
            foreach (var value in attribute.ByteValues) yield return string.Format(CultureInfo.InvariantCulture, "[binary, {0:N0} bytes]", value.Length);
        if (attribute.HasBoolValues)
            foreach (var value in attribute.BoolValues) yield return value ? "true" : "false";
    }

    /// <summary>An attribute being fitted to the limits, with what it costs in the stored document.</summary>
    private sealed class WorkingAttribute
    {
        // The property names, braces and the counts an attribute can carry: {"name":,"type":"LongNumber","values":[],"omittedValueCount":N,"withheld":true}
        private const int OverheadBytes = 96;

        private readonly List<int> _valueBytes;

        private WorkingAttribute(ImportedObjectSnapshotAttribute attribute, int index, bool isProtected, List<int> valueBytes)
        {
            Attribute = attribute;
            Index = index;
            IsProtected = isProtected;
            _valueBytes = valueBytes;
            EstimatedBytes = OverheadBytes + EncodedBytes(attribute.Name) + valueBytes.Sum();
        }

        public ImportedObjectSnapshotAttribute Attribute { get; }

        public int Index { get; }

        public bool IsProtected { get; }

        public int EstimatedBytes { get; private set; }

        public static WorkingAttribute From(ConnectedSystemImportObjectAttribute imported, int index, bool isProtected)
        {
            var attribute = new ImportedObjectSnapshotAttribute
            {
                Name = Shorten(imported.Name, MaxNameLength) ?? string.Empty,
                Type = imported.Type,
                NamedInError = isProtected ? true : null
            };

            if (IsCredential(imported.Name))
            {
                attribute.Withheld = true;
                return new WorkingAttribute(attribute, index, isProtected, []);
            }

            var total = 0;
            foreach (var value in ValuesAsText(imported))
            {
                total++;
                if (attribute.Values.Count < MaxValuesPerAttribute)
                    attribute.Values.Add(Shorten(value, MaxValueLength)!);
            }

            if (total > attribute.Values.Count)
                attribute.OmittedValueCount = total - attribute.Values.Count;

            // Each value costs its encoded text, its quotes and the comma before the next.
            return new WorkingAttribute(attribute, index, isProtected, attribute.Values.Select(v => EncodedBytes(v) + 3).ToList());
        }

        public void DropLastValue()
        {
            var last = Attribute.Values.Count - 1;
            Attribute.Values.RemoveAt(last);
            EstimatedBytes -= _valueBytes[last];
            _valueBytes.RemoveAt(last);
            Attribute.OmittedValueCount = (Attribute.OmittedValueCount ?? 0) + 1;
        }
    }
}
