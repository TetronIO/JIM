---
title: Initial Password
description: "Give every Connected System Object an export Synchronisation Rule provisions its first password, and follow what happens next."
type: object
audience: administrator
---

# Initial Password

A Connected System Object a Synchronisation Rule has just provisioned has no password, and in most directories cannot be signed in to or even enabled without one. The **Initial Password** tab of an export Synchronisation Rule tells JIM to set one on every Connected System Object that rule creates.

```jim-diagram
archetype: decision
title: What happens to an initial password after provisioning
desc: 'When the export gives a new Connected System Object its external id, JIM queues a password change for it. If the object, or the Synchronisation Rule that provisioned it, has since been removed, the change is Withdrawn, which is not a failure. Otherwise the Password Delivery Service tries to set it. A password set is Delivered. A password the target refuses, or settings that cannot produce one, is Parked until you save a change to the rule''s initial password settings. An object that is unreachable or not visible yet is retried on the Connected System''s schedule, each attempt asking again whether the object and rule are still there, until a week has passed, when it is Expired and needs a password set by other means.'
start: {label: Password change queued}
questions:
- {id: there, label: 'Object and rule still there?'}
- {id: attempt, label: Delivery attempt, sub: 'what happened?'}
- {id: week, label: 'A week passed?'}
outcomes:
- {id: withdrawn, label: Withdrawn, sub: not a failure}
- {id: delivered, label: Delivered, sub: the password was set, good: true}
- {id: parked, label: Parked, sub: until you save the initial password settings}
- {id: expired, label: Expired, sub: set a password by other means}
answers:
- {from: there, to: withdrawn, label: 'no'}
- {from: there, to: attempt, label: 'yes'}
- {from: attempt, to: delivered, label: password set}
- {from: attempt, to: parked, label: refused or cannot be made}
- {from: attempt, to: week, label: unreachable or not visible}
- {from: week, to: there, label: 'no: retry on schedule'}
- {from: week, to: expired, label: 'yes'}
```

For how the password channel works as a whole (policy discovery and its limits, where a password comes from, and the security rules that hold across every surface) see [Passwords](../concepts/passwords.md).

It is off until you turn it on, on every rule: JIM setting passwords on Connected System Objects nobody asked it to is not a sensible default.

It also depends on the rule provisioning. Only a newly created Connected System Object has never had a password, so the tab appears only on an export rule with **Provision ... to the Connected System?** switched on, which is a setting on the Details tab. Switching that off removes the tab and switches the initial password off with it, rather than leaving a setting that reads as configured and can never run; any Connected System Objects parked waiting on those settings stop waiting. Switching provisioning back on brings the tab back with its settings intact, switched off.

The setting lives on the Synchronisation Rule rather than on the Connected System because rules are how JIM distinguishes populations. A rule provisioning contractors and a rule provisioning permanent staff into the same directory can reasonably want different password rules.

## :material-tune-variant: What you configure

- **Password Settings**<br /> Where the password comes from. Either the policy JIM discovered on the Connected System itself (the default, so the generated password satisfies the target's own rules without you restating them), or settings you write here, or [one password you choose for every Connected System Object](#one-password-for-every-connected-system-object). The first two generate a different password per object: choosing your own settings starts from the discovered policy rather than from nothing, switching between the two never discards what you configured, and the generator produces random characters, words, or a pronounceable password, telling you the minimum length and character classes the result is guaranteed to carry.
- **After the password is set**<br /> Whether the account holder must choose a new password at their next sign-in (the default), whether it ages normally, or whether it never expires. Only the behaviours the Connector can apply are offered; a directory that cannot apply the one chosen still takes the password, and its delivery completes with a warning naming what was applied (see [LDAP Connector Passwords](../connectors/jim-ldap-connector-passwords.md#setting-passwords)). On Active Directory and Samba AD, "Password never expires" follows this choice only where no export has written the object's `userAccountControl` by the time the password is delivered; where one has, your Attribute Flow decides it, and requiring a change at the next sign-in is still applied, though Active Directory ignores it while the flow keeps "Password never expires" set. The portal warns you under this setting when the rule maps `userAccountControl`. See [Enabling new Active Directory accounts](../connectors/jim-ldap-connector-passwords.md#enabling-new-active-directory-accounts) for how to set the flag in the flow.
- **Enable the Connected System Object once the password is set**<br /> On by default. A provisioned Connected System Object nobody can sign in to is rarely what was wanted, and directories that refuse to enable an object without a policy-compliant password need the enable to follow the password rather than accompany the create. It applies only to objects whose enabled state no export has written by the time the password is delivered: where one has (`userAccountControl` on Active Directory and Samba AD), your Attribute Flow decides, so an object it creates disabled, such as a pre-hire's, stays disabled. Off leaves the object as it was created. It applies, as does "Password never expires" from the setting above, when a password change already waiting for the person is delivered as the object's first password instead of the Initial Password ([Passwords](../concepts/passwords.md#giving-new-connected-system-objects-their-first-password)); requiring a change at the next sign-in does not, because that belongs to the password. See [Accounts your Attribute Flow creates disabled](../connectors/jim-ldap-connector-passwords.md#accounts-your-attribute-flow-creates-disabled).

**An Attribute Flow that enables accounts can stay.** Active Directory refuses to create an enabled account with no password, so a `userAccountControl` Attribute Flow writing `512` on provisioning is refused there unless an Initial Password follows. With one switched on, JIM creates the account disabled (`512` is sent as `514`), the password's delivery enables it, and the confirming import waits for the password rather than reporting the interim value as unconfirmed; the flow goes on enabling and disabling the account from source data afterwards. An account the flow creates disabled (`514`) is sent unchanged and stays disabled when its password lands; if the flow enables it while the password is still queued, retrying or parked (a pre-hire's start date arriving first, say), JIM holds the enable back in the same way and the delivery completes it. How this works, and what happens while a password is retrying, parked or expired, is described under [Enabling new Active Directory accounts](../connectors/jim-ldap-connector-passwords.md#enabling-new-active-directory-accounts).

**No generated password is ever stored**, in JIM's database, its logs, its Activities, its API responses or anywhere else. Each is generated at the moment it is delivered, handed to the Connector, and dropped.

**Nobody receives a generated password, including you.** Its job is to get the Connected System Object into a working state, since most directories will not enable an object or let it be used until it holds a password that meets their rules. When the person actually needs to sign in, set their password then with the [set-password action on the Connected System Object](connected-system-passwords.md#setting-the-password-on-one-connected-system-object) and hand them the value; requiring a change at their next sign-in then does what you would expect. See [Passwords](../concepts/passwords.md#so-how-does-the-person-get-their-password).

## :material-form-textbox-password: One password for every Connected System Object

The third Password Settings option sets one password you choose on every Connected System Object the rule provisions, so you can tell a new starter what it is. **This option is not recommended**, and the portal says so beside it: every Connected System Object the rule provisions shares that password until each person changes it, so anybody who learns of this can sign in as any new starter who has not.

Leave **After the password is set** on *Require a change at the next sign-in*. It is what ends each object's share of the password; any other choice leaves every Connected System Object the rule provisions on it until somebody changes it by hand. The same happens on Active Directory if the rule's `userAccountControl` Attribute Flow sets "Password never expires", because the directory then ignores the change asked for: the delivery's Activity completes with a warning saying so, so keep that flag out of the flow for accounts given this password.

Apart from password changes queued for delivery (see [how long any of it is kept](../concepts/password-synchronisation.md#how-long-any-of-it-is-kept)), this is the only Connected System Object password JIM stores. It is stored encrypted and cannot be shown to you again: the portal fields are blank whenever you open them, no REST response or cmdlet returns it, and Configuration Change History records a keyed hash rather than the value. It is protected at rest exactly as a Connected System's credentials are. Leaving those fields blank keeps the stored password, so changing another setting is safe.

What JIM will tell you is that a password is set and when it last changed, on the panel and through `Get-JIMSyncRuleInitialPassword` (`staticPasswordSet` and `staticPasswordSetAt`). Change it whenever somebody who knew it leaves; that date is the only thing that can date a shared password:

```powershell
$password = Read-Host -AsSecureString "New shared initial password"
Set-JIMSyncRuleInitialPassword -Id 5 -StaticPassword $password -ChangeReason "Rotated after a leaver (CHG0043)"
```

A password the Connected System would refuse is rejected when you set it, rather than parking every Connected System Object the rule provisions. A rule set to this option with no password stored is refused too, for the same reason.

## :material-history: What happens after provisioning

Setting the password is a separate concern from creating the Connected System Object, and deliberately cannot fail the export that created it. The Connected System Object exists; reporting its export as failed would have JIM retry the create.

Instead, the moment the export gives the new Connected System Object its external id, JIM queues a password change for it and the [Password Delivery Service](../concepts/password-synchronisation.md#the-password-delivery-service) takes it from there, typically within a second or two while the export run is still going. An unreachable or refused Connected System Object is retried on the Connected System's own [Password Synchronisation](../concepts/password-synchronisation.md) schedule, or JIM's default (five attempts, backing off from five minutes) where the system has none configured, capped by the time to live below, rather than waiting for another export run.

Each Connected System Object ends up in one of these states, each recorded as a child Activity of the one written when the password was queued:

| State | Meaning | What clears it |
|-------|---------|----------------|
| Delivered | The password was set. | Nothing; the Connected System Object no longer owes one and JIM keeps no record beyond the Activity. |
| Retrying | Something JIM cannot control got in the way: the directory was unreachable, or the Connected System Object was not visible yet (after a create, usually replication catching up, which a directory with several domain controllers can need an extra attempt for). | The next attempt on the Connected System's own schedule. |
| Parked | The target refused the password itself, or the settings cannot produce one at all, for not satisfying the rules in force for that Connected System Object. Retrying would produce another password refused for the same reason, so JIM stops. | You. See below. |
| Withdrawn | The Connected System Object or the Synchronisation Rule that provisioned it has since been removed, so there is nothing left to deliver a password to. | Nothing; this is not a failure. |
| Expired | A week passed without success. JIM stops trying and records the fact rather than quietly forgetting the Connected System Object. | Nothing automatic; the Connected System Object needs a password set by other means. |

The target's own words are kept verbatim on a parked Connected System Object, because why a directory refuses a password is a property of that directory's policy and the single most useful thing to be shown.

## :material-lock-reset: Clearing parked Connected System Objects

Parking is not a one-way door. **Saving a change to the Synchronisation Rule's initial password settings releases every Connected System Object parked against that rule**, and the Password Delivery Service attempts them again within seconds, with no export run needed. Nothing needs to be regenerated or invalidated in the meantime: a generated password is produced afresh at delivery, and setting a new shared password is itself the change that releases the work.

Saving an unrelated part of the same rule releases nothing. Those Connected System Objects were refused on settings the target has already given its answer on, so retrying them unchanged would fail identically and inflate an attempt count that is supposed to mean "distinct configurations tried".

The typical loop is therefore: read what the target said on the parked Connected System Object, correct the generator settings (most often length or the character classes), and save; delivery follows within seconds.

## :material-bell-outline: Where JIM tells you

You do not have to go looking. Parked and expired Connected System Objects are reported in two places:

- **The rule's Passwords tab itself**<br /> The tab carries the parked count as a badge, so you see it without opening the tab, and the tab shows the Connected System Objects grouped by what the target said, biggest group first, with the target's own words unaltered and how long each fault has been there. Correct the settings and it confirms, before you save, how many Connected System Objects saving will release; it stays quiet for an edit that would not change what is delivered.
- **Automation**<br /> [`Get-JIMSyncRuleInitialPassword`](../powershell/initial-password.md#get-jimsyncruleinitialpassword) and the Synchronisation Rule's initial password endpoint report `parkedAccountCount`, `expiredAccountCount` and the same grouped reasons.

An initial password also shows up wherever JIM shows any other password change: on **Operations > Passwords**, labelled with origin **Initial**, and on the identity's own Password panel, as an **Initial** entry with a child Activity naming the system it was set on. Where a password change already waiting for the person was delivered as the first password instead, the Password panel shows the export's own words as an **Initial** note, and the change itself is marked **First password** until it is delivered.
