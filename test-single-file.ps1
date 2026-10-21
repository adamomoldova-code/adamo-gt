param()
$ErrorActionPreference = 'Stop'
$projectPath = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$testPath = Join-Path $projectPath 'tests/single-file-bin'
New-Item -ItemType Directory -Path $testPath -Force | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.IO.Compression.dll', 'System.Windows.Forms.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /platform:anycpu /optimize+ /utf8output ('/out:' + (Join-Path $testPath 'ArgumentProbe.exe')) (Join-Path $projectPath 'tests/launcher/ArgumentProbe.cs') (Join-Path $projectPath 'src/SensorStartup.cs')
if ($LASTEXITCODE -ne 0) { throw 'Launcher argument probe compilation failed.' }
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /platform:anycpu /optimize+ /utf8output /main:AdamoGT.SingleFile.Tests.LauncherTests ('/out:' + (Join-Path $testPath 'LauncherTests.exe')) @references (Join-Path $projectPath 'launcher/SingleFileLauncher.cs') (Join-Path $projectPath 'src/SensorStartup.cs') (Join-Path $projectPath 'tests/launcher/LauncherTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Launcher test compilation failed.' }
& (Join-Path $testPath 'LauncherTests.exe') (Join-Path $testPath 'ArgumentProbe.exe') (Join-Path $projectPath 'tests/single-file-artifacts')
if ($LASTEXITCODE -ne 0) { throw 'Single-file launcher tests failed.' }
