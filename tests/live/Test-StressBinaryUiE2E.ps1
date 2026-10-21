param(
    [string]$ProductDirectory = 'build/stress260-final/dist',
    [string]$OutputDirectory = 'tests/artifacts/stress-binary-ui',
    [string]$SnapshotPath = ''
)
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$productPath = if ([IO.Path]::IsPathRooted($ProductDirectory)) { [IO.Path]::GetFullPath($ProductDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectPath $ProductDirectory)) }
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectPath $OutputDirectory)) }
if ($productPath.TrimEnd('\') -eq $outputPath.TrimEnd('\')) { throw 'Choose an output directory separate from the product directory.' }
$productExe = Join-Path $productPath 'ADAMO-GT.exe'
if (!(Test-Path -LiteralPath $productExe -PathType Leaf)) { throw 'Build the product first; this harness never compiles product sources.' }
if ($SnapshotPath) { $SnapshotPath = [IO.Path]::GetFullPath($SnapshotPath); if (!(Test-Path -LiteralPath $SnapshotPath -PathType Leaf)) { throw 'Saved inventory file does not exist.' } }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$references = @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'WPF/WindowsBase.dll', 'WPF/PresentationCore.dll', 'WPF/PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += '/reference:' + $productExe
$runner = Join-Path $outputPath 'StressBinaryUiE2E.exe'
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /platform:x64 /utf8output ('/out:' + $runner) @references (Join-Path $PSScriptRoot 'StressBinaryUiE2E.cs')
if ($LASTEXITCODE -ne 0) { throw 'Built-product stress UI harness compilation failed.' }
Copy-Item -LiteralPath $productExe -Destination $outputPath -Force
Copy-Item -LiteralPath (Join-Path $productPath 'ADAMO-GT.exe.config') -Destination ($runner + '.config') -Force
Get-ChildItem -LiteralPath $productPath -Filter '*.dll' | Copy-Item -Destination $outputPath -Force
$originalHash = (Get-FileHash -LiteralPath $productExe -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath (Join-Path $outputPath 'ADAMO-GT.exe') -Algorithm SHA256).Hash -ne $originalHash) { throw 'Product copy differs from the requested built artifact.' }
$arguments = @(('"' + $outputPath + '"'))
if ($SnapshotPath) { $arguments += ('"' + $SnapshotPath + '"') }
# The runner never invokes Start and never calls Activate or injects global input.
$probe = Start-Process -FilePath $runner -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $outputPath 'results.txt') -RedirectStandardError (Join-Path $outputPath 'errors.txt')
$ownedHandle = $probe.Handle
if (!$probe.WaitForExit(60000)) { Stop-Process -Id $probe.Id; throw 'Built-product UI check exceeded 60 seconds; owned runner terminated.' }
$probe.WaitForExit()
Get-Content -LiteralPath (Join-Path $outputPath 'results.txt') -Encoding UTF8
if ($probe.ExitCode -ne 0) { throw ('Built-product stress UI check failed; inspect ' + $outputPath) }
$result = Get-Content -LiteralPath (Join-Path $outputPath 'stress-binary-ui-results.json') -Raw | ConvertFrom-Json
if ($result.ProductSha256 -ne $originalHash.ToLowerInvariant()) { throw 'Executed product hash differs from the requested artifact.' }
