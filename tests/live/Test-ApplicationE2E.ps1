param([Parameter(Mandatory=$true)][string]$ProductDirectory, [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$productPath = [IO.Path]::GetFullPath($ProductDirectory)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $outputPath 'results.json')) { throw 'Choose a fresh output directory.' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$references = @('System.dll','System.Core.dll','System.Web.Extensions.dll','WPF/WindowsBase.dll','WPF/UIAutomationClient.dll','WPF/UIAutomationTypes.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$runner = Join-Path $outputPath 'ApplicationUiE2E.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /utf8output ('/out:' + $runner) @references (Join-Path $PSScriptRoot 'ApplicationUiE2E.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI E2E compilation failed.' }
$probe = Start-Process -FilePath $runner -ArgumentList @(('"' + (Join-Path $productPath 'ADAMO-GT.exe') + '"'), ('"' + $outputPath + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $outputPath 'results.txt') -RedirectStandardError (Join-Path $outputPath 'errors.txt')
$null = $probe.Handle
if (!$probe.WaitForExit(240000)) { Stop-Process -Id $probe.Id; throw 'UI E2E deadline exceeded; inspect owned product process before rerunning.' }
$probe.WaitForExit()
Get-Content -LiteralPath (Join-Path $outputPath 'results.txt')
if ($probe.ExitCode -ne 0) { throw 'Application UI E2E found failures; inspect output.' }
