# Passwords

JIM sets passwords on the accounts it manages, and never reads one back. This page explains the channel that does it, how JIM knows what a target will accept, where a password comes from, and what happens when a target refuses one.

Everything here concerns writing a password **to** a Connected System. Synchronising a password *between* systems is a separate capability that is not yet available.

## The password channel

A password is not an attribute, and JIM does not treat it as one.

Attribute values flow through the Metaverse: imported from a source, held on a Metaverse Object, evaluated by Synchronisation Rules, staged as a Pending Export, and exported. That machinery is built to remember values, compare them, show them, and retry them. All four are exactly the wrong behaviours for a credential.

So passwords travel a **separate channel** that runs parallel to attribute flow and never through it:

| | Attribute flow | Password channel |
|---|---|---|
| Held in the Metaverse | Yes | Never |
| Staged as a Pending Export | Yes | No; written straight to the Connected System |
| Recorded in change history | Yes | Never |
| Retried automatically | Yes | Only for an initial password, and only while retrying could help |
| Read back on import | Yes | Never |

Credential attributes are consequently denylisted throughout JIM: they cannot be imported, and they cannot be chosen in an Attribute Flow. `unicodePwd`, `userPassword` and their relatives are not available to select, and a deployment that had selected one before the denylist existed has it deselected and locked at the next schema refresh rather than deleted, so the Synchronisation Rules referencing it stay intact. See [Credential attributes are never managed](../configuration/connected-systems.md#credential-attributes-are-never-managed).

Whether a Connector can use the channel at all is a capability it declares. The [LDAP Connector](../connectors/jim-ldap-connector.md#setting-passwords) writes `unicodePwd` itself for Active Directory, with the correct encoding, and uses the LDAP Password Modify extended operation (RFC 3062) everywhere else; it never writes a password attribute directly, because a directly written `userPassword` is stored exactly as supplied and would leave the password readable in the directory.

## Knowing what the target will accept

A password JIM generates has to satisfy rules JIM did not write, so JIM reads them from the target.

Whenever a Connected System's schema is retrieved or refreshed, JIM also reads the system's password policy and records it: minimum length, whether complexity is required and how many character categories that implies, password history length, and maximum and minimum password age. That policy pre-fills the generator, so configuring a compliant password does not mean retyping rules the system already publishes.

**Three limits matter, and none of them are edge cases.**

- **Most systems publish nothing.** Only Active Directory and Samba AD expose a password policy a client can read. Other directories keep their rules in configuration an ordinary connection cannot see, and there is no cross-vendor standard for exposing them. JIM reports that it found nothing rather than implying the system has no rules.
- **A policy can apply to only some accounts.** Active Directory calls these Fine-Grained Password Policies. Reading them requires privileges JIM's service account should not hold, so JIM detects whether any exist rather than enumerating them, and answers one of three ways: none, some exist, or it could not tell. "Could not tell" is its own answer rather than being folded into "none", because an empty result from a directory is precisely what a caller with no rights over them receives.
- **A custom password filter is exposed over no protocol at all.** A system can run its own rules that nothing can discover. A password satisfying everything JIM read can still be refused.

Every discovered value is therefore a **floor, not a guarantee**, and a null means JIM could not read that rule rather than that no such rule exists. Handling a refusal is part of how the channel works rather than an error case, and no amount of discovery removes the need for it.

The **Check password channel** preflight on the Connected System's Schema tab reports the things that commonly stop a password set (encryption, the mechanism, whether the service account may actually reset passwords in each container, whether the policy could be read) and writes nothing, so it is safe against production. There is no way to prove the whole chain without really setting a password on a real account, and JIM does not offer one. See [Password policy and the password channel](../configuration/connected-systems.md#password-policy-and-the-password-channel).

## Where a password comes from

JIM generates passwords itself, in three styles: random characters, words, or a pronounceable password. Each is generated from a cryptographic random source, and JIM reports the entropy, the minimum length and the character classes the result is guaranteed to carry, so the choice is made on figures rather than on feel.

Generation is available wherever a password is needed:

- On the [Synchronisation Rule](../configuration/synchronisation-rules.md#initial-password), for accounts it provisions.
- In the portal, on a single Connected System Object or on a person's accounts across several systems.
- To automation, through `Set-JIMConnectedSystemObjectPassword -Generate`, `Set-JIMMetaverseObjectPassword -Generate`, and their REST equivalents.

You can always supply your own password instead. What you cannot do is have JIM guess: where a request names several Connected Systems, generating is the better choice precisely because the reconciled policy is not something you can see in order to reason about it.

### One password, several systems

A person usually has accounts in more than one place, and one password across them is both less work and better for them than four. JIM reconciles the selected systems' discovered policies into a single set of requirements: the longest minimum length any of them demands, and only the character categories **all** of them count. A category one system recognises cannot help satisfy another system's complexity rule, so JIM counts only the intersection.

Where no single password can satisfy them all, JIM refuses outright rather than returning one that would be accepted on the first account and refused on the second, after the first has already changed. Where a selected system published no policy, that is reported as a warning rather than being read as consent.

There is no transaction across Connected Systems. A run routinely ends with some accounts changed and others not, so every account's outcome is reported separately, and a refusal of the **password itself** is offered a fresh password for every account rather than only the failed one, because replacing it only where it failed would leave the person with two.

## Initial passwords on provisioning

An account a Synchronisation Rule has just provisioned has no password, and most directories will not let it be signed in to or even enabled without one. Turning on **Initial Password** on an export Synchronisation Rule has JIM set one on every account that rule creates. It is off until you turn it on, on every rule.

The setting lives on the Synchronisation Rule rather than on the Connected System because rules are how JIM distinguishes populations: contractors and permanent staff provisioned into the same directory can reasonably want different password rules.

**Setting the password is a separate step from creating the account, and deliberately cannot fail the export that created it.** The account exists; reporting its export as failed would have JIM retry the create. The password is instead delivered in its own pass at the end of every export run, over everything the Connected System still owes rather than only what this run created. An ordinary export run is therefore the retry vehicle: a directory brought back online, or a right granted to JIM's service account, is picked up by the next run that was going to happen anyway, with no separate Run Profile to schedule.

Each account is in exactly one of four states afterwards:

| State | Meaning | Who resolves it |
|---|---|---|
| Delivered | The password was set. | Nobody; the account no longer owes one. |
| Retrying | Not yet attempted, or the last attempt failed for a reason a retry could fix: the directory was unreachable, or the account was not visible yet. | JIM, on the next export run over that Connected System. |
| Parked | The target refused the password **itself**, for not satisfying the rules in force for that account. Another password generated the same way would be refused for the same reason, so JIM stops. | You. |
| Expired | A week passed without success. JIM stops trying and records the fact rather than quietly forgetting the account. | Nobody automatically; the account needs a password by other means. |

A parked account keeps the target's own words verbatim, because why a directory refuses a password is a property of that directory's policy and the single most useful thing to be shown.

**Parking is not a one-way door.** Saving a change to the Synchronisation Rule's initial password settings releases every account parked against that rule, and they are attempted again on that Connected System's next export run. Nothing needs regenerating or invalidating in the meantime, because no password was ever stored: the retry uses your corrected settings by construction. Before you save, the portal says how many accounts saving will release, and stays quiet for an edit that would not change what is delivered.

Parked and expired counts appear on the Synchronisation Rule and Connected System list pages, on the rule's Initial Password panel, and through `Get-JIMSyncRuleInitialPassword` and `Get-JIMConnectedSystem`. The two counts are never summed: parked work is fixable where it is reported and expired work is not.

## The security model

Five rules hold across every surface.

1. **No password value is ever stored.** Not in JIM's database, its logs, its Activities, its change history, its previews, its search, or its API responses. Each is generated at the moment it is delivered, handed to the Connector, and dropped. The single deliberate exception is a caller who explicitly asked JIM to generate one and is handed it back once, and nothing is stored even then.
2. **Getting the password to the person is not JIM's job.** Requiring a change at the next sign-in is the default, and the right one for a password somebody else chose. Where you need a password in hand for a specific account, ask for one for that account rather than expecting JIM to have kept the provisioning one.
3. **All generation uses a cryptographic random source.** Never a general-purpose pseudorandom generator.
4. **Setting a password is a password-reset primitive.** Anyone who can reach the action can reset any account in that connector space, up to and including privileged ones, subject only to what the Connected System's service account may do. Grant the Administrator role accordingly, and scope the service account's rights to the containers JIM manages. This is why JIM offers no "test the password channel by setting one" feature: every route to proving the chain end to end is a real reset against a real account.
5. **The service account needs the reset right and nothing more.** In Active Directory that is the **Reset Password** control access right delegated on the OUs JIM manages, which is separate from write access to attributes. **It does not need to be a Domain Admin, and should not be.** See [Service Account Permissions](../connectors/jim-ldap-connector.md#service-account-permissions).

Every attempt is recorded as an Activity against the object, whether it succeeded or not, carrying the outcome and the target's verbatim reason on a refusal. The Activity records that a password was set, never the password.

## Where to go next

| To | See |
|---|---|
| Configure initial passwords on a rule | [Synchronisation Rules: Initial password](../configuration/synchronisation-rules.md#initial-password) |
| Review a discovered policy, or run the preflight | [Connected Systems: Password policy and the password channel](../configuration/connected-systems.md#password-policy-and-the-password-channel) |
| Set a password on one account, or on a person's accounts | [Connected Systems: Setting the password on one account](../configuration/connected-systems.md#setting-the-password-on-one-account) |
| Directory specifics, encryption and permissions | [LDAP Connector: Setting Passwords](../connectors/jim-ldap-connector.md#setting-passwords) |
| Do any of it from a script | [PowerShell: Connected Systems](../powershell/connected-systems.md#set-jimconnectedsystemobjectpassword), [Metaverse](../powershell/metaverse.md#set-jimmetaverseobjectpassword), [Synchronisation Rules](../powershell/synchronisation-rules.md#initial-password) |
