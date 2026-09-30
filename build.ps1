param([switch]$SkipTests, [string]$DotnetPath)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = if ($DotnetPath) { $DotnetPath } else { Join-Path $root '.dotnet-sdk\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
if (-not $SkipTests) {
    & $dotnet run --project (Join-Path $root 'tests\ChatGptTimezoneLauncher.Tests') -c Release
    if ($LASTEXITCODE -ne 0) { throw '测试失败，已停止发布。' }
}
$output = Join-Path $root 'dist\win-x64'
& $dotnet publish (Join-Path $root 'src\ChatGptTimezoneLauncher\ChatGptTimezoneLauncher.csproj') -c Release -r win-x64 --self-contained true -o $output `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
& (Join-Path $root 'tests\Test-PublishedExe.ps1') -ExePath (Join-Path $output 'ChatGPT时区启动器.exe')
Get-Item -LiteralPath (Join-Path $output 'ChatGPT时区启动器.exe') | Select-Object FullName,Length,LastWriteTime
