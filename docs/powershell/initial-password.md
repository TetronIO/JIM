---
title: Initial Password
description: "The cmdlets that read and change whether JIM sets an initial password on the Connected System Objects a Synchronisation Rule provisions."
type: reference
audience: automation-author
---

# Initial Password

These cmdlets read and change whether JIM sets an initial password on the Connected System Objects an export Synchronisation Rule provisions, how it generates one, and which Connected System Objects are waiting on a person because a target refused the password. No surface returns a password. What each setting does, and the rule's **Initial Password** tab, are covered in [Initial Password](../configuration/initial-password.md).

---

## Get-JIMSyncRuleInitialPassword

Gets whether JIM sets an initial password on the Connected System Objects a Synchronisation Rule provisions, how it
generates one, and which Connected System Objects are waiting on a person.

No password value is ever returned. A generated password is produced at the moment it is set and stored nowhere;
where the rule uses one password for every Connected System Object, that password is stored encrypted and is write-only, so all
that comes back is that one is set and when it last changed.

<!-- BEGIN GENERATED: Get-JIMSyncRuleInitialPassword -->
### Syntax

```powershell
# ById (default)
Get-JIMSyncRuleInitialPassword -Id <int>

# ByInputObject
Get-JIMSyncRuleInitialPassword -InputObject <PSCustomObject>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Synchronisation Rule from `Get-JIMSyncRule`, through the pipeline. Accepts pipeline input. |

### Output

The rule's initial password settings, and the Connected System Objects waiting on them.

| Property | Type | Description |
|----------|------|-------------|
| `Enabled` | `bool` | Whether JIM sets an initial password on the Connected System Objects this Synchronisation Rule provisions. |
| `Source` | `string` | Where the password comes from: Discovered generates one from the Connected System's discovered policy, Custom from the settings saved on this rule, and Static sets one stored password on every Connected System Object. One of `Discovered`, `Custom` or `Static`. |
| `CustomPolicy` | `object` | The generator settings used when Source is Custom. Present whatever the source, since they are what would be used if it were switched. It has `Style`, `Length`, `MinimumUppercase`, `MinimumLowercase`, `MinimumDigits`, `MinimumSymbols`, `PermittedSymbols`, `WordCount`, `WordSeparator`, `WordCapitalisation`, `AppendedDigitCount`, `AppendSymbol` and `ExcludeAmbiguousCharacters`. |
| `ExpiryBehaviour` | `string` | What happens to the password once it is set. On Active Directory and Samba AD, "Password never expires" follows this only where no export has written the account's userAccountControl by the time the password is delivered; where one has, the Attribute Flow decides it. Requiring a change at the next sign-in always applies. One of `RequireChangeAtNextSignIn`, `ExpiresAccordingToTargetPolicy` or `NeverExpires`. |
| `EnableAccount` | `bool` | Whether the Connected System Object is enabled once the password is set. It applies only where no export has written the object's enabled state (such as userAccountControl) by the time the password is delivered; where one has, the Attribute Flow decides. False leaves it as it was created. It also applies, as does whether the password never expires, when a password change already waiting for the person is delivered as the object's first password instead of the Initial Password. |
| `StaticPasswordSet` | `bool` | Whether one password is stored for every Connected System Object this rule provisions. The password itself is never returned. |
| `StaticPasswordSetAt` | `datetime` | When the stored password was last changed (UTC); empty when none is set. |
| `ParkedReasons` | `object[]` | The Connected System Objects waiting on a change to these settings, grouped by what the target said when it refused the password, the biggest group first. Empty in the ordinary case. Each has `TargetMessage`, `FailureReason`, `AccountCount` and `FirstSeenAt`. |
| `ParkedAccountCount` | `int` | How many Connected System Objects are waiting on a change to these settings: the total across ParkedReasons. |
| `ExpiredAccountCount` | `int` | How many Connected System Objects this rule provisioned were never given an initial password within its time to live. Correcting these settings does nothing for them; they need a password by other means. |
<!-- END GENERATED: Get-JIMSyncRuleInitialPassword -->

### Examples

```powershell title="See what a target objected to"
(Get-JIMSyncRuleInitialPassword -Id 5).ParkedReasons |
    Format-Table AccountCount, TargetMessage -AutoSize
```

```powershell title="Find every rule with initial password work waiting"
Get-JIMSyncRule | ForEach-Object {
    $p = Get-JIMSyncRuleInitialPassword -Id $_.Id
    if ($p.ParkedAccountCount -or $p.ExpiredAccountCount) {
        [PSCustomObject]@{ Rule = $_.Name; Parked = $p.ParkedAccountCount; Expired = $p.ExpiredAccountCount }
    }
}
```

```powershell title="Find shared initial passwords nobody has changed for 90 days"
Get-JIMSyncRule | ForEach-Object {
    $p = Get-JIMSyncRuleInitialPassword -Id $_.Id
    if ($p.StaticPasswordSet -and $p.StaticPasswordSetAt -lt (Get-Date).AddDays(-90)) {
        [PSCustomObject]@{ Rule = $_.Name; LastChanged = $p.StaticPasswordSetAt }
    }
}
```

## Set-JIMSyncRuleInitialPassword

Changes the settings `Get-JIMSyncRuleInitialPassword` reports. Saving a change that alters what would be delivered releases every Connected System Object
parked against the rule, and the Password Delivery Service attempts them again within seconds, with no export run
needed; saving a change that would deliver the same password in the same way releases nothing.

Only what you supply changes, with one exception: the generator settings travel as a set, so supplying any one of
them sends the whole policy.

<!-- BEGIN GENERATED: Set-JIMSyncRuleInitialPassword -->
### Syntax

```powershell
# ById (default)
Set-JIMSyncRuleInitialPassword -Id <int> [-Enable] [-Disable] [-Source <string>] [-StaticPassword <SecureString>] [-Style <string>] [-Length <int>] [-MinimumUppercase <int>] [-MinimumLowercase <int>] [-MinimumDigits <int>] [-MinimumSymbols <int>] [-PermittedSymbols <string>] [-WordCount <int>] [-WordSeparator <string>] [-WordCapitalisation <string>] [-AppendedDigitCount <int>] [-AppendSymbol <bool>] [-ExcludeAmbiguousCharacters <bool>] [-ExpiryBehaviour <string>] [-EnableAccount <bool>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Set-JIMSyncRuleInitialPassword -InputObject <PSCustomObject> [-Enable] [-Disable] [-Source <string>] [-StaticPassword <SecureString>] [-Style <string>] [-Length <int>] [-MinimumUppercase <int>] [-MinimumLowercase <int>] [-MinimumDigits <int>] [-MinimumSymbols <int>] [-PermittedSymbols <string>] [-WordCount <int>] [-WordSeparator <string>] [-WordCapitalisation <string>] [-AppendedDigitCount <int>] [-AppendSymbol <bool>] [-ExcludeAmbiguousCharacters <bool>] [-ExpiryBehaviour <string>] [-EnableAccount <bool>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Synchronisation Rule from `Get-JIMSyncRule`, through the pipeline. Accepts pipeline input. |
| `Enable` | `switch` | No |  | Sets an initial password on the accounts this Synchronisation Rule provisions. |
| `Disable` | `switch` | No |  | Stops setting an initial password. The settings are kept, so re-enabling restores them. |
| `Source` | `string` | No |  | Where the password comes from. Discovered generates one per Connected System Object from the password policy JIM discovered on the Connected System, re-deriving the settings whenever the policy is read again. Custom generates one per Connected System Object from the settings saved on this rule. Static sets the one password given with `-StaticPassword` on every Connected System Object; it is not recommended. One of `Discovered`, `Custom` or `Static`. |
| `StaticPassword` | `SecureString` | No |  | The one password to set on every Connected System Object this Synchronisation Rule provisions, used when `-Source` is Static. Give it as a SecureString, so it does not sit in the session's command history in clear text. Write-only: JIM encrypts it and never returns it. Omit it to leave the stored password as it is, which is what makes changing another setting safe. A password the Connected System would refuse is rejected here, rather than parking every Connected System Object the rule provisions. |
| `Style` | `string` | No |  | How the password is composed. One of `RandomCharacters`, `Words` or `Pronounceable`. |
| `Length` | `int` | No |  | How many characters to produce. Ignored by the Words style, whose length follows from the words drawn. From 1 to 256. |
| `MinimumUppercase` | `int` | No |  | The fewest uppercase letters a generated password must contain (RandomCharacters style). From 0 to 64. |
| `MinimumLowercase` | `int` | No |  | The fewest lowercase letters a generated password must contain (RandomCharacters style). From 0 to 64. |
| `MinimumDigits` | `int` | No |  | The fewest digits a generated password must contain (RandomCharacters style). From 0 to 64. |
| `MinimumSymbols` | `int` | No |  | The fewest symbols a generated password must contain (RandomCharacters style). From 0 to 64. |
| `PermittedSymbols` | `string` | No |  | The symbols JIM may use. Narrow this where something downstream cannot cope with a given character. |
| `WordCount` | `int` | No |  | How many words to draw (Words style). From 1 to 16. |
| `WordSeparator` | `string` | No |  | What goes between the words (Words style). One of `None`, `Hyphen`, `FullStop`, `Underscore`, `Digit` or `RandomSymbol`. |
| `WordCapitalisation` | `string` | No |  | How the words are capitalised (Words style). One of `Lowercase`, `EachWord`, `Uppercase`, `FirstWordOnly` or `RandomWord`. |
| `AppendedDigitCount` | `int` | No |  | How many digits to append (Words and Pronounceable styles). Usually how a passphrase reaches the three character categories a stock Active Directory domain requires. From 0 to 16. |
| `AppendSymbol` | `bool` | No |  | Whether to append one symbol (Words and Pronounceable styles). |
| `ExcludeAmbiguousCharacters` | `bool` | No |  | Whether to leave out characters that are easily confused when a password is read out or copied by hand. |
| `ExpiryBehaviour` | `string` | No |  | What happens to the password once it is set. A Connector that cannot honour the choice records what it applied instead, for each Connected System Object. On Active Directory and Samba AD, Password never expires follows this only where no export has written the account's userAccountControl by the time the password is delivered; where one has, the Attribute Flow decides it. Requiring a change at the next sign-in always applies. One of `RequireChangeAtNextSignIn`, `ExpiresAccordingToTargetPolicy` or `NeverExpires`. |
| `EnableAccount` | `bool` | No |  | Whether the Connected System Object is enabled once the password is set. It applies only where no export has written the object's enabled state (such as userAccountControl) by the time the password is delivered; where one has, the Attribute Flow decides. `$false` leaves it as it was created. It also applies, with whether the password never expires, when a password change already waiting for the person is delivered as the object's first password instead of the Initial Password. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded against this Synchronisation Rule's change history. |
| `PassThru` | `switch` | No |  | Returns the settings as changed. |

### Output

With `-PassThru`, the settings as changed; otherwise nothing.

Properties as under [Get-JIMSyncRuleInitialPassword](#get-jimsyncruleinitialpassword).
<!-- END GENERATED: Set-JIMSyncRuleInitialPassword -->

### Examples

```powershell title="Set a generated password the person must change at first sign-in"
Set-JIMSyncRuleInitialPassword -Id 5 -Enable -Source Discovered -ExpiryBehaviour RequireChangeAtNextSignIn
```

```powershell title="Generate passphrases of four words instead"
Set-JIMSyncRuleInitialPassword -Id 5 -Source Custom -Style Words -WordCount 4 -WordSeparator Hyphen -AppendedDigitCount 2
```

### One password for every Connected System Object

`-Source Static` with `-StaticPassword` sets one password you choose on every Connected System Object the rule
provisions, so you can tell a new starter what it is. **This option is not recommended**: every Connected System
Object the rule provisions shares that password until each person changes it. See
[Passwords](../concepts/passwords.md#one-password-for-every-connected-system-object) before using it.

`-StaticPassword` takes a `SecureString`, so the password does not sit in your session's command history in clear
text. It is write-only: JIM encrypts it and never returns it. Omit it to leave the stored password as it is, which
is what makes changing another setting safe.

```powershell title="Set one password for every Connected System Object this rule provisions"
$password = Read-Host -AsSecureString "Initial password for every new Connected System Object"
Set-JIMSyncRuleInitialPassword -Id 5 -Enable -Source Static -StaticPassword $password
```

```powershell title="Rotate the shared password after a leaver"
$password = Read-Host -AsSecureString "New shared initial password"
Set-JIMSyncRuleInitialPassword -Id 5 -StaticPassword $password -ChangeReason "Rotated after a leaver (CHG0043)"
```

---

## See also

- [Initial Password](../configuration/initial-password.md): what the settings do, and following a Connected System Object to its first sign-in
- [Passwords](../concepts/passwords.md): how JIM handles passwords, and the rules that hold on every surface
- [Synchronisation Rules](synchronisation-rules.md): the export Synchronisation Rule the settings belong to
- [Password Synchronisation](password-synchronisation.md): set a Metaverse Object's password, and follow the changes queued for delivery
