$ErrorActionPreference = 'Stop'
$hostName = 'com.audiosourcemixer.bridge'
$extensionId = 'edbfelppckjcfhadggldaifbleoofkio'
$applicationRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$hostPath = Join-Path $applicationRoot 'AudioSourceMixer.NativeHost.exe'
$manifestPath = Join-Path $applicationRoot 'native-host-manifest.generated.json'
if (-not (Test-Path -LiteralPath $hostPath)) { throw "Native host not found: $hostPath" }
$manifest = [ordered]@{
    name = $hostName
    description = 'Audio Source Mixer browser bridge'
    path = $hostPath
    type = 'stdio'
    allowed_origins = @("chrome-extension://$extensionId/")
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
$keys = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
)
foreach ($key in $keys) {
    New-Item -Path $key -Force | Out-Null
    Set-Item -Path $key -Value $manifestPath
}
Write-Output "Registered Native Messaging Host for extension $extensionId."
