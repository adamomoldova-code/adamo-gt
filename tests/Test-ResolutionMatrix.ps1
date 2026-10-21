param([string]$OutputDirectory = 'tests/artifacts/resolution-2.5.0')
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path $PSScriptRoot -Parent
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectPath $OutputDirectory }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$references = @('System.dll','System.Core.dll','System.Management.dll','System.Xaml.dll','System.Xml.dll','System.Web.Extensions.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += '/reference:' + (Join-Path $projectPath 'vendor/LibreHardwareMonitor-0.9.6/LibreHardwareMonitorLib.dll')
$sources = Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -Filter '*.cs' | ForEach-Object FullName
$sources += @('tests/DashboardLayoutTests.cs','tests/DashboardViewportTests.cs','tests/DialogViewportTests.cs','tests/TemperaturePresentationTests.cs','tests/HistoryPresentationTests.cs','tests/resolution/ResolutionMatrix.cs') | ForEach-Object { Join-Path $projectPath $_ }
$resources = @('models.json','panels.json') | ForEach-Object { '/resource:' + (Join-Path $projectPath ('data/' + $_)) + ',PcPulse.Data.' + $_ }
$resources += '/resource:' + (Join-Path $projectPath 'src/Dashboard.xaml') + ',Dashboard.xaml'
$resources += '/resource:' + (Join-Path $projectPath 'src/assets/adamo-logo-transparent.png') + ',PcPulse.BrandLogo.png'
$runner = Join-Path $outputPath 'ResolutionMatrix.exe'
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /utf8output /main:ResolutionMatrix ('/out:' + $runner) @resources @references @sources 2>&1 | Tee-Object -FilePath (Join-Path $outputPath 'compile.txt')
if ($LASTEXITCODE -ne 0) { throw 'Resolution harness compilation failed.' }
# These are existing managed runtime dependencies; no application build or hardware call occurs here.
Get-ChildItem -LiteralPath (Join-Path $projectPath 'dist') -Filter '*.dll' | Copy-Item -Destination $outputPath -Force
& $runner $outputPath 2>&1 | Tee-Object -FilePath (Join-Path $outputPath 'results.txt')
if ($LASTEXITCODE -ne 0) { throw 'Resolution matrix contains geometry failures; inspect matrix.json and results.txt.' }
