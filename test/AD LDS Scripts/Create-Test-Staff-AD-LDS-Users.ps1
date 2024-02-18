Set-StrictMode -Version 2
$ErrorActionPreference = "Stop"
Clear-Host

##[ VARIABLES ]################################################################

# per-run specifics! set these!
$users_to_create = 100
$upn_prefix = "s"
$ou = "CN=Staff,CN=Users,CN=RD,DC=Borton,DC=Com"
$domain = "rd.borton.com"
$what_if_mode = $false

# data sources and destinations
$male_first_name_file = "../Data/Firstnames-m.csv"     # Format: FirstName
$female_first_name_file = "../Data/Firstnames-f.csv"   # Format: FirstName
$last_name_file = "../Data/Lastnames.csv"           # Format: LastName
$output_filename = "StaffUsers.csv"

# read in the source data
$first_names_male = Import-CSV $male_first_name_file
$first_names_female = Import-CSV $female_first_name_file
$last_names = Import-CSV $last_name_file

##[ CREATE THE REFERENCE DATA ]################################################

# setup reference data variables
$departments = (
    @{"Name" = "Finance & Accounting"; Positions = ("Manager", "Accountant", "Data Entry")},
    @{"Name" = "Human Resources"; Positions = ("Manager", "Administrator", "Officer", "Coordinator")},
    @{"Name" = "Sales"; Positions = ("Manager", "Representative", "Consultant")},
    @{"Name" = "Marketing"; Positions = ("Manager", "Coordinator", "Assistant", "Specialist")},
    @{"Name" = "Engineering"; Positions = ("Manager", "Engineer", "Scientist")},
    @{"Name" = "Consulting"; Positions = ("Project Manager", "Consultant")},
    @{"Name" = "IT"; Positions = ("Manager", "Engineer", "Technician")},
    @{"Name" = "Planning"; Positions = ("Manager", "Engineer")},
    @{"Name" = "Contracts"; Positions = ("Manager", "Coordinator", "Clerk")},
    @{"Name" = "Purchasing"; Positions = ("Manager", "Coordinator", "Clerk", "Purchaser")}
)

$companies = ("Borton R&D", "Celestial Science", "Phase Remediations")

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

$countries = New-Object System.Collections.ArrayList
$countries.Add($country_gb) | Out-Null
          
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

    # have we created a unique user?
    if ($null -ne ($csv | Where-Object { $_.DisplayName -eq $display_name })) {
        Write-Output "Duplicate user '$display_name' created. Skipping."
        continue;
    }

    # create the user in AD LDS
    [ADSI]$user = "LDAP://localhost/CN=$account_name,$ou"
    $exists = $null -ne $user.Guid
    if (!$exists) {
        if ($what_if_mode) {
            Write-Output "Whatif: Would have created $account_name user"
        } else {
            # Set all variables according to the table names in the Excel  
            # sheet / import CSV. The names can differ in every project, but  
            # if the names change, make sure to change it below as well.
            [ADSI]$user_container = "LDAP://localhost/CN=Staff,CN=Users,CN=RD,DC=Borton,DC=Com"
            $user = $user_container.Create("inetOrgPerson","CN=$account_name")
            $user.put("msDS-UserDontExpirePassword", $true)
            $user.put("msDS-UserAccountDisabled", $false)
            
            if ($null -ne $company) {
                $user.put("company", $company)
            }
            if ($null -ne $country.Code) {
                $user.put("c", $country.Code)
            }
            if ($null -ne $department) {
                $user.put("department", $department)
            }
            if ($null -ne $display_name) {
                $user.put("displayName", $display_name)
            }
            if ($null -ne $employee_id) {
                $user.put("employeeID", $employee_id)
            }
            #$user.put("facsimileTelephoneNumber", $fax)
            if ($null -ne $first_name) {
                $user.put("givenName", $first_name)
            }
            if ($null -ne $last_name) {
                $user.put("sn", $last_name)
            }
            if ($null -ne $home_phone) {
                $user.put("homePhone", $home_phone)
            }
            #if ($null -ne $initials) {
            #    $user.put("initials", $initials)
            #}
            if ($null -ne $mobile_phone) {
                $user.put("mobile", $mobile_phone)
            }
            if ($null -ne $office.Street) {
                $user.put("streetAddress", $office.Street)
            }
            if ($null -ne $location.State) {
                $user.put("st", $location.State)
            }
            if ($null -ne $office.PostalCode) {
                $user.put("postalCode", $office.PostalCode)
            }
            if ($null -ne $office_phone) {
                $user.put("telephoneNumber", $office_phone)
            }
            if ($null -ne $jobTitle) {
                $user.put("title", $jobTitle)
            }
            if ($null -ne $email) {
                $user.put("userPrincipalName", $email)
                $user.put("mail", $email)
            }
            if ($null -ne $pager) {
                $user.put("pager", $pager)
            }
            if ($null -ne $other_home_phone) {
                $user.put("otherHomePhone", $other_home_phone)
            }
            if ($null -ne $other_telephone) {
                $user.put("otherTelephone", $other_telephone)
            }
            $user.setinfo()

            #New-ADUser `
            #-Name $account_name `
            #####-SamAccountName $account_name `
            #-Company $company `
            #-Country $country.Code `
            #-Department $department `
            #-DisplayName $display_name `
            #-EmployeeID $employee_id `
            #-Fax $fax `
            #-GivenName $first_name `
            #-Surname $last_name `
            #-HomePhone $home_phone `
            #-Initials $initials `
            #####-City $location.Name `
            #-MobilePhone $mobile_phone `
            #####-Office $office.Name `
            #-StreetAddress $office.Street `
            #-State $location.State `
            #-PostalCode $office.PostalCode `
            #-OfficePhone $office_phone `
            #-Title $jobTitle `
            ####-AccountPassword $account_password `
            #####-Enabled $enabled `
            #####-PasswordNeverExpires $password_never_expires `
            #-UserPrincipalName $email `
            #-EmailAddress $email `
            #####-Path $ou `
            #####-OtherAttributes $other_attributes

            Write-Output "Created user: $account_name"
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
    $row | Add-Member -MemberType NoteProperty -Name "Domain" -Value $domain
    $row | Add-Member -MemberType NoteProperty -Name "MobilePhone" -Value $mobile_phone
    $row | Add-Member -MemberType NoteProperty -Name "Office" -Value $office.Name
    $row | Add-Member -MemberType NoteProperty -Name "StreetAddress" -Value $office.Street
    $row | Add-Member -MemberType NoteProperty -Name "PostalCode" -Value $office.PostalCode
    $row | Add-Member -MemberType NoteProperty -Name "OfficePhone" -Value $office_phone
    $row | Add-Member -MemberType NoteProperty -Name "Title" -Value $jobTitle
    $row | Add-Member -MemberType NoteProperty -Name "UserPrincipalName" -Value $email
    $row | Add-Member -MemberType NoteProperty -Name "st" -Value $location.State
    $row | Add-Member -MemberType NoteProperty -Name "otherHomePhone" -Value $other_home_phone
    $row | Add-Member -MemberType NoteProperty -Name "otherTelephone" -Value $other_telephone
    $row | Add-Member -MemberType NoteProperty -Name "pager" -Value $pager
    
    $csv += $row
    $users_created++
}
until ($users_created -eq $users_to_create)

$csv | Export-Csv $output_filename -NoTypeInformation
Write-Output "All done. Created $users_to_create users in: $ou"