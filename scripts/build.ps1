param(
    [ValidateSet('Debug','Release')][string] $Configuration = 'Release',
    [switch] $IncludeProbes
)
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot
$dotnet = Get-DotnetExecutable
Push-Location $root
try {
    Invoke-Checked { & $dotnet restore '.\AudioSourceMixer.sln' } 'Dependency restore'
    Invoke-Checked { & $dotnet build '.\AudioSourceMixer.sln' --configuration $Configuration --no-restore } 'Solution build'
    if ($IncludeProbes) {
        $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio vswhere.exe is required to build the native audio probes.' }
        $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
            Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($msbuild)) { throw 'Visual Studio MSBuild with the C++ toolchain was not found.' }
        Invoke-Checked {
            & $msbuild '.\tools\ProcessLoopbackProbe\ProcessLoopbackProbe.vcxproj' /t:Build "/p:Configuration=$Configuration;Platform=x64" /m
        } 'x64 process-loopback probe build'
    }
} finally { Pop-Location }
