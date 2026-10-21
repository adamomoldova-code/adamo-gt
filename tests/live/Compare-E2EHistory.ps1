param(
    [Parameter(Mandatory=$true)][string]$BaselinePrefix,
    [Parameter(Mandatory=$true)][string]$ReportFile,
    [Parameter(Mandatory=$true)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$disks = @(Get-Content -LiteralPath ($BaselinePrefix + '.benchmarks.json') -Raw | ConvertFrom-Json)
$gpus = @(Get-Content -LiteralPath ($BaselinePrefix + '.gpu.json') -Raw | ConvertFrom-Json)
$report = Get-Content -LiteralPath $ReportFile -Raw | ConvertFrom-Json
if ($report.ChecksRunning) { throw 'The exported report still has running checks.' }
if ($disks.Count -eq 0 -or $gpus.Count -eq 0) { throw 'This comparison requires successful baseline SSD and GPU results.' }
if ($report.DiskTests.Count -ne $disks.Count -or $report.GpuTests.Count -ne $gpus.Count) { throw 'Device result counts differ.' }
$checks = @()
foreach ($disk in $disks) {
    $matches = @($report.DiskTests | Where-Object { $_.DiskIndex -eq $disk.DiskIndex })
    if ($matches.Count -ne 1) { throw 'SSD result has no unique matching index.' }
    foreach ($field in @('ReadBytesPerSecond','WriteBytesPerSecond','ReadBytes','WriteBytes','TestedAtUtc')) {
        if ($null -eq $disk.$field -or $matches[0].$field -ne $disk.$field) { throw ('SSD field changed: ' + $field) }
        $checks += [pscustomobject]@{Kind='SSD';Id=$disk.DiskIndex;Field=$field;Equal=$true}
    }
    if (!$matches[0].FromHistory) { throw 'SSD result is not labelled as history.' }
    $checks += [pscustomobject]@{Kind='SSD';Id=$disk.DiskIndex;Field='FromHistory';Equal=$true}
}
foreach ($gpu in $gpus) {
    $matches = @($report.GpuTests | Where-Object { $_.AdapterIndex -eq $gpu.AdapterIndex })
    if ($matches.Count -ne 1) { throw 'GPU result has no unique matching adapter.' }
    foreach ($field in @('FramesPerSecond','FrameTimeMs','DurationSeconds','TestedAtUtc')) {
        if ($null -eq $gpu.$field -or $matches[0].$field -ne $gpu.$field) { throw ('GPU field changed: ' + $field) }
        $checks += [pscustomobject]@{Kind='GPU';Id=$gpu.AdapterIndex;Field=$field;Equal=$true}
    }
    if (!$matches[0].FromHistory -or !$matches[0].Completed) { throw 'GPU history/completion label was lost.' }
    foreach ($field in @('FromHistory','Completed')) { $checks += [pscustomobject]@{Kind='GPU';Id=$gpu.AdapterIndex;Field=$field;Equal=$true} }
}
$checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputFile -Encoding UTF8
Write-Output ('PASS ' + $checks.Count + ' exact result/timestamp/history comparisons after cancellation.')
