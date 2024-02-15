$root_dn = 'CN=RD,DC=Borton,DC=Com'
[ADSI]$root = "LDAP://localhost/$root_dn"

# looking for the following container structure:
# root/Users/ServiceAccounts
# root/Users/Staff
# root/Groups/DistributionGroups
# root/Groups/SecurityGroups

# users
[ADSI]$users_container = "LDAP://localhost/CN=Users,$root_dn"
if ($null -eq $users_container.Guid) 
{
    $users_container = $root.Create('container', 'CN=Users')
    $users_container.setInfo()
    Write-Host "Created Users container"

    $service_accounts_container = $users_container.Create("container", "CN=ServiceAccounts")
    $service_accounts_container.setInfo()
    Write-Host "Created ServiceAccounts container"

    $staff_container = $users_container.Create("container", "CN=Staff")
    $staff_container.setInfo()
    Write-Host "Created Staff container"
} 
else 
{
    Write-Host "Users container structure already exists"
    [ADSI]$service_accounts_container = "LDAP://localhost/CN=ServiceAccounts,CN=Users,$root_dn"
}

# groups
[ADSI]$groups_container = "LDAP://localhost/CN=Groups,$root_dn"
if ($null -eq $groups_container.Guid)
{
    $groups_container = $root.Create('container', 'CN=Groups')
    $groups_container.setInfo()
    Write-Host "Created Groups container"

    $distribution_lists_container = $users_container.Create("container", "CN=DistributionLists")
    $distribution_lists_container.setInfo()
    Write-Host "Created DistributionLists container"

    $security_groups_container = $users_container.Create("container", "CN=SecurityGroups")
    $security_groups_container.setInfo()
    Write-Host "Created SecurityGroups container"
}
else 
{
    Write-Host "Groups container structure already exists"
}

$service_account_username = "svc-jim-adlds"
[ADSI]$service_account = "LDAP://localhost/CN=$service_account_username,CN=ServiceAccounts,CN=Users,$root_dn"
if ($null -eq $service_account.Guid) 
{
    # create the service account JIM will use to interact with the directory
    $service_account = $service_accounts_container.Create("inetOrgPerson","CN=$service_account_username")
    $service_account.put("DisplayName","JIM ADLDS Service Account")
    $service_account.put("Description", "JIM uses this account to manage user and group objects in this directory.")
    $service_account.setinfo()
    Write-Host "Created JIM service account ($service_account_username)"
} 
else 
{
    Write-Host "Service account ($service_account_username) already exists"
}

# add the service account to the Administrators role
[ADSI]$admin_role = "LDAP://localhost/CN=Administrators,CN=Roles,$root_dn"
if ($admin_role.member.Contains($service_account.distinguishedName))
{
    Write-Host "Service account is already a member of the Administrators role"
}
else 
{
    $admin_role.Add($service_account.ADSPath)
    $admin_role.setInfo()
    Write-Host "Added '$($service_account.distinguishedName)' to admin role"
}