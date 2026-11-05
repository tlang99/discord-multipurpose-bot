# Downloads what voice commands need, into voice/natives and voice/models (neither is committed). Rebuild the bot
# afterwards so the libraries are copied next to it. See voice/README.md.
#
# Every download is pinned to an exact release or commit and checked against its SHA-256, so what you get is exactly
# what was tested. To move to a newer version, change its link and hash together.
#
#   powershell -ExecutionPolicy Bypass -File voice/setup.ps1

$ErrorActionPreference = 'Stop'
$natives = Join-Path $PSScriptRoot 'natives'
$models = Join-Path $PSScriptRoot 'models'
$temp = Join-Path ([IO.Path]::GetTempPath()) "modbot-voice-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $natives, $models, $temp | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

$downloads = @(
    @{
        # Discord's own build of its voice encryption (DAVE), which Discord requires for voice.
        Name = 'libdave'; File = "$natives/libdave.dll"; ZipEntry = 'bin/libdave.dll'
        Url = 'https://github.com/discord/libdave/releases/download/v1.2.2/cpp/libdave-Windows-X64-boringssl.zip'
        Sha256 = '8ab9ef71bc8841d0211268aa8c3910f9a574a519a8f3cd1881be2c20c2c2b2fe'
    },
    @{
        # The voice connection's encryption, from the official libsodium NuGet package.
        Name = 'libsodium'; File = "$natives/libsodium.dll"; ZipEntry = 'runtimes/win-x64/native/libsodium.dll'
        Url = 'https://api.nuget.org/v3-flatcontainer/libsodium/1.0.22/libsodium.1.0.22.nupkg'
        Sha256 = '64a1f143868309069f0a0a3c8141c0853c4f17243ddf734e92b11c5411739771'
    },
    @{
        # The Opus audio codec. Opus publishes no Windows builds; this is the one Discord.Net's own audio sample uses,
        # pinned to a commit.
        Name = 'opus'; File = "$natives/opus.dll"; ZipEntry = $null
        Url = 'https://github.com/AvionBlock/OpusSharp/raw/0147dd6e11b5e9750b683727a8242cb364c672b2/OpusSharp.Natives/runtimes/win-x64/native/opus.dll'
        Sha256 = '5ddd7cc5a100f486c97f8707a36473fb40975b81585cb0bed69f12b237593b7b'
    },
    @{
        # Whisper's small English speech-to-text model (~140 MB), pinned to a revision.
        Name = 'the speech-to-text model (~140 MB)'; File = "$models/ggml-base.en.bin"; ZipEntry = $null
        Url = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.en.bin'
        Sha256 = 'a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002'
    }
)

function Test-Hash($path, $expected) {
    (Test-Path $path) -and ((Get-FileHash $path -Algorithm SHA256).Hash -eq $expected.ToUpperInvariant())
}

try {
    foreach ($d in $downloads) {
        if (Test-Hash $d.File $d.Sha256) { continue }

        Write-Host "Downloading $($d.Name)..."
        $download = Join-Path $temp ([IO.Path]::GetFileName($d.File) + '.download')
        Invoke-WebRequest $d.Url -OutFile $download -UseBasicParsing

        $file = $download
        if ($d.ZipEntry) {
            $file = Join-Path $temp ([IO.Path]::GetFileName($d.File))
            $archive = [IO.Compression.ZipFile]::OpenRead($download)
            try {
                $entry = $archive.Entries | Where-Object FullName -eq $d.ZipEntry | Select-Object -First 1
                if (-not $entry) { throw "$($d.ZipEntry) isn't in $($d.Url)" }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $file, $true)
            } finally { $archive.Dispose() }
        }

        if (-not (Test-Hash $file $d.Sha256)) {
            throw "$($d.Name) from $($d.Url) doesn't match its expected SHA-256, so it wasn't installed."
        }

        Move-Item $file $d.File -Force
    }
} finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}

Write-Host 'Voice commands are set up. Rebuild (or restart with dotnet run) so the libraries are copied next to the bot.'
