---
title: Troubleshooting the LDAP Connector
description: "Fix an LDAP Connected System that cannot connect, sign in, import, read its changes or export, by the message it shows."
type: troubleshooting
audience: administrator
---

# Troubleshooting the LDAP Connector

Each entry is titled by what you see on a run or in the log. For a run that ends with a warning or an error whatever the Connector, see [Troubleshooting Synchronisation](../administration/troubleshooting-synchronisation.md).

## Connection failures

If JIM cannot connect to the directory server:

- Verify the hostname or IP address is correct and reachable from the JIM container (`ping` or `nslookup` from within the container).
- Check that the port is correct (389 for LDAP, 636 for LDAPS) and not blocked by a firewall.
- Increase the Connection Timeout if the directory server is slow to respond.
- If connections succeed but each takes about two minutes, the host name probably resolves to several addresses and one of them does not answer, often a domain controller in another site behind a firewall. JIM logs a warning naming that address. Point **Host** (or, for Active Directory, **Preferred Domain Controller**) at a server JIM can reach directly.

!!! tip "LDAPS failures show you the certificate"
    The LDAP client library reports a rejected certificate the same way it reports an unreachable server, so its own message ("The LDAP server is unavailable") tells you nothing. JIM therefore looks at the certificate itself when an LDAPS connection fails, and shows it to you: its subject, the names it was issued for, its issuer, its validity dates and its thumbprint, alongside which check it failed and what to do about it.

    You will see it in two places: on the Connected System's settings when you test the connection, and on the failed Activity when a Run Profile could not connect. The same detail is available to automation on the Activity's `errorDetail` field in the REST API. If the connection failed for a reason that is nothing to do with the certificate, the original error stands unchanged.

## Authentication failures

If authentication fails with "invalid credentials":

- Verify the username format matches the authentication type. For Simple bind, use a full DN (e.g. `CN=svc-jim,OU=Service Accounts,DC=corp,DC=local`) or UPN (e.g. `svc-jim@corp.local`). For NTLM, use `DOMAIN\username` format.
- Check that the service account password is correct and has not expired.
- Ensure the service account is not locked out or disabled.

## Sign-in refused because the directory requires LDAP signing or encryption

A domain controller that enforces LDAP signing refuses a simple bind over plain LDAP, and Windows Server 2025 domain controllers enforce it by default. JIM reports the refusal with the remedy rather than the directory's bare "Strong authentication is required for this operation": turn on "Use Secure Connection (LDAPS)?" on the Connected System and set the port to 636 (or wherever the directory listens for LDAPS), adding the directory's certificate authority under Admin > Certificates first if it is a private one. Do not relax the domain controller's signing policy to suit JIM; LDAPS protects the service account's credentials, which a plain LDAP bind sends unencrypted.

If the same refusal arrives over LDAPS, encryption is not the problem: the domain controller wants more than a simple bind provides, most often because it enforces LDAP channel binding. JIM's message says so; check the directory's signing and channel binding policy for the account JIM connects as.

## Import fails with "The directory stopped the import ... at its search limit"

The account JIM binds as is subject to the directory's search limits, and the container being imported holds more objects than they allow. The run ends as **Failed with error** before importing anything from that container, because continuing would import a truncated container and a Full Import would then treat every object past the limit as gone. The message names the container, the object type and the account, for example:

> The directory stopped the import of jimPerson objects from People at its search limit (The size limit was exceeded), so nothing from People was imported. The account JIM connects as, cn=svc-jim,ou=Services,dc=example,dc=com, is subject to the directory's search limits, which its rootDN is not, and a smaller page size does not help: OpenLDAP applies the limit across a paged search as a whole. Ask the directory administrator to exempt the account (on OpenLDAP, an olcLimits entry for the JIM group on each suffix; see Service Account Permissions in the JIM LDAP Connector documentation) rather than raising the limit for every client.

On OpenLDAP the limit is `olcSizeLimit` (default 500) and it is enforced across a paged search as a whole for every client except the rootDN, so moving JIM from the rootDN to a delegated service account brings the failure with it. Exempt the JIM group on each suffix with the limits file under [Service Account Permissions](jim-ldap-connector-delta-import.md#openldap); do not raise the database-wide limit for every client.

On 389 Directory Server the limits are `nsslapd-sizelimit` (default 2000) and `nsslapd-lookthroughlimit` (default 5000), applied across a paged search in the same way for every account but the Directory Manager. Set `nsSizeLimit`, `nsLookThroughLimit` and `nsPagedSizeLimit` to `-1` on the service account's own entry, as the recipe under [389 Directory Server](jim-ldap-connector-permissions.md#389-directory-server) does, rather than raising the server-wide limits.

## Delta import not detecting changes

If delta imports return no changes when changes are expected:

- JIM no longer completes a Delta Import that could not read the directory's change source: such a run ends as Failed with error naming the source (the Deleted Objects container, `cn=accesslog`, or the changelog DN) and the remedy, and the schema refresh preview warns of the same gap. Start there; a Delta Import that completes successfully with no changes has read the source and found none.
- **Active Directory**: verify that the service account has read access to the `uSNChanged` attribute, and that the domain controller it reached is the one that issued the watermark (see the next section).
- **OpenLDAP**: verify that the accesslog overlay is configured and that the service account may read `cn=accesslog` (the access-control rule under [OpenLDAP](jim-ldap-connector-delta-import.md#openldap)).
- **389 Directory Server and generic directories**: verify that the changelog exists (389 Directory Server: `dsconf <instance> plugin retro-changelog enable`, then restart) and that the service account may read it (389 Directory Server: the `cn=changelog` rule under [389 Directory Server](jim-ldap-connector-permissions.md#389-directory-server)). A Delta Import whose last import found no readable changelog performs a Full Import, says so, and records the watermark; the Delta Imports after it read the changelog.
- **389 Directory Server, deletions in particular**: a Delta Import that refuses to run naming `nsslapd-log-deleted` has found the Retro Changelog plug-in not recording deleted entries; set it to `on` on `cn=Retro Changelog Plugin,cn=plugins,cn=config` and restart the directory. A run that completes with a warning that JIM could not confirm the plug-in records deleted entries needs the plug-in rule under [389 Directory Server](jim-ldap-connector-permissions.md#389-directory-server), which grants the service account read on that entry. A run that warns that a particular deletion could not be identified read a deletion recorded before the setting was turned on; a Full Import detects it by absence.
- Run a full import to re-baseline, then test delta import again.

## Password Modify refused with "Confidentiality required"

389 Directory Server accepts the Password Modify operation only over a secure connection, and there is no setting on the directory that relaxes this. Turn on "Use Secure Connection (LDAPS)?" on the Connected System, switching the port to the directory's LDAPS port (636 by default), and if the directory's certificate is issued by a private certificate authority, add that authority under Admin > Certificates first. See [Setting Passwords](jim-ldap-connector-passwords.md#setting-passwords). Nothing is lost in the meantime: a password change the directory refused is parked, with the directory's own words, rather than retried into the same refusal, and an account owed its first password stays owed it; [Passwords](../concepts/passwords.md) says how parked work is released.

## Delta import fails with a domain controller mismatch error

Active Directory and Samba AD Delta Imports check that they are still talking to the same domain controller that produced the persisted USN watermark, and fail fast with an error naming the previous and current domain controller (or their invocationId) if not. This is expected, protective behaviour, not a bug: a USN watermark from one domain controller is meaningless against another, and continuing regardless risks silently skipping or re-importing changes.

The error names both invocationIds (or both hostnames, for a watermark recorded before JIM began recording the invocationId): "Delta import aborted: the domain controller's invocationId has changed since the watermark was recorded (previous: ..., current: ...)", followed by the likely causes and the remedy. The failed Delta Import changes nothing: the recorded watermark is left exactly as it was, so every Delta Import fails the same way until a Full Import runs. There are two common causes:

- **The directory was restored from a backup, snapshot or checkpoint.** A domain controller restored this way is issued a new invocationId even though its hostname is unchanged, and its USNs restart from the restored point. Changes made after the restore reuse USN values the watermark has already passed, so a Delta Import reading from that watermark would silently miss them; JIM refuses to read any change instead.
- **A different domain controller answered.** With [domain controller discovery and pinning](jim-ldap-connector-topology.md#domain-controller-discovery-and-pinning) in place, the most common case is the previously pinned domain controller having become unreachable: JIM already cleared the pin and failed that run outright, and the following run resolved via Host, discovered a different domain controller, and re-pinned to it.

A restore that keeps the domain controller's invocationId (a file-level or volume snapshot restore that bypasses Active Directory's own restore process) is caught a different way. A live domain controller's highest committed USN never goes backwards, so when it is now below the recorded watermark the Delta Import fails fast with "Delta import aborted: the domain controller's highest committed update sequence number (USN) has gone backwards since the watermark was recorded (watermark: ..., current: ...)". The remedy is the same Full Import. JIM can only see this kind of restore while the directory's USN is still behind the watermark: once enough changes have been made since the restore to carry it past, it looks like ordinary progress, so run a Full Import after any restore rather than relying on the Delta Import to notice.

What to do:

- Run a Full Import. It imports everything, and records the domain controller's current invocationId together with a new USN watermark, so the Delta Imports after it run normally for as long as that domain controller keeps its invocationId.
- If you need consistent affinity to one specific domain controller regardless of availability, set Preferred Domain Controller rather than relying on auto-discovery.
- If the account JIM connects as cannot read the domain controller's NTDS Settings object, the invocationId cannot be read. JIM then compares hostnames only, which catches a different domain controller but catches a restore of the same one only through the USN check above, and logs a warning that identity could not be verified. Grant the account read access to that object to restore the stronger check.

## Export failures

If exports fail with LDAP errors:

- Check the activity log for the specific LDAP error code and message.
- For "insufficient access rights" errors, verify the service account has write permissions on the target container.
- For "constraint violation" errors on multi-valued attributes, try reducing the Modify Batch Size setting.
- For group membership operations, ensure the Group Placeholder Member DN setting is appropriate for your directory.
- A member who is already in the group, or already out of it, is not the cause of a failure: JIM treats those as done and applies the rest of the change (see [Values already in place](jim-ldap-connector-values-in-use.md#values-already-in-place)). If the worker log shows a modify applied in parts and the export still failed, the directory refused a value for the reason in the error message.

### "Active Directory will not enable an account that has no password"

Active Directory refuses to create a user account enabled, or to enable an existing one, while the account has no password, answering `0000052D` (WILL_NOT_PERFORM). JIM never sends a password with the account itself: passwords travel on their own channel once the account exists (see [Setting Passwords](jim-ldap-connector-passwords.md#setting-passwords)). So a Synchronisation Rule that provisions accounts with an enabled `userAccountControl` value, such as `512`, and gives them no password is refused on every Create, and a flow that enables an existing account with no password is refused on the Update. Where a password is still owed to the account (queued, retrying or parked), JIM holds the enable back instead and the error does not arise, so on an Update it means no password is owed at all. JIM reports the refusal on the export's execution item with the remedies named, and the directory's own words after them, rather than as a bare directory error. Samba AD accepts an enabled account with no password, which is why a configuration that works against a Samba AD lab can be refused by Windows Server.

Any of these remedies works:

- **Give the provisioning Synchronisation Rule an [Initial Password](../configuration/initial-password.md).**<br /> On Active Directory, JIM then holds the account disabled, adding ACCOUNTDISABLE to the `userAccountControl` value the Attribute Flow sends, and enables it once the password is set. Your `userAccountControl` Attribute Flow stays as it is and goes on deciding whether each account is enabled, so an account it creates disabled stays disabled when its password lands. See [Enabling new Active Directory accounts](jim-ldap-connector-passwords.md#enabling-new-active-directory-accounts) for the whole sequence.
- **Create the account disabled.**<br /> Have the Attribute Flow write `userAccountControl` with ACCOUNTDISABLE (`0x2`) set, such as `514`, until the account has a password.
- **For an account that already exists, set its password.**<br /> Set a password on the account with the [set-password action](../configuration/connected-system-passwords.md#setting-the-password-on-one-connected-system-object), enabling it there.

If the Synchronisation Rule already has an Initial Password, the account's password will not now be delivered: it expired or was cancelled before it could be set, and **Operations > Passwords** shows which. At the next import of the Connected System, Full or Delta, JIM stops waiting for a password, since none is owed any more, and the export after it writes your Attribute Flow's enabled value, which Active Directory refuses for the same reason. Set a password on the account with the set-password action, enabling it there. A password that is merely queued, retrying or parked does not cause this error, on a Create or an Update: the account waits, disabled, until the password is delivered.

Nothing is lost while you fix it. The account stays disabled, the Pending Export stays queued and is retried on later export runs with a growing delay between attempts, up to the ordinary retry limit, and the Metaverse Object is untouched.

## Still stuck?

Collect these before asking for help:

- **The directory type and version,** as the Connected System's **Detected** strip shows it, and whether JIM connects over LDAPS.
- **The run's Activity,** with its message and one failing execution item's error.
- **The worker's log from the time of the run:** `docker logs jim.worker`, or `sudo podman logs jim-worker`.
- **For a permission problem,** the service account's rights on the containers involved; leave its password out.

Then ask in the [Q&A](https://github.com/TetronIO/JIM/discussions/categories/q-a) category of GitHub Discussions, or open a [GitHub Issue](https://github.com/TetronIO/JIM/issues) for a bug.
