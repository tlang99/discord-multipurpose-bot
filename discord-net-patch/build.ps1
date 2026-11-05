# Builds the patched Discord.Net the bot uses, into discord-net-patch/packages. Run it once before the first build
# (and again after pulling a change to this folder); the build stops with a message saying so if it's missing.
#
# Why: Discord.Net 3.20.2 can't decrypt voice when the bot joins a channel with 2+ people in it (it rejects Discord's
# DAVE encryption-group invite, then gets disconnected): https://github.com/discord-net/Discord.Net/issues/3281.
# The fix is https://github.com/discord-net/Discord.Net/pull/3288, not released yet. This applies it (pr3288.patch,
# two small changes you can read) to Discord.Net's official 3.20.2 source, checked to be the exact release commit,
# and packs Core, Rest, WebSocket and Dave as 3.20.2.1, which every package that needs 3.20.2 accepts. Nothing
# prebuilt is committed. Delete this folder (and nuget.config, and the 3.20.2.1 references in ModBot.csproj and
# tools/VoiceTest) once a Discord.Net release includes the fix.
#
#   powershell -ExecutionPolicy Bypass -File discord-net-patch/build.ps1     (Windows)
#   pwsh -File discord-net-patch/build.ps1           (Linux/macOS)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$source = Join-Path $here 'vendor/Discord.Net'
$packages = Join-Path $here 'packages'
$version = '3.20.2.1'
# The commit Discord.Net's 3.20.2 release tag points to. A tag can be moved; a commit can't.
$releaseCommit = 'be2c8de62d27e38c4e886c209eb1fd9fe4fca1f4'

if (-not (Test-Path $source)) {
    git clone --quiet --depth 1 --branch 3.20.2 https://github.com/discord-net/Discord.Net.git $source
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed' }
}

$head = (git -C $source rev-parse HEAD).Trim()
if ($head -ne $releaseCommit) {
    throw "discord-net-patch/vendor/Discord.Net is at $head, not the 3.20.2 release ($releaseCommit). Delete the vendor folder and run this again."
}

$patch = Join-Path $here 'pr3288.patch'
git -C $source apply --reverse --check $patch 2>$null
if ($LASTEXITCODE -ne 0) {
    git -C $source apply $patch
    if ($LASTEXITCODE -ne 0) { throw 'The patch no longer applies.' }
}

foreach ($project in 'src/Discord.Net.Core', 'tools/Discord.Net.Dave', 'src/Discord.Net.Rest', 'src/Discord.Net.WebSocket') {
    dotnet pack (Join-Path $source $project) -c Release -p:Version=$version -p:IncludeSymbols=false -o $packages -v q
    if ($LASTEXITCODE -ne 0) { throw "Packing $project failed" }
}

Write-Host "Built Discord.Net $version into discord-net-patch/packages."
