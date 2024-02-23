# designed to run on Windows desktops, i.e. 10, 11, etc.
# server requires the use of different cmdlets.
Add-WindowsCapability -Online -Name 'ADLDS'
Add-WindowsCapability -Online -Name 'RSAT-AD-PowerShell'
Add-WindowsCapability -Online -Name 'RSAT-ADDS-Tools'
Add-WindowsCapability -Online -Name 'RSAT-ADLDS'

# now run the AD LDS configuration wizard to create the directory
# then run the Create-Service-Account.ps1 script to create the account JIM will use.