# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    A stand-in for Exchange's remote PowerShell commands, for the remoting harness (#2021).

.DESCRIPTION
    The stand-in WinRM server opens a session on the Microsoft.Exchange configuration with one of the session files
    beside this module, which import it and make some or all of these commands visible, as Exchange's role-based access
    control does. A command the account's roles do not grant is hidden, not refused, which is what JIM's settings check
    reads.

    The commands JIM manages recipients with only exist here so far, so a session can see them; they gain behaviour
    as JIM's Exchange session is built.
#>

function Get-ExchangeServer {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Identity)

    [pscustomobject]@{
        Name                = ($Identity -split '\.')[0]
        Fqdn                = $Identity
        AdminDisplayVersion = 'Version 15.2 (Build 1544.4)'
    }
}

function Get-Recipient { [CmdletBinding()] param([Parameter(Position = 0)][string]$Identity) }
function Get-User { [CmdletBinding()] param([Parameter(Position = 0)][string]$Identity) }
function Get-Contact { [CmdletBinding()] param([Parameter(Position = 0)][string]$Identity) }
function Get-Group { [CmdletBinding()] param([Parameter(Position = 0)][string]$Identity) }
function Enable-Mailbox { [CmdletBinding()] param([Parameter(Position = 0)][string]$Identity) }
function Set-Mailbox { [CmdletBinding()] param([Parameter(Position = 0)][string]$Identity) }
function Enable-MailUser { [CmdletBinding()] param([Parameter(Position = 0)][string]$Identity) }
function Set-MailUser { [CmdletBinding()] param([Parameter(Position = 0)][string]$Identity) }
function Enable-MailContact { [CmdletBinding()] param([Parameter(Position = 0)][string]$Identity) }
function Set-MailContact { [CmdletBinding()] param([Parameter(Position = 0)][string]$Identity) }
function Enable-DistributionGroup { [CmdletBinding()] param([Parameter(Position = 0)][string]$Identity) }
function Set-DistributionGroup { [CmdletBinding()] param([Parameter(Position = 0)][string]$Identity) }

Export-ModuleMember -Function *
