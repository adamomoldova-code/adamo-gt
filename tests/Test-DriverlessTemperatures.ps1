param([switch]$Live)
$ErrorActionPreference = 'Stop'
$temperatureProject = Split-Path -Parent $PSScriptRoot
$temperatureOutput = Join-Path $PSScriptRoot 'artifacts/driverless-temperature'
New-Item -ItemType Directory -Path $temperatureOutput -Force | Out-Null
$temperatureCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$temperatureSources = @('src/Models.cs', 'src/DriverlessTemperatureReader.cs', 'src/WindowsGpuTemperatureReader.cs', 'tests/DriverlessTemperatureTests.cs', 'tests/live/DriverlessTemperatureProbe.cs') | ForEach-Object { Join-Path $temperatureProject $_ }
$temperatureTests = Join-Path $temperatureOutput 'DriverlessTemperatureTests.exe'
& $temperatureCompiler /nologo /target:exe /platform:x64 /optimize+ /utf8output /r:System.Management.dll /r:System.Web.Extensions.dll /main:PcPulseTests.DriverlessTemperatureTests ('/out:' + $temperatureTests) @temperatureSources
if ($LASTEXITCODE -ne 0) { throw 'Driverless temperature test compilation failed.' }
& $temperatureTests | Tee-Object -FilePath (Join-Path $temperatureOutput 'tests.txt')
if ($LASTEXITCODE -ne 0) { throw 'Driverless temperature regression failed.' }
$temperatureProbe = Join-Path $temperatureOutput 'DriverlessTemperatureProbe.exe'
& $temperatureCompiler /nologo /target:exe /platform:x64 /optimize+ /utf8output /r:System.Management.dll /r:System.Web.Extensions.dll /main:PcPulseTests.DriverlessTemperatureProbe ('/out:' + $temperatureProbe) @temperatureSources
if ($LASTEXITCODE -ne 0) { throw 'Driverless temperature probe compilation failed.' }
if ($Live) {
    & $temperatureProbe (Join-Path $temperatureOutput 'live-probe.json')
    if ($LASTEXITCODE -ne 0) { throw 'Driverless temperature live probe failed.' }
}
