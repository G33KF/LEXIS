# Builds release\LEXIS-<version>-win-x64.exe: one self-contained exe that carries ALEAPP and iLEAPP (no Python, no LEAPP folder needed).
#   .\build.ps1             PyInstaller builds of .\ALEAPP and .\ILEAPP, then the reader with both embedded
#   .\build.ps1 -SkipLeapp  reuse the last builds in ALEAPP\dist\ALEAPP and ILEAPP\dist\iLEAPP
# Each LEAPP needs its own .venv (python -m venv .venv inside its folder); packaging\build.py installs the rest.
param([Alias('SkipAleapp')][switch]$SkipLeapp)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$engine = Join-Path $root 'src\AleappReader.App\engine'
$env:PYTHONIOENCODING = 'utf-8'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force $engine | Out-Null

# Module list for the extraction window, made by the same script the reader runs on a source checkout.
$runner = Get-Content -Raw (Join-Path $root 'src\AleappReader.Core\ExtractionRunner.cs')
$script = [regex]::Match($runner, 'ListScript = """\r?\n(.*?)\r?\n\s*""";', 'Singleline').Groups[1].Value -replace '(?m)^ {8}', ''

foreach ($t in @(@{ Tool = 'aleapp'; Dir = 'ALEAPP'; Dist = 'ALEAPP' }, @{ Tool = 'ileapp'; Dir = 'ILEAPP'; Dist = 'iLEAPP' })) {
    $src = Join-Path $root $t.Dir
    $python = Join-Path $src '.venv\Scripts\python.exe'
    $dist = Join-Path $src "dist\$($t.Dist)"
    if (-not $SkipLeapp) {
        # From inside the LEAPP folder: iLEAPP's requirements name its wheels relative to it (whl_files/...).
        Push-Location $src
        try { & $python (Join-Path $src 'packaging\build.py') exe } finally { Pop-Location }
        if ($LASTEXITCODE) { throw "$($t.Dir) build failed ($LASTEXITCODE)" }
    }
    if (-not (Test-Path (Join-Path $dist "$($t.Tool).exe"))) { throw "No $($t.Dir) build in $dist" }

    $tmp = Join-Path $env:TEMP "$($t.Tool)_list_$PID.py"
    Set-Content -Encoding utf8 $tmp $script
    Push-Location $src
    try { $out = & $python $tmp } finally { Pop-Location; Remove-Item $tmp }
    $line = $out | Where-Object { $_ -like '@@PLUGINS@@*' } | Select-Object -Last 1
    if (-not $line) { throw "$($t.Dir) did not list its plugins" }
    [IO.File]::WriteAllText((Join-Path $dist 'plugins.json'), $line.Substring(11))

    $zip = Join-Path $engine "$($t.Tool)-engine.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    [IO.Compression.ZipFile]::CreateFromDirectory($dist, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
}

# The release is the exe alone (no .pdb, no reference .xml docs), named for its version, with its SHA-256.
$version = ([xml](Get-Content (Join-Path $root 'src\AleappReader.App\AleappReader.App.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$release = Join-Path $root 'release'
$publish = Join-Path $root 'obj\publish'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\AleappReader.App') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:AllowedReferenceRelatedFileExtensions=none `
    -o $publish
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
New-Item -ItemType Directory -Force $release | Out-Null
$exe = Join-Path $release "LEXIS-$version-win-x64.exe"
Move-Item -Force (Join-Path $publish 'AleappReader.exe') $exe
Remove-Item $publish -Recurse -Force
# LF only, so sha256sum -c reads it on any system
[IO.File]::WriteAllText("$exe.sha256", "$((Get-FileHash $exe -Algorithm SHA256).Hash.ToLower())  $(Split-Path $exe -Leaf)`n")
Get-Item $exe | Select-Object FullName, @{n='MB'; e={[math]::Round($_.Length / 1MB)}}
