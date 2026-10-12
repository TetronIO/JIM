# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Checks a captured screenshot library for images that must not be published (#582).

.DESCRIPTION
    A release publishes its screenshots to junctional.io and tetron.io with no one looking at them first, so the
    capture refuses two things a person would have caught at a glance:

    - an image that is blank: its luminance barely varies (a white or black screen, a page that never drew);
    - a dark and light pair in the same theme: the light image is not clearly brighter than the dark one.

    The thresholds sit well clear of every real capture. Across the 78-image library the least contrast is 10.6 (a
    dialog over a dimmed dark page) and the smallest gap between a pair's mean luminance is 128.

    The PNG decoder is a few lines of C#, compiled once into the session: measuring a 3348 by 2092 image in PowerShell
    itself would take minutes. It reads 8-bit greyscale, grey with alpha, RGB and RGBA, non-interlaced, which is what
    Chromium writes; anything else is refused rather than measured wrongly.
#>

Set-StrictMode -Version Latest

$script:ScreenshotBlankThreshold = 4.0
$script:ScreenshotThemeGapThreshold = 60.0

if (-not ('JimPngLuminance' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.IO.Compression;

public static class JimPngLuminance
{
    // The mean and standard deviation of each pixel's luminance (ITU-R 601-2, as Pillow's "L" mode), alpha ignored.
    public static double[] Measure(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (file.Length < 8) { throw new InvalidDataException(path + " is not a PNG."); }
        for (int i = 0; i < 8; i++)
        {
            if (file[i] != signature[i]) { throw new InvalidDataException(path + " is not a PNG."); }
        }

        int width = 0, height = 0, bitDepth = 0, colourType = 0, interlace = 0;
        MemoryStream idat = new MemoryStream();
        int pos = 8;
        while (pos + 8 <= file.Length)
        {
            int length = BigEndian(file, pos);
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            int data = pos + 8;
            if (length < 0 || data + length > file.Length) { throw new InvalidDataException(path + " is truncated."); }
            if (type == "IHDR")
            {
                width = BigEndian(file, data);
                height = BigEndian(file, data + 4);
                bitDepth = file[data + 8];
                colourType = file[data + 9];
                interlace = file[data + 12];
            }
            else if (type == "IDAT") { idat.Write(file, data, length); }
            else if (type == "IEND") { break; }
            pos = data + length + 4;
        }

        int channels;
        switch (colourType)
        {
            case 0: channels = 1; break;
            case 2: channels = 3; break;
            case 4: channels = 2; break;
            case 6: channels = 4; break;
            default: throw new NotSupportedException(path + " uses PNG colour type " + colourType + ", which is not measured.");
        }
        if (bitDepth != 8 || interlace != 0)
        {
            throw new NotSupportedException(path + " is not an 8-bit, non-interlaced PNG.");
        }
        if (width <= 0 || height <= 0) { throw new InvalidDataException(path + " has no image header."); }

        int stride = width * channels;
        byte[] previous = new byte[stride];
        byte[] current = new byte[stride];
        double sum = 0, sumOfSquares = 0;
        idat.Position = 0;
        using (ZLibStream inflated = new ZLibStream(idat, CompressionMode.Decompress))
        {
            for (int y = 0; y < height; y++)
            {
                int filter = inflated.ReadByte();
                if (filter < 0) { throw new InvalidDataException(path + " ends before its last row."); }
                inflated.ReadExactly(current, 0, stride);
                Unfilter(filter, current, previous, channels, path);
                for (int x = 0; x < width; x++)
                {
                    int o = x * channels;
                    double luminance = channels >= 3
                        ? 0.299 * current[o] + 0.587 * current[o + 1] + 0.114 * current[o + 2]
                        : current[o];
                    sum += luminance;
                    sumOfSquares += luminance * luminance;
                }
                byte[] swap = previous; previous = current; current = swap;
            }
        }

        double count = (double)width * height;
        double mean = sum / count;
        double variance = Math.Max(0, sumOfSquares / count - mean * mean);
        return new double[] { mean, Math.Sqrt(variance) };
    }

    private static int BigEndian(byte[] b, int i)
    {
        return (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    }

    private static void Unfilter(int filter, byte[] row, byte[] above, int bpp, string path)
    {
        switch (filter)
        {
            case 0:
                return;
            case 1:
                for (int i = bpp; i < row.Length; i++) { row[i] = (byte)(row[i] + row[i - bpp]); }
                return;
            case 2:
                for (int i = 0; i < row.Length; i++) { row[i] = (byte)(row[i] + above[i]); }
                return;
            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    row[i] = (byte)(row[i] + ((left + above[i]) >> 1));
                }
                return;
            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0;
                    int b = above[i];
                    int c = i >= bpp ? above[i - bpp] : 0;
                    int p = a + b - c;
                    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    int predictor = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                    row[i] = (byte)(row[i] + predictor);
                }
                return;
            default:
                throw new InvalidDataException(path + " has a row with unknown filter " + filter + ".");
        }
    }
}
'@
}

# The mean and standard deviation of an image's luminance, 0 (black) to 255 (white).
function Get-PngLuminance {
    param([Parameter(Mandatory)][string]$Path)

    try {
        $values = [JimPngLuminance]::Measure((Resolve-Path -LiteralPath $Path).ProviderPath)
    }
    catch [System.Management.Automation.MethodInvocationException] {
        throw $_.Exception.InnerException.Message
    }
    return [pscustomobject]@{ Mean = $values[0]; StdDev = $values[1] }
}

<#
.SYNOPSIS
    Describes each image that must not be published: blank ones, and dark and light pairs in the same theme.
.OUTPUTS
    One sentence per problem; nothing when the images are fit to publish.
#>
function Find-ScreenshotContentProblem {
    param(
        [Parameter(Mandatory)][string[]]$Path,
        [double]$BlankThreshold = $script:ScreenshotBlankThreshold,
        [double]$ThemeGapThreshold = $script:ScreenshotThemeGapThreshold
    )

    $measured = @{}
    foreach ($image in $Path) { $measured[(Split-Path -Leaf $image)] = Get-PngLuminance -Path $image }

    foreach ($name in $measured.Keys | Sort-Object) {
        if ($measured[$name].StdDev -lt $BlankThreshold) {
            '{0} looks blank: its luminance barely varies (standard deviation {1:N1}, under {2}).' -f $name, $measured[$name].StdDev, $BlankThreshold
        }
    }
    foreach ($dark in $measured.Keys | Where-Object { $_ -like '*-dark.png' } | Sort-Object) {
        $light = $dark -replace '-dark\.png$', '-light.png'
        if (-not $measured.ContainsKey($light)) { continue }
        $gap = $measured[$light].Mean - $measured[$dark].Mean
        if ($gap -lt $ThemeGapThreshold) {
            '{0} and {1} look like the same theme: the light image is only {2:N1} brighter on average (expected at least {3}).' -f $dark, $light, $gap, $ThemeGapThreshold
        }
    }
}
