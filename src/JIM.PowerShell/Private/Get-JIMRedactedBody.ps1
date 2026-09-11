# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMRedactedBody {
    <#
    .SYNOPSIS
        Renders a request body for the debug stream with every credential-shaped value replaced.

    .DESCRIPTION
        Invoke-JIMApi writes the outgoing body to Write-Debug so an operator can see what a cmdlet
        sent. Several cmdlets send a password: Set-JIMMetaverseObjectPassword,
        Set-JIMConnectedSystemObjectPassword and Set-JIMSyncRuleInitialPassword each take one as a
        SecureString, precisely to keep it out of
        the session history, and then have to unwrap it to put it on the wire. Logging the body
        undid that: the value the SecureString protected went to the debug stream in clear text, and
        into any transcript running at the time.

        JIM's never-log invariant is that no password value reaches any log, at any level. The
        server side has always honoured it; this is the client side of the same rule.

        Redaction is by property name, applied recursively, and is deliberately conservative:

        - Names matching the credential heuristic below are replaced wholesale. The list mirrors the
          spirit of CredentialAttributes.HasCredentialLikeName on the server: broad, and erring
          towards redacting something harmless rather than missing something that matters.
        - Every stringValue is replaced, whatever it holds. Connected System setting values are the
          case that settles this: a service account's password travels as a stringValue keyed by
          setting identifier, exactly as a hostname or a base DN does, so the payload gives no way
          to tell them apart. Losing a base DN from a debug line costs less than leaking the
          credential beside it, and the setting identifiers and non-string values still show, which
          is most of what the line is read for.
        - The replacement is a fixed marker, never derived from the value, so nothing about the
          original (its length above all) survives.

        A body that arrives already serialised is parsed and redacted the same way. One that cannot
        be parsed is suppressed entirely rather than logged raw: an unparseable body cannot be
        inspected, so it cannot be shown to be free of secrets.

    .PARAMETER Body
        The request body, as a hashtable, PSCustomObject, array or already-serialised JSON string.

    .OUTPUTS
        A JSON string safe to write to the debug stream.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Body
    )

    if ($null -eq $Body) {
        return '<no body>'
    }

    if ($Body -is [string]) {
        try {
            $parsed = $Body | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            return '<body suppressed: not JSON, so it cannot be shown to hold no secret>'
        }

        return (Get-JIMRedactedValue -Value $parsed | ConvertTo-Json -Depth 10 -Compress)
    }

    return (Get-JIMRedactedValue -Value $Body | ConvertTo-Json -Depth 10 -Compress)
}

function Get-JIMRedactedValue {
    <#
    .SYNOPSIS
        Recursive worker for Get-JIMRedactedBody: returns a copy of a value with credential-shaped
        properties replaced.
    #>
    [CmdletBinding()]
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Value,

        [int]$Depth = 0
    )

    # A body deep enough to hit this is malformed rather than merely nested; ConvertTo-Json stops at
    # the same sort of bound. Returning the marker keeps the guard from being a way to smuggle a
    # value past the redaction by burying it.
    if ($Depth -gt 12) {
        return $script:JIMRedactionMarker
    }

    if ($null -eq $Value) {
        return $null
    }

    # Strings, numbers and booleans have no property names to judge, so the caller decided about
    # them; here they pass through as they are.
    if ($Value -is [string] -or $Value.GetType().IsPrimitive -or $Value -is [datetime] -or $Value -is [guid]) {
        return $Value
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $copy = @{}
        foreach ($key in $Value.Keys) {
            $copy[$key] = if (Test-JIMRedactedName -Name ([string]$key)) {
                $script:JIMRedactionMarker
            }
            else {
                Get-JIMRedactedValue -Value $Value[$key] -Depth ($Depth + 1)
            }
        }
        return $copy
    }

    if ($Value -is [System.Collections.IEnumerable]) {
        # Rebuilt as an array so a single-element collection does not unroll into a bare value and
        # change the shape of what is logged.
        return @($Value | ForEach-Object { Get-JIMRedactedValue -Value $_ -Depth ($Depth + 1) })
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $copy = @{}
        foreach ($property in $Value.PSObject.Properties) {
            $copy[$property.Name] = if (Test-JIMRedactedName -Name $property.Name) {
                $script:JIMRedactionMarker
            }
            else {
                Get-JIMRedactedValue -Value $property.Value -Depth ($Depth + 1)
            }
        }
        return $copy
    }

    # Anything else is a type this function does not understand well enough to prove is safe.
    return $script:JIMRedactionMarker
}

function Test-JIMRedactedName {
    <#
    .SYNOPSIS
        Whether a property of this name carries a value that must never reach a log.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Name
    )

    # Anchored on the whole name rather than searched within it, so a name that merely *mentions*
    # credentials keeps its value: passwordSynchronisationEnabled is a state and
    # passwordExpiryBehaviour is a policy, and both are worth seeing in a debug line. A name that
    # ends in one of these words is the value itself (bindPassword, clientSecret, staticPassword).
    return $Name -match '(?i)^(.*(password|passwd|pwd|secret|credential|passphrase|apikey|token|privatekey)|stringvalue)$'
}
