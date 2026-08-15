#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $Source = (Join-Path $PSScriptRoot '..\src\GoWinUI.App\Assets\AppLogo.ico'),

    [string] $Destination = (Join-Path $PSScriptRoot '..\src\GoAi.Server.App\Assets\AppLogo.Dark.ico')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sourcePath = [IO.Path]::GetFullPath($Source)
$destinationPath = [IO.Path]::GetFullPath($Destination)
$sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
$invalidHeader = $sourceBytes.Length -lt 22
if (-not $invalidHeader) {
    $invalidHeader = [BitConverter]::ToUInt16($sourceBytes, 0) -ne 0 -or
        [BitConverter]::ToUInt16($sourceBytes, 2) -ne 1
}
if ($invalidHeader) {
    throw "Source is not a supported Windows ICO file: $sourcePath"
}

$imageCount = [BitConverter]::ToUInt16($sourceBytes, 4)
if ($imageCount -lt 1 -or $sourceBytes.Length -lt 6 + 16 * $imageCount) {
    throw "Source ICO directory is invalid: $sourcePath"
}

$encodedImages = [Collections.Generic.List[byte[]]]::new()
for ($index = 0; $index -lt $imageCount; $index++) {
    $entryOffset = 6 + 16 * $index
    $imageLength = [BitConverter]::ToUInt32($sourceBytes, $entryOffset + 8)
    $imageOffset = [BitConverter]::ToUInt32($sourceBytes, $entryOffset + 12)
    if ($imageLength -lt 8 -or $imageOffset + $imageLength -gt $sourceBytes.Length) {
        throw "Source ICO image entry $index is invalid."
    }
    $isPng = $sourceBytes[$imageOffset] -eq 0x89 -and
        $sourceBytes[$imageOffset + 1] -eq 0x50 -and
        $sourceBytes[$imageOffset + 2] -eq 0x4e -and
        $sourceBytes[$imageOffset + 3] -eq 0x47
    if (-not $isPng) {
        throw "Source ICO image entry $index is not PNG encoded."
    }

    $payload = [byte[]]::new($imageLength)
    [Buffer]::BlockCopy($sourceBytes, [int] $imageOffset, $payload, 0, [int] $imageLength)
    $input = [IO.MemoryStream]::new($payload, $false)
    $sourceBitmap = $null
    $darkBitmap = $null
    $output = $null
    try {
        $sourceBitmap = [Drawing.Bitmap]::FromStream($input)
        $darkBitmap = [Drawing.Bitmap]::new(
            $sourceBitmap.Width,
            $sourceBitmap.Height,
            [Drawing.Imaging.PixelFormat]::Format32bppArgb)

        for ($y = 0; $y -lt $sourceBitmap.Height; $y++) {
            for ($x = 0; $x -lt $sourceBitmap.Width; $x++) {
                $color = $sourceBitmap.GetPixel($x, $y)
                if ($color.A -eq 0) {
                    $darkBitmap.SetPixel($x, $y, [Drawing.Color]::Transparent)
                    continue
                }

                $maximum = [Math]::Max($color.R, [Math]::Max($color.G, $color.B))
                $minimum = [Math]::Min($color.R, [Math]::Min($color.G, $color.B))
                $chroma = $maximum - $minimum
                $luminance = 0.2126 * $color.R + 0.7152 * $color.G + 0.0722 * $color.B

                # Preserve the white GO lettering, its antialiasing and the bright
                # glass rim. Darken only the original background surfaces.
                if ($luminance -ge 205 -and $chroma -le 26) {
                    $red = $color.R
                    $green = $color.G
                    $blue = $color.B
                }
                else {
                    $red = [Math]::Min(255, [Math]::Max(0, [int] [Math]::Round($color.R * 0.40)))
                    $green = [Math]::Min(255, [Math]::Max(0, [int] [Math]::Round($color.G * 0.40)))
                    $blue = [Math]::Min(255, [Math]::Max(0, [int] [Math]::Round($color.B * 0.40)))
                }
                $darkBitmap.SetPixel($x, $y, [Drawing.Color]::FromArgb($color.A, $red, $green, $blue))
            }
        }

        $output = [IO.MemoryStream]::new()
        $darkBitmap.Save($output, [Drawing.Imaging.ImageFormat]::Png)
        $encodedImages.Add($output.ToArray())
    }
    finally {
        if ($null -ne $output) { $output.Dispose() }
        if ($null -ne $darkBitmap) { $darkBitmap.Dispose() }
        if ($null -ne $sourceBitmap) { $sourceBitmap.Dispose() }
        $input.Dispose()
    }
}

$destinationDirectory = Split-Path $destinationPath -Parent
[IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
$temporaryPath = "$destinationPath.$([Guid]::NewGuid().ToString('N')).tmp"
$stream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([UInt16] 0)
    $writer.Write([UInt16] 1)
    $writer.Write([UInt16] $imageCount)
    $nextImageOffset = 6 + 16 * $imageCount
    for ($index = 0; $index -lt $imageCount; $index++) {
        $writer.Write($sourceBytes, 6 + 16 * $index, 8)
        $writer.Write([UInt32] $encodedImages[$index].Length)
        $writer.Write([UInt32] $nextImageOffset)
        $nextImageOffset += $encodedImages[$index].Length
    }
    foreach ($image in $encodedImages) {
        $writer.Write($image)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

if (Test-Path -LiteralPath $destinationPath) {
    Remove-Item -LiteralPath $destinationPath -Force
}
[IO.File]::Move($temporaryPath, $destinationPath)
Write-Host "Dark GO AI Server icon created: $destinationPath" -ForegroundColor Green
