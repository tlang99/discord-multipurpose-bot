# Builds the patched Discord.Net the bot uses, into discord-net-patch/packages (committed, so a plain `dotnet build`
# works without running this).
#
# Why: Discord.Net 3.20.2 can't decrypt voice when the bot joins a channel with 2+ people in it (it rejects Discord's
# DAVE encryption-group invite, then gets disconnected): https://github.com/discord-net/Discord.Net/issues/3281.
# The fix is https://github.com/discord-net/Discord.Net/pull/3288, not released yet. This applies it (pr3288.patch) to
# the 3.20.2 release and packs Core, Rest, WebSocket and Dave as 3.20.2.1, which every package that needs 3.20.2
# accepts. Delete this folder (and nuget.config, and the 3.20.2.1 references in the .csproj files) once a Discord.Net
# release includes the fix.
#
#   powershell -File discord-net-patch/build.ps1

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$source = Join-Path $here 'vendor/Discord.Net'
$version = '3.20.2.1'

if (-not (Test-Path $source)) {
    git clone --quiet --depth 1 --branch 3.20.2 https://github.com/discord-net/Discord.Net.git $source
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed' }
}

$patch = Join-Path $here 'pr3288.patch'
git -C $source apply --reverse --check $patch 2>$null
if ($LASTEXITCODE -ne 0) {
    git -C $source apply $patch
    if ($LASTEXITCODE -ne 0) { throw 'The patch no longer applies.' }
}

foreach ($project in 'src/Discord.Net.Core', 'tools/Discord.Net.Dave', 'src/Discord.Net.Rest', 'src/Discord.Net.WebSocket') {
    dotnet pack (Join-Path $source $project) -c Release -p:Version=$version -p:IncludeSymbols=false -o (Join-Path $here 'packages') -v q
    if ($LASTEXITCODE -ne 0) { throw "Packing $project failed" }
}

Write-Host "Built Discord.Net $version into discord-net-patch/packages."
