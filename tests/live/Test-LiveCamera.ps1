param(
    [switch]$ActivateCamera,
    [Parameter(Mandatory = $true)][string]$ProductDirectory,
    [string]$OutputDirectory = 'tests/artifacts/camera-2.6.1'
)
$ErrorActionPreference = 'Stop'
if (!$ActivateCamera) { throw 'Camera activation requires -ActivateCamera. No image or video is saved.' }
$projectPath = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$productPath = if ([IO.Path]::IsPathRooted($ProductDirectory)) { [IO.Path]::GetFullPath($ProductDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectPath $ProductDirectory)) }
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectPath $OutputDirectory)) }
if (!(Test-Path -LiteralPath (Join-Path $productPath 'ADAMO-GT.exe') -PathType Leaf)) { throw 'ProductDirectory must contain a built ADAMO-GT.exe.' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Web.Extensions.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += '/reference:' + (Join-Path $productPath 'ADAMO-GT.exe')
$runner = Join-Path $outputPath 'CameraLiveProbe.exe'
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /platform:x64 /utf8output ('/out:' + $runner) @references (Join-Path $PSScriptRoot 'CameraLiveProbe.cs') (Join-Path $PSScriptRoot 'CameraEvidence.cs')
if ($LASTEXITCODE -ne 0) { throw 'Camera probe compilation failed.' }
Copy-Item -LiteralPath (Join-Path $productPath 'ADAMO-GT.exe') -Destination $outputPath -Force
Copy-Item -LiteralPath (Join-Path $productPath 'ADAMO-GT.exe.config') -Destination ($runner + '.config') -Force
Get-ChildItem -LiteralPath $productPath -Filter '*.dll' | Copy-Item -Destination $outputPath -Force
$probe = Start-Process -FilePath $runner -ArgumentList @('--allow-camera', ('"' + (Join-Path $outputPath 'camera-results.json') + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $outputPath 'results.txt') -RedirectStandardError (Join-Path $outputPath 'errors.txt')
$ownedHandle = $probe.Handle
if (!$probe.WaitForExit(90000)) {
    Stop-Process -Id $probe.Id
    throw 'The owned camera probe exceeded 90 seconds and was terminated.'
}
$probe.WaitForExit()
Get-Content -LiteralPath (Join-Path $outputPath 'results.txt') -Encoding UTF8
if ($probe.ExitCode -ne 0) { throw ('Camera probe failed; inspect ' + $outputPath) }
