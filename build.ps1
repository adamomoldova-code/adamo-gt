$ErrorActionPreference = 'Stop'
$projectPath = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$vendorPath = Join-Path $projectPath 'vendor/LibreHardwareMonitor-0.9.6'
$distPath = Join-Path $projectPath 'dist'
New-Item -ItemType Directory -Path $distPath -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $projectPath 'src/ADAMO-GT.ico'
$brandPath = Join-Path $projectPath 'src/assets/adamo-logo-transparent.png'
if (!(Test-Path -LiteralPath $brandPath -PathType Leaf)) { throw 'The original ADAMO app icon is missing.' }
$brandImage = [Drawing.Image]::FromFile($brandPath)
$iconSizes = @(16, 24, 32, 48, 64, 128, 256)
$iconFrames = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $iconSizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($brandImage, 0, 0, $size, $size)
    $iconPng = [IO.MemoryStream]::new()
    $bitmap.Save($iconPng, [Drawing.Imaging.ImageFormat]::Png)
    $iconFrames.Add($iconPng.ToArray())
    $iconPng.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$writer = [IO.BinaryWriter]::new([IO.File]::Create($iconPath))
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$iconSizes.Count)
$offset = 6 + 16 * $iconSizes.Count
for ($frame = 0; $frame -lt $iconSizes.Count; $frame++) {
    $dimension = if ($iconSizes[$frame] -eq 256) { 0 } else { $iconSizes[$frame] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$iconFrames[$frame].Length); $writer.Write([uint32]$offset)
    $offset += $iconFrames[$frame].Length
}
foreach ($frameBytes in $iconFrames) { $writer.Write($frameBytes) }
$writer.Dispose(); $brandImage.Dispose()
$references = @('System.dll','System.Core.dll','System.Security.dll','System.Management.dll','System.Xaml.dll','System.Xml.dll','System.Web.Extensions.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += '/reference:' + (Join-Path $vendorPath 'LibreHardwareMonitorLib.dll')
$sources = Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -Filter '*.cs' | ForEach-Object FullName
$catalogResources = @('models.json','panels.json') | ForEach-Object { '/resource:' + (Join-Path $projectPath ('data/' + $_)) + ',PcPulse.Data.' + $_ }
$catalogResources += '/resource:' + (Join-Path $projectPath 'PRIVACY.md') + ',PcPulse.Privacy.md'
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /utf8output ('/out:' + (Join-Path $distPath 'ADAMO-GT.exe')) ('/win32icon:' + $iconPath) ('/win32manifest:' + (Join-Path $projectPath 'src/app.manifest')) ('/resource:' + (Join-Path $projectPath 'src/Dashboard.xaml') + ',Dashboard.xaml') ('/resource:' + $brandPath + ',PcPulse.BrandLogo.png') @catalogResources @references @sources
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
Get-ChildItem -LiteralPath $vendorPath -Filter '*.dll' | Where-Object { $_.Name -notmatch '^(Aga\.|OxyPlot|Microsoft\.Win32\.TaskScheduler)' } | Copy-Item -Destination $distPath
Copy-Item -LiteralPath (Join-Path $projectPath 'src/ADAMO-GT.exe.config') -Destination $distPath
Copy-Item -LiteralPath (Join-Path $projectPath 'COMPATIBILITY.md') -Destination $distPath
Copy-Item -LiteralPath (Join-Path $projectPath 'SINGLE-FILE.md') -Destination $distPath
foreach ($document in @('README.md', 'PRIVACY.md', 'CODE-SIGNING.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE')) {
    $documentPath = Join-Path $projectPath $document
    if (Test-Path -LiteralPath $documentPath -PathType Leaf) { Copy-Item -LiteralPath $documentPath -Destination $distPath }
}
Copy-Item -LiteralPath (Join-Path $projectPath 'data') -Destination $distPath -Recurse -Force
$smartPath = Join-Path $distPath 'tools/smartmontools'
New-Item -ItemType Directory -Path $smartPath -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectPath 'vendor/smartmontools-7.5/runtime') -File | Copy-Item -Destination $smartPath -Force
$diskSpdPath = Join-Path $distPath 'tools/diskspd'
New-Item -ItemType Directory -Path $diskSpdPath -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectPath 'vendor/diskspd-2.3/runtime') -File | Copy-Item -Destination $diskSpdPath -Force
$licensesPath = Join-Path $distPath 'Licenses'
New-Item -ItemType Directory -Path $licensesPath -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectPath 'vendor') -File | Where-Object { $_.Extension -in '.txt','.json' } | Copy-Item -Destination $licensesPath
Copy-Item -LiteralPath (Join-Path $projectPath 'vendor/PawnIO.Modules-0.1.6-source.zip') -Destination $licensesPath
Write-Output ('Built: ' + (Join-Path $distPath 'ADAMO-GT.exe'))
