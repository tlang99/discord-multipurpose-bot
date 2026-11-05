# Builds the patched Discord.Net the voice test uses, into tools/VoiceTest/packages (not committed).
#
# Why: Discord.Net 3.20.2 can't decrypt voice when the bot joins a channel with 2+ people in it (it rejects Discord's
# DAVE encryption group invite, then gets disconnected): https://github.com/discord-net/Discord.Net/issues/3281.
# The fix is https://github.com/discord-net/Discord.Net/pull/3288, not released yet. This applies it to the 3.20.2
# release (the version the bot uses) and packs it as 3.20.2-dave.1. Delete this once a release includes the fix.
#
#   powershell -File tools/VoiceTest/build-discord-net.ps1

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$vendor = Join-Path $here 'vendor'
$source = Join-Path $vendor 'Discord.Net'
$version = '3.20.2-dave.1'

if (-not (Test-Path $source)) {
    New-Item -ItemType Directory -Force $vendor | Out-Null
    git clone --quiet --depth 1 --branch 3.20.2 https://github.com/discord-net/Discord.Net.git $source
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed' }
}

# The fix, kept here so the build doesn't depend on the PR staying as it is.
$patch = Join-Path $here 'discord-net-pr3288.patch'
git -C $source apply --reverse --check $patch 2>$null
if ($LASTEXITCODE -ne 0) {
    git -C $source apply $patch
    if ($LASTEXITCODE -ne 0) { throw 'The patch no longer applies.' }
}

foreach ($project in 'src/Discord.Net.Core', 'tools/Discord.Net.Dave', 'src/Discord.Net.Rest', 'src/Discord.Net.WebSocket') {
    dotnet pack (Join-Path $source $project) -c Release -p:Version=$version -o (Join-Path $here 'packages') -v q
    if ($LASTEXITCODE -ne 0) { throw "Packing $project failed" }
}

Write-Host "Built Discord.Net $version into tools/VoiceTest/packages."
