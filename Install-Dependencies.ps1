param(
    [string]$ToolsDirectory = (Join-Path $env:LOCALAPPDATA 'SRT Island\tools')
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$toolRoot = [IO.Path]::GetFullPath($ToolsDirectory)
$null = New-Item -ItemType Directory -Path $toolRoot -Force
$staging = Join-Path $toolRoot ('.install-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $staging

function Install-Archive {
    param([string]$Name, [string]$Url, [string]$Executable, [string]$Hash = '',
          [string[]]$ExtraFiles = @())
    $destination = Join-Path $toolRoot $Name
    if (Test-Path -LiteralPath (Join-Path $destination $Executable) -PathType Leaf) {
        Write-Host "$Name is installed."
        return
    }
    $archive = Join-Path $staging "$Name.zip"
    $extracted = Join-Path $staging "$Name-extracted"
    $ready = Join-Path $staging "$Name-ready"
    Write-Host "Downloading $Name..."
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $archive
    if ($Hash -and (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Hash) {
        throw "$Name download did not match its publisher's SHA-256."
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $extracted
    $binary = Get-ChildItem -LiteralPath $extracted -Recurse -File -Filter $Executable | Select-Object -First 1
    if (!$binary) { throw "$Name archive does not contain $Executable." }
    $null = New-Item -ItemType Directory -Path $ready
    Copy-Item -LiteralPath $binary.FullName -Destination $ready
    Get-ChildItem -LiteralPath $binary.DirectoryName -File | Where-Object {
        $_.Extension -eq '.dll' -or $_.Name -in $ExtraFiles
    } | Copy-Item -Destination $ready
    foreach ($file in $ExtraFiles) {
        if (!(Test-Path -LiteralPath (Join-Path $ready $file))) { throw "$Name archive does not contain $file." }
    }
    if (Test-Path -LiteralPath $destination) { throw "An incomplete installation already exists at $destination." }
    Move-Item -LiteralPath $ready -Destination $destination
    Write-Host "Installed $Name."
}

try {
    $webviewId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $versions = @(
        (Get-ItemPropertyValue -LiteralPath "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$webviewId" -Name pv -ErrorAction SilentlyContinue)
        (Get-ItemPropertyValue -LiteralPath "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$webviewId" -Name pv -ErrorAction SilentlyContinue)
    )
    if (!($versions | Where-Object { $_ -and $_ -ne '0.0.0.0' })) {
        $bootstrapper = Join-Path $staging 'MicrosoftEdgeWebView2Setup.exe'
        Invoke-WebRequest -UseBasicParsing -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper
        $signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
            throw 'Microsoft WebView2 installer signature could not be verified.'
        }
        $webview = Start-Process -FilePath $bootstrapper -ArgumentList '/silent','/install' -WindowStyle Hidden -Wait -PassThru
        if ($webview.ExitCode -ne 0) { throw "WebView2 installer returned $($webview.ExitCode)." }
    }
    if (!(Test-Path -LiteralPath (Join-Path $toolRoot 'receiver\ffmpeg.exe'))) {
        $ffmpeg = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
        $checksum = (Invoke-WebRequest -UseBasicParsing -Uri "$ffmpeg.sha256").Content.Trim()
        $hash = [regex]::Match($checksum, '(?i)\b[0-9a-f]{64}\b').Value
        if (!$hash) { throw 'FFmpeg did not return a valid SHA-256.' }
        Install-Archive receiver $ffmpeg 'ffmpeg.exe' $hash @('ffprobe.exe')
    }
    if (!(Test-Path -LiteralPath (Join-Path $toolRoot 'mpv\mpv.com'))) {
        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/mpv-player/mpv/releases/tags/v0.42.0'
        $asset = $release.assets | Where-Object name -Match 'x86_64-pc-windows-msvc\.zip$' | Select-Object -First 1
        if (!$asset -or $asset.digest -notmatch '^sha256:([0-9a-f]{64})$') { throw 'mpv Windows archive or digest unavailable.' }
        Install-Archive mpv $asset.browser_download_url 'mpv.com' $Matches[1] @('mpv.exe')
    }
    if (!(Test-Path -LiteralPath (Join-Path $toolRoot 'decklink\ffmpeg-decklink.exe'))) {
        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/danielbrodie/ffmpeg-decklink/releases/tags/v1.0.0'
        $asset = $release.assets | Where-Object name -EQ 'ffmpeg-decklink-windows-amd64.zip' | Select-Object -First 1
        if (!$asset -or $asset.digest -notmatch '^sha256:([0-9a-f]{64})$') { throw 'DeckLink FFmpeg archive or digest unavailable.' }
        Install-Archive decklink $asset.browser_download_url 'ffmpeg-decklink.exe' $Matches[1]
    }
    Write-Host "Media dependencies are ready in $toolRoot."
}
finally {
    $resolved = (Resolve-Path -LiteralPath $staging).Path
    if ([IO.Path]::GetDirectoryName($resolved) -eq $toolRoot -and
        [IO.Path]::GetFileName($resolved) -match '^\.install-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
