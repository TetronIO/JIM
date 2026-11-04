// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Utilities;
using Serilog;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Where one Partition's next Delta Import reads its changes after, in the form the directory's change source keeps
/// it: the highest committed USN and the domain controller that issued it (Active Directory and Samba AD), the change
/// number and its changeTime (a changelog), or the reqStart of a write (OpenLDAP's accesslog). JIM holds one for every
/// Partition, serialised, and hands it back unread at the start of the next import (#2715); the Connected System-level
/// <see cref="LdapConnectorRootDse"/> holds no position at all.
/// </summary>
/// <remarks>
/// A record, so two Partitions holding the same position compare equal: the import verifies each distinct watermark
/// once, naming every Partition that holds it.
/// </remarks>
internal sealed record LdapPartitionWatermark
{
    /// <summary>
    /// The change source that took this watermark. A watermark of another kind than the one the directory is now read
    /// through (the Connected System has come to reach a different type of directory) is no watermark at all.
    /// </summary>
    [JsonRequired]
    [JsonConverter(typeof(JsonStringEnumConverter<LdapDeltaSourceKind>))]
    public required LdapDeltaSourceKind Kind { get; init; }

    /// <summary>USN: the domain controller's highestCommittedUSN when the watermark was taken.</summary>
    public long? Usn { get; init; }

    /// <summary>
    /// USN: the invocationId of the domain controller that issued <see cref="Usn"/>. A USN means something only against
    /// that domain controller, and a restore from backup gives it a new one (#230). Null when it could not be read.
    /// </summary>
    public Guid? InvocationId { get; init; }

    /// <summary>USN: the dnsHostName of the domain controller that issued <see cref="Usn"/>, compared when either invocationId is unknown.</summary>
    public string? DnsHostName { get; init; }

    /// <summary>Changelog: the highest change number the import that took this saw.</summary>
    public long? ChangeNumber { get; init; }

    /// <summary>
    /// Changelog: the changeTime of change <see cref="ChangeNumber"/>, read back by the next Delta Import to prove the
    /// number still names the same change (#2008). Null when there was no such change or time to record.
    /// </summary>
    public string? ChangeTime { get; init; }

    /// <summary>Accesslog: the reqStart the next Delta Import reads writes from, as generalised time.</summary>
    public string? AccesslogTimestamp { get; init; }

    /// <summary>
    /// Accesslog: whether <see cref="AccesslogTimestamp"/> is the reqStart of a logged write, which the next Delta Import
    /// can read back to prove the accesslog still reaches it, rather than one generated for an empty accesslog.
    /// </summary>
    public bool? AccesslogTimestampIsLogged { get; init; }

    private static readonly JsonSerializerOptions SerialiserOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The watermark as JIM stores it: compact JSON naming its kind, with only the fields its kind sets.</summary>
    internal string Serialise() => JsonSerializer.Serialize(this, SerialiserOptions);

    /// <summary>
    /// Reads a watermark JIM handed back. Null, never an exception, for a value that holds none: none recorded, an
    /// empty value, one that is not a watermark of any kind this Connector reads. A value JIM holds but cannot be read
    /// is logged, because the Partition's next Delta Import will read every object in it as a result.
    /// </summary>
    internal static LdapPartitionWatermark? Parse(string? value, ILogger logger)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        try
        {
            return JsonSerializer.Deserialize<LdapPartitionWatermark>(value, SerialiserOptions);
        }
        catch (JsonException ex)
        {
            logger.Warning("LdapPartitionWatermark: A recorded Partition watermark could not be read ({Message}), so it is treated as none", LogSanitiser.Sanitise(ex.Message));
            return null;
        }
    }
}
