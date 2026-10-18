# Builds the portable autodock bundle:
#   dist\autodock\autodock.exe       tray app (self-contained .NET 8, single file)
#   dist\autodock\engine\cswap.exe   claude-swap engine (PyInstaller onedir)
#   dist\autodock-win-x64.zip
# Then runs the Python suite and the C# suite (including the end-to-end engine test).
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$dist = Join-Path $repo 'dist'
$out = Join-Path $dist 'autodock'
$work = Join-Path $repo 'build\pyinstaller'
Push-Location $repo
try {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    Write-Host '== engine (PyInstaller)'
    uv sync --locked
    uv run --with 'pyinstaller==6.22.3' pyinstaller --noconfirm --clean --log-level WARN `
        --name cswap --console --onedir `
        --paths src --collect-submodules claude_swap --collect-data claude_swap --collect-data textual `
        --distpath (Join-Path $work 'dist') --workpath (Join-Path $work 'work') --specpath $work `
        src\claude_swap\__main__.py
    if ($LASTEXITCODE) { throw 'PyInstaller failed' }

    Write-Host '== tray app (dotnet publish)'
    dotnet publish dock\Autodock\Autodock.csproj -c Release -o $out --nologo
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }
    Remove-Item (Join-Path $out '*.pdb') -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $work 'dist\cswap') (Join-Path $out 'engine') -Recurse
    Copy-Item LICENSE (Join-Path $out 'LICENSE.txt')
    Copy-Item README.md (Join-Path $out 'README.md')

    $engine = Join-Path $out 'engine\cswap.exe'
    & $engine --version
    if ($LASTEXITCODE) { throw 'engine smoke test failed' }

    if (-not $SkipTests) {
        Write-Host '== tests (Python)'
        uv run pytest -q
        if ($LASTEXITCODE) { throw 'Python tests failed' }
        Write-Host '== tests (C#, with end-to-end engine test)'
        $env:AUTODOCK_TEST_ENGINE = $engine
        dotnet test dock\Autodock.Tests\Autodock.Tests.csproj --nologo
        if ($LASTEXITCODE) { throw 'C# tests failed' }
    }

    $zip = Join-Path $dist 'autodock-win-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $out -DestinationPath $zip
    Write-Host "== done: $out\autodock.exe  ($zip)"
}
finally { Pop-Location }
