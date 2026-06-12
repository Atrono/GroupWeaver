#Requires -Modules ActiveDirectory
<#
.SYNOPSIS
  Seeds the AGDLP-Lab integration-test fixtures. THE ONLY SANCTIONED AD-WRITE
  PATH in this project (CLAUDE.md non-negotiables); run exclusively by the
  ad-fixture-admin subagent. Idempotent. Writes ONLY beneath
  OU=AGDLP-Lab,DC=agdlp,DC=lab and aborts on any other domain.
.DESCRIPTION
  ~200 objects mirroring the DemoProvider dataset spec (PLANNING.md AP 1.4 -
  the DemoProvider JSON is a separate deliverable): users, GG/DL/UG groups,
  nested memberships, deliberate AGDLP violations, naming violations, one
  circular nesting (A->B->A), empty groups.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$domain = Get-ADDomain
if ($domain.DNSRoot -ne 'agdlp.lab') {
    throw "SAFETY ABORT: connected domain is '$($domain.DNSRoot)', expected 'agdlp.lab'."
}
$baseDN = $domain.DistinguishedName
$labDN  = "OU=AGDLP-Lab,$baseDN"

function Assert-LabPath([string]$dn) {
    if (-not $dn.EndsWith($labDN, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SAFETY ABORT: refusing to write outside ${labDN}: $dn"
    }
}

$script:created = 0
$script:skipped = 0

function Test-AdObjectDn([string]$dn) {
    try { Get-ADObject -Identity $dn | Out-Null; $true } catch { $false }
}

function Ensure-OU([string]$name, [string]$path) {
    $dn = "OU=$name,$path"
    Assert-LabPath $dn
    if (Test-AdObjectDn $dn) { $script:skipped++ }
    else {
        New-ADOrganizationalUnit -Name $name -Path $path -ProtectedFromAccidentalDeletion $false | Out-Null
        $script:created++
    }
    $dn
}

function Ensure-User([string]$sam, [string]$given, [string]$surname, [string]$path) {
    Assert-LabPath $path
    if (Get-ADUser -Filter "sAMAccountName -eq '$sam'" -SearchBase $labDN) { $script:skipped++; return }
    New-ADUser -Name "$given $surname ($sam)" -GivenName $given -Surname $surname `
        -SamAccountName $sam -Path $path -Enabled $false | Out-Null
    $script:created++
}

function Ensure-Group([string]$name, [string]$scope, [string]$path) {
    Assert-LabPath $path
    if (Get-ADGroup -Filter "sAMAccountName -eq '$name'" -SearchBase $labDN) { $script:skipped++; return }
    New-ADGroup -Name $name -SamAccountName $name -GroupScope $scope -GroupCategory Security -Path $path | Out-Null
    $script:created++
}

function Ensure-Computer([string]$name, [string]$path) {
    Assert-LabPath $path
    if (Get-ADComputer -Filter "name -eq '$name'" -SearchBase $labDN) { $script:skipped++; return }
    New-ADComputer -Name $name -Path $path -Enabled $false | Out-Null
    $script:created++
}

function Ensure-Member([string]$group, [string]$member) {
    # Resolve BOTH sides inside the lab OU so this write path is pinned to
    # OU=AGDLP-Lab regardless of call order or sAMAccountName collisions.
    $g = Get-ADGroup -Filter "sAMAccountName -eq '$group'" -SearchBase $labDN -Properties member
    if (-not $g) { throw "Ensure-Member: group '$group' not found under $labDN" }
    $m = Get-ADObject -Filter "sAMAccountName -eq '$member'" -SearchBase $labDN
    if (-not $m) { throw "Ensure-Member: member '$member' not found under $labDN" }
    Assert-LabPath $g.DistinguishedName
    Assert-LabPath $m.DistinguishedName
    # Pre-check via the raw member attribute, NOT Get-ADGroupMember: that cmdlet
    # resolves every member to a principal and throws an unspecified error on
    # groups containing an unresolvable (dangling) FSP - which DL_App-ERP_RW
    # deliberately does (see Ensure-ForeignSidMember below).
    if (@($g.member) -contains $m.DistinguishedName) { $script:skipped++ }
    else { Add-ADGroupMember -Identity $g -Members $m; $script:created++ }
}

function Ensure-ForeignSidMember([string]$group, [string]$sid) {
    # Adds a foreign-domain SID to an in-OU group's member attribute via the
    # <SID=...> binding form. The ONLY object this function writes is the group
    # itself (Assert-LabPath enforced below); see the call site for the
    # ForeignSecurityPrincipals side effect.
    $g = Get-ADGroup -Filter "sAMAccountName -eq '$group'" -SearchBase $labDN -Properties member
    if (-not $g) { throw "Ensure-ForeignSidMember: group '$group' not found under $labDN" }
    Assert-LabPath $g.DistinguishedName
    # Once the DC resolves the binding, the member attribute stores the FSP DN
    # (not the <SID=...> form) - pre-check against that DN for idempotency.
    $fspDn = "CN=$sid,CN=ForeignSecurityPrincipals,$baseDN"
    if ($g.member | Where-Object { $_ -eq $fspDn }) { $script:skipped++; return }
    Set-ADGroup -Identity $g.DistinguishedName -Add @{ member = "<SID=$sid>" }
    $script:created++
}

# --- OU structure --------------------------------------------------------------
# The lab root OU is the single allowed write at domain level.
if (-not (Test-AdObjectDn $labDN)) {
    New-ADOrganizationalUnit -Name 'AGDLP-Lab' -Path $baseDN -ProtectedFromAccidentalDeletion $false | Out-Null
    $script:created++
}
else { $script:skipped++ }

$ouUsers     = Ensure-OU 'Users' $labDN
$ouGroups    = Ensure-OU 'Groups' $labDN
$ouComputers = Ensure-OU 'Computers' $labDN

# --- Users (140, deterministic fictional names, disabled = no passwords) -------
$firstNames = 'Anna','Ben','Carla','David','Elena','Felix','Greta','Henrik','Ines','Jonas',
              'Katja','Lars','Mara','Nils','Olga','Paul','Rita','Sven','Tina','Udo'
$lastNames  = 'Acker','Brandt','Claus','Dorn','Ebert','Falk','Gruber','Hahn','Iben','Jung','Kraus','Lenz'
for ($i = 1; $i -le 140; $i++) {
    $g = $firstNames[($i - 1) % $firstNames.Count]
    $s = $lastNames[[math]::Floor(($i - 1) / $firstNames.Count) % $lastNames.Count]
    Ensure-User ('u{0:d3}' -f $i) $g $s $ouUsers
}

# --- Computers (10) -------------------------------------------------------------
for ($i = 1; $i -le 10; $i++) { Ensure-Computer ('LAB-PC{0:d2}' -f $i) $ouComputers }

# --- Groups ---------------------------------------------------------------------
$departments = 'Sales','IT','HR','Finance','Ops'

# Global groups (roles): GG_<Dept>_<Role>
foreach ($d in $departments) { foreach ($r in 'Staff','Lead') { Ensure-Group "GG_${d}_${r}" Global $ouGroups } }
foreach ($r in 'Admins','Helpdesk','Backup') { Ensure-Group "GG_IT_$r" Global $ouGroups }

# Domain-local groups (resource access): DL_<Resource>_<Perm>
$resources = 'FS-Sales','FS-IT','FS-HR','FS-Finance','FS-Ops','Print-HQ','App-CRM','App-ERP'
foreach ($res in $resources) { foreach ($perm in 'RW','RO') { Ensure-Group "DL_${res}_${perm}" DomainLocal $ouGroups } }

# Universal groups
foreach ($u in 'UG_AllStaff','UG_Managers','UG_ProjectX') { Ensure-Group $u Universal $ouGroups }

# --- Correct AGDLP wiring: users -> GG -> (UG) -> DL ----------------------------
for ($i = 1; $i -le 100; $i++) {
    Ensure-Member ("GG_$($departments[($i - 1) % $departments.Count])_Staff") ('u{0:d3}' -f $i)
}
for ($i = 101; $i -le 110; $i++) {
    Ensure-Member ("GG_$($departments[($i - 101) % $departments.Count])_Lead") ('u{0:d3}' -f $i)
}
# u111-u140 deliberately belong to no group (orphans for the violation list)
foreach ($d in $departments) {
    Ensure-Member "DL_FS-${d}_RW" "GG_${d}_Staff"
    Ensure-Member "DL_FS-${d}_RO" "GG_${d}_Lead"
    Ensure-Member 'UG_AllStaff'   "GG_${d}_Staff"
    Ensure-Member 'UG_Managers'   "GG_${d}_Lead"
}
Ensure-Member 'DL_App-ERP_RW'  'UG_Managers'
Ensure-Member 'DL_App-CRM_RW'  'UG_AllStaff'
Ensure-Member 'DL_Print-HQ_RW' 'GG_IT_Helpdesk'
Ensure-Member 'GG_IT_Admins'   'GG_IT_Backup'      # GG-in-GG: allowed, matrix decides

# --- Cross-forest FSP member (canonical AGDLP cross-domain scenario) --------------
# Fixed, deterministic SID from a NONEXISTENT foreign domain - exactly what a
# dangling cross-forest FSP looks like once the trusted forest is gone. The write
# below targets ONLY the in-OU group DL_App-ERP_RW; the DC system-creates
# CN=<sid>,CN=ForeignSecurityPrincipals,DC=agdlp,DC=lab as a side effect of the
# <SID=...> binding form. Documented tension with the lab-OU-only rule; reviewer
# signs off in the PR. (Well-known SIDs like S-1-5-11 were refused by the DC, #9.)
Ensure-ForeignSidMember 'DL_App-ERP_RW' 'S-1-5-21-1100000001-2200000002-3300000003-1106'

# --- Deliberate AGDLP violations (allowed by AD, flagged by GroupWeaver) --------
Ensure-Member 'DL_FS-Sales_RW' 'u001'               # user directly in a DL
Ensure-Member 'UG_AllStaff'    'u002'               # user directly in a UG
Ensure-Group  'DL_Nested_RO' DomainLocal $ouGroups
Ensure-Member 'DL_FS-Finance_RO' 'DL_Nested_RO'     # DL nested inside a DL

# --- Naming violations -----------------------------------------------------------
Ensure-Group 'SalesTeamGlobal'  Global      $ouGroups   # missing GG_ prefix
Ensure-Group 'dl-finance-extra' DomainLocal $ouGroups   # wrong case/format
Ensure-Group 'GG_X'             Global      $ouGroups   # missing role segment

# --- One circular nesting: GG_Circle_A <-> GG_Circle_B ----------------------------
Ensure-Group 'GG_Circle_A' Global $ouGroups
Ensure-Group 'GG_Circle_B' Global $ouGroups
Ensure-Member 'GG_Circle_A' 'GG_Circle_B'
Ensure-Member 'GG_Circle_B' 'GG_Circle_A'   # AD permits GG<->GG cycles; abort on any real error

# --- Empty groups ------------------------------------------------------------------
Ensure-Group 'GG_Empty_Marketing' Global      $ouGroups
Ensure-Group 'DL_FS-Legacy_RO'    DomainLocal $ouGroups

# --- Summary ------------------------------------------------------------------------
$total = (Get-ADObject -SearchBase $labDN -Filter * | Measure-Object).Count
Write-Host "seed-testad: $($script:created) created, $($script:skipped) already present, $total objects under $labDN"
