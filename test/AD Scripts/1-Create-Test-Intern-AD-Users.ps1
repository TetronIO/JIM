Set-StrictMode -Version 2
$ErrorActionPreference = "Stop"
Import-Module ActiveDirectory -ErrorAction Stop
Clear-Host

##[ VARIABLES ]################################################################

# per-run specifics! set these!
$users_to_create = 20
$organisation_name = "SUBATOMIC"
$upn_prefix = "itn"
$domain = "corp.subatomic.com"
$domain_netbios = "CORP"
$ou = "OU=Interns,OU=Corp,DC=corp,DC=subatomic,DC=com"
$default_password = "1Password1"
$what_if_mode = $false

# data sources and destinations
$male_first_name_file = "../Data/Firstnames-m.csv"     # Format: FirstName
$female_first_name_file = "../Data/Firstnames-f.csv"   # Format: FirstName
$last_name_file = "../Data/Lastnames-fr.csv"           # Format: LastName
$output_filename = "InternUsers.csv"

# read in the source data
$first_names_male = Import-CSV $male_first_name_file
$first_names_female = Import-CSV $female_first_name_file
$last_names = Import-CSV $last_name_file

##[ CREATE THE REFERENCE DATA ]################################################

# setup reference data variables
$departments = (
    @{"Name" = "Finance & Accounting"; Positions = ("Accountant", "Data Entry Clerk")},
    @{"Name" = "Human Resources"; Positions = ("Officer", "Coordinator")},
    @{"Name" = "Sales"; Positions = ("Representative", "Consultant")},
    @{"Name" = "Marketing"; Positions = ("Coordinator", "Assistant", "Specialist")},
    @{"Name" = "Engineering"; Positions = ("Designer", "Engineer", "Scientist")},
    @{"Name" = "Consulting"; Positions = ("Programmer", "Account Executive")},
    @{"Name" = "IT"; Positions = ("Support Agent", "Engineer", "Technician")},
    @{"Name" = "Planning"; Positions = ("Executive", "Engineer")},
    @{"Name" = "Contracts"; Positions = ("Coordinator", "Clerk")},
    @{"Name" = "Purchasing"; Positions = ("Coordinator", "Clerk", "Purchaser")}
)

$companies = ("SwiftTech Solutions", "BlueBloom Innovations", "StellarCraft")

$country_gb = @{"Code" = "GB"; 
    Locations = (
        @{"Name" = "London"; "State" = $null; Offices = (
                @{"Name" = "Summit Tower"; "Street" = "22 Frith St"; "PostalCode" = "W1D 4RF"},
                @{"Name" = "Exchange Square Plaza"; "Street" = "Ace Corner, N Circular Rd"; "PostalCode" = "NW10 7UD"},
                @{"Name" = "Tower House Business Centre"; "Street" = "31 Chesham Pl"; "PostalCode" = "SW1X 8DL"}
            );
        },
        @{"Name" = "Manchester"; "State" = $null; Offices = (
                @{"Name" = "Riverside Tower"; "Street" = "42 Maple Street"; "PostalCode" = "M14 6JX"},
                @{"Name" = "Britannia House"; "Street" = "17 Oxford Road"; "PostalCode" = "M13 9PL"}
            )
        })
}

$country_fr = @{"Code" = "FR"; 
    Locations = (
        @{"Name" = "Paris"; "State" = "Île-de-France"; Offices = (
                @{"Name" = "Le Marais Tower"; "Street" = "210 Rue Saint-Maur"; "PostalCode" = "75010"},
                @{"Name" = "La Défense"; "Street" = "144 Rue Lecourbe"; "PostalCode" = "75015"},
                @{"Name" = "La Belle Étoile Plaza"; "Street" = "9 Rue Louis Willaume, Bois-Colombes"; "PostalCode" = "92270"}
            );
        },
        @{"Name" = "Nice"; "State" = "Provence-Alpes-Côte d'Azur"; Offices = (
                @{"Name" = "Le Riviera Tower"; "Street" = "47 Boulevard Victor Hugo"; "PostalCode" = "06000"},
                @{"Name" = "La Promenade Plaza"; "Street" = "21 Rue Auguste Gal"; "PostalCode" = "06300"}
            )
        })
}

$country_it = @{"Code" = "IT"; 
    Locations = (
        @{"Name" = "Rome"; "State" = "Lazio"; Offices = (
                @{"Name" = "Il Colosseo Tower"; "Street" = "Salita del Grillo, 17"; "PostalCode" = "00184"},
                @{"Name" = "La Piazza Navona Plaza"; "Street" = "Via Nazionale, 243"; "PostalCode" = "00184"},
                @{"Name" = "Il Vaticano Business Center"; "Street" = "Via Sicilia, 154"; "PostalCode" = "00187"}
            );
        },
        @{"Name" = "Bologna"; "State" = "Emilia-Romagna"; Offices = (
                @{"Name" = "La Torre di San Petronio"; "Street" = "Via Paradiso, 7"; "PostalCode" = "40122"},
                @{"Name" = "Il Centro Galleria"; "Street" = "Via Morandi, 6/B"; "PostalCode" = "40124"}
            )
        })
}

$countries = New-Object System.Collections.ArrayList
$countries.Add($country_gb) | Out-Null
#$countries.Add($country_fr) | Out-Null
#$countries.Add($country_it) | Out-Null
          
##[ CREATE THE USER DATA ]#####################################################

$users_created = 0
$csv = @()
do {
    # identity properties
    [bool] $male = Get-Random -Minimum 0 -Maximum 1
    if ($male) {
        $first_name = $first_names_male[$(Get-Random -Minimum 0 -Maximum $first_names_male.Count)].FirstName
    } else {
        $first_name = $first_names_female[$(Get-Random -Minimum 0 -Maximum $first_names_female.Count)].FirstName
    }
    $last_name = $last_names[$(Get-Random -Minimum 0 -Maximum $last_names.Count)].LastName
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
    $department_index = Get-Random -Minimum 0 -Maximum ($departments.Count - 1)
    $department = $departments[$department_index].Name
    $jobTitle = $departments[$department_index].Positions[$(Get-Random -Minimum 0 -Maximum $departments[$department_index].Positions.Count)]
    $employee_id = Get-Random -Minimum 10000000 -Maximum 99999999    
    $company_index = Get-Random -Minimum 0 -Maximum $companies.Count
    $company = $companies[$company_index]
    #$country_index = Get-Random -Minimum 0 -Maximum ($countries.Count - 1)
    $country_index = 0
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

    $other_attributes = @{"mailNickname" = $mail_nickname;}
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
            -Name $account_name `
            -SamAccountName $account_name `
            -Company $company `
            -Country $country.Code `
            -Department $department `
            -DisplayName $display_name `
            -EmployeeID $employee_id `
            -Fax $fax `
            -GivenName $first_name `
            -Surname $last_name `
            -HomePhone $home_phone `
            -Initials $initials `
            -City $location.Name `
            -MobilePhone $mobile_phone `
            -Office $office.Name `
            -StreetAddress $office.Street `
            -State $location.State `
            -PostalCode $office.PostalCode `
            -OfficePhone $office_phone `
            -Title $jobTitle `
            -AccountPassword $account_password `
            -Enabled $enabled `
            -PasswordNeverExpires $password_never_expires `
            -UserPrincipalName $email `
            -EmailAddress $email `
            -Path $ou `
            -OtherAttributes $other_attributes        

            Write-Output "Created $account_name user"
        }
    } 

    # put the user into a csv for use later when creating groups and their memberships
    $row = New-Object System.Object
    $row | Add-Member -MemberType NoteProperty -Name "AccountName" -Value $account_name
    $row | Add-Member -MemberType NoteProperty -Name "Company" -Value $company
    $row | Add-Member -MemberType NoteProperty -Name "DisplayName" -Value $display_name
    $row | Add-Member -MemberType NoteProperty -Name "EmployeeID" -Value $employee_id
    $row | Add-Member -MemberType NoteProperty -Name "Fax" -Value $Fax
    $row | Add-Member -MemberType NoteProperty -Name "GivenName" -Value $first_name
    $row | Add-Member -MemberType NoteProperty -Name "Surname" -Value $mobile_phone
    $row | Add-Member -MemberType NoteProperty -Name "HomePhone" -Value $home_phone
    $row | Add-Member -MemberType NoteProperty -Name "Initials" -Value $initials
    $row | Add-Member -MemberType NoteProperty -Name "City" -Value $location.Name
    $row | Add-Member -MemberType NoteProperty -Name "Domain" -Value $domain_netbios
    $row | Add-Member -MemberType NoteProperty -Name "MobilePhone" -Value $mobile_phone
    $row | Add-Member -MemberType NoteProperty -Name "Office" -Value $office.Name
    $row | Add-Member -MemberType NoteProperty -Name "StreetAddress" -Value $office.Street
    $row | Add-Member -MemberType NoteProperty -Name "PostalCode" -Value $office.PostalCode
    $row | Add-Member -MemberType NoteProperty -Name "OfficePhone" -Value $office_phone
    $row | Add-Member -MemberType NoteProperty -Name "Title" -Value $jobTitle
    $row | Add-Member -MemberType NoteProperty -Name "Enabled" -Value $enabled
    $row | Add-Member -MemberType NoteProperty -Name "PasswordNeverExpires" -Value $password_never_expires
    $row | Add-Member -MemberType NoteProperty -Name "UserPrincipalName" -Value $email
    $row | Add-Member -MemberType NoteProperty -Name "mailNickname" -Value $mail_nickname
    $row | Add-Member -MemberType NoteProperty -Name "st" -Value $location.State
    $row | Add-Member -MemberType NoteProperty -Name "otherHomePhone" -Value $other_home_phone
    $row | Add-Member -MemberType NoteProperty -Name "otherTelephone" -Value $other_telephone
    $row | Add-Member -MemberType NoteProperty -Name "pager" -Value $pager
    $row | Add-Member -MemberType NoteProperty -Name "msExchHideFromAddressLists" -Value $hide_from_address_lists
    $row | Add-Member -MemberType NoteProperty -Name "targetAddress" -Value $target_address
    $row | Add-Member -MemberType NoteProperty -Name "textEncodedOrAddress" -Value $text_encoded_or_address
    $row | Add-Member -MemberType NoteProperty -Name "legacyExchangeDN" -Value $legacy_exchange_dn
    #$row | Add-Member -MemberType NoteProperty -Name "msRTCSIP-PrimaryUserAddress" -Value $sip_address

    $csv += $row
    $users_created++
}
until ($users_created -eq $users_to_create)

$csv | Export-Csv $output_filename -NoTypeInformation
Write-Output "All done. Created $users_to_create users in $domain"