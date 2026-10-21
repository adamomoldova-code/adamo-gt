param(
    [switch]$RunHardware,
    [string]$ProductDirectory = 'build/stress260/dist',
    [string]$OutputDirectory,
    [string[]]$Components = @('Cpu', 'Ram', 'Gpu', 'Ssd')
)
$ErrorActionPreference = 'Stop'
$componentSelection = @($Components | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Select-Object -Unique)
if ($componentSelection.Count -eq 0) { throw 'Choose at least one component.' }
foreach ($component in $componentSelection) {
    if (@('Cpu', 'Ram', 'Gpu', 'Ssd') -notcontains $component) { throw ('Unknown component: ' + $component + '. Choose Cpu, Ram, Gpu, Ssd.') }
}
$componentArgument = $componentSelection -join ','
$projectPath = [IO.Path]::GetFullPath((Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$productPath = if ([IO.Path]::IsPathRooted($ProductDirectory)) { [IO.Path]::GetFullPath($ProductDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectPath $ProductDirectory)) }
if (!$OutputDirectory) { $OutputDirectory = 'build/stress-hardware/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') }
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectPath $OutputDirectory)) }
if (!$outputPath.StartsWith($projectPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory must stay inside this workspace.' }
if (Test-Path -LiteralPath $outputPath) { throw 'Use a new output directory so previous evidence and running binaries are preserved.' }
$productExe = Join-Path $productPath 'ADAMO-GT.exe'
if (!(Test-Path -LiteralPath $productExe -PathType Leaf)) { throw 'ProductDirectory must contain the built ADAMO-GT.exe.' }
$expectedHash = (Get-FileHash -LiteralPath $productExe -Algorithm SHA256).Hash
$runtimePath = Join-Path $outputPath 'runtime'
New-Item -ItemType Directory -Path $runtimePath -Force | Out-Null
# The private runtime copy preserves the product bytes and its dependency/helper layout.
# Do not add the test harness to a release or modify the original product directory.
Get-ChildItem -LiteralPath $productPath -Force | Copy-Item -Destination $runtimePath -Recurse
if ((Get-FileHash -LiteralPath (Join-Path $runtimePath 'ADAMO-GT.exe') -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Runtime copy differs from the requested product.' }
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$references = @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += '/reference:' + $productExe
$runnerPath = Join-Path $runtimePath 'StressHardwareSmoke.exe'
$manifestPath = Join-Path $runtimePath 'StressHardwareSmoke.manifest'
@'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="AdamoGT.StressHardwareSmoke"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="requireAdministrator" uiAccess="false"/></requestedPrivileges></security></trustInfo>
</assembly>
'@ | Set-Content -LiteralPath $manifestPath -Encoding UTF8
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /utf8output ('/out:' + $runnerPath) ('/win32manifest:' + $manifestPath) @references (Join-Path $PSScriptRoot 'StressHardwareSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Hardware smoke harness compilation failed.' }
Copy-Item -LiteralPath (Join-Path $productPath 'ADAMO-GT.exe.config') -Destination ($runnerPath + '.config')
[ordered]@{ ProductPath = $productExe; ProductSha256 = $expectedHash.ToLowerInvariant(); RunnerPath = $runnerPath; CompiledOnly = !$RunHardware; Components = $componentSelection } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'harness-build.json') -Encoding UTF8
Write-Output ('Compiled against SHA256: ' + $expectedHash.ToLowerInvariant())
Write-Output ('Harness: ' + $runnerPath)
Write-Output ('Selected components: ' + $componentArgument)
if (!$RunHardware) { Write-Output 'Compile-only: no hardware workload started. Use -RunHardware with a new output directory after coordinating other tests.'; return }
# The harness requires elevation for the same temperature/volume access as the product.
# Logs are written by the harness because ShellExecute/RunAs cannot redirect console handles.
$process = Start-Process -FilePath $runnerPath -ArgumentList @('--run-hardware', ('"' + $outputPath + '"'), $componentArgument) -WorkingDirectory $runtimePath -WindowStyle Hidden -Verb RunAs -PassThru
$null = $process.Handle
$finished = $false
for ($attempt = 0; $attempt -lt 6; $attempt++) {
    if ($process.WaitForExit(30000)) { $finished = $true; break }
}
if (!$finished) {
    Stop-Process -Id $process.Id
    throw 'The owned hardware-smoke process exceeded 180 seconds and was terminated. Existing JSON is partial; do not claim completion or successful cleanup.'
}
$process.WaitForExit()
Get-Content -LiteralPath (Join-Path $outputPath 'stdout.txt')
Write-Output ('Evidence: ' + (Join-Path $outputPath 'stress-hardware-results.json'))
if ($process.ExitCode -ne 0) { throw ('Hardware smoke found an unexpected failure or safety stop; inspect ' + $outputPath) }
