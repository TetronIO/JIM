// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors.ActiveDirectory;

/// <summary>
/// The real Windows Server domain controller the <c>RequiresActiveDirectory</c> probes run against (#1853).
/// </summary>
/// <remarks>
/// <para>
/// Every probe in this category drives a connector primitive that only a real Active Directory can prove: Samba AD
/// advertises the same capability OID but the connector takes a different branch for it, so the Samba integration
/// lab never executes the <c>ActiveDirectory</c> paths at all. The probes self-ignore unless
/// <c>JIM_TEST_AD_HOST</c> is set, so an ordinary <c>dotnet test</c> is unaffected, and they are meant to be run from
/// a machine with a route to the lab: <c>dotnet test test/JIM.Worker.Tests/ --filter Category=RequiresActiveDirectory</c>.
/// </para>
/// <para>
/// Environment variables: <c>JIM_TEST_AD_HOST</c> (the domain controller's FQDN as its certificate names it; also
/// the opt-in switch), <c>JIM_TEST_AD_PORT</c> (LDAPS, default 636), <c>JIM_TEST_AD_PLAIN_PORT</c> (LDAP, optional;
/// only the signing probe uses it), <c>JIM_TEST_AD_BASE_DN</c>, <c>JIM_TEST_AD_USERNAME</c> and
/// <c>JIM_TEST_AD_PASSWORD</c> (the delegated account JIM binds as, e.g. <c>CN=svc-jim,OU=Services,DC=panoply,DC=local</c>),
/// <c>JIM_TEST_AD_ADMIN_USERNAME</c> and <c>JIM_TEST_AD_ADMIN_PASSWORD</c> (an account allowed to create the probe
/// fixtures; defaults to the JIM account) and <c>JIM_TEST_AD_CA_PATH</c> (the PEM certificate to trust for LDAPS,
/// omitted when the domain controller's certificate is already trusted by the operating system).
/// </para>
/// <para>
/// Fixtures live under <c>OU=JIM Probes,&lt;base DN&gt;</c> and are created idempotently by the raw LDAP helpers here,
/// never through the connector under test, so a connector defect cannot hide behind a fixture it failed to create.
/// The helpers page and follow ranged attributes themselves for the same reason.
/// </para>
/// </remarks>
internal static class ActiveDirectoryLab
{
    internal const string Category = "RequiresActiveDirectory";

    private const string HostVariable = "JIM_TEST_AD_HOST";
    private const int FixtureChunkSize = 500;

    /// <summary>
    /// Trust directories handed to raw connections. Kept for the life of the test process: the platform LDAP client
    /// reads the directory when the connection is established, and the sweep in
    /// <see cref="LdapTrustedCertificateDirectory"/> removes abandoned ones on a later run.
    /// </summary>
    private static readonly List<LdapTrustedCertificateDirectory> TrustDirectories = [];

    internal sealed record Coordinates(
        string Host,
        int Port,
        int? PlainPort,
        string BaseDn,
        string Username,
        string Password,
        string AdminUsername,
        string AdminPassword,
        string? CaCertificatePath)
    {
        /// <summary>
        /// Where every probe fixture lives.
        /// </summary>
        public string ProbeOu => $"OU=JIM Probes,{BaseDn}";
    }

    /// <summary>
    /// Reads the lab's coordinates, ignoring the calling test when the lab is not configured.
    /// </summary>
    internal static Coordinates Require()
    {
        var host = Environment.GetEnvironmentVariable(HostVariable);
        if (string.IsNullOrEmpty(host))
            Assert.Ignore($"{HostVariable} not set; skipping. This probe needs the Active Directory lab (engineering/prd/PRD_ACTIVE_DIRECTORY_LAB.md).");

        var username = Required("JIM_TEST_AD_USERNAME");
        var password = Required("JIM_TEST_AD_PASSWORD");
        var plainPort = Environment.GetEnvironmentVariable("JIM_TEST_AD_PLAIN_PORT");

        return new Coordinates(
            host!,
            int.Parse(Environment.GetEnvironmentVariable("JIM_TEST_AD_PORT") ?? LdapConnectorConstants.DEFAULT_LDAPS_PORT.ToString()),
            string.IsNullOrEmpty(plainPort) ? null : int.Parse(plainPort),
            Required("JIM_TEST_AD_BASE_DN"),
            username,
            password,
            Environment.GetEnvironmentVariable("JIM_TEST_AD_ADMIN_USERNAME") ?? username,
            Environment.GetEnvironmentVariable("JIM_TEST_AD_ADMIN_PASSWORD") ?? password,
            Environment.GetEnvironmentVariable("JIM_TEST_AD_CA_PATH"));
    }

    private static string Required(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(value))
            Assert.Fail($"{HostVariable} is set but {variable} is not; the lab is only partly configured.");
        return value!;
    }

    // -----------------------------------------------------------------------
    // The connector under test
    // -----------------------------------------------------------------------

    /// <summary>
    /// A connector that trusts the lab's certificate the way a deployment trusts one added to the JIM certificate
    /// store.
    /// </summary>
    internal static LdapConnector NewConnector(Coordinates lab)
    {
        var connector = new LdapConnector();
        if (lab.CaCertificatePath != null)
            connector.SetCertificateProvider(new LdapsTestConnections.FakeCertificateProvider([lab.CaCertificatePath]));
        return connector;
    }

    /// <summary>
    /// The Connected System settings a deployment would hold: LDAPS, simple bind as the delegated account.
    /// </summary>
    internal static List<ConnectedSystemSettingValue> ConnectorSettings(Coordinates lab, bool useSecureConnection = true, int? port = null)
    {
        var settings = LdapsTestConnections.BuildSettingValues(lab.Host, port ?? lab.Port, useSecureConnection, lab.Username, lab.Password);
        settings.Add(new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" },
            StringValue = "Delete"
        });
        return settings;
    }

    /// <summary>
    /// A Connected System scoped to one container and one Object Type, the minimum an import needs.
    /// </summary>
    internal static ConnectedSystem NewConnectedSystem(Coordinates lab, ConnectedSystemObjectType objectType, string containerDn, string containerName)
    {
        var connectedSystem = new ConnectedSystem
        {
            Name = "Active Directory lab",
            SettingValues = ConnectorSettings(lab),
            ObjectTypes = [objectType]
        };
        objectType.ConnectedSystem = connectedSystem;

        var partition = new ConnectedSystemPartition
        {
            ConnectedSystem = connectedSystem,
            ExternalId = lab.BaseDn,
            Name = lab.BaseDn,
            Selected = true,
            Containers = []
        };
        partition.Containers.Add(new ConnectedSystemContainer
        {
            ConnectedSystem = connectedSystem,
            Partition = partition,
            ExternalId = containerDn,
            Name = containerName,
            Selected = true,
            Scope = ConnectedSystemContainerScope.Subtree
        });
        connectedSystem.Partitions = [partition];
        return connectedSystem;
    }

    /// <summary>
    /// The <c>group</c> Object Type with the attributes a membership import needs. <c>objectGUID</c> is the external
    /// id and the Distinguished Name the secondary one, as the schema discovery recommends for Active Directory.
    /// </summary>
    internal static ConnectedSystemObjectType GroupObjectType()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "group" };
        objectType.Attributes.AddRange(
        [
            Attribute(objectType, 1, "objectGUID", AttributeDataType.Guid, isExternalId: true),
            Attribute(objectType, 2, "distinguishedName", AttributeDataType.Text, isSecondaryExternalId: true),
            Attribute(objectType, 3, "cn", AttributeDataType.Text),
            Attribute(objectType, 4, "member", AttributeDataType.Reference, AttributePlurality.MultiValued)
        ]);
        return objectType;
    }

    internal static ConnectedSystemObjectTypeAttribute Attribute(
        ConnectedSystemObjectType objectType,
        int id,
        string name,
        AttributeDataType type,
        AttributePlurality plurality = AttributePlurality.SingleValued,
        bool isExternalId = false,
        bool isSecondaryExternalId = false)
    {
        return new ConnectedSystemObjectTypeAttribute
        {
            Id = id,
            Name = name,
            Type = type,
            AttributePlurality = plurality,
            Selected = true,
            IsExternalId = isExternalId,
            IsSecondaryExternalId = isSecondaryExternalId,
            ConnectedSystemObjectType = objectType
        };
    }

    // -----------------------------------------------------------------------
    // Raw access, for fixtures and independent assertions
    // -----------------------------------------------------------------------

    /// <summary>
    /// A plain platform LDAP connection over LDAPS, bound as the fixture account. Independent of the connector under
    /// test by design.
    /// </summary>
    internal static LdapConnection OpenAdminConnection(Coordinates lab, ILogger logger)
    {
        var connection = new LdapConnection(
            new LdapDirectoryIdentifier(lab.Host, lab.Port),
            new NetworkCredential(lab.AdminUsername, lab.AdminPassword),
            AuthType.Basic);
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        if (lab.CaCertificatePath != null && !OperatingSystem.IsWindows())
        {
            var trustDirectory = LdapTrustedCertificateDirectory.Create(
                [X509CertificateLoader.LoadCertificateFromFile(lab.CaCertificatePath)], logger);
            lock (TrustDirectories)
                TrustDirectories.Add(trustDirectory);
            connection.SessionOptions.TrustedCertificatesDirectory = trustDirectory.DirectoryPath;
            // As LdapConnector does: the platform LDAP client applies the directory only to a new TLS context, so
            // without this the connection keeps the default trust store and rejects the lab's self-signed
            // certificate ("certificate verify failed (self-signed certificate)", seen on the Ubuntu 26.04 runner).
            connection.SessionOptions.StartNewTlsSessionContext();
        }

        connection.SessionOptions.SecureSocketLayer = true;
        connection.Bind();
        return connection;
    }

    internal static bool Exists(LdapConnection connection, string dn)
    {
        try
        {
            var response = (SearchResponse)connection.SendRequest(new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, "distinguishedName"));
            return response.Entries.Count > 0;
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return false;
        }
    }

    internal static void EnsureEntry(LdapConnection connection, string dn, string objectClass, params (string Name, string Value)[] attributes)
    {
        if (Exists(connection, dn))
            return;

        var request = new AddRequest(dn, objectClass);
        foreach (var (name, value) in attributes)
            request.Attributes.Add(new DirectoryAttribute(name, value));
        connection.SendRequest(request);
    }

    internal static void EnsureProbeOu(LdapConnection connection, Coordinates lab) =>
        EnsureEntry(connection, lab.ProbeOu, "organizationalUnit", ("ou", "JIM Probes"));

    /// <summary>
    /// Ensures <paramref name="count"/> contacts exist under the probe OU and returns their Distinguished Names in
    /// order. Contacts are the cheapest object a group can hold.
    /// </summary>
    internal static IReadOnlyList<string> EnsureContacts(LdapConnection connection, Coordinates lab, int count)
    {
        EnsureProbeOu(connection, lab);
        var existing = ListEntryDns(connection, lab.ProbeOu, "(objectClass=contact)", SearchScope.OneLevel);
        var dns = new List<string>(count);
        for (var i = 1; i <= count; i++)
        {
            var cn = $"JIM Probe Contact {i:0000}";
            var dn = $"CN={cn},{lab.ProbeOu}";
            if (!existing.Contains(dn))
                connection.SendRequest(new AddRequest(dn, "contact") { Attributes = { new DirectoryAttribute("cn", cn) } });
            dns.Add(dn);
        }

        return dns;
    }

    /// <summary>
    /// Ensures a global security group exists under the probe OU holding at least the given members (existing
    /// members are kept; missing ones are added in chunks), and returns its Distinguished Name. Reads the members
    /// back afterwards and throws if any is absent, so a fixture that did not build fails here, naming what is
    /// missing, rather than as a count the probe then misreads (#2041).
    /// </summary>
    internal static string EnsureGroup(LdapConnection connection, Coordinates lab, string cn, string sAMAccountName, IReadOnlyList<string> memberDns)
    {
        EnsureProbeOu(connection, lab);
        var dn = $"CN={cn},{lab.ProbeOu}";
        EnsureEntry(connection, dn, "group", ("cn", cn), ("sAMAccountName", sAMAccountName), ("groupType", "-2147483646"));

        var present = new HashSet<string>(ReadAllValues(connection, dn, "member"), StringComparer.OrdinalIgnoreCase);
        var missing = memberDns.Where(member => !present.Contains(member)).ToList();
        foreach (var chunk in missing.Chunk(FixtureChunkSize))
        {
            var modify = new ModifyRequest(dn, DirectoryAttributeOperation.Add, "member", chunk.Cast<object>().ToArray());
            var response = (ModifyResponse)connection.SendRequest(modify);
            if (response.ResultCode != ResultCode.Success)
                throw new InvalidOperationException($"Adding {chunk.Length} members to the fixture group {dn} was answered {response.ResultCode}: {response.ErrorMessage}");
        }

        var held = new HashSet<string>(ReadAllValues(connection, dn, "member"), StringComparer.OrdinalIgnoreCase);
        var absent = memberDns.Where(member => !held.Contains(member)).ToList();
        if (absent.Count > 0)
            throw new InvalidOperationException(
                $"The fixture group {dn} was given {memberDns.Count} members but {absent.Count} of them are not in it when read back " +
                $"({held.Count} values read), the first being {absent[0]}. Either the adds did not land or they cannot be read back.");

        return dn;
    }

    /// <summary>
    /// Ensures <paramref name="count"/> organisational units exist directly under <paramref name="parentDn"/>.
    /// </summary>
    internal static void EnsureChildContainers(LdapConnection connection, string parentDn, string parentName, int count)
    {
        EnsureEntry(connection, parentDn, "organizationalUnit", ("ou", parentName));
        var existing = ListEntryDns(connection, parentDn, "(objectClass=organizationalUnit)", SearchScope.OneLevel);
        for (var i = 1; i <= count; i++)
        {
            var name = $"JIM Probe Container {i:0000}";
            var dn = $"OU={name},{parentDn}";
            if (!existing.Contains(dn))
                connection.SendRequest(new AddRequest(dn, "organizationalUnit") { Attributes = { new DirectoryAttribute("ou", name) } });
        }
    }

    /// <summary>
    /// Ensures a user exists under the probe OU (disabled, without a password, as Active Directory creates one) and
    /// returns its Distinguished Name.
    /// </summary>
    internal static string EnsureUser(LdapConnection connection, Coordinates lab, string cn, string sAMAccountName)
    {
        EnsureProbeOu(connection, lab);
        var dn = $"CN={cn},{lab.ProbeOu}";
        EnsureEntry(connection, dn, "user", ("cn", cn), ("sAMAccountName", sAMAccountName));
        return dn;
    }

    /// <summary>
    /// Every entry matching the filter, read with the paged-results control so a set over the directory's
    /// MaxPageSize comes back whole. Case-insensitive on the Distinguished Name.
    /// </summary>
    internal static HashSet<string> ListEntryDns(LdapConnection connection, string baseDn, string filter, SearchScope scope)
    {
        var dns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[]? cookie = null;
        do
        {
            var request = new SearchRequest(baseDn, filter, scope, "distinguishedName");
            var paging = new PageResultRequestControl(1000);
            if (cookie is { Length: > 0 })
                paging.Cookie = cookie;
            request.Controls.Add(paging);

            var response = (SearchResponse)connection.SendRequest(request);
            foreach (SearchResultEntry entry in response.Entries)
                dns.Add(entry.DistinguishedName);

            cookie = response.Controls.OfType<PageResultResponseControl>().SingleOrDefault()?.Cookie;
        } while (cookie is { Length: > 0 });

        return dns;
    }

    /// <summary>
    /// Every value of a multi-valued attribute, following Active Directory's ranged retrieval
    /// (<c>member;range=0-1499</c>, then <c>member;range=1500-*</c>) so a set over MaxValRange comes back whole.
    /// This is the reference behaviour the connector is expected to implement; it is kept here, independently, so
    /// the probe can count what the directory actually holds.
    /// </summary>
    /// <remarks>
    /// The ranged description is looked for before the plain one, because a directory may return the plain attribute,
    /// empty, beside the range. Taking whichever came first read a 1,600-member group as empty on a Windows Server 2025
    /// domain controller (#2041).
    /// </remarks>
    internal static List<string> ReadAllValues(LdapConnection connection, string dn, string attributeName)
    {
        var values = new List<string>();
        var next = 0;
        while (true)
        {
            var requested = next == 0 ? attributeName : $"{attributeName};range={next}-*";
            var response = (SearchResponse)connection.SendRequest(new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, requested));
            if (response.Entries.Count == 0)
                return values;

            var entry = response.Entries[0];
            var names = entry.Attributes.AttributeNames.Cast<string>().ToList();
            var ranged = names.FirstOrDefault(name => name.StartsWith(attributeName + ";range=", StringComparison.OrdinalIgnoreCase));
            var returned = ranged ?? names.FirstOrDefault(name => name.Equals(attributeName, StringComparison.OrdinalIgnoreCase));
            if (returned == null)
                return values;

            values.AddRange(entry.Attributes[returned].GetValues(typeof(string)).Cast<string>());
            if (ranged == null || ranged.EndsWith("-*", StringComparison.Ordinal))
                return values;

            next = int.Parse(ranged[(ranged.LastIndexOf('-') + 1)..]) + 1;
        }
    }

    /// <summary>
    /// The values of one attribute on one entry, in the order the directory lists them.
    /// </summary>
    internal static string[] ReadValues(LdapConnection connection, string dn, string attributeName)
    {
        var response = (SearchResponse)connection.SendRequest(new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, attributeName));
        if (response.Entries.Count == 0 || !response.Entries[0].Attributes.Contains(attributeName))
            return [];
        return (string[])response.Entries[0].Attributes[attributeName].GetValues(typeof(string));
    }
}
