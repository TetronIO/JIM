Set-StrictMode -Version 2
$ErrorActionPreference = "Stop"
Import-Module ActiveDirectory -ErrorAction Stop
Clear-Host

##[ VARIABLES ]################################################################

# per-run specifics! set these!
$users_to_create = 15
$organisation_name = "SUBATOMIC"
$upn_prefix = "ptn"
$domain = "corp.subatomic.com"
$domain_netbios = "CORP"
$ou = "OU=Partners,OU=Corp,DC=corp,DC=subatomic,DC=com"
$default_password = "1Password1"
$what_if_mode = $false

# data sources and destinations
$male_first_name_file = "Firstnames-m.csv"     # Format: FirstName
$female_first_name_file = "Firstnames-f.csv"   # Format: FirstName
$last_name_file = "Lastnames-fr.csv"           # Format: LastName
$output_filename = "Users.csv"

# read in the source data
$first_names_male = Import-CSV $male_first_name_file
$first_names_female = Import-CSV $female_first_name_file
$last_names = Import-CSV $last_name_file

##[ CREATE THE REFERENCE DATA ]################################################

# setup reference data variables
$departments = (
    @{"Name" = "Engineering"; Positions = ("Manager", "Engineer", "Scientist")},
    @{"Name" = "Consulting"; Positions = ("Project Manager", "Architect", "Consultant")}
)

$companies = ("NovaTech Solutions", "Precision Engineering Group", "CodeWave Technologies")

$country_gb = @{"Code" = "GB"; 
    Locations = (
        @{"Name" = "London"; "State" = $null; Offices = (
                @{"Name" = "Venture Towers"; "Street" = "10 Park Avenue"; "PostalCode" = "SW1X 7XT"},
                @{"Name" = "Horizon Plaza"; "Street" = "135 King's Road, Chelsea"; "PostalCode" = "SW3 4PX"}
            );
        },
        @{"Name" = "Edinburgh"; "State" = $null; Offices = (
                @{"Name" = "Summit Tower"; "Street" = "82 Princes Street"; "PostalCode" = "W1D 4RF"},
                @{"Name" = "Riverside Tower"; "Street" = "47 George Street"; "PostalCode" = "EH2 2HT"}
            );
        })        
}

$country_us = @{"Code" = "US"; 
    Locations = (
        @{"Name" = "New York"; "State" = "New York"; Offices = (
                @{"Name" = "Skyline Tower"; "Street" = "123 Main Street"; "PostalCode" = "12345"},
                @{"Name" = "Commerce Center Plaza"; "Street" = "789 Park Avenue"; "PostalCode" = "10021"}
            );
        },
        @{"Name" = "Cityville"; "State" = "California"; Offices = (
                @{"Name" = "Innovation Park Office Suites"; "Street" = "456 Elm Avenue"; "PostalCode" = "67890"},
                @{"Name" = "Capital Square Tower"; "Street" = "987 Oak Street"; "PostalCode" = "90001"}
            )
        })
}

$countries = New-Object System.Collections.ArrayList
$countries.Add($country_gb) | Out-Null
$countries.Add($country_us) | Out-Null
          
##[ CREATE THE USER DATA ]#####################################################

$users_created = 0
$csv = @()
do {
    # identity properties
    [bool] $male = Get-Random -Minimum 0 -Maximum 1
    if ($male) {
        $first_name = $first_names_male[$(Get-Random -Minimum 0 -Maximum ($first_names_male.Count - 1))].FirstName
    } else {
        $first_name = $first_names_female[$(Get-Random -Minimum 0 -Maximum ($first_names_female.Count - 1))].FirstName
    }
    $last_name = $last_names[$(Get-Random -Minimum 0 -Maximum ($last_names.Count - 1))].LastName
    $display_name = "$first_name $last_name"
    $account_name = "$upn_prefix-" + $first_name.Substring(0, 1).ToLower() + $last_name.Substring(0, 1).ToLower() + "-" + ($users_created + 1)
    
    # other properties
    $enabled = $true
    $password_never_expires = $true
    $mail_nickname = "$first_name.$last_name".ToLower()
    $email = "$mail_nickname@$domain"
    $home_phone = "0203 " + (Get-Random -Minimum 221 -Maximum 986) + " " + (Get-Random -Minimum 1000 -Maximum 9999)
    $fax = "0208 " + (Get-Random -Minimum 221 -Maximum 986) + " " + (Get-Random -Minimum 1000 -Maximum 9999)
    $fax_chance = Get-Random -Minimum 0 -Maximum 10
    if ($fax_chance -gt 4) {
        $fax = $null
    }

    $office_phone = "0208 " + (Get-Random -Minimum 221 -Maximum 986) + " " + (Get-Random -Minimum 1000 -Maximum 9999)
    $mobile_phone = "07" + (Get-Random -Minimum 106 -Maximum 999) + " " + (Get-Random -Minimum 221 -Maximum 986) + " " + (Get-Random -Minimum 100 -Maximum 999)    
    $department_index = Get-Random -Minimum 0 -Maximum $departments.Count
    $department = $departments[$department_index].Name
    $jobTitle = $departments[$department_index].Positions[$(Get-Random -Minimum 0 -Maximum $departments[$department_index].Positions.Count)]
    $employee_id = Get-Random -Minimum 10000000 -Maximum 99999999    
    $company_index = Get-Random -Minimum 0 -Maximum $companies.Count
    $company = $companies[$company_index]
    $country_index = Get-Random -Minimum 0 -Maximum $countries.Count
    $country = $countries[$country_index]
    $location_index = Get-Random -Minimum 0 -Maximum $country.Locations.Count
    $location = $country.Locations[$location_index]
    $office_index = Get-Random -Minimum 0 -Maximum $location.Offices.Count
    $office = $location.Offices[$office_index]

    $initials = (65..90) + (97..122) | Get-Random -Count 1 | ForEach-Object { [char]$_ }
    $initials_chance = Get-Random -Minimum 0 -Maximum 10
    if ($initials_chance -gt 4) {
        $initials = $null
    }

    $hide_from_address_lists = $null
    $hfal_chance = Get-Random -Minimum 0 -Maximum 10
    if ($hfal_chance -le 3) {
        $hide_from_address_lists = $false
        $hfal2_chance = Get-Random -Minimum 0 -Maximum 10
        if ($hfal2_chance -le 4) {
            $hide_from_address_lists = $true
        }
    }

    $sip_address = "sip:$email"
    $sip_address_chance = Get-Random -Minimum 0 -Maximum 10
    if ($sip_address_chance -gt 4) {
        $sip_address = $null
    }

    $other_home_phone = "0209 " + (Get-Random -Minimum 221 -Maximum 986) + " " + (Get-Random -Minimum 1000 -Maximum 9999)
    $other_home_phone_chance = Get-Random -Minimum 0 -Maximum 10
    if ($other_home_phone_chance -le 3) {
        $other_home_phone = $null
    }

    $other_telephone = "0206 " + (Get-Random -Minimum 221 -Maximum 986) + " " + (Get-Random -Minimum 1000 -Maximum 9999)
    $other_telephone_chance = Get-Random -Minimum 0 -Maximum 10
    if ($other_telephone_chance -le 4) {
        $other_telephone = $null
    }

    $pager = "0205 " + (Get-Random -Minimum 221 -Maximum 986) + " " + (Get-Random -Minimum 1000 -Maximum 9999)
    $pager_chance = Get-Random -Minimum 0 -Maximum 100
    if ($pager_chance -le 2) {
        $pager = $null
    }

    $target_address = "SMTP:$email"
    $target_address_chance = Get-Random -Minimum 0 -Maximum 10
    if ($target_address_chance -le 2) {
        $target_address = $null
    }

    $text_encoded_or_address = "C=$($country.Code);A= ;P=$domain_netbios;O=$organisation_name;S=$last_name;G=$first_name;"
    $legacy_exchange_dn = "/o=$organisation_name/ou=Exchange Administrative Group (FYDIBOHF23SPDLT)/cn=Recipients/cn=$display_name"
    $legacy_attribs_chance = Get-Random -Minimum 0 -Maximum 10
    if ($legacy_attribs_chance -le 3) {
        $text_encoded_or_address = $null
        $legacy_exchange_dn = $null
    }

    $other_attributes = @{"mailNickname" = $mail_nickname; }
    if ($null -ne $other_home_phone -and $what_if_mode -eq $false) {
        $other_attributes.Add("otherHomePhone", $other_home_phone)
    }
    if ($null -ne $other_telephone -and $what_if_mode -eq $false) {
        $other_attributes.Add("otherTelephone", $other_telephone)
    }
    if ($null -ne $pager -and $what_if_mode -eq $false) {
        $other_attributes.Add("pager", $pager)
    }

    # EXCHANGE SCHEMA EXTENSIONS REQUIRED!
    if ($null -ne $hide_from_address_lists -and $what_if_mode -eq $false) {
        $other_attributes.Add("msExchHideFromAddressLists", $hide_from_address_lists)
    }
    if ($null -ne $target_address -and $what_if_mode -eq $false) {
        $other_attributes.Add("targetAddress", $target_address)
    }
    if ($null -ne $text_encoded_or_address -and $what_if_mode -eq $false) {
        $other_attributes.Add("textEncodedOrAddress", $text_encoded_or_address)
    }
    if ($null -ne $legacy_exchange_dn -and $what_if_mode -eq $false) {
        $other_attributes.Add("legacyExchangeDN", $legacy_exchange_dn)
    }

    # SKYPE FOR BUSINESS SCHEMA EXTENSIONS REQUIRED!
    #if ($null -ne $sip_address -and $what_if_mode -eq $false) {
    #    $other_attributes.Add("msRTCSIP-PrimaryUserAddress", $sip_address)
    #}

    # have we created a unique user?
    if ($null -ne ($csv | Where-Object { $_.DisplayName -eq $display_name })) {
        Write-Output "Duplicate user '$display_name' created. Skipping."
        continue;
    }

    # create the user in AD
    $exists = $false
    try { $exists = Get-ADUser -LDAPFilter "(sAMAccountName=$account_name)" } 
    catch { } 

    if (!$exists) {
        if ($what_if_mode) {
            Write-Output "Whatif: Would have created $account_name user"
        } else {
            # Set all variables according to the table names in the Excel  
            # sheet / import CSV. The names can differ in every project, but  
            # if the names change, make sure to change it below as well. 
            $account_password = ConvertTo-SecureString -AsPlainText $default_password -force 

            New-ADUser `
            -AccountPassword $account_password `
            -City $location.Name `
            -Company $company `
            -Country $country.Code `
            -Department $department `
            -DisplayName $display_name `
            -EmailAddress $email `
            -EmployeeID $employee_id `
            -Enabled $enabled `
            -Fax $fax `
            -GivenName $first_name `
            -HomePhone $home_phone `
            -Initials $initials `
            -MobilePhone $mobile_phone `
            -Name $account_name `
            -Office $office.Name `
            -OfficePhone $office_phone `
            -OtherAttributes $other_attributes `
            -PasswordNeverExpires $password_never_expires `
            -Path $ou `
            -PostalCode $office.PostalCode `
            -SamAccountName $account_name `
            -State $location.State `
            -StreetAddress $office.Street `
            -Surname $last_name `
            -Title $jobTitle `
            -UserPrincipalName $email

            Write-Output "Created $account_name user"
        }
    } 

    # put the user into a csv for use later when creating groups and their memberships
    $row = New-Object System.Object
    $row | Add-Member -MemberType NoteProperty -Name "AccountName" -Value $account_name
    $row | Add-Member -MemberType NoteProperty -Name "City" -Value $location.Name
    $row | Add-Member -MemberType NoteProperty -Name "Company" -Value $company
    $row | Add-Member -MemberType NoteProperty -Name "DisplayName" -Value $display_name
    $row | Add-Member -MemberType NoteProperty -Name "Domain" -Value $domain_netbios
    $row | Add-Member -MemberType NoteProperty -Name "EmployeeID" -Value $employee_id
    $row | Add-Member -MemberType NoteProperty -Name "Enabled" -Value $enabled
    $row | Add-Member -MemberType NoteProperty -Name "Fax" -Value $Fax
    $row | Add-Member -MemberType NoteProperty -Name "GivenName" -Value $first_name
    $row | Add-Member -MemberType NoteProperty -Name "HomePhone" -Value $home_phone
    $row | Add-Member -MemberType NoteProperty -Name "Initials" -Value $initials
    $row | Add-Member -MemberType NoteProperty -Name "MobilePhone" -Value $mobile_phone
    $row | Add-Member -MemberType NoteProperty -Name "Office" -Value $office.Name
    $row | Add-Member -MemberType NoteProperty -Name "OfficePhone" -Value $office_phone
    $row | Add-Member -MemberType NoteProperty -Name "PasswordNeverExpires" -Value $password_never_expires
    $row | Add-Member -MemberType NoteProperty -Name "PostalCode" -Value $office.PostalCode
    $row | Add-Member -MemberType NoteProperty -Name "State" -Value $location.State
    $row | Add-Member -MemberType NoteProperty -Name "StreetAddress" -Value $office.Street
    $row | Add-Member -MemberType NoteProperty -Name "Surname" -Value $mobile_phone
    $row | Add-Member -MemberType NoteProperty -Name "Title" -Value $jobTitle
    $row | Add-Member -MemberType NoteProperty -Name "UserPrincipalName" -Value $email
    $row | Add-Member -MemberType NoteProperty -Name "legacyExchangeDN" -Value $legacy_exchange_dn
    $row | Add-Member -MemberType NoteProperty -Name "mailNickname" -Value $mail_nickname
    $row | Add-Member -MemberType NoteProperty -Name "msExchHideFromAddressLists" -Value $hide_from_address_lists
    $row | Add-Member -MemberType NoteProperty -Name "otherHomePhone" -Value $other_home_phone
    $row | Add-Member -MemberType NoteProperty -Name "otherTelephone" -Value $other_telephone
    $row | Add-Member -MemberType NoteProperty -Name "pager" -Value $pager
    $row | Add-Member -MemberType NoteProperty -Name "st" -Value $location.State
    $row | Add-Member -MemberType NoteProperty -Name "targetAddress" -Value $target_address
    $row | Add-Member -MemberType NoteProperty -Name "textEncodedOrAddress" -Value $text_encoded_or_address
    #$row | Add-Member -MemberType NoteProperty -Name "msRTCSIP-PrimaryUserAddress" -Value $sip_address

    $csv += $row
    $users_created++
}
until ($users_created -eq $users_to_create)

$csv | Export-Csv $output_filename -NoTypeInformation
Write-Output "All done. Created $users_to_create users in $domain"