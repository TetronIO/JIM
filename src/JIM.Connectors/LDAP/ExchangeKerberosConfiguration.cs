// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Tetron.PowerShellRemoting.Kerberos;

namespace JIM.Connectors.LDAP;

/// <summary>
/// The Kerberos configuration file (<c>krb5.conf</c>) this process signs in to Exchange with (#2021, D8). MIT Kerberos
/// reads one file per process, named by <c>KRB5_CONFIG</c>, so the file holds every realm a session in this process has
/// needed. Both the Worker (delivering Exchange changes) and the web process (checking Exchange settings as they are
/// saved) open sessions, so each keeps its own.
/// </summary>
/// <remarks>
/// <para>
/// A session asks for its realm with <see cref="EnsureRealmAsync"/> before it opens. Realms are only ever added: a
/// realm another Connected System still uses must never disappear from under its sessions, and one nobody uses any more
/// costs nothing. Where two Connected Systems pin different domain controllers in one domain, the file lists both as KDCs,
/// the most recently asked for first; either can answer for the realm.
/// </para>
/// <para>
/// The file is rewritten only when what it says changes, replaced in one step so a reader never sees half of it, and lives
/// in a directory of its own readable by this process's user alone. It names no secret, but it decides which servers are
/// trusted as KDCs. Kerberos rereads the file whenever it builds a context, so a rewrite needs no restart.
/// </para>
/// </remarks>
internal sealed class ExchangeKerberosConfiguration
{
    private static readonly Lazy<ExchangeKerberosConfiguration> ForThisProcess = new(() =>
        new ExchangeKerberosConfiguration(KerberosEnvironment.UseConfiguration,
            () => Directory.CreateTempSubdirectory("jim-kerberos-").FullName));

    private readonly Action<string> _useConfiguration;
    private readonly Func<string> _createPrivateDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Realms in the order they were first asked for, so the first stays the default realm; each realm's KDCs in the
    // order they are to be tried.
    private readonly List<(string Name, List<string> Kdcs)> _realms = [];
    private string? _writtenText;
    private bool _inUse;

    internal ExchangeKerberosConfiguration(Action<string> useConfiguration, Func<string> createPrivateDirectory)
    {
        _useConfiguration = useConfiguration;
        _createPrivateDirectory = createPrivateDirectory;
    }

    /// <summary>
    /// The configuration of this process. The directory is created, and <c>KRB5_CONFIG</c> set, only when a session first
    /// needs a realm, so a process that never reaches Exchange writes nothing.
    /// </summary>
    public static ExchangeKerberosConfiguration Process => ForThisProcess.Value;

    /// <summary>The file Kerberos has been pointed at, or null until the first realm has been written to it.</summary>
    public string? ConfigurationPath { get; private set; }

    /// <summary>
    /// Makes sure the configuration names <paramref name="realm"/> with its KDCs, and that Kerberos reads it, before a
    /// session that signs in to that realm opens.
    /// </summary>
    /// <exception cref="ArgumentException">The realm or a KDC is not a plain name; nothing is changed.</exception>
    public async Task EnsureRealmAsync(KerberosRealm realm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realm);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var proposed = Merge(_realms, realm);
            var realms = proposed.Select(r => new KerberosRealm(r.Name, r.Kdcs)).ToList();

            // Rendering checks every realm and KDC, so a refused name throws here, before anything is kept or written.
            var text = Krb5Configuration.Render(realms);
            if (text != _writtenText)
            {
                ConfigurationPath ??= Path.Combine(_createPrivateDirectory(), "krb5.conf");
                await Krb5Configuration.WriteAsync(ConfigurationPath, realms, cancellationToken).ConfigureAwait(false);
                _writtenText = text;
                _realms.Clear();
                _realms.AddRange(proposed);
            }

            if (!_inUse)
            {
                _useConfiguration(ConfigurationPath!);
                _inUse = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The realms as they would stand with <paramref name="realm"/> asked for: added at the end if new, otherwise with its
    /// KDCs moved to the front of that realm's list. Realm names are upper-cased, as Active Directory's are; host names
    /// compare as DNS compares them, ignoring case.
    /// </summary>
    private static List<(string Name, List<string> Kdcs)> Merge(List<(string Name, List<string> Kdcs)> current, KerberosRealm realm)
    {
        var name = realm.Name.Trim().ToUpperInvariant();
        var asked = realm.Kdcs.Select(k => k.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var merged = current.Select(r => (r.Name, Kdcs: r.Kdcs.ToList())).ToList();

        var index = merged.FindIndex(r => r.Name == name);
        if (index < 0)
        {
            merged.Add((name, asked));
            return merged;
        }

        // A KDC already listed keeps the spelling it was written with, so naming it again in another case changes nothing.
        var existing = merged[index].Kdcs;
        var first = asked.Select(k => existing.FirstOrDefault(e => string.Equals(e, k, StringComparison.OrdinalIgnoreCase)) ?? k).ToList();
        merged[index] = (name, first.Concat(existing.Where(e => !first.Contains(e, StringComparer.OrdinalIgnoreCase))).ToList());
        return merged;
    }
}
