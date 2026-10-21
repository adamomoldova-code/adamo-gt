param(
    [string]$VisualStudioPath,
    [string]$AtlInclude,
    [string]$AtlLibrary,
    [string]$BuildDirectory,
    [string]$WindowsSdkVersion = '10.0.26100.0'
)

$ErrorActionPreference = 'Stop'
$diskSpdCommit = '5e7025bfc9d1364f185d4c30963d7ac79195435e'
$diskSpdArchiveHash = 'E47A390810467CE79777D7FE34878920394921F4BDD4C146C1C19119AB1356F3'
$diskSpdShippedHash = 'E0C1070A8481EE0C7D1C05DE4158F50019E75F20B42D1083E98870E52C02DEFF'
$diskSpdArchive = Join-Path $PSScriptRoot 'diskspd-v2.3-source.zip'
if ((Get-FileHash -LiteralPath $diskSpdArchive -Algorithm SHA256).Hash -ne $diskSpdArchiveHash) {
    throw 'DiskSpd source archive does not match the pinned upstream source.'
}
if (-not $VisualStudioPath) {
    $diskSpdVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $diskSpdVswhere)) { throw 'Install Visual Studio C++ x64 build tools and ATL before building.' }
    $VisualStudioPath = & $diskSpdVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $VisualStudioPath) { throw 'Visual Studio C++ x64 build tools were not found.' }
$diskSpdMsbuild = Join-Path $VisualStudioPath 'MSBuild/Current/Bin/MSBuild.exe'
$diskSpdVcTools = Join-Path $VisualStudioPath 'VC/Tools/MSVC/14.44.35207'
if (-not $AtlInclude) { $AtlInclude = Join-Path $diskSpdVcTools 'atlmfc/include' }
if (-not $AtlLibrary) { $AtlLibrary = Join-Path $diskSpdVcTools 'atlmfc/lib/x64' }
if (-not (Test-Path -LiteralPath (Join-Path $AtlInclude 'atlbase.h'))) { throw 'ATL headers are required. Pass -AtlInclude or install the Visual Studio ATL C++ component.' }
if (-not (Test-Path -LiteralPath (Join-Path $AtlLibrary 'atls.lib'))) { throw 'ATL x64 libraries are required. Pass -AtlLibrary or install the Visual Studio ATL C++ component.' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $PSScriptRoot 'build' }
$diskSpdBuildRoot = [IO.Path]::GetFullPath($BuildDirectory)
if (Test-Path -LiteralPath $diskSpdBuildRoot) { throw 'Use a new, empty build directory. Existing files are never replaced or removed by this script.' }
New-Item -ItemType Directory -Path $diskSpdBuildRoot | Out-Null
Expand-Archive -LiteralPath $diskSpdArchive -DestinationPath $diskSpdBuildRoot
$diskSpdSource = Join-Path $diskSpdBuildRoot ('diskspd-' + $diskSpdCommit)
$diskSpdArguments = @(
    (Join-Path $diskSpdSource 'diskspd_vs/diskspd.sln'),
    '/nologo', '/m:2', '/t:CmdRequestCreator', '/p:Configuration=Release', '/p:Platform=x64',
    '/p:PlatformToolset=v143', '/p:VCToolsVersion=14.44.35207',
    ('/p:WindowsTargetPlatformVersion=' + $WindowsSdkVersion),
    ('/p:ForceImportBeforeCppTargets=' + (Join-Path $PSScriptRoot 'BuildOverrides.props')),
    ('/p:DiskSpdAtlInclude=' + [IO.Path]::GetFullPath($AtlInclude)),
    ('/p:DiskSpdAtlLibrary=' + [IO.Path]::GetFullPath($AtlLibrary)),
    '/verbosity:minimal',
    ('/flp:logfile=' + (Join-Path $diskSpdBuildRoot 'build.log') + ';verbosity=normal')
)
& $diskSpdMsbuild @diskSpdArguments
if ($LASTEXITCODE -ne 0) { throw 'DiskSpd MIT source build failed.' }
$diskSpdOutput = Join-Path $diskSpdSource 'diskspd_vs/Release/x64/diskspd.exe'
$diskSpdResultHash = (Get-FileHash -LiteralPath $diskSpdOutput -Algorithm SHA256).Hash
[pscustomobject]@{
    Executable = $diskSpdOutput
    Sha256 = $diskSpdResultHash
    MatchesDistributedEngine = $diskSpdResultHash -eq $diskSpdShippedHash
    License = 'MIT'
    SourceCommit = $diskSpdCommit
}
if ($diskSpdResultHash -ne $diskSpdShippedHash) {
    Write-Warning 'Compiler/build path differences can change the binary. Review the build and update EngineSha256 plus SOURCE.json before replacing the bundled engine. This script does not replace runtime files or run benchmarks.'
}
