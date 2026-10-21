param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'releases'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectPath = $PSScriptRoot
$distPath = Join-Path $projectPath 'dist'
$programPath = Join-Path $distPath 'ADAMO-GT.exe'
$version = ([Version](Get-Item -LiteralPath $programPath).VersionInfo.FileVersion).ToString(3)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$portableZip = Join-Path $outputPath ('ADAMO-GT-' + $version + '-Windows-x64.zip')
$sourceZip = Join-Path $outputPath ('ADAMO-GT-' + $version + '-source.zip')
if ((Test-Path -LiteralPath $portableZip) -or (Test-Path -LiteralPath $sourceZip)) {
    throw 'Release archives already exist. Choose another output directory to preserve them.'
}
foreach ($required in @('ADAMO-GT.exe.config', 'tools/diskspd/diskspd.exe', 'tools/smartmontools/smartctl.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $distPath $required) -PathType Leaf)) { throw ('Missing runtime component: ' + $required) }
}
[IO.Compression.ZipFile]::CreateFromDirectory($distPath, $portableZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    $sourceFiles = @('build.ps1', 'test.ps1', 'package.ps1', 'build-single-file.ps1', 'test-single-file.ps1', 'COMPATIBILITY.md', 'SINGLE-FILE.md') | ForEach-Object { Get-Item -LiteralPath (Join-Path $projectPath $_) }
    foreach ($document in @('README.md', 'PRIVACY.md', 'CODE-SIGNING.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE')) {
        $documentPath = Join-Path $projectPath $document
        if (Test-Path -LiteralPath $documentPath -PathType Leaf) { $sourceFiles += Get-Item -LiteralPath $documentPath }
    }
    foreach ($folder in @('src', 'data', 'launcher', 'tests/launcher', 'tests/resolution', 'tests/live')) { $sourceFiles += Get-ChildItem -LiteralPath (Join-Path $projectPath $folder) -Recurse -File }
    $sourceFiles += Get-Item -LiteralPath (Join-Path $projectPath 'tests/Test-ResolutionMatrix.ps1')
    $sourceFiles += Get-ChildItem -LiteralPath (Join-Path $projectPath 'tests') -File -Filter '*.cs'
    $sourceFiles += Get-ChildItem -LiteralPath (Join-Path $projectPath 'vendor') -File
    foreach ($dependency in @('LibreHardwareMonitor-0.9.6', 'smartmontools-7.5', 'diskspd-2.3/runtime')) {
        $sourceFiles += Get-ChildItem -LiteralPath (Join-Path $projectPath ('vendor/' + $dependency)) -Recurse -File
    }
    foreach ($file in @('diskspd-v2.3-source.zip', 'BuildOverrides.props', 'build-engine.ps1')) {
        $path = Join-Path $projectPath ('vendor/diskspd-2.3/' + $file)
        if (Test-Path -LiteralPath $path -PathType Leaf) { $sourceFiles += Get-Item -LiteralPath $path }
    }
    foreach ($file in $sourceFiles) {
        $relative = $file.FullName.Substring($projectPath.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Get-Item -LiteralPath $portableZip, $sourceZip | Select-Object Name, Length, FullName
