# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    A stand-in for Exchange's remote PowerShell commands, for the remoting harness (#2021).

.DESCRIPTION
    The stand-in WinRM server opens a session on the Microsoft.Exchange configuration with one of the session files
    beside this module, which import it and make some or all of these commands visible, as Exchange's role-based access
    control does. A command the account's roles do not grant is hidden, not refused, which is what JIM reads.

    The recipient commands behave as Exchange's do, closely enough for JIM's convergence to be tested end to end:
    - Get-User, Get-Contact and Get-Group find any object, as a plain User, Contact or group until it is enabled;
    - Enable-* makes it a recipient, and refuses one that already is;
    - Set-* changes an existing recipient's alias and addresses;
    - Get-Recipient reads a recipient's alias and addresses.

    Recipients are kept in a file every shell in the container shares, keyed by identity, so a change one session makes
    is what the next session reads. Tests use identities of their own, so they never meet another test's recipients.

    Two identities stand for failures: any identity containing "missing" is not found, as an object Exchange cannot see
    yet is not; and a mailbox database named DB99 does not exist.

    Never run against Exchange; the harness only.
#>

$script:StatePath = '/tmp/fake-exchange/recipients.json'

function Read-FakeState {
    if (-not (Test-Path $script:StatePath)) { return @{} }
    $text = Get-Content -Raw -Path $script:StatePath
    if ([string]::IsNullOrWhiteSpace($text)) { return @{} }
    return ($text | ConvertFrom-Json -AsHashtable)
}

function Write-FakeState([hashtable]$State) {
    $directory = Split-Path -Parent $script:StatePath
    if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $State | ConvertTo-Json -Depth 4 | Set-Content -Path $script:StatePath
}

function Get-FakeKey([string]$Identity) { return $Identity.Trim().ToLowerInvariant() }

function Test-FakeFound([string]$Identity, [string]$Command, [string]$DomainController) {
    if ($Identity -notmatch 'missing') { return $true }
    $server = if ($DomainController) { $DomainController } else { 'dc1.harness.test' }
    Write-Error -Message "The operation couldn't be performed because object '$Identity' couldn't be found on '$server'." `
        -ErrorId "[Server=FAKE-EX1][FailureCategory=Cmdlet-ManagementObjectNotFoundException] 4E7F0A21,$Command" `
        -Category ObjectNotFound -TargetObject $Identity
    return $false
}

function Write-FakeRefusal([string]$Message, [string]$Category, [string]$Command, [string]$Identity) {
    Write-Error -Message $Message -ErrorId "[Server=FAKE-EX1][FailureCategory=Cmdlet-$Category] 9B2C4D10,$Command" `
        -Category InvalidOperation -TargetObject $Identity
}

function Get-FakeAlias([string]$Identity) {
    $plain = ($Identity -replace '^CN=([^,]+),.*$', '$1') -replace '[^A-Za-z0-9]', ''
    if ($plain.Length -gt 12) { $plain = $plain.Substring(0, 12) }
    return "r$plain".ToLowerInvariant()
}

function New-FakeObject([string]$Identity, [string]$Details) {
    $state = Read-FakeState
    $recipient = $state[(Get-FakeKey $Identity)]
    [pscustomobject]@{
        Identity             = $Identity
        RecipientTypeDetails = if ($recipient) { $recipient.RecipientTypeDetails } else { $Details }
    }
}

function Enable-FakeRecipient([string]$Identity, [string]$Command, [string]$Details, [string]$Alias, [string]$ExternalEmailAddress,
    [string]$DomainController, [string]$Database) {
    if (-not (Test-FakeFound $Identity $Command $DomainController)) { return }
    $state = Read-FakeState
    $key = Get-FakeKey $Identity
    if ($state.ContainsKey($key)) {
        Write-FakeRefusal "The object '$Identity' is already mail-enabled." 'RecipientTaskException' $Command $Identity
        return
    }
    if ($Database -eq 'DB99') {
        Write-FakeRefusal "Couldn't find database `"$Database`". Make sure you have typed it correctly." 'ManagementObjectNotFoundException' $Command $Identity
        return
    }
    $resolvedAlias = if ($Alias) { $Alias } else { Get-FakeAlias $Identity }
    $state[$key] = @{
        RecipientTypeDetails = $Details
        Alias                = $resolvedAlias
        PrimarySmtpAddress   = "$resolvedAlias@harness.test"
        ExternalEmailAddress = if ($ExternalEmailAddress) { "SMTP:$ExternalEmailAddress" } else { $null }
        Database             = $Database
        DomainController     = $DomainController
    }
    Write-FakeState $state
}

function Set-FakeRecipient([string]$Identity, [string]$Command, [string[]]$Kinds, [string]$Alias, [string]$PrimarySmtpAddress,
    [string]$ExternalEmailAddress, [string]$DomainController) {
    if (-not (Test-FakeFound $Identity $Command $DomainController)) { return }
    $state = Read-FakeState
    $key = Get-FakeKey $Identity
    if (-not $state.ContainsKey($key) -or $state[$key].RecipientTypeDetails -notin $Kinds) {
        Write-Error -Message "The operation couldn't be performed because object '$Identity' couldn't be found on '$DomainController'." `
            -ErrorId "[Server=FAKE-EX1][FailureCategory=Cmdlet-ManagementObjectNotFoundException] 4E7F0A22,$Command" -Category ObjectNotFound -TargetObject $Identity
        return
    }
    if ($Alias) { $state[$key].Alias = $Alias }
    if ($PrimarySmtpAddress) { $state[$key].PrimarySmtpAddress = $PrimarySmtpAddress }
    if ($ExternalEmailAddress) { $state[$key].ExternalEmailAddress = "SMTP:$ExternalEmailAddress" }
    $state[$key].DomainController = $DomainController
    Write-FakeState $state
}

$script:MailboxKinds = @('UserMailbox', 'SharedMailbox', 'RoomMailbox', 'EquipmentMailbox')

function Get-ExchangeServer {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity)

    [pscustomobject]@{
        Name                = ($Identity -split '\.')[0]
        Fqdn                = $Identity
        AdminDisplayVersion = 'Version 15.2 (Build 1544.4)'
    }
}

function Get-User {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity, [string]$DomainController)
    if (Test-FakeFound $Identity 'Get-User' $DomainController) { New-FakeObject $Identity 'User' }
}

function Get-Contact {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity, [string]$DomainController)
    if (Test-FakeFound $Identity 'Get-Contact' $DomainController) { New-FakeObject $Identity 'Contact' }
}

function Get-Group {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity, [string]$DomainController)
    if (Test-FakeFound $Identity 'Get-Group' $DomainController) { New-FakeObject $Identity 'UniversalDistributionGroup' }
}

function Get-Recipient {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity, [string]$DomainController)
    if (-not (Test-FakeFound $Identity 'Get-Recipient' $DomainController)) { return }
    $recipient = (Read-FakeState)[(Get-FakeKey $Identity)]
    if (-not $recipient) {
        Write-Error -Message "The operation couldn't be performed because object '$Identity' couldn't be found on '$DomainController'." `
            -ErrorId "[Server=FAKE-EX1][FailureCategory=Cmdlet-ManagementObjectNotFoundException] 4E7F0A23,Get-Recipient" -Category ObjectNotFound -TargetObject $Identity
        return
    }
    [pscustomobject]@{
        Identity             = $Identity
        RecipientTypeDetails = $recipient.RecipientTypeDetails
        Alias                = $recipient.Alias
        PrimarySmtpAddress   = $recipient.PrimarySmtpAddress
        ExternalEmailAddress = $recipient.ExternalEmailAddress
        OriginatingServer    = $recipient.DomainController
    }
}

function Enable-Mailbox {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity, [switch]$Shared, [switch]$Room, [switch]$Equipment, [string]$Database, [string]$Alias,
        [string]$DomainController)
    $details = if ($Shared) { 'SharedMailbox' } elseif ($Room) { 'RoomMailbox' } elseif ($Equipment) { 'EquipmentMailbox' } else { 'UserMailbox' }
    Enable-FakeRecipient -Identity $Identity -Command 'Enable-Mailbox' -Details $details -Alias $Alias -DomainController $DomainController -Database $Database
}

function Set-Mailbox {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity, [string]$Alias, [string]$PrimarySmtpAddress, [bool]$EmailAddressPolicyEnabled, [string]$DomainController)
    Set-FakeRecipient -Identity $Identity -Command 'Set-Mailbox' -Kinds $script:MailboxKinds -Alias $Alias -PrimarySmtpAddress $PrimarySmtpAddress -DomainController $DomainController
}

function Enable-MailUser {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity, [string]$ExternalEmailAddress, [string]$Alias, [string]$DomainController)
    Enable-FakeRecipient -Identity $Identity -Command 'Enable-MailUser' -Details 'MailUser' -Alias $Alias -ExternalEmailAddress $ExternalEmailAddress -DomainController $DomainController
}

function Set-MailUser {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity, [string]$Alias, [string]$PrimarySmtpAddress, [bool]$EmailAddressPolicyEnabled, [string]$ExternalEmailAddress,
        [string]$DomainController)
    Set-FakeRecipient -Identity $Identity -Command 'Set-MailUser' -Kinds @('MailUser') -Alias $Alias -PrimarySmtpAddress $PrimarySmtpAddress `
        -ExternalEmailAddress $ExternalEmailAddress -DomainController $DomainController
}

function Enable-MailContact {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity, [string]$ExternalEmailAddress, [string]$Alias, [string]$DomainController)
    Enable-FakeRecipient -Identity $Identity -Command 'Enable-MailContact' -Details 'MailContact' -Alias $Alias -ExternalEmailAddress $ExternalEmailAddress -DomainController $DomainController
}

function Set-MailContact {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity, [string]$Alias, [string]$PrimarySmtpAddress, [bool]$EmailAddressPolicyEnabled, [string]$ExternalEmailAddress,
        [string]$DomainController)
    Set-FakeRecipient -Identity $Identity -Command 'Set-MailContact' -Kinds @('MailContact') -Alias $Alias -PrimarySmtpAddress $PrimarySmtpAddress `
        -ExternalEmailAddress $ExternalEmailAddress -DomainController $DomainController
}

function Enable-DistributionGroup {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity, [string]$Alias, [string]$DomainController)
    Enable-FakeRecipient -Identity $Identity -Command 'Enable-DistributionGroup' -Details 'MailUniversalDistributionGroup' -Alias $Alias -DomainController $DomainController
}

function Set-DistributionGroup {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity, [string]$Alias, [string]$PrimarySmtpAddress, [bool]$EmailAddressPolicyEnabled, [string]$DomainController)
    Set-FakeRecipient -Identity $Identity -Command 'Set-DistributionGroup' -Kinds @('MailUniversalDistributionGroup') -Alias $Alias `
        -PrimarySmtpAddress $PrimarySmtpAddress -DomainController $DomainController
}

Export-ModuleMember -Function Get-ExchangeServer, Get-Recipient, Get-User, Get-Contact, Get-Group, Enable-Mailbox, Set-Mailbox, Enable-MailUser,
    Set-MailUser, Enable-MailContact, Set-MailContact, Enable-DistributionGroup, Set-DistributionGroup
