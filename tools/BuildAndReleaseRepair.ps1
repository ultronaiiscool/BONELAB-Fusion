param(
    [Parameter(Mandatory = $true)]
    [string]$BonelabDir,

    [string]$Tag = "v1.14.2-fork.2",

    [switch]$Publish
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$bonelab = (Resolve-Path $BonelabDir).Path
$il2cppDir = Join-Path $bonelab "MelonLoader\Il2CppAssemblies"
$melonNet6 = Join-Path $bonelab "MelonLoader\net6"

$required = @(
    (Join-Path $il2cppDir "Assembly-CSharp.dll"),
    (Join-Path $il2cppDir "Il2CppFacepunch.Steamworks.Win64.dll"),
    (Join-Path $il2cppDir "Il2CppSLZ.Marrow.dll"),
    (Join-Path $il2cppDir "UnityEngine.CoreModule.dll"),
    (Join-Path $melonNet6 "0Harmony.dll"),
    (Join-Path $melonNet6 "Newtonsoft.Json.dll")
)

$missing = @($required | Where-Object { -not (Test-Path $_) })
if ($missing.Count -gt 0) {
    Write-Error ("BONELAB/MelonLoader build references are missing:`n - " + ($missing -join "`n - "))
}

$env:BONELAB_DIR = $bonelab

Push-Location $repoRoot
try {
    Write-Host "Running lifecycle/token simulation harness..."
    dotnet run --project ".\tools\FusionSafetyHarness\FusionSafetyHarness.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Safety harness failed." }

    Write-Host "Building LabFusion.dll..."
    dotnet build ".\LabFusion\LabFusion.csproj" -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "LabFusion build failed." }

    $dll = Get-ChildItem ".\LabFusion\bin\Release" -Recurse -Filter "LabFusion.dll" |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if (-not $dll) { throw "LabFusion.dll was not produced." }

    $stage = Join-Path $repoRoot "artifacts\steam-token-repair"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    $stagedDll = Join-Path $stage "LabFusion.dll"
    Copy-Item $dll.FullName $stagedDll -Force

    $hash = (Get-FileHash $stagedDll -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashLine = "$hash  LabFusion.dll"
    Set-Content -Path (Join-Path $stage "SHA256SUMS.txt") -Value $hashLine -Encoding ascii

    Write-Host "Built: $stagedDll"
    Write-Host "SHA-256: $hash"

    if ($Publish) {
        if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
            throw "GitHub CLI (gh) is required for -Publish."
        }

        gh auth status | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "GitHub CLI is not authenticated." }

        $notes = @"
Fusion 1.14.2 Fork - Steam Lifecycle, Token, Stability and Latency Repair

Based on upstream BONELAB Fusion v1.14.2.

Changes:
- Integrates FusionModIOToken.txt token loading directly into Fusion; FusionTokenBridge.dll is no longer required.
- Avoids shutting down BONELAB's Steamworks client and reinitializing the game process under SteamVR App ID 250820.
- Uses Fusion's existing isolated proxy/Fusion Helper path when BONELAB owns the Steam client.
- Makes Login/Logout generation-aware, idempotent, cancellable, and terminal on Helper failure or timeout.
- Adds bounded, exactly-once Browse completion, stale-callback protection, and one-native-request gating.
- Requests proxy lobby metadata concurrently under one global timeout to reduce Browse latency.
- Validates Helper packet lengths/counts and rate-limits malformed-packet diagnostics.
- Keeps public Fusion APIs and packet formats intact for existing mods.

Installation:
1. Exit BONELAB.
2. Back up your existing Mods\LabFusion.dll.
3. Remove FusionTokenBridge.dll from Mods if installed.
4. Replace LabFusion.dll with the attached file.
5. Keep BoneLib and normal Fusion requirements installed.
6. Optionally place a token in BONELAB\UserData\FusionModIOToken.txt.
7. Install Fusion Helper v1.2.0 in BONELAB\Fusion Helper for the isolated desktop SteamVR route.
8. Launch BONELAB, press Log In, open Browse repeatedly, join, disconnect/reconnect, change scenes, and Browse again.

Rollback:
Exit BONELAB, remove the new DLL, restore the backed-up LabFusion.dll, and restore the previous Helper/network-layer configuration if it was changed.

Validation status:
The automated lifecycle/token simulation harness passes. Compilation was performed against the BONELAB installation supplied to this script. Native Steam stability must still be validated in a real BONELAB process; compilation and simulation cannot prove that steam_api64.dll will never fault.

SHA-256:
$hashLine
"@

        $existing = gh release view $Tag --repo "ultronaiiscool/BONELAB-Fusion" 2>$null
        if ($LASTEXITCODE -eq 0) {
            throw "Release tag $Tag already exists; refusing to overwrite it."
        }

        gh release create $Tag `
            $stagedDll `
            (Join-Path $stage "SHA256SUMS.txt") `
            --repo "ultronaiiscool/BONELAB-Fusion" `
            --target "main" `
            --title "Fusion 1.14.2 Fork - Steam Lifecycle, Token, Stability and Latency Repair" `
            --notes $notes

        if ($LASTEXITCODE -ne 0) { throw "GitHub release creation failed." }
        Write-Host "Published release $Tag."
    }
}
finally {
    Pop-Location
}
