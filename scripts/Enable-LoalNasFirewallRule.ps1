param(
    [int]$Port = 5034,
    [string]$RuleName = "千私云 电脑访问 TCP 5034",
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
    Description = "允许千私云通过 TCP $Port 端口接收来自其他设备的访问"
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