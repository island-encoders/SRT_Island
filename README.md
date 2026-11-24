# SRT Island

SRT Island is a Windows x64 contribution receiver and playout station for live production.

## Features

- Eight SRT slots with caller, listener and rendezvous modes, automatic reconnect, native Haivision bonding, local recordings, signal previews and source-rate pop-outs.
- CPU, NVIDIA NVDEC and Direct3D decoding. DeckLink SDI/HDMI playout with embedded stereo or multichannel PCM. GPU HDMI/DisplayPort playout to a selected extended display and Windows audio endpoint. AJA routes use an AJA-enabled GStreamer build.
- Simultaneous restreaming from each SRT input to RTMP/RTMPS, Enhanced RTMP, local or HTTP PUT HLS, and SRT using MPEG-TS or Matroska. Configure each destination to copy encoded video and a selected audio track or encode H.264, HEVC or AV1 with AAC.
- Island Encoder address book with Windows-encrypted saved logins, Tailscale peer discovery and per-encoder SRT controls.
- Optional authenticated remote API for slot status and start/stop, including their configured outputs and restreams.

## Replay workflow with vMix or a hardware mixer

**Remote cameras → SRT Island ingest PC → DeckLink SDI → vMix capture PC or hardware mixer.**

Receive each field camera on its own slot and send it through a dedicated DeckLink output. Connect each SDI cable to a capture input on the vMix machine or to an input on a hardware switcher. Match the playout resolution and frame rate to the production and replay session. In vMix, add the capture inputs as cameras in Instant Replay. This puts SRT reception and decoding on the ingest PC and gives the replay machine a conventional SDI capture path. vMix also supports direct SRT replay, with its **Use Hardware Decoder** option cleared. [vMix guidance on SRT replay](https://sd8.vmix.com/knowledgebase/article.aspx/325/using-srt-sources-with-vmix-replay)

## Install

Extract the Windows x64 package and run **Install SRT Island.cmd**. It downloads and verifies FFmpeg, mpv and the DeckLink FFmpeg build, and installs Microsoft WebView2 when needed. Open **SRT Island.exe** after installation. The Windows build includes .NET and the native bonding helper. Settings and recordings default to the current user's Local AppData. Blackmagic or AJA drivers are installed separately for the card you use. Configure the GStreamer launcher under **Settings** for AJA output. [FFmpeg distribution](https://www.gyan.dev/ffmpeg/builds/) · [mpv release](https://github.com/mpv-player/mpv/releases/tag/v0.42.0) · [DeckLink FFmpeg release](https://github.com/danielbrodie/ffmpeg-decklink/releases/tag/v1.0.0) · [GStreamer AJA plug-in](https://gstreamer.freedesktop.org/documentation/aja/index.html)

## Receive, playout and restream

1. Open **SRT Slots**, configure a slot and select the incoming SRT mode. Set the sender to Caller if the slot is in Listener mode. Use **Bonded listener** for native Haivision SRT socket groups. SRTLA and other vendor bonding protocols require a compatible gateway.
2. Choose Preview, DeckLink, AJA or an extended GPU display under **Output device**. Use **Hardware outputs → Identify** to distinguish GPU displays and keep the operator monitor primary. When using an output card, choose the audio track and pair mapping for the destination equipment.
3. Expand **Restream destinations** and add the destinations for that slot. Start the slot to start ingest, playout and restreaming together. Apply configuration changes while the slot is stopped.
4. Use **Bounce** when the receiver accepts the existing video and audio codecs. Use **Transcode** when a service requires a different video codec, bitrate or AAC audio. Classic RTMP normally expects H.264 and AAC. Use Enhanced RTMP for HEVC or AV1 where the service supports it. For SRT AV1 output, select Matroska and confirm that the destination accepts it.
5. For local HLS, use **Copy HLS URL** from the active slot. This URL is served on the station loopback interface. For remote HLS publication, choose **HTTP PUT** and enter the receiving server's playlist URL. The receiver must accept creation or replacement of the playlist and segments.

Each destination runs in an independent FFmpeg pipeline. RTMP and SRT/MPEG-TS outputs retry transport errors with bounded buffering. If a target falls too far behind, its output stops while the other destinations continue. Check the slot log for per-destination activity. Bounce HLS uses MPEG-TS segments cut at source keyframes. Transcoded HLS uses fragmented MP4. Stream URLs and keys are stored in the local slot configuration. [FFmpeg muxers](https://ffmpeg.org/ffmpeg-formats.html) · [FFmpeg protocols](https://ffmpeg.org/ffmpeg-protocols.html)

## Remote control

Set a random bearer token before launching the station from PowerShell. Launching without the token keeps the remote API disabled.

~~~powershell
$bytes = New-Object byte[] 32
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$random.GetBytes($bytes)
$random.Dispose()
$env:SRT_ISLAND_REMOTE_TOKEN = [Convert]::ToBase64String($bytes)
Start-Process -FilePath '.\SRT Island.exe'
~~~

The API listens on loopback port **8778**. For access from another machine on your tailnet, run `tailscale serve --bg --https=8443 http://127.0.0.1:8778` on the station and use the HTTPS URL it reports. Share the token with the operator through your usual secret-sharing process. The desktop configuration UI remains local. [Tailscale Serve documentation](https://tailscale.com/docs/reference/tailscale-cli/serve)

~~~powershell
$base = 'https://your-station.example.ts.net:8443'
$headers = @{ Authorization = "Bearer $env:SRT_ISLAND_REMOTE_TOKEN" }
Invoke-RestMethod "$base/remote/v1/slots" -Headers $headers
Invoke-RestMethod "$base/remote/v1/slots/1/start" -Method Post -Headers $headers
Invoke-RestMethod "$base/remote/v1/slots/1/stop" -Method Post -Headers $headers
~~~

## Build

~~~powershell
dotnet publish -c Release -r win-x64 --self-contained true -o release
~~~

The [Windows release workflow](https://github.com/island-encoders/SRT_Island/actions/workflows/windows.yml) packages the desktop app and builds the native receiver from pinned Haivision SRT source when changes land on main. Use the generated **SRT-Island-Windows-x64** artifact. The workflow excludes machine settings, recordings, runtime logs and credentials.
