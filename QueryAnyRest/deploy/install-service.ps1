<#
.SYNOPSIS
    Publishes Query Any REST and registers it as a Windows service.

.DESCRIPTION
    Run from an elevated PowerShell prompt. Publishing and installing are separate
    switches so you can push a new build over an installed service without
    re-registering it.

.EXAMPLE
    .\install-service.ps1 -Publish -Install -Start

.EXAMPLE
    .\install-service.ps1 -Publish -Restart
#>

[CmdletBinding()]
param(
    [string] $ProjectPath   = (Join-Path $PSScriptRoot '..\QueryAnyRest.csproj'),
    [string] $InstallPath   = 'C:\Program Files\QueryAnyRest',
    [string] $DataPath      = 'C:\ProgramData\QueryAnyRest',
    [string] $ServiceName   = 'QueryAnyRest',
    [string] $DisplayName   = 'Query Any REST',
    [string] $ServiceUser   = 'NT AUTHORITY\NetworkService',
    [int]    $Port          = 5290,

    [switch] $Publish,
    [switch] $Install,
    [switch] $Start,
    [switch] $Restart,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell prompt.'
    }
}

Assert-Elevated

$exePath = Join-Path $InstallPath 'QueryAnyRest.exe'

if ($Uninstall) {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host "Stopping and removing $ServiceName."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
    }
    else {
        Write-Host "$ServiceName is not installed."
    }
    Write-Host "Files in $InstallPath and data in $DataPath were left in place."
    Write-Host "Keep $DataPath\keys if you want the saved credentials to stay readable;"
    Write-Host "without that folder every password and client secret must be typed in again."
    return
}

if ($Publish) {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host 'Stopping the service so its files can be replaced.'
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }

    Write-Host "Publishing to $InstallPath."
    dotnet publish $ProjectPath -c Release -r win-x64 --self-contained false -o $InstallPath
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}

if ($Install) {
    if (-not (Test-Path $exePath)) {
        throw "$exePath was not found. Run with -Publish first."
    }

    New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $DataPath 'exports') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $DataPath 'keys') | Out-Null

    # The data folder holds credentials and the key ring that decrypts them, so the
    # inherited ProgramData permissions (which let any interactive user read) are dropped
    # and replaced with an explicit list.
    Write-Host "Restricting $DataPath to SYSTEM, Administrators and $ServiceUser."
    $acl = Get-Acl $DataPath
    $acl.SetAccessRuleProtection($true, $false)   # stop inheriting, discard inherited entries

    foreach ($account in @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators', $ServiceUser)) {
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $account, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }

    Set-Acl -Path $DataPath -AclObject $acl

    # Anyone who can read both the key ring and the configuration file can recover every
    # credential, so confirm afterwards that nothing else was granted access.
    Write-Host 'Effective permissions on the data folder:'
    (Get-Acl $DataPath).Access |
        Select-Object IdentityReference, FileSystemRights, IsInherited |
        Format-Table -AutoSize

    # The event log source must exist before the service writes to it.
    if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
        Write-Host "Creating the event log source $ServiceName."
        New-EventLog -LogName Application -Source $ServiceName
    }

    # http.sys needs a URL reservation when the service does not run as SYSTEM.
    Write-Host "Reserving http://+:$Port/ for $ServiceUser."
    netsh http add urlacl url="http://+:$Port/" user="$ServiceUser" | Out-Null

    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host "$ServiceName already exists, updating its binary path."
        sc.exe config $ServiceName binPath= "`"$exePath`"" | Out-Null
    }
    else {
        Write-Host "Creating the service $ServiceName."
        sc.exe create $ServiceName binPath= "`"$exePath`"" DisplayName= "$DisplayName" start= auto obj= "$ServiceUser" | Out-Null
        sc.exe description $ServiceName 'Runs saved REST queries and exports the results to file.' | Out-Null
    }

    # Restart on failure rather than sitting dead until someone notices.
    sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null

    Write-Host "Adding a firewall rule for port $Port (local subnet only)."
    if (-not (Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $DisplayName -Direction Inbound -Protocol TCP `
            -LocalPort $Port -Action Allow -Profile Domain,Private -RemoteAddress LocalSubnet | Out-Null
    }
}

if ($Start -or $Restart) {
    Write-Host "Starting $ServiceName."
    Restart-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    if ((Get-Service -Name $ServiceName).Status -ne 'Running') {
        Start-Service -Name $ServiceName
    }

    Start-Sleep -Seconds 2
    Get-Service -Name $ServiceName | Format-Table -AutoSize
    Write-Host "Open http://localhost:$Port/"
}
