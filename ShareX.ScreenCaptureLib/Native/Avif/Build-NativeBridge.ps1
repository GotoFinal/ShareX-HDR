[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("x64", "ARM64")]
    [string] $Architecture,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [switch] $KeepWorkDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$libAvifTag = "v1.4.2"
$libAvifCommit = "c5240fc79fe5c2407e10afd35f5505ef6333ea49"
$generatorArchitecture = if ($Architecture -eq "ARM64") { "ARM64" } else { "x64" }
$aomTargetCpu = if ($Architecture -eq "ARM64") { "arm64" } else { "x86_64" }
$expectedMachine = if ($Architecture -eq "ARM64") { 0xAA64 } else { 0x8664 }
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$workDirectory = Join-Path $temporaryRoot "ShareX-Avif-$([Guid]::NewGuid().ToString('N'))"
$sourceDirectory = Join-Path $workDirectory "libavif"
$buildDirectory = Join-Path $workDirectory "build"
$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

function Invoke-CheckedTool
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & $FilePath @Arguments

    if ($LASTEXITCODE -ne 0)
    {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Get-PeMachine
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $stream = [System.IO.File]::OpenRead($Path)

    try
    {
        $reader = [System.IO.BinaryReader]::new($stream)

        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D)
        {
            throw "$Path is not a valid PE image."
        }

        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()

        if ($peOffset -lt 0 -or $peOffset -gt $stream.Length - 6)
        {
            throw "$Path contains an invalid PE header offset."
        }

        $stream.Position = $peOffset

        if ($reader.ReadUInt32() -ne 0x00004550)
        {
            throw "$Path does not contain a PE signature."
        }

        return $reader.ReadUInt16()
    }
    finally
    {
        $stream.Dispose()
    }
}

Get-Command git -ErrorAction Stop | Out-Null
Get-Command cmake -ErrorAction Stop | Out-Null
Get-Command perl -ErrorAction Stop | Out-Null

New-Item -ItemType Directory -Path $workDirectory | Out-Null

try
{
    Invoke-CheckedTool git @(
        "clone",
        "--branch", $libAvifTag,
        "--depth", "1",
        "https://github.com/AOMediaCodec/libavif.git",
        $sourceDirectory)

    $actualCommit = (& git -C $sourceDirectory rev-parse HEAD).Trim()

    if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $libAvifCommit)
    {
        throw "Expected libavif commit $libAvifCommit but resolved $actualCommit."
    }

    Invoke-CheckedTool cmake @(
        "-S", $PSScriptRoot,
        "-B", $buildDirectory,
        "-G", "Visual Studio 17 2022",
        "-A", $generatorArchitecture,
        "-DAVIF_SOURCE_DIR=$sourceDirectory",
        "-DAOM_TARGET_CPU=$aomTargetCpu")

    Invoke-CheckedTool cmake @(
        "--build", $buildDirectory,
        "--config", "Release",
        "--target", "sharex_avif",
        "--parallel")

    $bridgePath = Join-Path $buildDirectory "Release\ShareX.Avif.dll"

    if (-not (Test-Path -LiteralPath $bridgePath -PathType Leaf))
    {
        throw "The AVIF build completed without producing $bridgePath."
    }

    $machine = Get-PeMachine $bridgePath

    if ($machine -ne $expectedMachine)
    {
        throw "Expected PE machine 0x$($expectedMachine.ToString('X4')) but built 0x$($machine.ToString('X4'))."
    }

    New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
    $destination = Join-Path $resolvedOutputDirectory "ShareX.Avif.dll"
    Copy-Item -LiteralPath $bridgePath -Destination $destination -Force

    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    Write-Host "Built $Architecture AVIF bridge: $destination"
    Write-Host "SHA-256: $hash"
}
finally
{
    if ($KeepWorkDirectory)
    {
        Write-Host "AVIF work directory retained at $workDirectory"
    }
    elseif ($workDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $workDirectory))
    {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }
}
