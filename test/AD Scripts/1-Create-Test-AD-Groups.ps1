Set-StrictMode -Version 2
$ErrorActionPreference = "Stop"
Import-Module ActiveDirectory -ErrorAction Stop
Clear-Host

##[ VARIABLES ]################################################################

# per-run specifics! set these!
$domain = "corp.subatomic.com"
$ou = "OU=Groups,OU=Corp,DC=corp,DC=subatomic,DC=com"
$what_if_mode = $true
$in_scope_groups_to_create = 100
$out_of_scope_groups_to_create = 5
$mixed_scope_groups_to_create = 5

# define the OUs we'll draw users from, for our new groups
$user_ous_in_scope = @(
    "OU=Staff,OU=Corp,DC=corp,DC=subatomic,DC=com"
    "OU=Partners,OU=Corp,DC=corp,DC=subatomic,DC=com"
    "OU=Contractors,OU=Corp,DC=corp,DC=subatomic,DC=com"
)
$user_ous_out_of_scope = @("OU=Interns,OU=Corp,DC=corp,DC=subatomic,DC=com")

# read in the source data
$adjectives = Import-CSV "Adjectives.en.txt"
$colours = Import-CSV "Colours.en.txt"
$group_name_endings = Import-CSV "GroupNameEndings.en.txt"

# create some text reference data
$descriptions = @()
$descriptions += "Curabitur vel dolor orci. Duis consequat nec risus ac accumsan. Vestibulum in odio neque. Sed et sagittis dolor. Ut vel lectus ante. Sed sed molestie sapien. Donec dignissim gravida urna nec feugiat. Vivamus tellus nunc, aliquet et libero sit amet, aliquet feugiat elit."
$descriptions += "Class aptent taciti sociosqu ad litora torquent per conubia nostra, per inceptos himenaeos. Nulla ultricies magna libero, nec placerat tortor convallis quis. Cras tempor cursus nunc, ut gravida sem vestibulum eget. Duis erat odio, dapibus in lacus a, venenatis convallis sapien. Donec congue pretium nisi id dapibus. Vivamus a porttitor dui. Vestibulum sed leo non lacus iaculis rhoncus. Aenean in sodales libero. Vivamus non lorem felis."
$descriptions += "Sed viverra orci vitae elit tincidunt iaculis pellentesque id nunc. Donec accumsan nibh sed vestibulum blandit. Phasellus tincidunt lacinia risus id ultricies. Sed nec enim ipsum. Nulla facilisi. Ut turpis tellus, imperdiet a quam ac, porttitor placerat tortor. Donec lorem quam, aliquam eget facilisis ac, sollicitudin ac mi. Nulla interdum lectus sit amet velit tincidunt faucibus."
$descriptions += "Nam venenatis nisi a posuere ultricies. Nunc feugiat diam eu arcu volutpat rhoncus vel in lorem. Aliquam erat volutpat. Donec." 
$descriptions += "Nam in scelerisque neque. Integer suscipit ex fringilla, fermentum neque sit amet, sodales nunc. Donec sit amet nisl eget turpis finibus tincidunt et sed ipsum. Aenean sit amet ex dictum, convallis est quis, posuere massa. Fusce in eros quis nulla dictum ultricies. Nullam vel erat sit amet neque porttitor egestas."       

##[ CREATE THE GROUP DATA ]#####################################################
#
# Random names
# Some with members all in the same OU, i.e. to test in-scope imports
# Some with members from different OUs, to test out-of-scope imports
$in_scope_groups_created = 0
$mixed_scope_groups_created = 0
$out_of_scope_groups_created = 0
do {    
    # basic group attributes
    $adjective = $adjectives[$(Get-Random -Minimum 0 -Maximum $adjectives.Count)]
    $colour = $colours[$(Get-Random -Minimum 0 -Maximum $colours.Count)]
    $ending = $group_name_endings[$(Get-Random -Minimum 0 -Maximum $group_name_endings.Count)]
    $group_name = "$adjective $colour $ending"
    $group_category = "Security"
    $group_scope = "Global"
    $group_description = $descriptions[$(Get-Random -Minimum 0 -Maximum $descriptions.Count)]

    # is this an in-scope/out-scope/mixed-scope group? i.e. do all members reside within the configured JIM OU scope, not, or a mixture of in and out?
    # 1 = in scope
    # 2 = mixed scope
    # 3 = out of scope
    $group_type = (Get-Random -Minimum 1 -Maximum 3)
    $group_sam_account_name = $null
    if ($group_type -eq 1) {
        $group_sam_account_name = "sg-is-$($in_scope_groups_created + 1)"
        $in_scope_groups_created++
    } elseif ($group_type -eq 2) {
        $group_sam_account_name = "sg-ms-$($mixed_scope_groups_created + 1)"
        $mixed_scope_groups_created++
    } elseif ($group_type -eq 3) {
        $group_sam_account_name = "sg-os-$($out_of_scope_groups_created + 1)"
        $out_of_scope_groups_created++
    }
    
    # create the group first, then we can add members to it next
    if ($what_if_mode) {
        New-ADGroup -Name $group_name -DisplayName $group_name -Description $group_description -GroupCategory $group_category -GroupScope $group_scope -SamAccountName $group_sam_account_name -Path $ou
        Write-Host "Created new AD group: $group_name ($group_sam_account_name)"
    } else {
        Write-Host "Would have created new AD group: $group_name ($group_sam_account_name)"
    }
    
    # get a list of member usernames
    $null -eq $usernames
    $null -eq $usernames_to_get
    if ($group_type -eq 1) {
        # in-scope users, choose from any of the in-scope OUs
        $in_scope_ous_to_use = Get-Random -Minimum 1 -Maximum $user_ous_in_scope.Count
        $usernames_to_get = Get-Random -Minimum 1 -Maximum 1000
        $usernames_to_get_from_each_ou = $usernames_to_get / $in_scope_ous_to_use
        for ($i = 0; $i -lt $in_scope_ous_to_use; $i ++) {
            $ou_index = Get-Random -Minimum 0 -Maximum ($user_ous_in_scope.Count - 1)
            $usernames += Get-ADUser -Filter * -SearchBase $user_ous_in_scope[$ou_index] | Select-Object -Property sAMAccountName | Sort-Object { Get-Random } | Select-Object -First $usernames_to_get_from_each_ou    
        }
    } elseif ($group_type -eq 2) {
        # mixed-scope users, choose from any of the in-scope OUs and one of the out of scope OUs
        $usernames_to_get = Get-Random -Minimum 1 -Maximum 250
        $in_scope_ous_to_use = Get-Random -Minimum 1 -Maximum $user_ous_in_scope.Count
        $usernames_to_get_from_each_ou = $usernames_to_get / ($in_scope_ous_to_use + 1)
        for ($i = 0; $i -lt $in_scope_ous_to_use; $i ++) {
            $ou_index = Get-Random -Minimum 0 -Maximum ($user_ous_in_scope.Count - 1)
            $usernames += Get-ADUser -Filter * -SearchBase $user_ous_in_scope[$ou_index] | Select-Object -Property sAMAccountName | Sort-Object { Get-Random } | Select-Object -First $usernames_to_get_from_each_ou    
        }
        $usernames += Get-ADUser -Filter * -SearchBase $user_ous_out_of_scope[0] | Select-Object -Property sAMAccountName | Sort-Object { Get-Random } | Select-Object -First $usernames_to_get_from_each_ou
    } elseif ($group_type -eq 3) {
        # out of scope users
        $usernames_to_get = Get-Random -Minimum 1 -Maximum 20
        $usernames = Get-ADUser -Filter * -SearchBase $user_ous_out_of_scope[0] | Select-Object -Property sAMAccountName | Sort-Object { Get-Random } | Select-Object -First $usernames_to_get
    }

    # add the users to the group
    if ($what_if_mode) {
        Add-ADGroupMember -Identity $group_sam_account_name -Members $usernames
        Write-Host "`Added $($usernames.Count) users to the group"
    } else {
        Write-Host "`tWould have added $($usernames.Count) users to the group"
    }
}
until ($groups_created -eq ($in_scope_groups_to_create + $out_of_scope_groups_to_create + $mixed_scope_groups_to_create))
$groups_created = $in_scope_groups_created + $mixed_scope_groups_created + $out_of_scope_groups_created
Write-Output "All done. Created $groups_created groups in $domain"