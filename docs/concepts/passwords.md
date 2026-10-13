---
title: Passwords
description: How JIM gives new accounts their first password, learns what each system accepts, sets a password on demand, and keeps passwords safe.
type: concept
audience: administrator
---

# Passwords

A newly provisioned Connected System Object is not much use until somebody gives it a password. JIM can do that for you, on the Connected System Objects it manages, without anyone touching the target system by hand.

It can also set a password on demand: on the Connected System Objects you name, or on every system configured to receive a Metaverse Object's password changes.

!!! info "Nothing happens until you ask for it"
    A Connected System is only a candidate if its Connector supports setting passwords. Even then, JIM sets nothing until you configure it: initial passwords are off on every Synchronisation Rule until you switch them on, and every other route here is a deliberate action on a named Connected System Object. Today the [LDAP Connector](../connectors/jim-ldap-connector-passwords.md#setting-passwords) is the Connector that supports it, covering Active Directory, Samba AD, OpenLDAP, 389 Directory Server and generic LDAP directories.

This page covers what JIM does with passwords and why. To actually configure it, follow the links in [Where to go next](#where-to-go-next).

## 🔑 Giving new Connected System Objects their first password

Most directories will not let a newly created object be used, or even enabled, until it has a password. Switching on **Initial Password** on an export Synchronisation Rule has JIM set one on every Connected System Object that rule creates, typically within seconds of the object existing, so it is complete and enabled without waiting on somebody to do it by hand.

Active Directory goes further and refuses to create an account enabled at all while it has no password. When an Initial Password will follow, JIM creates the account disabled, even where the Synchronisation Rule's `userAccountControl` Attribute Flow asks for it enabled, and the password's delivery enables it; JIM waits for the password before checking the account against what the flow asked for. The [LDAP Connector](../connectors/jim-ldap-connector-passwords.md#enabling-new-active-directory-accounts) describes this, and what happens to the account if its password cannot be delivered.

Where an export has written the account's enabled state by the time its password is delivered, your Attribute Flow decides and the delivery never overrides it: an account the flow deliberately creates disabled, such as a pre-hire's, gets its password and stays disabled until the flow enables it. **Enable the Connected System Object once the password is set** applies only to the other accounts. [Accounts your Attribute Flow creates disabled](../connectors/jim-ldap-connector-passwords.md#accounts-your-attribute-flow-creates-disabled) has the detail.

By default that password is different for every Connected System Object and JIM keeps no copy, so it is not one anybody receives; see [how the person gets their password](#so-how-does-the-person-get-their-password) below. One password for every Connected System Object is offered as well, and is [not recommended](#one-password-for-every-connected-system-object).

--8<-- "assets/diagrams/initial-password.svg"

<p class="jim-diagram-caption">The Connected System Object joins the same queue and delivery service as every other password change, and is usually set within seconds of the Connected System Object existing.<span class="jimdg-caption-motion"> Moving dots trace a Connected System Object through to a delivered password.</span></p>

The setting lives on the Synchronisation Rule rather than on the Connected System because rules are how JIM separates populations: contractors and permanent staff provisioned into the same directory can want different password rules.

The Connected System Object exists before its password does: setting a password cannot fail the export that created it, because if it could, JIM would treat the Connected System Object as never created and try to create it again. Instead, the moment the export gives the new Connected System Object its external id, JIM queues a password change for it, recorded as "Initial password queued for delivery to {system}", and the [Password Delivery Service](password-synchronisation.md#the-password-delivery-service) takes it from there, typically within a second or two while the export run is still going. Because it shares the queue with every other password change, a refused or unreachable Connected System Object is retried on the Connected System's own [Password Synchronisation schedule](password-synchronisation.md) rather than waiting for another export run.

**A password change already waiting for the person becomes the first password instead.** Where the person's own password change, or a password an administrator set on the new Connected System Object before it was created, is already waiting when the export creates it, JIM queues no Initial Password: that change is delivered as the Connected System Object's first password, usually within seconds, and the export records "Initial password not queued for {system}" saying so. Passwords replace each other; what the Initial Password promises about the Connected System Object carries forward. The delivery enables it as **Enable the Connected System Object once the password is set** says, and sets or clears "Password never expires" from the Initial Password's expiry, except where an export has written those itself, exactly as it would for the Initial Password. It keeps its own password and expiry, so a password the person chose is never made to change at the next sign-in. The same holds when the person's change arrives after the Initial Password was queued but before it was delivered. On **Operations > Passwords** and the identity's Password panel, such a change is marked **First password**; cancelling it leaves the Connected System Object without a password from JIM, and the portal asks you before it does.

Two cases go the other way, and the export's Activity says which and why:

- **The waiting change cannot reach the Connected System Object.** One held because Password Synchronisation is switched off on the system has not reached it, and a first password is delivered whatever the pause; one JIM stopped trying because the system refused it, or because it can never be sent, would leave the Connected System Object with no password at all; and an administrator's set for a Connected System Object that no longer exists, or is no longer the person's, has nothing to go to. The Initial Password replaces it, with a warning, and the person's change records that it was not set on that system. Nothing else of the person's change is lost: it is still delivered to every other system.
- **The waiting change is for another Object Type.** Where the system's Password Synchronisation sets passwords on an Object Type the new Connected System Object is not, the person's change cannot reach it. JIM holds one password change per person per Connected System, so the Initial Password is not queued and that change stays; the export completes with a warning. The change cannot be delivered while the new Connected System Object is the person's, since a person has one per Connected System: cancel it on **Operations > Passwords**, then set a password on the new Connected System Object with the [set-password action](../configuration/connected-system-passwords.md#setting-the-password-on-one-connected-system-object), choosing to enable it if it should be.

### What you will see afterwards

Every Connected System Object ends up in one of five states, each recorded as a child Activity of the one written when the password was queued.

| State | What it means | What you do |
|---|---|---|
| Delivered | The password was set and the Connected System Object is ready to use. | Nothing. |
| Retrying | JIM could not reach the system, or the Connected System Object was not visible yet. | Nothing; JIM retries on the Connected System's own Password Synchronisation schedule. |
| Parked | The system refused the password itself, or the settings cannot produce one at all (a stored static password that can no longer be decrypted, say). Another attempt under the same settings would be refused for the same reason, so JIM stops rather than spending attempts on the same answer. | Correct the rule's password settings. See below. |
| Withdrawn | The Connected System Object or the Synchronisation Rule that provisioned it has since been removed, so there is nothing left to deliver a password to. | Nothing; this is not a failure, it is JIM noticing there is no longer a job to do. |
| Expired | The Connected System's initial password window passed without success, a week by default. JIM stops trying, and records that it did rather than quietly forgetting the Connected System Object. | Set a password on those Connected System Objects another way. |

A parked Connected System Object keeps **the system's own words, unaltered**, because why a directory refused a password is a fact about that directory, and it is the most useful thing you can be shown.

!!! note "Several domain controllers can mean one extra retry"
    Against a directory with several domain controllers, JIM's first attempt can arrive before the new Connected System Object has finished replicating to the one it reaches. That attempt retries on the Connected System's ordinary schedule like any other, and normally succeeds within the same run.

### Getting parked Connected System Objects moving again

Parking is not a dead end. **Saving a change to the rule's initial password settings releases every Connected System Object parked against it**, and the Password Delivery Service attempts them again within seconds, with no export run needed. There is nothing to regenerate or invalidate first: a generated password is produced afresh at delivery, and setting a new shared password is itself the change that releases the work, so the retry uses your corrected settings either way. Before you save, the portal tells you how many Connected System Objects saving will release, and says nothing at all for an edit that would not change what gets delivered.

You are told where the work is waiting without going looking for it: on **Operations > Passwords**, where an initial password appears alongside every other kind with origin **Initial**; on the identity's own Password panel; and on the Synchronisation Rule's own Passwords tab, with parked and expired counts and the parked reasons.

### How long JIM keeps trying

A Connected System Object stays owed its first password for **seven days** by default, after which JIM records the expiry above and stops. That window belongs to the Connected System rather than to the Synchronisation Rule, because what it has to outlast is that system being unavailable, and how long that lasts is a property of the system.

**Raise it before taking a system out of service for longer than the current window.** Every Connected System Object provisioned while the target is unreachable otherwise expires without a password, and each one then needs a password set by hand. Set it on the Connected System's Settings tab, under **Initial Passwords**, or with `Set-JIMConnectedSystem -Id 1 -InitialPasswordTimeToLive (New-TimeSpan -Days 30)`.

Parked, withdrawn and expired records are kept so you can see what became of a Connected System Object, under the same [Password Synchronisation retention period](password-synchronisation.md#how-long-any-of-it-is-kept) as every other finished password change, a year by default. A record still being worked is never removed, however old, and the Activity recording what happened to the Connected System Object outlives the record either way.

### One password for every Connected System Object

A generated password nobody can be told is the right answer for getting a Connected System Object working and the wrong one for the day a new starter arrives. **One password for every Connected System Object** is the third option under Password Settings: you choose it, JIM sets that same password on every Connected System Object the rule provisions, and you can put it on an onboarding sheet or read it out.

!!! warning "This option is not recommended"
    Every Connected System Object the rule provisions shares this password until each person changes it, so anybody who learns of this can sign in as any new starter who has not. Note: the password is stored encrypted and cannot be shown to you again. Apart from password changes queued for delivery, which JIM holds encrypted until they are delivered or [retention](password-synchronisation.md#how-long-any-of-it-is-kept) removes them, it is the only Connected System Object password JIM stores.

    Leave **Require a change at the next sign-in** switched on: it is what ends each object's share of the password. Any other setting leaves every Connected System Object the rule provisions on it until somebody changes it by hand.

If you use it, three things are worth knowing:

- **You cannot read it back.** JIM encrypts it and no surface will show it to you again, so keep your own record of it. What JIM will tell you is that one is set and when it last changed, on the rule's Passwords tab and through `Get-JIMSyncRuleInitialPassword`.
- **Change it whenever somebody who knew it leaves.** The date JIM reports is what makes that checkable across every rule at once; there is nothing else that can date a shared password.
- **A password the target would refuse is refused here.** JIM checks it against the policy it discovered when you set it, rather than letting it park every Connected System Object the rule provisions.

Delivering a generated password to somebody who should have it, by email, is the answer that replaces this one; it is not built yet.

## 🧭 Knowing what each system will accept

A password JIM generates has to satisfy rules JIM did not write, so JIM reads them from the system itself.

Whenever a Connected System's schema is retrieved or refreshed, JIM also reads its password policy and remembers it: minimum length, whether complexity is required and how many character categories that means, password history length, and maximum and minimum password age. Those figures pre-fill the generator, so a compliant password normally needs no configuration from you at all.

### Discovering the target's rules

What JIM can read depends on the directory, because each one keeps its policy in a different place and none of them publishes all of it. The LDAP Connector recognises three that publish one, and reads each where it keeps its rules; the [LDAP Connector reference](../connectors/jim-ldap-connector-passwords.md#password-policy-discovery) says where, and what your service account needs to be allowed to read.

| Rule | Active Directory and Samba AD | OpenLDAP (`ppolicy` overlay) | 389 Directory Server |
|------|-------------------------------|------------------------------|----------------------|
| Minimum length | ✅ | ✅ | ✅ When syntax checking is on |
| Password history | ✅ | ✅ | ✅ When history is on |
| Maximum age | ✅ | ✅ | ✅ When expiry is on |
| Minimum age | ✅ | ✅ | ✅ |
| Character classes | ✅ Complexity means three of five | ❌ Not published | ✅ A count of its five, when syntax checking is on |
| Further checks | ❌ A password filter is invisible | ✅ JIM sees that a check module is configured | ✅ JIM sees that dictionary, palindrome, repetition, sequence or per-class checks are on |

A blank figure on the panel means that directory does not publish the rule, or has it switched off. It never means "no rule".

**What JIM cannot always find out is worth knowing up front:**

- **Some directories publish nothing.**<br /> A generic LDAP directory, and an OpenLDAP without the `ppolicy` overlay, keep whatever rules they enforce in configuration an ordinary connection cannot see, and no cross-vendor standard exists for exposing them. JIM tells you it found nothing rather than implying the system has no rules.
- **A policy can apply to only some objects.**<br /> Every directory above has a way to give some objects a different policy from the one JIM read: Active Directory's Fine-Grained Password Policies, an OpenLDAP entry's own `pwdPolicySubentry` (or a second `pwdPolicy` entry beside the default), and 389 Directory Server's subtree and per-object policies (`nsPwPolicyContainer` and `pwdpolicysubentry`). Reading them needs privileges your JIM service account should not have, so JIM checks whether any exist rather than reading them, and gives you one of three answers: none, some exist, or it could not tell. "Could not tell" is kept separate from "none" on purpose, because a directory hides what you may not see by returning nothing, which looks identical to there being nothing; a search that finds nothing is therefore always "could not tell". A definite "none" needs something JIM can prove: an Active Directory domain at a functional level that cannot carry Fine-Grained Password Policies, or an OpenLDAP that does not load the overlay which would enforce an override. 389 Directory Server offers no such proof, so "could not tell" is the best answer it can give.
- **A system can enforce rules nothing can discover.**<br /> Where the directory tells JIM that further checks are configured (an OpenLDAP check module, or 389 Directory Server's dictionary and character checks), the panel says so: the directory applies further checks JIM cannot see. A custom Active Directory password filter is exposed over no protocol at all, so there the panel cannot even say that. Either way, a password meeting everything JIM read can still be refused.

**When the panel shows no rules, it says why**, and the four reasons want different responses:

- **Read**<br /> JIM read the policy, and any blank figure is a rule that directory does not publish. If nothing at all is shown, JIM has not read this system yet; **Refresh Schema** reads it.
- **Not published**<br /> This directory publishes no password policy that JIM can read. There is nothing to grant and nothing to refresh; configure the generator's rules by hand if the directory enforces any.
- **Configuration not readable**<br /> The directory keeps its policy in server configuration, and the account JIM connects as may not read it. Grant the read described under [Service Account Permissions](../connectors/jim-ldap-connector-permissions.md), then refresh the schema.
- **No policy configured**<br /> The directory's password policy mechanism is loaded but no policy is configured, so no rules apply.

So treat what JIM discovered as a **floor, not a guarantee**, and read a blank value as "JIM could not find this out", never as "there is no such rule". That is also why the parked state above exists: handling a refusal is part of how this works, not a sign something went wrong.

!!! tip "Check the channel before you rely on it"
    **Check password channel**, on the Connected System's Passwords tab, tests the things that usually stop a password being set: whether the connection is encrypted, whether the mechanism JIM needs is available, whether your service account may actually reset passwords in each container, and whether the policy could be read. It sets no password on anything, so it is safe to run against production whenever you like.

    It cannot prove the whole chain, and JIM deliberately offers nothing that does, because the only way to prove it end to end is to reset a real Connected System Object's password.

## 🔐 Setting a password on demand

Alongside provisioning, you can set a password whenever you need to: the new starter about to sign in for the first time, the Connected System Object whose provisioning password was refused, the reset that has to happen now.

- **One Connected System Object.**<br /> Open the Connected System Object and use **Set Password**. The password is masked from the moment it is generated, and **Copy works while it is still masked**, so handing someone their password never means putting it on a screen others can read. Reveal is there for reading one aloud, and hides itself again after thirty seconds.
- **One Metaverse Object, several systems.**<br /> Open the Metaverse Object's **Password** tab and **Set Password** lists every Connected System Object it has that JIM can set a password on. Nothing is selected by default, so a reset in one system never quietly resets the others.

JIM generates passwords in three styles (random characters, words, or a pronounceable password), always from a cryptographic random source, and tells you the length and character categories the result is guaranteed to carry. You can type your own instead. Automation has the same choice, through `-Generate` or `-Password` on the set-password cmdlets and their REST equivalents.

Whichever way you start it, the password is not written while you watch. It is queued, encrypted, one change per Connected System, and the [Password Delivery Service](password-synchronisation.md#the-password-delivery-service) writes it within about a second, whatever the synchronisation engine is doing. The dialog and the API wait briefly and tell you what each system did with it; see [Setting a password](password-synchronisation.md#setting-a-password) below for the outcomes and what each one asks of you.

### One password across several systems

Giving somebody four different passwords on their first morning is more work for you and worse for them; they end up on a sticky note. JIM works out a single password that satisfies every selected system at once: the longest minimum length any of them demands, and only the character categories **all** of them count, since a category one system does not recognise cannot help satisfy another's complexity rule.

This is the case where letting JIM generate the password matters most. You cannot see those policies in order to reason about them, and JIM can.

!!! warning "Each system is delivered to independently"
    There is no transaction across Connected Systems. Each one gets its own queued change, so one system being down or refusing the password does not stop the others, and the person can end up with the new password in some systems and not yet in others. JIM tells you exactly where each one stands: **Set**, **Retrying** where a system could not be reached (JIM keeps trying on its own clock), or **Parked** where the system refused it.

    Where a system refused the **password itself**, sending it again would fail identically, so JIM offers **Try another password**: a fresh one for every Connected System Object, including the ones that already took the first. Replacing it only where it failed would leave the person with two passwords.

    Where JIM could read no policy from a selected system, it says so rather than assuming that system will accept anything. Where no single password could satisfy them all, it refuses before queueing anything, rather than handing you one that the first Connected System Object accepts and the second rejects after the first has already changed.

## 🛡️ How JIM handles passwords safely

**A password is held, encrypted, only until it is delivered.** A password you set or propagate is encrypted the moment JIM receives it and sits on the queue only as long as it takes the Password Delivery Service to hand it to each Connected System; the moment a system has it, JIM's copy for that system is gone. A copy a system refused is kept, still encrypted, so JIM can finish the job once the cause is dealt with, until the change [expires or retention removes it](password-synchronisation.md#how-long-any-of-it-is-kept). Nothing else holds one: not JIM's logs, its Activities, its configuration history, its previews or its search, and no page, REST response or cmdlet will show you a queued password. An initial password JIM generates during provisioning is produced at the moment it is delivered and never queued at all.

The one password that is stored for longer is a [shared initial password](#one-password-for-every-connected-system-object) you choose to set on a Synchronisation Rule. That one has to survive until the next Connected System Object is provisioned, so it is stored, encrypted at rest exactly as a Connected System's credentials are. It is write-only on every surface: no portal page, REST response or cmdlet will return it, and your configuration history records a keyed hash of it, which is enough to show that it changed and when without carrying the password.

A password you explicitly asked JIM to generate for you is handed back once, to you, at the moment it is made; after that JIM's copy is the queued one, and it goes when the systems have it. That is why the portal generates one for you on screen but the provisioning path does not; there is nothing kept to look up later.

Every attempt is recorded as an Activity, whether it worked or not, carrying the outcome and the system's own words on a refusal. The Activity records that a password was set. It never records the password.

### So how does the person get their password?

**Not the generated one set during provisioning.** It is different for every Connected System Object and JIM keeps no copy, so there is nothing for anyone to look up or pass on. Nobody can tell a new starter what it is, including you.

That is deliberate, and it means the initial password is doing a different job from the one it might look like it is doing. Its job is to get the Connected System Object into a working state: many directories will not enable an object, or let it be used at all, until it holds a password that meets their rules, and an object left sitting with no password while it waits for somebody to get round to it is worth closing off. It is not a password anybody is meant to receive.

There are two ways to give somebody something they can actually sign in with:

- **Set their password when they need it,** using **Set Password** on that Connected System Object, and hand them the value. Requiring a change at next sign-in (the default) then does what you would expect: they use what you gave them once, and choose their own. This is the right answer for one person at a time.
- **Use [one password for every Connected System Object](#one-password-for-every-connected-system-object)** where handing out a password per person is not practical, accepting that every new starter shares it until they change it. Read that section before you do.

!!! warning "Anyone who can set a password can reset any Connected System Object"
    These actions reset the password on whichever Connected System Object they are pointed at, including privileged ones, limited only by what the Connected System's service account is allowed to do. Grant the Administrator role accordingly, and restrict the service account's rights to the parts of the directory JIM manages.

    That service account needs the **Reset Password** right on those containers and nothing more; in Active Directory this is separate from write access to attributes. **It does not need to be a Domain Admin, and should not be.** See [Service Account Permissions](../connectors/jim-ldap-connector-permissions.md).

## ⚙️ Why passwords do not behave like other data

Everything else JIM manages flows through the Metaverse: imported from a source system, held centrally, evaluated by your Synchronisation Rules, queued as a Pending Export, and written out. That machinery remembers values, compares them, shows them to you, and retries them. All four are the wrong thing to do with a credential.

Passwords therefore travel their own path, which never touches any of it.

| | Ordinary attributes | Passwords |
|---|---|---|
| Held centrally in the Metaverse | ✅ | ❌ |
| Queued as a Pending Export | ✅ | ❌ queued on their own encrypted queue, held only until delivered |
| Kept in configuration and object history | ✅ | ❌ a shared initial password is recorded as a keyed hash, never a value |
| Read back when JIM imports | ✅ | ❌ |
| Retried automatically | ✅ | ✅ on their own clock, and parked rather than retried into the same refusal |

The practical consequence is that **attributes holding credentials cannot be managed as attributes at all.** `unicodePwd`, `userPassword` and their relatives cannot be imported and cannot be chosen in an Attribute Flow; JIM does not offer them. If an earlier version of your deployment had selected one, the next schema refresh deselects and locks it rather than deleting it, so any Synchronisation Rule referring to it stays intact. See [Credential attributes are never managed](../configuration/connected-system-attributes.md#credential-attributes-are-never-managed).

It also means JIM writes passwords the way each directory expects rather than writing an attribute: for Active Directory it sets `unicodePwd` with the correct encoding, and elsewhere it uses the standard LDAP Password Modify operation. It never writes a password attribute directly, because directories store a directly written value exactly as supplied, which would leave the password readable in the directory.

## 🔁 Password Synchronisation

Everything above sets a password on one Connected System Object, at the moment you ask. Password Synchronisation is the other half: one password change reaching every system a Metaverse Object has a Connected System Object in, durably, without you standing over it. See [Password Synchronisation](password-synchronisation.md).

## Where to go next

| To | See |
|---|---|
| Switch on initial passwords for a rule | [Initial Password](../configuration/initial-password.md) |
| See a discovered policy, or run the channel check | [Connected Systems: Password policy and the password channel](../configuration/connected-system-passwords.md#password-policy-and-the-password-channel) |
| Set a password on one Connected System Object, or on a Metaverse Object | [Connected Systems: Setting the password on one Connected System Object](../configuration/connected-system-passwords.md#setting-the-password-on-one-connected-system-object) |
| Understand Password Synchronisation | [Password Synchronisation](password-synchronisation.md) |
| Configure Password Synchronisation on a system | [Connected Systems: Password Synchronisation](../configuration/connected-system-passwords.md#password-synchronisation) |
| Directory specifics: encryption, mechanisms, permissions | [LDAP Connector: Setting Passwords](../connectors/jim-ldap-connector-passwords.md#setting-passwords) |
| See what is queued, and retry or cancel it | [PowerShell: Password Synchronisation](../powershell/password-synchronisation.md) |
| Do any of it from a script | [PowerShell: Connected Systems](../powershell/connected-system-objects.md#set-jimconnectedsystemobjectpassword), [Password Synchronisation](../powershell/password-synchronisation.md#set-jimmetaverseobjectpassword), [Initial Password](../powershell/initial-password.md) |
