param()
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path $PSScriptRoot -Parent
$scripts = @('scripts/Export-PublicSource.ps1', 'scripts/Test-SignedBinary.ps1', 'scripts/Test-SigningPreparation.ps1')
foreach ($relative in $scripts) {
    $parseErrors = $null
    $tokens = $null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $projectPath $relative), [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count) { throw ($relative + ': ' + ($parseErrors.Message -join '; ')) }
}
foreach ($relative in @('signing/inner-exe.xml', 'signing/wrapper-exe.xml')) {
    [xml]$configuration = Get-Content -LiteralPath (Join-Path $projectPath $relative) -Raw
    $manager = [Xml.XmlNamespaceManager]::new($configuration.NameTable)
    $manager.AddNamespace('s', 'http://signpath.io/artifact-configuration/v1')
    $files = $configuration.SelectNodes('/s:artifact-configuration/s:zip-file/s:pe-file', $manager)
    if ($files.Count -ne 1) { throw ($relative + ': exactly one owned executable must be signed.') }
    $file = $files[0]
    if ($file.GetAttribute('product-name') -cne 'ADAMO GT' -or $file.GetAttribute('product-version') -cne '${productVersion}' -or $file.GetAttribute('file-version') -cne '${productVersion}') {
        throw ($relative + ': product/version restrictions are required.')
    }
    $expected = if ($relative -eq 'signing/inner-exe.xml') { 'ADAMO-GT.exe' } else { 'ADAMO-GT-${releaseVersion}-Single.exe' }
    if ($file.GetAttribute('path') -cne $expected -or $file.SelectNodes('s:authenticode-sign', $manager).Count -ne 1) { throw ('Unexpected signing target: ' + $relative) }
}
$workflow = Get-Content -LiteralPath (Join-Path $projectPath '.github/workflows/windows-build-sign.yml') -Raw
if ($workflow -match '(?m)^\s+(pull_request_target|self-hosted|contents:\s*write|actions:\s*write)') { throw 'Signing workflow must use hosted runners with read-only GitHub permissions.' }
if ($workflow -notmatch 'default: false' -or $workflow -notmatch 'environment: signpath-release' -or $workflow -notmatch 'github.event.repository.default_branch') { throw 'Signing requires an explicit dispatch, protected environment and default branch.' }
foreach ($match in [regex]::Matches($workflow, '(?m)^\s+(?:- )?uses:\s*([^\r\n#]+)')) {
    if ($match.Groups[1].Value.Trim() -notmatch '@[a-f0-9]{40}$') { throw 'All workflow actions must be pinned to full commit hashes.' }
}
Write-Output 'Signing preparation: PowerShell parses, two scoped XML configurations and workflow safety checks passed. SignPath server validation and GitHub execution remain required.'
