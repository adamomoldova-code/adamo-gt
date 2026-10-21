param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectPath = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$exportRoot = Join-Path $projectPath 'build/public-source'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $exportRoot ([Guid]::NewGuid().ToString('N')) }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (!$destination.StartsWith($exportRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The export must be a new directory inside build/public-source.'
}
if (Test-Path -LiteralPath $destination) { throw 'Export destination already exists; choose a new directory.' }

# This is an allowlist, not a copy of the workspace. Never add reports or artifacts.
$files = [Collections.Generic.List[IO.FileInfo]]::new()
function Add-SourceFile([string]$relative, [bool]$optional = $false) {
    $path = Join-Path $projectPath $relative
    if ($optional -and !(Test-Path -LiteralPath $path -PathType Leaf)) { return }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.PSIsContainer) { throw ('Expected a file: ' + $relative) }
    $files.Add($item)
}
foreach ($relative in @('build.ps1', 'test.ps1', 'build-single-file.ps1', 'test-single-file.ps1', 'package.ps1', 'COMPATIBILITY.md', 'SINGLE-FILE.md')) {
    Add-SourceFile $relative
}
foreach ($relative in @('README.md', 'LICENSE', 'LICENSE.md', 'LICENSE.txt', 'PRIVACY.md', 'CODE-SIGNING.md', 'THIRD-PARTY-NOTICES.md', '.gitignore')) {
    Add-SourceFile $relative $true
}
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -File) {
    if ($item.Extension -in '.cs', '.xaml', '.manifest', '.config') { $files.Add($item) }
}
foreach ($relative in @('src/assets/adamo-logo-transparent.png', 'src/assets/adamo-app-icon.png', 'data/models.json', 'data/panels.json')) {
    Add-SourceFile $relative
}
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $projectPath 'tests') -Filter '*.cs' -File) { $files.Add($item) }
Add-SourceFile 'tests/Test-ResolutionMatrix.ps1'
Add-SourceFile 'tests/Test-DriverlessTemperatures.ps1' $true
foreach ($folder in @('launcher', 'tests/launcher', 'tests/resolution', 'tests/live')) {
    foreach ($item in Get-ChildItem -LiteralPath (Join-Path $projectPath $folder) -File) {
        if ($item.Extension -in '.cs', '.ps1', '.md', '.xml', '.yml', '.yaml') { $files.Add($item) }
    }
}
foreach ($relative in @('scripts/Export-PublicSource.ps1', 'scripts/Test-SignedBinary.ps1', 'scripts/Test-SigningPreparation.ps1', 'signing/README.md', 'signing/inner-exe.xml', 'signing/wrapper-exe.xml', '.github/workflows/windows-build-sign.yml')) {
    Add-SourceFile $relative
}
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $projectPath 'vendor') -File) {
    if ($item.Extension -in '.txt', '.json', '.zip', '.nupkg' -and $item.Name -ne 'LibreHardwareMonitor-0.9.6.zip') { $files.Add($item) }
}
foreach ($folder in @('vendor/LibreHardwareMonitor-0.9.6', 'vendor/smartmontools-7.5/runtime', 'vendor/diskspd-2.3/runtime')) {
    foreach ($item in Get-ChildItem -LiteralPath (Join-Path $projectPath $folder) -File) {
        if ($folder -ne 'vendor/LibreHardwareMonitor-0.9.6' -or ($item.Extension -in '.dll', '.xml' -and $item.Name -notmatch '^(Aga\.|OxyPlot|Microsoft\.Win32\.TaskScheduler)')) { $files.Add($item) }
    }
}
foreach ($relative in @('vendor/diskspd-2.3/diskspd-v2.3-source.zip', 'vendor/diskspd-2.3/BuildOverrides.props', 'vendor/diskspd-2.3/build-engine.ps1')) {
    Add-SourceFile $relative
}

# Reject links in both source and output ancestry before any copy.
function Assert-NoLinks([string]$path) {
    $current = $path
    while ($current -and $current.StartsWith($projectPath, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('Filesystem links are not allowed in public exports: ' + $current)
            }
        }
        if ($current -eq $projectPath) { break }
        $current = Split-Path $current -Parent
    }
}
Assert-NoLinks $destination
$selected = @($files | Sort-Object FullName -Unique)
foreach ($file in $selected) { Assert-NoLinks $file.FullName }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$manifest = foreach ($file in $selected) {
    $relative = $file.FullName.Substring($projectPath.Length + 1).Replace('\', '/')
    $target = Join-Path $destination $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
    [pscustomobject]@{ Path = $relative; Length = $file.Length; Sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $destination 'SOURCE-MANIFEST.json') -Encoding UTF8
[pscustomobject]@{ Directory = $destination; FileCount = $selected.Count; HasLicense = [bool]($selected | Where-Object { $_.DirectoryName -eq $projectPath -and $_.Name -in 'LICENSE', 'LICENSE.md', 'LICENSE.txt' }) }
