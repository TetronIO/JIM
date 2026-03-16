function Test-JIMAuthorisation {
    <#
    .SYNOPSIS
        Internal function to verify the authenticated user is authorised to use JIM.

    .DESCRIPTION
        Calls the /api/v1/userinfo endpoint to check whether the authenticated user
        has a JIM identity (MetaverseObject) and appropriate roles. Displays a warning
        if the user is authenticated but not authorised.

        This is called during Connect-JIM after successful authentication to provide
        early feedback rather than letting the user discover 403 errors later.

    .OUTPUTS
        Returns the userinfo response object, or $null if the check could not be performed.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param()

    try {
        $userInfo = Invoke-JIMApi -Endpoint '/api/v1/userinfo'

        if ($userInfo -and $userInfo.authorised -eq $false) {
            Write-Host ""
            Write-Host "WARNING: You are authenticated but not authorised to use JIM." -ForegroundColor Yellow
            Write-Host ""
            Write-Host "Your identity has not been provisioned in JIM yet." -ForegroundColor Yellow
            Write-Host "Please sign in to the JIM web portal first to create your identity," -ForegroundColor Yellow
            Write-Host "then reconnect with Connect-JIM." -ForegroundColor Yellow
            Write-Host ""
        }

        return $userInfo
    }
    catch {
        Write-Verbose "Could not verify user authorisation: $_"
        return $null
    }
}
