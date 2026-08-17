[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string] $InstallRoot,

    [Parameter(Mandatory)]
    [string] $DataRoot,

    [string] $RecoveryRoot = "$env:ProgramData\OokiGrader\uninstall-recovery",

    [string] $ServiceName = 'OokiGrader.Host',

    [string] $FirewallRuleName = 'Ooki Grader HTTPS',

    [Parameter(Mandatory)]
    [switch] $OfflineConfirmed,

    [switch] $InstallerManagedApplicationRemoval
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
Assert-OokiAdministrator
if (-not $OfflineConfirmed) {
    throw 'Uninstall requires explicit confirmation that teacher traffic is offline.'
}
$install = Assert-OokiInstallRoot -InstallRoot $InstallRoot
$data = Assert-OokiDataRoot -DataRoot $DataRoot
$recovery = Resolve-OokiExactPath -Path $RecoveryRoot `
    -Purpose 'Application recovery root'
Assert-OokiDisjointPaths -Paths @{
    'Install root' = $install
    'Data root' = $data
    'Recovery root' = $recovery
} | Out-Null
Assert-OokiServiceName -ServiceName $ServiceName | Out-Null
if (-not [IO.Path]::GetPathRoot($recovery).Equals(
    [IO.Path]::GetPathRoot($install),
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The recovery root must be on the same volume so uninstall can quarantine files atomically.'
}
$archiveName = 'OokiGrader-app-' +
    [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$archivePath = Join-Path $recovery $archiveName
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$existingFirewall = Get-NetFirewallRule -DisplayName $FirewallRuleName `
    -ErrorAction SilentlyContinue
if (-not [IO.Directory]::Exists($install) -and
    $null -eq $existingService -and
    $null -eq $existingFirewall) {
    [pscustomobject]@{
        state = 'already-uninstalled'
        dataRootPreserved = $true
        backupDataPreserved = $true
        certificateTrustPreserved = $true
        destructiveDataRemovalSupported = $false
    } | ConvertTo-Json -Depth 4
    return
}

if ($PSCmdlet.ShouldProcess(
    $ServiceName,
    'Uninstall service and quarantine application files while preserving all data')) {
    $service = $existingService
    if ($null -ne $service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force
            $service.WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(60))
        }
    }

    if ([IO.Directory]::Exists($install)) {
        [IO.Directory]::CreateDirectory($recovery) | Out-Null
        Invoke-OokiNative -FilePath "$env:SystemRoot\System32\icacls.exe" `
            -ArgumentList @(
                $recovery,
                '/inheritance:r',
                '/grant:r',
                '*S-1-5-18:(OI)(CI)F',
                '*S-1-5-32-544:(OI)(CI)F'
            )
        if ([IO.Directory]::Exists($archivePath)) {
            throw 'The generated recovery archive path already exists.'
        }
        if ($InstallerManagedApplicationRemoval) {
            [IO.Directory]::CreateDirectory($archivePath) | Out-Null
            Get-ChildItem -LiteralPath $install -Force | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $archivePath `
                    -Recurse -Force
            }
        } else {
            [IO.Directory]::Move($install, $archivePath)
        }
    } else {
        $archivePath = $null
    }

    if ($null -ne $service) {
        $service.Dispose()
        Invoke-OokiNative -FilePath "$env:SystemRoot\System32\sc.exe" `
            -ArgumentList @('delete', $ServiceName)
        $deleteDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        do {
            $remainingService = Get-Service -Name $ServiceName `
                -ErrorAction SilentlyContinue
            if ($null -eq $remainingService) {
                break
            }
            $remainingService.Dispose()
            Start-Sleep -Milliseconds 500
        } while ([DateTimeOffset]::UtcNow -lt $deleteDeadline)
        $remainingService = Get-Service -Name $ServiceName `
            -ErrorAction SilentlyContinue
        if ($null -ne $remainingService) {
            $remainingService.Dispose()
            throw 'The service is still marked for deletion after 30 seconds. Close Service Manager and other service handles, then reboot before reinstalling.'
        }
    }
    $firewall = $existingFirewall
    if ($null -ne $firewall) {
        $firewall | Remove-NetFirewallRule
    }

    [pscustomobject]@{
        state = 'uninstalled-recoverably'
        applicationArchive = $archivePath
        dataRootPreserved = $true
        backupDataPreserved = $true
        certificateTrustPreserved = $true
        destructiveDataRemovalSupported = $false
        nextAction = 'Retain the application archive until a technician verifies the preserved database and backups. Dispose of data only under the school record-retention process.'
    } | ConvertTo-Json -Depth 5
}
