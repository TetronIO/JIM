# Passwords

A newly provisioned account is not much use until somebody gives it a password. JIM can do that for you, on the accounts it manages, without anyone touching the target system by hand.

It can also set a password on demand: on one account, or the same password across every account a person has.

!!! info "Nothing happens until you ask for it"
    A Connected System is only a candidate if its Connector supports setting passwords. Even then, JIM sets nothing until you configure it: initial passwords are off on every Synchronisation Rule until you switch them on, and every other route here is a deliberate action on a named account. Today the [LDAP Connector](../connectors/jim-ldap-connector.md#setting-passwords) is the Connector that supports it, covering Active Directory, Samba AD, OpenLDAP and generic LDAP directories.

This page covers what JIM does with passwords and why. To actually configure it, follow the links in [Where to go next](#where-to-go-next).

## 🔑 Giving new accounts their first password

Most directories will not let an account be used, or even enabled, until it has a password. Switching on **Initial Password** on an export Synchronisation Rule has JIM set one on every account that rule creates, so the account is complete and enabled from the moment it exists instead of waiting on somebody to do it by hand.

By default that password is different for every account and JIM keeps no copy, so it is not one anybody receives; see [how the person gets their password](#so-how-does-the-person-get-their-password) below. One password for every account is offered as well, and is [not recommended](#one-password-for-every-account).

--8<-- "assets/diagrams/initial-password.svg"

<p class="jim-diagram-caption">The password is set in its own pass at the end of the export run, not as part of creating the account.<span class="jimdg-caption-motion"> Moving dots trace an account through to a delivered password.</span></p>

The setting lives on the Synchronisation Rule rather than on the Connected System because rules are how JIM separates populations: contractors and permanent staff provisioned into the same directory can want different password rules.

### Why it is a separate step

Setting the password happens after the account is created, and cannot fail the export that created it. If it could, JIM would treat the account as never created and try to create it again.

Instead the password is delivered in its own pass at the end of every export run, covering every account on that Connected System still owing one, not just the accounts this run created. **An ordinary export run is therefore the retry.** A directory that was offline, or a permission your service account was missing, is picked up by the next run that was going to happen anyway. There is nothing extra to schedule.

### What you will see afterwards

Every account ends up in one of four states, reported on the export's Activity.

| State | What it means | What you do |
|---|---|---|
| Delivered | The password was set and the account is ready to use. | Nothing. |
| Retrying | JIM could not reach the system, or the account was not visible yet. | Nothing; JIM tries again on the next export run. |
| Parked | The system refused the password itself, for not meeting the rules that apply to that account. Another password generated the same way would be refused for the same reason, so JIM stops rather than spending attempts on the same answer. | Correct the rule's password settings. See below. |
| Expired | A week passed without success. JIM stops trying, and records that it did rather than quietly forgetting the account. | Set a password on those accounts another way. |

A parked account keeps **the system's own words, unaltered**, because why a directory refused a password is a fact about that directory, and it is the most useful thing you can be shown.

### Getting parked accounts moving again

Parking is not a dead end. **Saving a change to the rule's initial password settings releases every account parked against it**, and they are tried again on that Connected System's next export run. There is nothing to regenerate or invalidate first: a generated password is produced afresh at delivery, and setting a new shared password is itself the change that releases the work, so the retry uses your corrected settings either way. Before you save, the portal tells you how many accounts saving will release, and says nothing at all for an edit that would not change what gets delivered.

You are told where the work is waiting without going looking for it: parked and expired counts appear on the Synchronisation Rules and Connected Systems list pages, on the rule's own Initial Password tab, and through `Get-JIMSyncRuleInitialPassword` and `Get-JIMConnectedSystem`. The two counts are shown separately and never added together, because parked work is fixable where it is reported and expired work is not.

### One password for every account

A generated password nobody can be told is the right answer for getting an account working and the wrong one for the day a new starter arrives. **One password for every account** is the third option under Password Settings: you choose it, JIM sets that same password on every account the rule provisions, and you can put it on an onboarding sheet or read it out.

!!! warning "This option is not recommended"
    Every account the rule provisions shares this password until each person changes it, so anybody who learns of this can sign in as any new starter who has not. Note: the password is stored encrypted and cannot be shown to you again, and it is the only password JIM stores anywhere.

    Leave **Require a change at the next sign-in** switched on: it is what ends each account's share of the password. Any other setting leaves every account the rule provisions on it until somebody changes it by hand.

If you use it, three things are worth knowing:

- **You cannot read it back.** JIM encrypts it and no surface will show it to you again, so keep your own record of it. What JIM will tell you is that one is set and when it last changed, on the rule's Initial Password tab and through `Get-JIMSyncRuleInitialPassword`.
- **Change it whenever somebody who knew it leaves.** The date JIM reports is what makes that checkable across every rule at once; there is nothing else that can date a shared password.
- **A password the target would refuse is refused here.** JIM checks it against the policy it discovered when you set it, rather than letting it park every account the rule provisions.

Delivering a generated password to somebody who should have it, by email, is the answer that replaces this one; it is not built yet.

## 🧭 Knowing what each system will accept

A password JIM generates has to satisfy rules JIM did not write, so JIM reads them from the system itself.

Whenever a Connected System's schema is retrieved or refreshed, JIM also reads its password policy and remembers it: minimum length, whether complexity is required and how many character categories that means, password history length, and maximum and minimum password age. Those figures pre-fill the generator, so a compliant password normally needs no configuration from you at all.

**What JIM cannot always find out is worth knowing up front:**

- **Most systems publish nothing.**<br /> Only Active Directory and Samba AD expose a password policy a client can read. Other directories keep their rules in configuration an ordinary connection cannot see, and no cross-vendor standard exists for exposing them. JIM tells you it found nothing rather than implying the system has no rules.
- **A policy can apply to only some accounts.**<br /> Active Directory calls these Fine-Grained Password Policies. Reading them needs privileges your JIM service account should not have, so JIM checks whether any exist rather than reading them, and gives you one of three answers: none, some exist, or it could not tell. "Could not tell" is kept separate from "none" on purpose, because a directory hides what you may not see by returning nothing, which looks identical to there being nothing.
- **A system can enforce rules nothing can discover.**<br /> A custom password filter is exposed over no protocol at all. A password meeting everything JIM read can still be refused.

So treat what JIM discovered as a **floor, not a guarantee**, and read a blank value as "JIM could not find this out", never as "there is no such rule". That is also why the parked state above exists: handling a refusal is part of how this works, not a sign something went wrong.

!!! tip "Check the channel before you rely on it"
    **Check password channel**, on the Connected System's Schema tab, tests the things that usually stop a password being set: whether the connection is encrypted, whether the mechanism JIM needs is available, whether your service account may actually reset passwords in each container, and whether the policy could be read. It sets no password on anything, so it is safe to run against production whenever you like.

    It cannot prove the whole chain, and JIM deliberately offers nothing that does, because the only way to prove it end to end is to reset a real account's password.

## 🔐 Setting a password on demand

Alongside provisioning, you can set a password whenever you need to: the new starter about to sign in for the first time, the account whose provisioning password was refused, the reset that has to happen now.

- **One account.**<br /> Open a Connected System Object and use **Set Password**. The password is masked from the moment it is generated, and **Copy works while it is still masked**, so handing someone their password never means putting it on a screen others can read. Reveal is there for reading one aloud, and hides itself again after thirty seconds.
- **One person, several systems.**<br /> Open a person and the same action lists every account they have that JIM can set a password on. Nothing is selected by default, so a reset in one system never quietly resets the others.

JIM generates passwords in three styles (random characters, words, or a pronounceable password), always from a cryptographic random source, and tells you the length and character categories the result is guaranteed to carry. You can type your own instead. Automation has the same choice, through `-Generate` or `-Password` on the set-password cmdlets and their REST equivalents.

### One password across several systems

Giving somebody four different passwords on their first morning is more work for you and worse for them; they end up on a sticky note. JIM works out a single password that satisfies every selected system at once: the longest minimum length any of them demands, and only the character categories **all** of them count, since a category one system does not recognise cannot help satisfy another's complexity rule.

This is the case where letting JIM generate the password matters most. You cannot see those policies in order to reason about them, and JIM can.

!!! warning "Each system is written to independently"
    There is no transaction across Connected Systems, so a run can end with some accounts changed and others not, leaving the person with a different password where it failed. JIM tells you exactly which, and offers to retry just those, reusing the password already in hand.

    Where a system refused the **password itself**, sending it again would fail identically, so JIM offers a fresh password for every account instead, including the ones that already worked. Replacing it only where it failed would leave the person with two passwords.

    Where JIM could read no policy from a selected system, it says so rather than assuming that system will accept anything. Where no single password could satisfy them all, it refuses before writing anything, rather than handing you one that the first account accepts and the second rejects after the first has already changed.

## 🛡️ How JIM handles passwords safely

**No password value is stored, with one exception you have to choose.** Not in JIM's database, its logs, its Activities, its configuration history, its previews or its search. Each password is generated at the moment it is needed, handed to the Connected System, and dropped.

The exception is a [shared initial password](#one-password-for-every-account) you set on a Synchronisation Rule. That one has to survive until the next account is provisioned, so it is stored, encrypted at rest exactly as a Connected System's credentials are. It is write-only on every surface: no portal page, REST response or cmdlet will return it, and your configuration history records a keyed hash of it, which is enough to show that it changed and when without carrying the password.

A password you explicitly asked JIM to generate for you is a different case and is stored nowhere: it is handed back once, to you, and forgotten. That is why the portal generates one for you on screen but the provisioning path does not; there is nothing kept to look up later.

Every attempt is recorded as an Activity against the account, whether it worked or not, carrying the outcome and the system's own words on a refusal. The Activity records that a password was set. It never records the password.

### So how does the person get their password?

**Not the generated one set during provisioning.** It is different for every account and JIM keeps no copy, so there is nothing for anyone to look up or pass on. Nobody can tell a new starter what it is, including you.

That is deliberate, and it means the initial password is doing a different job from the one it might look like it is doing. Its job is to get the account into a working state: many directories will not enable an account, or let it be used at all, until it holds a password that meets their rules, and an account left sitting with no password while it waits for somebody to get round to it is worth closing off. It is not a password anybody is meant to receive.

There are two ways to give somebody something they can actually sign in with:

- **Set their password when they need it,** using **Set Password** on that account, and hand them the value. Requiring a change at next sign-in (the default) then does what you would expect: they use what you gave them once, and choose their own. This is the right answer for one person at a time.
- **Use [one password for every account](#one-password-for-every-account)** where handing out a password per person is not practical, accepting that every new starter shares it until they change it. Read that section before you do.

!!! warning "Anyone who can set a password can reset any account"
    These actions reset the password on whichever account they are pointed at, including privileged ones, limited only by what the Connected System's service account is allowed to do. Grant the Administrator role accordingly, and restrict the service account's rights to the parts of the directory JIM manages.

    That service account needs the **Reset Password** right on those containers and nothing more; in Active Directory this is separate from write access to attributes. **It does not need to be a Domain Admin, and should not be.** See [Service Account Permissions](../connectors/jim-ldap-connector.md#service-account-permissions).

## ⚙️ Why passwords do not behave like other data

Everything else JIM manages flows through the Metaverse: imported from a source system, held centrally, evaluated by your Synchronisation Rules, queued as a Pending Export, and written out. That machinery remembers values, compares them, shows them to you, and retries them. All four are the wrong thing to do with a credential.

Passwords therefore travel their own path, which never touches any of it.

| | Ordinary attributes | Passwords |
|---|---|---|
| Held centrally in the Metaverse | ✅ | ❌ |
| Queued as a Pending Export | ✅ | ❌ written straight to the system |
| Kept in configuration and object history | ✅ | ❌ a shared initial password is recorded as a keyed hash, never a value |
| Read back when JIM imports | ✅ | ❌ |
| Retried automatically | ✅ | Only an initial password, and only while a retry could help |

The practical consequence is that **attributes holding credentials cannot be managed as attributes at all.** `unicodePwd`, `userPassword` and their relatives cannot be imported and cannot be chosen in an Attribute Flow; JIM does not offer them. If an earlier version of your deployment had selected one, the next schema refresh deselects and locks it rather than deleting it, so any Synchronisation Rule referring to it stays intact. See [Credential attributes are never managed](../configuration/connected-systems.md#credential-attributes-are-never-managed).

It also means JIM writes passwords the way each directory expects rather than writing an attribute: for Active Directory it sets `unicodePwd` with the correct encoding, and elsewhere it uses the standard LDAP Password Modify operation. It never writes a password attribute directly, because directories store a directly written value exactly as supplied, which would leave the password readable in the directory.

!!! note "Synchronising passwords between systems is a separate capability"
    Everything on this page concerns JIM writing a password **to** a system. Capturing a password change in one system and replaying it into others is not yet available.

## Where to go next

| To | See |
|---|---|
| Switch on initial passwords for a rule | [Synchronisation Rules: Initial password](../configuration/synchronisation-rules.md#initial-password) |
| See a discovered policy, or run the channel check | [Connected Systems: Password policy and the password channel](../configuration/connected-systems.md#password-policy-and-the-password-channel) |
| Set a password on one account, or on a person | [Connected Systems: Setting the password on one account](../configuration/connected-systems.md#setting-the-password-on-one-account) |
| Directory specifics: encryption, mechanisms, permissions | [LDAP Connector: Setting Passwords](../connectors/jim-ldap-connector.md#setting-passwords) |
| Do any of it from a script | [PowerShell: Connected Systems](../powershell/connected-systems.md#set-jimconnectedsystemobjectpassword), [Metaverse](../powershell/metaverse.md#set-jimmetaverseobjectpassword), [Synchronisation Rules](../powershell/synchronisation-rules.md#initial-password) |
