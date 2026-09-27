# ZipPlaFork v18 build script.
# Usage: powershell -ExecutionPolicy Bypass -File Build-Fork.ps1 [-NoTest]
# Reads VERSION (e.g. v18.1), builds AutoBuild, packages
#   Desktop\coding\ZipPlaFork-<ver>\ + ZipPlaFork-<ver>.zip,
# then bumps VERSION to the next patch (v18.2) for the following build.
param([switch]$NoTest)

$ErrorActionPreference = 'Stop'
$forkRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionFile = Join-Path $forkRoot 'VERSION'
$ver = (Get-Content $versionFile -Raw).Trim()
if ($ver -notmatch '^v\d+\.\d+$') { throw "Bad VERSION content: '$ver' (expected like v18.1)" }

# Keep ForkVersion.cs in sync with VERSION before compiling.
$forkCs = Join-Path $forkRoot 'source\ZipPla\ForkVersion.cs'
$text = Get-Content $forkCs -Raw
$text = [regex]::Replace($text, 'public const string Current = "v\d+\.\d+"', 'public const string Current = "' + $ver + '"')
Set-Content $forkCs $text -Encoding UTF8

# Locate MSBuild (VS18 Community first, then vswhere fallback).
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path $msbuild)) {
  $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
  if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
  }
}
if (-not $msbuild -or -not (Test-Path $msbuild)) { throw 'MSBuild not found.' }
Write-Host "MSBuild: $msbuild"
Write-Host "Version: $ver"

$sln = Join-Path $forkRoot 'source\ZipPla.sln'
& $msbuild $sln /p:Configuration=AutoBuild /p:Platform='Any CPU' /verbosity:minimal /maxcpucount
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

$outDir = Join-Path $forkRoot 'source\ZipPla\bin\AutoBuild'
$exe = Join-Path $outDir 'ZipPla.exe'
if (-not (Test-Path $exe)) { throw "Build output missing: $exe" }

if (-not $NoTest) {
  Write-Host 'Running selftest...'
  $log = Join-Path $forkRoot ("ZipPlaFork-{0}-selftest.log" -f $ver.TrimStart('v').Replace('.', '-'))
  & $exe -selftest 2>&1 | Tee-Object -FilePath $log | Select-Object -Last 5
  if ($LASTEXITCODE -ne 0) { Write-Warning "selftest exit code: $LASTEXITCODE (see $log)" }
}

# Package: exe + dlls + language + fork docs. Excludes user runtime files
# (config.xml, History.sor, ffmpeg.exe) and bin/obj build intermediates.
$desk = [Environment]::GetFolderPath('Desktop')
$distName = "ZipPlaFork-$ver"
$distDir = Join-Path (Join-Path $desk 'coding') $distName
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Copy-Item (Join-Path $outDir 'ZipPla.exe') (Join-Path $distDir 'ZipPla.exe') -Force
Copy-Item (Join-Path $outDir 'ZipPla.exe.config') (Join-Path $distDir 'ZipPla.exe.config') -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $outDir 'Microsoft.Deployment.Compression.dll') $distDir -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $outDir 'Microsoft.Deployment.Compression.Cab.dll') $distDir -Force -ErrorAction SilentlyContinue
$srcLang = Join-Path $outDir 'language'
if (Test-Path $srcLang) {
  $dstLang = Join-Path $distDir 'language'
  New-Item -ItemType Directory -Force -Path $dstLang | Out-Null
  Copy-Item (Join-Path $srcLang '*') $dstLang -Recurse -Force
}
foreach ($doc in @('CHANGELOG.md', "ZIPPLA_FORK_V18.md")) {
  $p = Join-Path $forkRoot $doc
  if (Test-Path $p) { Copy-Item $p $distDir -Force }
}

$zipPath = "$distDir.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $distDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "Packaged: $distDir"
Write-Host "Zip: $zipPath ($zipMb MB)"

# Bump VERSION for the next build (v18.1 -> v18.2).
if ($ver -match '^v(\d+)\.(\d+)$') {
  $next = 'v{0}.{1}' -f $Matches[1], ([int]$Matches[2] + 1)
  Set-Content $versionFile "$next`n" -Encoding UTF8 -NoNewline:$false
  $text2 = Get-Content $forkCs -Raw
  $text2 = [regex]::Replace($text2, 'public const string Current = "v\d+\.\d+"', 'public const string Current = "' + $next + '"')
  Set-Content $forkCs $text2 -Encoding UTF8
  Write-Host "VERSION bumped to $next (working tree ready for next build)."
}
