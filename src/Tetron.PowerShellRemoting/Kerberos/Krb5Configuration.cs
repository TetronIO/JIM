// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.Kerberos;

/// <summary>
/// Renders and writes the MIT Kerberos configuration (<c>krb5.conf</c>) for a set of realms, so a host never has to
/// mount one. One file serves every realm, because the library reads a single file per process (<c>KRB5_CONFIG</c>).
/// </summary>
public static class Krb5Configuration
{
    // AES only: RC4 and DES are refused by the configuration rather than merely not preferred.
    private const string EncryptionTypes = "aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96";

    /// <summary>Renders the configuration for <paramref name="realms"/>; the first is the default realm.</summary>
    /// <exception cref="ArgumentException">No realms, a realm given twice, or a realm or KDC that is not a plain name.</exception>
    public static string Render(IReadOnlyList<KerberosRealm> realms)
    {
        ArgumentNullException.ThrowIfNull(realms);
        if (realms.Count == 0)
            throw new ArgumentException("At least one Kerberos realm is required.", nameof(realms));

        var normalised = realms.Select(Normalise).ToList();
        var duplicate = normalised.GroupBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"The Kerberos realm {duplicate.Key} is given more than once.", nameof(realms));

        var lines = new List<string>
        {
            "# Generated. Do not edit: it is rewritten whenever the realms it serves change.",
            "[libdefaults]",
            $"    default_realm = {normalised[0].Name}",
            "    dns_lookup_kdc = true",
            "    dns_lookup_realm = false",
            "    dns_canonicalize_hostname = false",
            "    rdns = false",
            "    forwardable = false",
            "    udp_preference_limit = 1",
            $"    default_ccache_name = MEMORY:tetron-{Environment.ProcessId}",
            $"    permitted_enctypes = {EncryptionTypes}",
            $"    default_tkt_enctypes = {EncryptionTypes}",
            $"    default_tgs_enctypes = {EncryptionTypes}",
            "",
            "[realms]"
        };

        foreach (var realm in normalised)
        {
            lines.Add($"    {realm.Name} = {{");
            lines.AddRange(realm.Kdcs.Select(kdc => $"        kdc = {kdc}"));
            lines.Add("    }");
        }

        lines.Add("");
        lines.Add("[domain_realm]");
        foreach (var realm in normalised)
        {
            var domain = realm.Name.ToLowerInvariant();
            lines.Add($"    .{domain} = {realm.Name}");
            lines.Add($"    {domain} = {realm.Name}");
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// Writes the configuration to <paramref name="path"/>, readable and writable by the owner only, replacing any
    /// existing file in one step so a reader never sees half a file.
    /// </summary>
    public static async Task WriteAsync(string path, IReadOnlyList<KerberosRealm> realms, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var text = Render(realms);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            await using (var stream = new FileStream(temporary, options))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(text.AsMemory(), cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static KerberosRealm Normalise(KerberosRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var name = (realm.Name ?? "").ToUpperInvariant();
        if (!IsDomainName(name))
            throw new ArgumentException($"'{realm.Name}' is not a valid Kerberos realm: use a domain name such as PANOPLY.LOCAL.", nameof(realm));

        var kdcs = (realm.Kdcs ?? []).ToList();
        foreach (var kdc in kdcs.Where(k => !IsHostWithOptionalPort(k)))
            throw new ArgumentException($"'{kdc}' is not a valid KDC: use a host name, optionally with a port, such as dc1.panoply.local:88.", nameof(realm));

        return new KerberosRealm(name, kdcs);
    }

    private static bool IsHostWithOptionalPort(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var colon = value.LastIndexOf(':');
        if (colon < 0)
            return IsDomainName(value);

        var port = value[(colon + 1)..];
        return IsDomainName(value[..colon]) && ushort.TryParse(port, out var number) && number > 0 && port.All(char.IsAsciiDigit);
    }

    // Letters, digits and hyphens in dot-separated labels: nothing that could start a new line, section or value.
    private static bool IsDomainName(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 253
        && value.Split('.').All(label => label.Length is > 0 and <= 63
                                         && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                                         && label[0] != '-' && label[^1] != '-');
}
