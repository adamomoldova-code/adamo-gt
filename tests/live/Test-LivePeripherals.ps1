param([switch]$ActivateHardware, [string]$OutputDirectory = 'tests/artifacts/live-peripherals-2.5.1', [string]$ProductDirectory)
$ErrorActionPreference = 'Stop'
if (!$ActivateHardware) { throw 'Real camera/microphone/speaker activation requires -ActivateHardware.' }
$projectPath = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$productPath = if ($ProductDirectory) { [IO.Path]::GetFullPath($ProductDirectory) } else { Join-Path $projectPath 'dist' }
if (!(Test-Path -LiteralPath (Join-Path $productPath 'ADAMO-GT.exe') -PathType Leaf)) { throw 'ProductDirectory must contain a built ADAMO-GT.exe.' }
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectPath $OutputDirectory }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Web.Extensions.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += '/reference:' + (Join-Path $productPath 'ADAMO-GT.exe')
$runner = Join-Path $outputPath 'LivePeripherals.exe'
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /platform:x64 /utf8output ('/out:' + $runner) @references (Join-Path $PSScriptRoot 'LivePeripherals.cs') (Join-Path $PSScriptRoot 'CameraEvidence.cs')
if ($LASTEXITCODE -ne 0) { throw 'Live probe compilation failed.' }
Copy-Item -LiteralPath (Join-Path $productPath 'ADAMO-GT.exe') -Destination $outputPath -Force
Copy-Item -LiteralPath (Join-Path $productPath 'ADAMO-GT.exe.config') -Destination ($runner + '.config') -Force
Get-ChildItem -LiteralPath $productPath -Filter '*.dll' | Copy-Item -Destination $outputPath -Force
# The owned WPF test window remains transparent and does not take foreground focus.
$probe = Start-Process -FilePath $runner -ArgumentList @('--activate-hardware', ('"' + $outputPath + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $outputPath 'results.txt') -RedirectStandardError (Join-Path $outputPath 'errors.txt')
$null = $probe.Handle
if (!$probe.WaitForExit(120000)) { Stop-Process -Id $probe.Id; throw 'Live probe exceeded 120 seconds; owned process terminated.' }
$probe.WaitForExit()
Get-Content -LiteralPath (Join-Path $outputPath 'results.txt')
if ($probe.ExitCode -ne 0) { throw ('Live probe found a failure; inspect ' + $outputPath) }
