# Downloads what voice commands need, into voice/natives and voice/models (neither is committed). Rebuild the bot
# afterwards so the libraries are copied next to it. See voice/README.md.
#
#   powershell -File voice/setup.ps1

$ErrorActionPreference = 'Stop'
$natives = Join-Path $PSScriptRoot 'natives'
$models = Join-Path $PSScriptRoot 'models'
$temp = Join-Path ([IO.Path]::GetTempPath()) "modbot-voice-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $natives, $models, $temp | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Extract($zip, $entryPath, $to) {
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = $archive.Entries | Where-Object FullName -eq $entryPath | Select-Object -First 1
        if (-not $entry) { throw "$entryPath isn't in $zip" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $to, $true)
    } finally { $archive.Dispose() }
}

try {
    # Discord's own build of its voice encryption (DAVE), which Discord requires for voice.
    if (-not (Test-Path "$natives/libdave.dll")) {
        Write-Host 'Downloading libdave (Discord)...'
        Invoke-WebRequest 'https://github.com/discord/libdave/releases/download/v1.2.2/cpp/libdave-Windows-X64-boringssl.zip' -OutFile "$temp/libdave.zip"
        Extract "$temp/libdave.zip" 'bin/libdave.dll' "$natives/libdave.dll"
    }

    # The voice transport's encryption, from the official libsodium NuGet package.
    if (-not (Test-Path "$natives/libsodium.dll")) {
        Write-Host 'Downloading libsodium...'
        Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/libsodium/1.0.22/libsodium.1.0.22.nupkg' -OutFile "$temp/libsodium.zip"
        Extract "$temp/libsodium.zip" 'runtimes/win-x64/native/libsodium.dll' "$natives/libsodium.dll"
    }

    # The Opus audio codec. Opus publishes no Windows builds; this is the one Discord.Net's own audio sample uses.
    if (-not (Test-Path "$natives/opus.dll")) {
        Write-Host 'Downloading opus...'
        Invoke-WebRequest 'https://github.com/AvionBlock/OpusSharp/raw/refs/heads/master/OpusSharp.Natives/runtimes/win-x64/native/opus.dll' -OutFile "$natives/opus.dll"
    }

    # Whisper's small English speech-to-text model (~140 MB).
    if (-not (Test-Path "$models/ggml-base.en.bin")) {
        Write-Host 'Downloading the speech-to-text model (~140 MB)...'
        Invoke-WebRequest 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin' -OutFile "$models/ggml-base.en.bin"
    }
} finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}

Write-Host 'Voice commands are set up. Rebuild (or restart with dotnet run) so the libraries are copied next to the bot.'
