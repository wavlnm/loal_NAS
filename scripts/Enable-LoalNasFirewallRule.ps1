param(
    [int]$Port = 5034,
    [string]$RuleName = "loal_NAS Host TCP 5034",
    [ValidateSet("Any", "Domain", "Private", "Public")]
    [string[]]$Profiles = @("Any")
)

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Please run this script from an elevated PowerShell session."
}

$existingRule = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
if ($existingRule) {
    $existingRule | Remove-NetFirewallRule
}

$ruleParameters = @{
    DisplayName = $RuleName
    Description = "Allow inbound TCP traffic for loal_NAS host on port $Port"
    Direction = "Inbound"
    Action = "Allow"
    Enabled = "True"
    Profile = $Profiles
    Protocol = "TCP"
    LocalPort = $Port
}

$rule = New-NetFirewallRule @ruleParameters

Write-Host "Created firewall rule: $($rule.DisplayName)"
Write-Host "Opened port: TCP $Port"
Write-Host "Profiles: $($Profiles -join ', ')"
Write-Host "Override profiles with -Profiles Domain, Private, Public, or Any if needed."