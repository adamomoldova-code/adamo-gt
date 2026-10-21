param([switch]$MemoryOnly, [switch]$BatteryOnly, [switch]$PortsOnly, [switch]$StartupOnly, [switch]$PeripheralsOnly, [switch]$CrmOnly)
$ErrorActionPreference = 'Stop'
$projectPath = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$testPath = Join-Path $projectPath 'tests/bin'
New-Item -ItemType Directory -Path $testPath -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Security.dll','System.Management.dll','System.Xaml.dll','System.Xml.dll','System.Web.Extensions.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += '/reference:' + (Join-Path $projectPath 'vendor/LibreHardwareMonitor-0.9.6/LibreHardwareMonitorLib.dll')
$sources = Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -Filter '*.cs' | ForEach-Object FullName
$sources += Get-ChildItem -LiteralPath (Join-Path $projectPath 'tests') -Filter '*.cs' | ForEach-Object FullName
$catalogResources = @('models.json','panels.json') | ForEach-Object { '/resource:' + (Join-Path $projectPath ('data/' + $_)) + ',PcPulse.Data.' + $_ }
$catalogResources += '/resource:' + (Join-Path $projectPath 'PRIVACY.md') + ',PcPulse.Privacy.md'
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /utf8output /main:PcPulseTests.SmokeTests ('/out:' + (Join-Path $testPath 'SmokeTests.exe')) ('/resource:' + (Join-Path $projectPath 'src/Dashboard.xaml') + ',Dashboard.xaml') ('/resource:' + (Join-Path $projectPath 'src/assets/adamo-logo-transparent.png') + ',PcPulse.BrandLogo.png') @catalogResources @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
Get-ChildItem -LiteralPath (Join-Path $projectPath 'dist') -Filter '*.dll' | Copy-Item -Destination $testPath
Copy-Item -LiteralPath (Join-Path $projectPath 'data') -Destination $testPath -Recurse -Force
$smartPath = Join-Path $testPath 'tools/smartmontools'
New-Item -ItemType Directory -Path $smartPath -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectPath 'vendor/smartmontools-7.5/runtime') -File | Copy-Item -Destination $smartPath -Force
$diskSpdPath = Join-Path $testPath 'tools/diskspd'
New-Item -ItemType Directory -Path $diskSpdPath -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectPath 'vendor/diskspd-2.3/runtime') -File | Copy-Item -Destination $diskSpdPath -Force
$testArguments = @((Join-Path $projectPath 'tests/artifacts'))
if ($MemoryOnly) { $testArguments += '--memory-only' }
if ($BatteryOnly) { $testArguments += '--battery-only' }
if ($PortsOnly) { $testArguments += '--ports-only' }
if ($StartupOnly) { $testArguments += '--startup-only' }
if ($PeripheralsOnly) { $testArguments += '--peripherals-only' }
if ($CrmOnly) { $testArguments += '--crm-only' }
& (Join-Path $testPath 'SmokeTests.exe') @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
