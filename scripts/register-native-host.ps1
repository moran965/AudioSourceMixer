$ErrorActionPreference = 'Stop'
$hostName = 'com.audiosourcemixer.bridge'
$applicationRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$hostPath = Join-Path $applicationRoot 'AudioSourceMixer.NativeHost.exe'
$manifestPath = Join-Path $applicationRoot 'native-host-manifest.generated.json'
$trustedIdsPath = Join-Path $applicationRoot 'browser-extension-origins.json'
if (-not (Test-Path -LiteralPath $hostPath)) { throw "Native host not found: $hostPath" }
if (-not (Test-Path -LiteralPath $trustedIdsPath)) { throw "Trusted extension configuration not found: $trustedIdsPath" }
$trusted = Get-Content -LiteralPath $trustedIdsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$ids = @($trusted.developmentExtensionId, $trusted.chromeStoreExtensionId, $trusted.edgeStoreExtensionId) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
if ($ids.Count -eq 0 -or @($ids | Where-Object { $_ -notmatch '^[a-p]{32}$' }).Count -ne 0) {
    throw 'Trusted extension configuration contains a missing or invalid extension ID.'
}
$manifest = [ordered]@{
    name = $hostName
    description = 'Audio Source Mixer browser bridge'
    path = $hostPath
    type = 'stdio'
    allowed_origins = @($ids | ForEach-Object { "chrome-extension://$_/" })
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
Write-Output "Registered Native Messaging Host for $($ids.Count) explicitly trusted extension ID(s)."
