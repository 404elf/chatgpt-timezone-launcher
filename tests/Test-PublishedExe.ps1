param([Parameter(Mandatory)][string]$ExePath)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $ExePath).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ChatGptTzSmoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
# Only the EXE is copied: no SDK, runtimeconfig, DLLs or developer build directory.
$isolatedExe = Join-Path $testRoot 'ChatGPT Launcher 独立测试.exe'
Copy-Item -LiteralPath $exe -Destination $isolatedExe
foreach ($scenario in @('fresh', 'corrupt')) {
    $directory = Join-Path $testRoot $scenario
    New-Item -ItemType Directory -Path $directory | Out-Null
    $config = Join-Path $directory 'settings.json'
    if ($scenario -eq 'corrupt') { [IO.File]::WriteAllText($config, '{broken') }
    $start = [Diagnostics.ProcessStartInfo]::new($isolatedExe)
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $directory
    $start.Arguments = '--self-test "' + $directory + '"'
    $start.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $testRoot 'no-installed-runtime'
    $start.EnvironmentVariables['DOTNET_ROOT_X64'] = Join-Path $testRoot 'no-installed-runtime'
    $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $testRoot 'extract'
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(45000)) {
        # This isolated test process never launches ChatGPT or saves user settings.
        $process.Kill()
        throw "EXE startup timed out ($scenario); artifacts: $testRoot"
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path (Join-Path $directory 'startup-ok.txt'))) {
        throw "EXE startup failed ($scenario), exit $($process.ExitCode); artifacts: $testRoot"
    }
    if ($scenario -eq 'corrupt' -and [IO.File]::ReadAllText($config) -ne '{broken') {
        throw 'Startup changed the corrupt configuration.'
    }
    if ($scenario -eq 'fresh' -and (Test-Path $config)) { throw 'Startup unexpectedly saved configuration.' }
    Write-Output "PASS published EXE: $scenario"
}
Write-Output "Startup test artifacts: $testRoot"
