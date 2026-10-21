param([string]$ProductDirectory = 'build/e2e252/dist', [string]$OutputDirectory = 'tests/artifacts/e2e-2.5.2/peripherals-ui')
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$productPath = if ([IO.Path]::IsPathRooted($ProductDirectory)) { $ProductDirectory } else { Join-Path $projectPath $ProductDirectory }
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectPath $OutputDirectory }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Web.Extensions.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll','WPF/UIAutomationProvider.dll','WPF/UIAutomationTypes.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += '/reference:' + (Join-Path $productPath 'ADAMO-GT.exe')
$runner = Join-Path $outputPath 'PeripheralUiE2E.exe'
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /platform:x64 /utf8output ('/out:' + $runner) @references (Join-Path $PSScriptRoot 'PeripheralUiE2E.cs')
if ($LASTEXITCODE -ne 0) { throw 'Peripheral UI E2E compilation failed.' }
Copy-Item -LiteralPath (Join-Path $productPath 'ADAMO-GT.exe') -Destination $outputPath -Force
Copy-Item -LiteralPath (Join-Path $productPath 'ADAMO-GT.exe.config') -Destination ($runner + '.config') -Force
Get-ChildItem -LiteralPath $productPath -Filter '*.dll' | Copy-Item -Destination $outputPath -Force
# Only the owned test windows receive targeted HWND keyboard messages; no global
# keyboard injection. No real camera, microphone, speaker, SSD or GPU is started.
$probe = Start-Process -FilePath $runner -ArgumentList @(('"' + $outputPath + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $outputPath 'results.txt') -RedirectStandardError (Join-Path $outputPath 'errors.txt')
$ownedHandle = $probe.Handle
if (!$probe.WaitForExit(90000)) { Stop-Process -Id $probe.Id; throw 'Peripheral UI E2E exceeded 90 seconds; owned process terminated.' }
$probe.WaitForExit()
Get-Content -LiteralPath (Join-Path $outputPath 'results.txt') -Encoding UTF8
if ($probe.ExitCode -ne 0) { throw ('Peripheral UI E2E failed; inspect ' + $outputPath) }
