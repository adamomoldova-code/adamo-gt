param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$ExpectedSubject,
    [Parameter(Mandatory = $true)][string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
$file = Get-Item -LiteralPath $Path
if ($file.VersionInfo.ProductName -cne 'ADAMO GT' -or $file.VersionInfo.ProductVersion -cne $ExpectedVersion -or $file.VersionInfo.FileVersion -cne $ExpectedVersion) {
    throw 'Signed binary product/version metadata does not match the requested build.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
if ($signature.Status -ne 'Valid') { throw ('Authenticode verification failed: ' + $signature.Status) }
if (!$signature.SignerCertificate -or $signature.SignerCertificate.Subject -cne $ExpectedSubject) {
    throw 'The signed artifact does not have the configured SignPath certificate subject.'
}
if (!$signature.TimeStamperCertificate) { throw 'The signed artifact is missing a trusted timestamp.' }
Write-Output ('Verified signature and metadata: ' + $file.Name)
