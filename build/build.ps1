# Builds a release: runs the tests, publishes the self-contained exe, then makes the MSI and a portable zip in dist\.
# Previous dist\ contents are moved to build\archive\<time>\ first (never deleted).
#
#   powershell -ExecutionPolicy Bypass -File build\build.ps1            # full build
#   powershell -ExecutionPolicy Bypass -File build\build.ps1 -SkipTests
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\.."
Set-Location $repo
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

[xml]$props = Get-Content "$repo\Directory.Build.props"
$version = $props.Project.PropertyGroup.Version
$dist = "$repo\dist"
$out = "$repo\build\out"
Write-Host "Foretop $version"

function Step($name, [scriptblock]$body) {
    Write-Host "== $name"
    & $body
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit $LASTEXITCODE)" }
}

# Keep earlier builds: move them aside instead of deleting.
$stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
foreach ($old in @($dist, $out)) {
    if ((Test-Path $old) -and (Get-ChildItem $old -Force | Select-Object -First 1)) {
        $target = "$repo\build\archive\$stamp\$(Split-Path $old -Leaf)"
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Move-Item $old $target
        Write-Host "   previous $(Split-Path $old -Leaf) moved to build\archive\$stamp"
    }
}
New-Item -ItemType Directory -Force $dist, "$out\publish" | Out-Null

if (-not $SkipTests) {
    Step 'Tests' { dotnet test "$repo\tests\Foretop.Tests" -c Release --nologo -v q }
}

Step 'Publish (self-contained, single file)' {
    dotnet publish "$repo\src\Foretop.App" -c Release -o "$out\publish" --nologo -v q
}
# Only the exe ships; pdbs are embedded or not needed.
Get-ChildItem "$out\publish" -File | Where-Object { $_.Name -ne 'Foretop.exe' } | ForEach-Object {
    New-Item -ItemType Directory -Force "$out\symbols" | Out-Null
    Move-Item $_.FullName "$out\symbols\" -Force
}

Step 'MSI' {
    dotnet build "$repo\installer\Foretop.Installer.wixproj" -c Release --nologo -v q `
        -p:PublishDir="$out\publish\" -p:OutputPath="$out\msi\"
}
Copy-Item "$out\msi\Foretop-$version-x64.msi" $dist

Write-Host '== Portable zip'
$portable = "$out\portable\Foretop"
New-Item -ItemType Directory -Force $portable | Out-Null
Copy-Item "$out\publish\Foretop.exe" $portable
@"
Foretop $version - World of Warships battle monitor

Run Foretop.exe. No installation and no .NET install needed.
Settings, cache, history and logs are stored in %LOCALAPPDATA%\Foretop.
User guide: docs/USER_GUIDE.md in the source repository.

Foretop is not affiliated with or endorsed by Wargaming.
"@ | Set-Content -Encoding utf8 "$portable\README.txt"
Compress-Archive -Path $portable -DestinationPath "$dist\Foretop-$version-portable-x64.zip" -CompressionLevel Optimal

Write-Host '== Result'
Get-ChildItem $dist | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    "{0}  {1,8:N1} MB  sha256 {2}" -f $_.Name, ($_.Length / 1MB), $hash
} | Tee-Object -FilePath "$dist\SHA256SUMS.txt"
