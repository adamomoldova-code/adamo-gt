param([string]$OutputDirectory = 'tests/artifacts/stress-ui')
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectPath $OutputDirectory)) }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$vendorPath = Join-Path $projectPath 'vendor/LibreHardwareMonitor-0.9.6'
$references = @('System.dll', 'System.Core.dll', 'System.Management.dll', 'System.Xaml.dll', 'System.Xml.dll', 'System.Web.Extensions.dll', 'WPF/WindowsBase.dll', 'WPF/PresentationCore.dll', 'WPF/PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += '/reference:' + (Join-Path $vendorPath 'LibreHardwareMonitorLib.dll')
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -Filter '*.cs' | ForEach-Object FullName)
$sources += Join-Path $projectPath 'tests/StressWindowTests.cs'
$sources += Join-Path $PSScriptRoot 'StressUiE2E.cs'
$catalogResources = @('models.json', 'panels.json') | ForEach-Object { '/resource:' + (Join-Path $projectPath ('data/' + $_)) + ',PcPulse.Data.' + $_ }
$runner = Join-Path $outputPath 'StressUiE2E.exe'
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /utf8output /main:StressUiE2E ('/out:' + $runner) ('/resource:' + (Join-Path $projectPath 'src/Dashboard.xaml') + ',Dashboard.xaml') ('/resource:' + (Join-Path $projectPath 'src/assets/adamo-logo-transparent.png') + ',PcPulse.BrandLogo.png') @catalogResources @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Stress UI integration compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectPath 'src/ADAMO-GT.exe.config') -Destination ($runner + '.config') -Force
Get-ChildItem -LiteralPath $vendorPath -Filter '*.dll' | Where-Object { $_.Name -notmatch '^(Aga\.|OxyPlot|Microsoft\.Win32\.TaskScheduler)' } | Copy-Item -Destination $outputPath -Force
# This runner injects controlled workloads. It does not run physical stress tests.
# The WPF fixture windows do not request activation or change the display settings.
$probe = Start-Process -FilePath $runner -ArgumentList @(('"' + $outputPath + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $outputPath 'results.txt') -RedirectStandardError (Join-Path $outputPath 'errors.txt')
$ownedHandle = $probe.Handle
if (!$probe.WaitForExit(90000)) { Stop-Process -Id $probe.Id; throw 'Stress UI integration exceeded 90 seconds; owned process terminated.' }
$probe.WaitForExit()
Get-Content -LiteralPath (Join-Path $outputPath 'results.txt') -Encoding UTF8
if ($probe.ExitCode -ne 0) { throw ('Stress UI integration failed; inspect ' + $outputPath) }
