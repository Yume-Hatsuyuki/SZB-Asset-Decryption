#requires -version 5.1
<#
.SYNOPSIS
    Downloads the two external decoders (ffmpeg, vgmstream) and lays them out under tools/bin.

.DESCRIPTION
    ffmpeg  -> tools/bin/ffmpeg/bin/ffmpeg.exe
    vgmstream -> tools/bin/vgmstream/vgmstream-cli.exe (+ its DLLs)

    The GPL ffmpeg build is required: the LGPL builds ship without libx264, and the
    video step re-encodes to H.264.

.PARAMETER Proxy
    Optional proxy for restricted networks, e.g. socks5://192.168.88.1:22117

.PARAMETER SkipFfmpeg
    Only fetch vgmstream.

.PARAMETER SkipVgmstream
    Only fetch ffmpeg.

.PARAMETER ToApp
    Also copy the executables into src/SZB_Asset_Decryption.App/Dependencies so a published build
    is self-contained.

.EXAMPLE
    pwsh -File tools/fetch-deps.ps1

.EXAMPLE
    pwsh -File tools/fetch-deps.ps1 -Proxy socks5://192.168.88.1:22117
#>
[CmdletBinding()]
param(
    [string]$Proxy,
    [switch]$SkipFfmpeg,
    [switch]$SkipVgmstream,
    [switch]$ToApp
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$binRoot = Join-Path $repoRoot 'tools/bin'
$tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("szb-asset-decryption-deps-" + [guid]::NewGuid().ToString('N').Substring(0, 8))

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "    $text" -ForegroundColor Green }

New-Item -ItemType Directory -Force -Path $binRoot | Out-Null
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null

$proxyArg = @{}
if ($Proxy) {
    $proxyArg['Proxy'] = $Proxy
    Write-Host "    using proxy $Proxy" -ForegroundColor DarkGray
}

function Get-Remote($url, $outFile) {
    Write-Host "    GET $url" -ForegroundColor DarkGray
    $old = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'   # the progress bar makes downloads 5-10x slower
    try {
        Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing -MaximumRedirection 10 @proxyArg
    }
    finally {
        $ProgressPreference = $old
    }
    if (-not (Test-Path $outFile) -or (Get-Item $outFile).Length -eq 0) {
        throw "download produced no data: $url"
    }
}

try {
    # ------------------------------------------------------------------ ffmpeg
    if (-not $SkipFfmpeg) {
        Write-Step 'ffmpeg (GPL build, includes libx264)'
        $zip = Join-Path $tmpRoot 'ffmpeg.zip'
        Get-Remote 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip' $zip

        $expand = Join-Path $tmpRoot 'ffmpeg'
        Expand-Archive -Path $zip -DestinationPath $expand -Force

        $exe = Get-ChildItem -Path $expand -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
        if (-not $exe) { throw 'ffmpeg.exe not found inside the archive' }

        $srcBin = $exe.Directory.FullName
        $dstBin = Join-Path $binRoot 'ffmpeg/bin'
        New-Item -ItemType Directory -Force -Path $dstBin | Out-Null

        # 只要 ffmpeg.exe。ffprobe.exe 是同一个压缩包里另一个 158 MB 的静态二进制，
        # 程序从来没调用过它（探测、输出尺寸都由 UsmFile 自己解析）。
        $from = Join-Path $srcBin 'ffmpeg.exe'
        if (Test-Path $from) {
            Copy-Item $from -Destination $dstBin -Force
            Write-Ok 'ffmpeg.exe -> tools/bin/ffmpeg/bin'
        }
        else {
            throw 'ffmpeg.exe not found in the archive'
        }
    }

    # ------------------------------------------------------------------ vgmstream
    if (-not $SkipVgmstream) {
        Write-Step 'vgmstream-cli'
        $zip = Join-Path $tmpRoot 'vgmstream.zip'
        Get-Remote 'https://github.com/vgmstream/vgmstream/releases/latest/download/vgmstream-win64.zip' $zip

        $dst = Join-Path $binRoot 'vgmstream'
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Expand-Archive -Path $zip -DestinationPath $dst -Force
        Write-Ok 'vgmstream-cli.exe -> tools/bin/vgmstream'
    }

    # ------------------------------------------------------------------ optional copy
    if ($ToApp) {
        Write-Step 'copying into src/SZB_Asset_Decryption.App/Dependencies'
        $appDeps = Join-Path $repoRoot 'src/SZB_Asset_Decryption.App/Dependencies'
        New-Item -ItemType Directory -Force -Path $appDeps | Out-Null

        $ff = Join-Path $binRoot 'ffmpeg/bin/ffmpeg.exe'
        if (Test-Path $ff) { Copy-Item $ff -Destination $appDeps -Force }

        $vgm = Get-ChildItem -Path (Join-Path $binRoot 'vgmstream') -Filter '*.exe' -Recurse |
            Select-Object -First 1
        if ($vgm) {
            Copy-Item $vgm.FullName -Destination (Join-Path $appDeps 'vgmstream-cli.exe') -Force
            Get-ChildItem $vgm.Directory.FullName -Filter '*.dll' | ForEach-Object {
                Copy-Item $_.FullName -Destination $appDeps -Force
            }
        }
        Write-Ok "portable dependencies staged in $appDeps"
    }

    Write-Host ''
    Write-Host 'done.' -ForegroundColor Green
    Write-Host "  ffmpeg    : $(Join-Path $binRoot 'ffmpeg/bin/ffmpeg.exe')"
    Write-Host "  vgmstream : $(Join-Path $binRoot 'vgmstream')"
    Write-Host ''
    Write-Host 'The app probes these paths automatically. Press "重新探测" in 设置 if it was already running.'
}
finally {
    Remove-Item $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
}
