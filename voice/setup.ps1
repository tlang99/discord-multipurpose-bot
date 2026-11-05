# Downloads what voice commands need, into voice/natives, voice/models and voice/piper (none of them committed). Rebuild the bot
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
    },
    @{
        # The voice spoken replies use (Piper's "lessac", a natural US English voice, ~63 MB), pinned to a revision.
        Name = 'the speaking voice (~63 MB)'; File = "$models/en_US-lessac-medium.onnx"; ZipEntry = $null
        Url = 'https://huggingface.co/rhasspy/piper-voices/resolve/375a0fe641dea077c2a47b4e9a056d6da521eed3/en/en_US/lessac/medium/en_US-lessac-medium.onnx'
        Sha256 = '5efe09e69902187827af646e1a6e9d269dee769f9877d17b16b1b46eeaaf019f'
    },
    @{
        # Its settings (sample rate, pronunciation), which Piper reads next to the voice.
        Name = "the speaking voice's settings"; File = "$models/en_US-lessac-medium.onnx.json"; ZipEntry = $null
        Url = 'https://huggingface.co/rhasspy/piper-voices/resolve/375a0fe641dea077c2a47b4e9a056d6da521eed3/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json'
        Sha256 = 'efe19c417bed055f2d69908248c6ba650fa135bc868b0e6abb3da181dab690a0'
    }
)

# Piper, the text-to-speech program for spoken replies (~22 MB): a whole folder (the program, its libraries and its
# pronunciation data), so it's unpacked into voice/piper rather than picked out a file at a time. A note of the
# archive's hash marks it as installed.
$piper = @{
    Dir = Join-Path $PSScriptRoot 'piper'; Folder = 'piper/'
    Url = 'https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip'
    Sha256 = 'f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea'
}

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

    $marker = Join-Path $piper.Dir '.installed-sha256'
    $installed = (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $piper.Sha256) -and
        (Test-Path (Join-Path $piper.Dir 'piper.exe'))
    if (-not $installed) {
        Write-Host 'Downloading Piper (text-to-speech, ~22 MB)...'
        $zip = Join-Path $temp 'piper.zip.download'
        Invoke-WebRequest $piper.Url -OutFile $zip -UseBasicParsing
        if (-not (Test-Hash $zip $piper.Sha256)) {
            throw "Piper from $($piper.Url) doesn't match its expected SHA-256, so it wasn't installed."
        }
        if (Test-Path $piper.Dir) { Remove-Item -Recurse -Force $piper.Dir }
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            foreach ($entry in $archive.Entries) {
                if (-not $entry.FullName.StartsWith($piper.Folder) -or $entry.Name -eq '') { continue }
                $target = [IO.Path]::GetFullPath((Join-Path $piper.Dir $entry.FullName.Substring($piper.Folder.Length)))
                # Never outside voice/piper, whatever the archive's paths say.
                if (-not $target.StartsWith([IO.Path]::GetFullPath($piper.Dir) + [IO.Path]::DirectorySeparatorChar)) {
                    throw "Piper's archive has a file outside its folder ($($entry.FullName)), so it wasn't installed."
                }
                New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        } finally { $archive.Dispose() }
        Set-Content $marker $piper.Sha256
    }
} finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}

Write-Host 'Voice commands are set up. Rebuild (or restart with dotnet run) so the libraries are copied next to the bot.'
