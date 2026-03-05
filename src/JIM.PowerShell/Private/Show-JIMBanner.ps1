function Show-JIMBanner {
    <#
    .SYNOPSIS
        Displays the JIM ASCII art banner after successful connection.
    .DESCRIPTION
        Shows a branded banner with the JIM logo, server version, and connection URL.
        Used internally by Connect-JIM after successful authentication.
    #>
    [CmdletBinding()]
    param(
        [Parameter()]
        [string]$ServerVersion,

        [Parameter()]
        [string]$Url
    )

    $cyan = [System.ConsoleColor]::Cyan
    $green = [System.ConsoleColor]::Green
    $gray = [System.ConsoleColor]::DarkGray

    Write-Host ""
    Write-Host "     ██╗██╗███╗   ███╗" -ForegroundColor $cyan
    Write-Host "     ██║██║████╗ ████║" -ForegroundColor $cyan
    Write-Host "     ██║██║██╔████╔██║" -ForegroundColor $cyan
    Write-Host "██   ██║██║██║╚██╔╝██║" -ForegroundColor $cyan
    Write-Host "╚█████╔╝██║██║ ╚═╝ ██║" -ForegroundColor $cyan
    Write-Host " ╚════╝ ╚═╝╚═╝     ╚═╝" -ForegroundColor $cyan
    Write-Host "Junctional Identity Manager" -ForegroundColor $gray
    Write-Host ""

    if ($ServerVersion -and $Url) {
        Write-Host "Connected to JIM server v$ServerVersion at $Url" -ForegroundColor $green
    }
    elseif ($Url) {
        Write-Host "Connected to JIM at $Url" -ForegroundColor $green
    }
    else {
        Write-Host "Successfully connected to JIM!" -ForegroundColor $green
    }
    Write-Host ""
}
