param(
    [Parameter(Mandatory)]
    [string] $InstallerPath
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Run this smoke test only on a disposable GitHub-hosted Windows runner.'
}

$keys = @(
    'HKCU:\Software\Classes\Directory\shell\FolderSize',
    'HKCU:\Software\Classes\Directory\Background\shell\FolderSize'
)
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'FolderSize.lnk'
foreach ($path in @($keys) + @($shortcut)) {
    if (Test-Path $path) { throw "Refusing to overwrite existing integration: $path" }
}

$installDir = Join-Path $env:RUNNER_TEMP 'FolderSize installer smoke test'
$exe = Join-Path $installDir 'FolderSize.exe'
$uninstaller = Join-Path $installDir 'unins000.exe'
try {
    $process = Start-Process (Resolve-Path $InstallerPath).Path -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$installDir`""
    ) -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installation failed: $($process.ExitCode)" }

    foreach ($file in @($exe, $uninstaller, $shortcut,
        (Join-Path $installDir 'coreclr.dll'),
        (Join-Path $installDir 'PresentationFramework.dll'),
        (Join-Path $installDir 'THIRD-PARTY-NOTICES.txt'))) {
        if (!(Test-Path $file)) { throw "Installed file missing: $file" }
    }

    $arguments = @('%1', '%V')
    for ($i = 0; $i -lt $keys.Count; $i++) {
        $key = Get-Item $keys[$i]
        if ($key.GetValue('') -ne 'Scan with FolderSize' -or
            $key.GetValue('Icon') -ne "$exe,0") {
            throw "Incorrect Explorer label or icon: $($keys[$i])"
        }
        $command = (Get-Item "$($keys[$i])\command").GetValue('')
        $expected = "`"$exe`" `"$($arguments[$i])\.`""
        if ($command -ne $expected) { throw "Incorrect Explorer command: $command" }
    }
    if ((Get-Item $keys[0]).GetValue('MultiSelectModel') -ne 'Single') {
        throw 'Folder integration must use single selection.'
    }
} finally {
    if (Test-Path $uninstaller) {
        $process = Start-Process $uninstaller -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
        ) -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Uninstallation failed: $($process.ExitCode)" }
    }
}

foreach ($path in @($keys) + @($shortcut, $exe)) {
    if (Test-Path $path) { throw "Uninstallation left behind: $path" }
}
Write-Host 'Installer registration and uninstall cleanup verified.'
