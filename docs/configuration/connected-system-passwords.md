---
title: Connected System Passwords
description: "The password policy JIM reads from a Connected System, checking its password channel, setting one object's password, and Password Synchronisation."
type: object
audience: administrator
---

# Connected System Passwords

Where a Connected System accepts passwords, JIM reads its password policy, lets you check and use its password channel, and can synchronise password changes to it. [Passwords](../concepts/passwords.md) explains the model; this page covers each Connected System's part in it.

## Password policy and the password channel

Where a Connected System can accept passwords, its **Passwords** tab carries two panels beneath the Password Synchronisation settings: Password Channel, which lets you check the channel works before you rely on it, and Password Policy, which shows you the password rules JIM read from the system itself.

[Passwords](../concepts/passwords.md) explains the channel as a whole: why passwords do not travel through Attribute Flow, what discovery can and cannot tell you, and how a refused password is resolved.

### Discovered password policy

JIM reads the target's password policy whenever it retrieves or refreshes the Connected System's schema, and records it, so that configuring a generated password does not mean retyping rules the system already publishes. The policy is read during a schema retrieval, so if it is missing, **Refresh Schema** on the Schema tab reads it again; the panel says when it was last read. What is shown depends on what the system exposes: minimum length, whether complexity is required and how many character categories that means, password history length, and maximum and minimum password age.

Which directories publish which rules, and where each keeps them, is on [Passwords](../concepts/passwords.md#discovering-the-targets-rules); the LDAP Connector reads Active Directory, Samba AD, OpenLDAP (with the `ppolicy` overlay) and 389 Directory Server, and each publishes a different subset.

**A discovered policy is a floor, not a guarantee.** Two things routinely make the real rule stricter than the published one:

- **Policies that apply to only some objects.** Every directory that publishes a policy also has a way to give some objects a different one: Active Directory's Fine-Grained Password Policies, an OpenLDAP entry's own `pwdPolicySubentry`, and 389 Directory Server's subtree and per-object policies. Reading them normally requires privileges JIM's service account should not hold, so JIM detects whether any exist rather than enumerating them, and reports one of three answers: none exist, some exist, or it could not tell. "Could not tell" is shown as its own state rather than being treated as "none", because an empty result from a directory is exactly what a caller with no rights over them receives; a search that finds nothing is always could not tell. Where the panel says policies exist or that it could not tell, treat the figures shown as a minimum.
- **Further checks.** A directory can enforce rules beyond the figures it publishes. Where it tells JIM that such checks are configured (an OpenLDAP check module, or 389 Directory Server's dictionary and character checks), the panel says beneath the figures that the directory applies further checks JIM cannot see, so a password satisfying everything above can still be refused. A custom Active Directory password filter is exposed over no protocol at all, so there the panel cannot even say that.

For that reason, handling a refusal is part of how the password channel works rather than an error case, and no amount of discovery removes the need for it.

**When the panel shows no rules, it says why.** The four reasons are distinct because each wants a different response from you:

| The panel says | What it means | What to do |
|----------------|---------------|------------|
| Nothing has been read yet | JIM has not read this system's policy. | **Refresh Schema** on the Schema tab reads it. |
| This directory publishes no password policy that JIM can read | A generic LDAP directory, or an OpenLDAP without the `ppolicy` overlay. Whatever rules it enforces live in configuration an ordinary connection cannot see, and there is no cross-vendor standard for exposing them. | Nothing to grant or refresh. If the directory enforces rules, configure the generator by hand. |
| JIM could not read this directory's password policy: the account it connects as cannot read the server configuration that holds it | 389 Directory Server keeps its policy on `cn=config`, and OpenLDAP names its default policy in the overlay's configuration; the service account may not read them. | Grant the read described under [Service Account Permissions](../connectors/jim-ldap-connector-permissions.md), then refresh the schema. |
| This directory's password policy mechanism is loaded but no policy is configured, so no rules apply | OpenLDAP loads the `ppolicy` overlay but no `pwdPolicy` entry exists. | Nothing, unless you expected a policy: then configure one in the directory and refresh the schema. |

JIM reports that it found nothing rather than implying the system has no rules, whichever of these applies.

### Checking the password channel

The **Check password channel** button runs a read-only preflight. It sets no password on anything, so it is safe to run against production at any time.

It reports on four things:

| Check | What it means |
|-------|---------------|
| **Encryption** | Whether the connection is encrypted. A warning rather than a failure, because JIM permits an unencrypted password channel; Active Directory refuses one itself, so there it is very likely to be what stops a password set. |
| **Password mechanism** | Whether the mechanism JIM would use is available: writing `unicodePwd` for Active Directory, or the LDAP Password Modify extended operation elsewhere. A system offering neither is reported as a failure, because JIM will not fall back to writing a password attribute directly. |
| **Reset rights** | Whether the account JIM connects as may reset passwords in each container it manages. Reported per container, since directories grant rights per part of the tree. |
| **Policy discovery** | Whether the password policy could be read, so the generator can be pre-filled from it. Never a failure: an unreadable policy means you configure the rules by hand. |

Each check returns passed, warning, failed, or **could not tell**, and the last of those is deliberately distinct. A directory withholds what a caller may not see by omitting it, not by refusing: an attribute absent from a result, or a search returning no rows, both with a success code. Reporting either as a failure would tell you an account lacks rights it demonstrably has. Where the panel says JIM could not tell, the answer has to be confirmed at the system itself.

A preflight is not stored. Reachability, permissions and policy all change without JIM being told, so a result kept on file would go on reassuring you long after it stopped being true.

!!! note "The reset rights check needs somewhere to look"
    Rights are checked in the containers this Connected System manages, by reading the permissions of one ordinary account in each. Select the containers to manage on the Scope tab first, or the check has nowhere to look and says so. Accounts held in a directory's privileged groups are skipped: directories periodically overwrite their permissions from a template and switch off inheritance, so a delegation made on the container does not apply to them and sampling one would report the whole container as denied.

### Setting the password on one Connected System Object

Open a Connected System Object from the Connector Space and, where the Connector can set passwords, the object carries a **Set Password** button. It is the same operation as Set Password on the Metaverse Object, aimed at this one Connected System Object: the change is queued, encrypted, and the [Password Delivery Service](../concepts/password-synchronisation.md#the-password-delivery-service) writes it within about a second, whatever the synchronisation engine is doing. It is never staged as a Pending Export, and JIM holds the password only until the object has it; a password the system refused is kept, still encrypted, so JIM can finish the job once the cause is dealt with. The Connected System Object must be joined to a Metaverse Object, because a password belongs to a Metaverse Object and that is where its history is kept.

Use it for the new starter about to sign in for the first time, the Connected System Object whose provisioning password was refused, and the reset that has to happen now. Routine initial passwords belong on the [Synchronisation Rule](synchronisation-rules.md) that provisions the object, where they happen without anybody watching.

The dialog is built around one rule: **the password is masked from the moment it is generated, and copying it does not require showing it.**

- **Generate** produces a password satisfying the discovered policy, and puts it straight behind a mask.
- **Copy** works while the value is masked, so handing a password to the person who needs it never means putting it on a screen somebody else can read.
- **Reveal** is the secondary action, for reading a password aloud or checking a transcription. It hides itself again after thirty seconds.
- You can type your own password instead of generating one.

Choose what happens to the password once it is set (requiring a change at the next sign-in is the default, and the right one for a password somebody else chose), and whether to enable the Connected System Object at the same time. Leaving the enable switch off leaves the object's enabled state as it was, which is what a reset on a working object should do. Both choices apply to this object now, whatever its Attribute Flows say: switching the enable on enables it, and on Active Directory and Samba AD the expiry sets "Password never expires" for **Never expires** and clears it for the other two. Where a Synchronisation Rule flows `userAccountControl` with [Enforce State](synchronisation-rules.md#export-outbound) on, the next export puts the flow's value back, and an outcome that changed "Password never expires" completes with a warning saying so, because a change at the next sign-in stops being asked for once the flow sets the flag again.

Leaving the switch off has one exception: where JIM held an Active Directory account's enable back until it had a password, and its Attribute Flow asks for it enabled, the password enables it whichever way the switch is set ([Enabling new Active Directory accounts](../connectors/jim-ldap-connector-passwords.md#enabling-new-active-directory-accounts)).

The dialog waits for the outcome and shows it: **Set**, **Retrying** with the next attempt where the system could not be reached (JIM keeps trying on its own clock, and you can stop it), or **Parked** where the system refused the password, carrying its own words so you can try another one. Every attempt is recorded as an Activity, whether it succeeded or not; the Activity records that a password was set, never the password.

A system can take the password but not apply everything you chose for it. OpenLDAP and 389 Directory Server leave expiry to their own password policy, so they cannot require a change at the next sign-in or make a password never expire. The outcome is then **Set** with a warning, and the dialog shows what was applied instead, in the words of its Activity, which completes with a warning rather than plainly. Tell the person what the dialog says, not what you asked for: a password that was meant to be changed at the next sign-in still works after it.

!!! warning "This resets the password on whichever Connected System Object you point it at"
    Anyone who can reach this action can reset the password of any Connected System Object in this Connector Space, up to and including privileged ones, subject only to what the Connected System's own service account is permitted to do. Grant the Administrator role accordingly, and scope the service account's rights to the containers JIM manages.

!!! note "Copying and your operating system's clipboard"
    Copying needs an HTTPS connection: browsers deny clipboard access over plain HTTP, and the button says so rather than silently doing nothing. JIM clears the clipboard when the dialog closes where the browser allows it, but your operating system may keep the value in its own clipboard history, which no web page can reach.

The same action is available to automation through `Set-JIMConnectedSystemObjectPassword` and the REST API, which can either take a password you supply or generate one against the discovered policy. A generated password is returned to the caller, once, because they asked for it; JIM's own copy is the queued one, and it goes when the Connected System Object has it.

### One password across several Connected Systems

A Metaverse Object often has Connected System Objects in more than one place, and conveying a different password for each is both more work and worse for the person: four different passwords on a first morning end up on a sticky note. Open the Metaverse Object's **Password** tab and **Set Password** lists every Connected System Object it has whose Connector can set a password.

Choose some or all of them and JIM sets one password across them, one queued change per Connected System, each delivered on its own. **Nothing is selected by default**, so resetting a forgotten password in one system never silently resets the others. A Connected System Object in a system whose Password Synchronisation is switched off is still delivered to, and the dialog says the system is paused for propagated changes; you named the Connected System Object, which is the decision that switch exists to make.

The password is generated to satisfy the strictest of the selected systems' rules: the longest minimum length any of them demands, and the character categories all of them count. A category only one system recognises cannot help satisfy another system's complexity rule, so JIM counts only what they have in common. Where a selected system has never published a policy, JIM says so rather than assuming it will accept anything.

The dialog waits for the outcomes and shows one per Connected System: **Set**, **Retrying** with the next attempt (and a way to stop trying), **Parked** with the system's own words, or **Held**. A system that set the password but could not apply what you chose for its expiry reads **Set** with a warning saying what it applied, as it does for one Connected System Object above.

!!! warning "There is no transaction across Connected Systems"
    Each system is delivered to independently. A reset can end with the new password in some systems and not yet in others, and the person has a different password where a system refused it. JIM says which, in as many words. A system that could not be reached is retried by JIM on its own clock; nothing is lost while it is down.

    Where a system refused the **password itself**, retrying it unchanged will fail identically. **Try another password** generates a fresh one for every Connected System Object instead, including the ones that already took the first, because replacing it only where it failed would leave the person with two.

Every Connected System gets its own Activity, grouped under one parent for the change, so the whole action is findable afterwards and appears in the person's password history beside any propagated change.

For automation, `Set-JIMMetaverseObjectPassword -ConnectedSystemId` does the same thing over the same REST endpoint. Omitting `-ConnectedSystemId` aims the password at every Connected System configured for Password Synchronisation instead; see [Passwords > Setting a password](../concepts/password-synchronisation.md#setting-a-password) for the two modes.

## Password Synchronisation

Setting a password on named Connected System Objects, above, is a choice made per reset. Password Synchronisation is the standing arrangement: which systems receive a Metaverse Object's password when it is set without naming any, so that one change reaches every system it has a Connected System Object in.

It is configured on the Connected System's **Passwords** tab, which appears only where the connector can set passwords at all. Systems whose connector has no password channel do not show the tab, because there is nothing to configure rather than something to switch on later.

| Setting | What it does |
|---|---|
| **Deliver password changes to this Connected System** | Whether queued password changes are delivered. Separate from the configuration existing, so a system can be set up ahead of a change window and switched on during one. |
| **Object Type that receives passwords** | Which Connected System Object Type receives passwords. Only Object Types you have selected for synchronisation are offered: an unselected one holds no objects, so choosing it would queue passwords for objects that never appear. |
| **Maximum attempts** | How many delivery attempts JIM makes before it stops and asks you to look. Leave it at 0 to use JIM's default of five. |
| **First retry after** | How long to wait before the first retry. Each further attempt waits twice as long as the one before. |

Two things follow from the enable toggle being separate from the configuration:

- **Switching a system off does not discard anything.** Password changes for Metaverse Objects with a Connected System Object there accumulate, and switching it back on delivers what accumulated. That is what makes it safe to switch off for a maintenance window.
- **There is no way to remove a configuration, only to disable it.** Removing one would throw away everything queued against it, so JIM does not offer that. This is true of the REST API and PowerShell too.

Two settings that govern delivery here live on the **Settings** tab instead, under Passwords, because they govern every password JIM sends to this system rather than only synchronised ones:

- **Only send passwords over an encrypted connection.** JIM cannot always tell an encrypted connection from an unencrypted one; a signed and sealed bind is encrypted but does not look it from the system's settings, which is why JIM warns rather than refusing by default. Turn this on, once you know the connection is encrypted, to have JIM refuse to send instead. Nothing is discarded when it refuses: queued password changes wait, and Connected System Objects stay owed their first password. It applies equally to the initial password on a Connected System Object JIM provisions and to a password you set by hand.
- **Give up after.** How long a queued change waits before JIM expires it rather than delivering a password that has since been superseded. Shared with initial password provisioning deliberately: the question both are asking is how long this system may be unavailable before JIM stops trying, and the answer is a property of the system rather than of the deployment.

Every change to these settings reaches the Connected System's Configuration Change History, so switching Password Synchronisation on or off is attributable afterwards.

For automation, `Get-JIMConnectedSystemPasswordSynchronisation` and `Set-JIMConnectedSystemPasswordSynchronisation` do the same over the REST API; `ConnectorSupportsPasswordSet` on the response tells you whether a system can be configured at all.
