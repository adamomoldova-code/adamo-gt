param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'releases'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectPath = $PSScriptRoot
$distPath = Join-Path $projectPath 'dist'
$programPath = Join-Path $distPath 'ADAMO-GT.exe'
foreach ($required in @('ADAMO-GT.exe', 'ADAMO-GT.exe.config', 'tools/diskspd/diskspd.exe', 'tools/smartmontools/smartctl.exe', 'tools/smartmontools/drivedb.h')) {
    if (!(Test-Path -LiteralPath (Join-Path $distPath $required) -PathType Leaf)) { throw ('Missing runtime component: ' + $required + '. Run build.ps1 first.') }
}
$version = [Version](Get-Item -LiteralPath $programPath).VersionInfo.FileVersion
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$destination = Join-Path $outputPath ('ADAMO-GT-' + $version.ToString(3) + '-Single.exe')
if (Test-Path -LiteralPath $destination) { throw ('Release already exists: ' + $destination) }
$staging = Join-Path $projectPath ('build/single-file/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$payloadPath = Join-Path $staging 'Payload.zip'
$manifestPath = Join-Path $staging 'Manifest.txt'
$metadataPath = Join-Path $staging 'AssemblyInfo.cs'
$launcherPath = Join-Path $staging 'ADAMO-GT-Single.exe'
$items = Get-ChildItem -LiteralPath $distPath -Recurse -Force
if ($items | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) { throw 'Runtime tree must not contain filesystem links.' }
$files = @($items | Where-Object { !$_.PSIsContainer } | Sort-Object FullName)
if ($files.Count -eq 0) { throw 'Runtime folder is empty.' }
$manifestEntries = [Collections.Generic.List[string]]::new()
$archive = [IO.Compression.ZipFile]::Open($payloadPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($distPath.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifestEntries.Add($relative + "`t" + $file.Length.ToString([Globalization.CultureInfo]::InvariantCulture) + "`t" + $hash)
    }
} finally { $archive.Dispose() }
$payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestLines = @('ADAMO-GT-PACKAGE-1', $payloadHash) + $manifestEntries.ToArray()
[IO.File]::WriteAllLines($manifestPath, $manifestLines, [Text.UTF8Encoding]::new($false))
$metadata = @"
using System.Reflection;
[assembly: AssemblyTitle("ADAMO GT")]
[assembly: AssemblyProduct("ADAMO GT")]
[assembly: AssemblyDescription("ADAMO GT — single-file portable launcher")]
[assembly: AssemblyVersion("$($version.ToString(4))")]
[assembly: AssemblyFileVersion("$($version.ToString(4))")]
"@
[IO.File]::WriteAllText($metadataPath, $metadata, [Text.UTF8Encoding]::new($false))
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$references = @('System.dll', 'System.Core.dll', 'System.IO.Compression.dll', 'System.Windows.Forms.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /main:AdamoGT.SingleFile.Launcher ('/out:' + $launcherPath) ('/win32icon:' + (Join-Path $projectPath 'src/ADAMO-GT.ico')) ('/win32manifest:' + (Join-Path $projectPath 'src/app.manifest')) ('/resource:' + $payloadPath + ',AdamoGT.Payload.zip') ('/resource:' + $manifestPath + ',AdamoGT.Manifest.txt') @references (Join-Path $projectPath 'launcher/SingleFileLauncher.cs') (Join-Path $projectPath 'src/SensorStartup.cs') $metadataPath
if ($LASTEXITCODE -ne 0) { throw 'Single-file launcher compilation failed.' }
[IO.File]::Copy($launcherPath, $destination, $false)
Get-Item -LiteralPath $destination | Select-Object Name, Length, FullName
