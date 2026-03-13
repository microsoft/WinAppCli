// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using SkiaSharp;
using Svg.Skia;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal class ImageAssetService : IImageAssetService
{
    private static readonly ManifestAssetReference[] DefaultAssetReferences =
    [
        new("AppList.png", 44, 44),
        new("MedTile.png", 150, 150),
        new("WideTile.png", 310, 150),
        new("StoreLogo.png", 50, 50),
    ];

    private static readonly (string Suffix, float Scale)[] ScaleVariants =
    [
        ("", 1.0f),
        (".scale-125", 1.25f),
        (".scale-150", 1.5f),
        (".scale-200", 2.0f),
        (".scale-400", 4.0f),
    ];

    private static readonly int[] TargetSizes = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];

    private static readonly int[] IcoSizes = [16, 24, 32, 48, 256];

    public Task GenerateAssetsAsync(
        FileInfo sourceImagePath,
        DirectoryInfo outputDirectory,
        TaskContext taskContext,
        FileInfo? lightImagePath = null,
        CancellationToken cancellationToken = default)
    {
        return GenerateAssetsFromManifestAsync(
            sourceImagePath,
            outputDirectory,
            DefaultAssetReferences,
            taskContext,
            lightImagePath,
            cancellationToken);
    }

    public async Task GenerateAssetsFromManifestAsync(
        FileInfo sourceImagePath,
        DirectoryInfo manifestDirectory,
        IReadOnlyList<ManifestAssetReference> assetReferences,
        TaskContext taskContext,
        FileInfo? lightImagePath = null,
        CancellationToken cancellationToken = default)
    {
        if (!sourceImagePath.Exists)
        {
            throw new FileNotFoundException($"Source image not found: {sourceImagePath.FullName}");
        }

        if (lightImagePath is { Exists: false })
        {
            throw new FileNotFoundException($"Light theme source image not found: {lightImagePath.FullName}");
        }

        if (assetReferences.Count == 0)
        {
            taskContext.AddStatusMessage($"{UiSymbols.Warning} No asset references found in manifest. No assets generated.");
            return;
        }

        taskContext.AddStatusMessage($"{UiSymbols.Info} Generating MSIX image assets from: {sourceImagePath.FullName}");

        Bitmap sourceImage;
        try
        {
            sourceImage = LoadSourceImage(sourceImagePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to decode image: {sourceImagePath.FullName}. Please ensure the file is a valid image format.", ex);
        }

        Bitmap? lightImage = null;
        try
        {
            if (lightImagePath != null)
            {
                lightImage = LoadSourceImage(lightImagePath);
            }
        }
        catch (Exception ex)
        {
            sourceImage.Dispose();
            throw new InvalidOperationException($"Failed to decode image: {lightImagePath!.FullName}. Please ensure the file is a valid image format.", ex);
        }

        using (sourceImage)
        using (lightImage)
        {
            taskContext.AddDebugMessage($"Source image size: {sourceImage.Width}x{sourceImage.Height}");
            if (lightImage != null)
            {
                taskContext.AddDebugMessage($"Light image size: {lightImage.Width}x{lightImage.Height}");
            }

            var successCount = 0;
            var totalCount = 0;

            foreach (var assetReference in assetReferences)
            {
                var assetFullPath = Path.Combine(manifestDirectory.FullName, assetReference.RelativePath);
                var assetDirectory = Path.GetDirectoryName(assetFullPath) ?? manifestDirectory.FullName;
                var assetFileName = Path.GetFileNameWithoutExtension(assetReference.RelativePath);
                var assetExtension = Path.GetExtension(assetReference.RelativePath);

                if (!Directory.Exists(assetDirectory))
                {
                    Directory.CreateDirectory(assetDirectory);
                }

                foreach (var (suffix, scale) in ScaleVariants)
                {
                    var scaledWidth = (int)(assetReference.BaseWidth * scale);
                    var scaledHeight = (int)(assetReference.BaseHeight * scale);
                    var scaledFileName = $"{assetFileName}{suffix}{assetExtension}";
                    var scaledPath = Path.Combine(assetDirectory, scaledFileName);

                    totalCount++;
                    if (await TryGenerateAssetAsync(sourceImage, scaledPath, scaledFileName, scaledWidth, scaledHeight, taskContext, cancellationToken))
                    {
                        successCount++;
                    }

                    if (lightImage != null)
                    {
                        var lightScaleFileName = $"{assetFileName}.scale-{GetScalePercentage(scale)}_altform-colorful_theme-light{assetExtension}";
                        var lightScalePath = Path.Combine(assetDirectory, lightScaleFileName);

                        totalCount++;
                        if (await TryGenerateAssetAsync(lightImage, lightScalePath, lightScaleFileName, scaledWidth, scaledHeight, taskContext, cancellationToken))
                        {
                            successCount++;
                        }
                    }
                }

                if (IsTargetSizeAsset(assetReference, assetFileName))
                {
                    foreach (var targetSize in TargetSizes)
                    {
                        var platedFileName = $"{assetFileName}.targetsize-{targetSize}{assetExtension}";
                        var platedPath = Path.Combine(assetDirectory, platedFileName);

                        totalCount++;
                        if (await TryGenerateAssetAsync(sourceImage, platedPath, platedFileName, targetSize, targetSize, taskContext, cancellationToken))
                        {
                            successCount++;
                        }

                        var unplatedFileName = $"{assetFileName}.targetsize-{targetSize}_altform-unplated{assetExtension}";
                        var unplatedPath = Path.Combine(assetDirectory, unplatedFileName);

                        totalCount++;
                        if (await TryGenerateAssetAsync(sourceImage, unplatedPath, unplatedFileName, targetSize, targetSize, taskContext, cancellationToken))
                        {
                            successCount++;
                        }

                        if (lightImage != null)
                        {
                            var lightTargetFileName = $"{assetFileName}.targetsize-{targetSize}_altform-lightunplated{assetExtension}";
                            var lightTargetPath = Path.Combine(assetDirectory, lightTargetFileName);

                            totalCount++;
                            if (await TryGenerateAssetAsync(lightImage, lightTargetPath, lightTargetFileName, targetSize, targetSize, taskContext, cancellationToken))
                            {
                                successCount++;
                            }
                        }
                    }
                }
            }

            if (successCount == totalCount)
            {
                taskContext.AddStatusMessage($"{UiSymbols.Info} Successfully generated {totalCount} image assets");
            }
            else
            {
                taskContext.AddStatusMessage($"{UiSymbols.Info} Successfully generated {successCount} of {totalCount} image assets");
            }
        }
    }

    public async Task GenerateIcoAsync(FileInfo sourceImagePath, string outputPath, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        if (!sourceImagePath.Exists)
        {
            throw new FileNotFoundException($"Source image not found: {sourceImagePath.FullName}");
        }

        taskContext.AddStatusMessage($"{UiSymbols.Info} Generating ICO file: {outputPath}");

        Bitmap sourceImage;
        try
        {
            sourceImage = LoadSourceImage(sourceImagePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to decode image: {sourceImagePath.FullName}. Please ensure the file is a valid image format.", ex);
        }

        using (sourceImage)
        {
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            await Task.Run(() =>
            {
                var pngFrames = new List<byte[]>(IcoSizes.Length);

                foreach (var size in IcoSizes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var targetBitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                    using var graphics = Graphics.FromImage(targetBitmap);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.Clear(Color.Transparent);

                    var sourceAspect = (float)sourceImage.Width / sourceImage.Height;
                    int scaledWidth;
                    int scaledHeight;
                    if (sourceAspect > 1f)
                    {
                        scaledWidth = size;
                        scaledHeight = (int)(size / sourceAspect);
                    }
                    else
                    {
                        scaledHeight = size;
                        scaledWidth = (int)(size * sourceAspect);
                    }

                    var x = (size - scaledWidth) / 2f;
                    var y = (size - scaledHeight) / 2f;
                    graphics.DrawImage(sourceImage, new RectangleF(x, y, scaledWidth, scaledHeight));

                    using var memoryStream = new MemoryStream();
                    targetBitmap.Save(memoryStream, ImageFormat.Png);
                    pngFrames.Add(memoryStream.ToArray());
                }

                WriteIcoFile(outputPath, IcoSizes, pngFrames);
            }, cancellationToken);
        }

        taskContext.AddStatusMessage($"{UiSymbols.Info} Generated ICO file with {IcoSizes.Length} sizes");
    }

    private static Bitmap LoadSourceImage(FileInfo sourceImagePath)
    {
        if (sourceImagePath.Extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            using var icon = new Icon(sourceImagePath.FullName);
            return icon.ToBitmap();
        }

        if (sourceImagePath.Extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return LoadSvgAsBitmap(sourceImagePath);
        }

        return new Bitmap(sourceImagePath.FullName);
    }

    private static Bitmap LoadSvgAsBitmap(FileInfo sourceImagePath)
    {
        var svg = new SKSvg();
        using var stream = File.OpenRead(sourceImagePath.FullName);
        svg.Load(stream);

        var picture = svg.Picture ?? throw new InvalidOperationException(
            $"Failed to render SVG image: {sourceImagePath.FullName}. The file may be corrupted or contain unsupported SVG features.");
        var bounds = picture.CullRect;

        int width = (int)Math.Ceiling(bounds.Width);
        int height = (int)Math.Ceiling(bounds.Height);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                $"SVG image has invalid dimensions ({width}x{height}): {sourceImagePath.FullName}. Ensure the SVG has a valid viewBox or width/height attributes.");
        }

        // Render at a reasonable minimum size for quality when scaling down to asset sizes
        const float minRenderDimension = 1024f;
        float scaleFactor = 1f;
        if (width < minRenderDimension || height < minRenderDimension)
        {
            scaleFactor = Math.Max(minRenderDimension / width, minRenderDimension / height);
            width = (int)Math.Ceiling(bounds.Width * scaleFactor);
            height = (int)Math.Ceiling(bounds.Height * scaleFactor);
        }

        // Render SVG to SKBitmap, then convert to System.Drawing.Bitmap
        using var skBitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(skBitmap))
        {
            canvas.Clear(SKColors.Transparent);

            // Translate to handle non-zero origin bounds, then scale
            if (bounds.Left != 0 || bounds.Top != 0)
            {
                canvas.Translate(-bounds.Left * scaleFactor, -bounds.Top * scaleFactor);
            }

            if (scaleFactor > 1f)
            {
                canvas.Scale(scaleFactor);
            }

            canvas.DrawPicture(picture);
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(skBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        // Create the Bitmap from a fresh MemoryStream that it will own.
        // Bitmap keeps a reference to the stream, so we must NOT dispose it.
        var ms = new MemoryStream(data.ToArray());
        return new Bitmap(ms);
    }

    private static int GetScalePercentage(float scale)
    {
        return (int)Math.Round(scale * 100, MidpointRounding.AwayFromZero);
    }

    private static bool IsTargetSizeAsset(ManifestAssetReference assetReference, string assetFileName)
    {
        // App icon assets (44x44) get targetsize variants regardless of naming convention
        // Supports both old naming (Square44x44Logo) and new naming (AppList)
        return assetReference.BaseWidth == 44
            && assetReference.BaseHeight == 44;
    }

    private static async Task<bool> TryGenerateAssetAsync(
        Bitmap sourceImage,
        string outputPath,
        string fileName,
        int targetWidth,
        int targetHeight,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        try
        {
            await GenerateAssetAsync(sourceImage, outputPath, targetWidth, targetHeight, cancellationToken);
            taskContext.AddDebugMessage($"  {UiSymbols.Check} Generated: {fileName} ({targetWidth}x{targetHeight})");
            return true;
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"  {UiSymbols.Warning} Failed to generate {fileName}: {ex.Message}");
            return false;
        }
    }

    private static void WriteIcoFile(string outputPath, int[] sizes, List<byte[]> pngFrames)
    {
        if (sizes.Length != pngFrames.Count)
        {
            throw new InvalidOperationException("ICO size and frame counts must match.");
        }

        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fileStream);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);

        var dataOffset = 6 + (16 * sizes.Length);
        for (var i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            var pngData = pngFrames[i];

            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)pngData.Length);
            writer.Write((uint)dataOffset);

            dataOffset += pngData.Length;
        }

        foreach (var pngData in pngFrames)
        {
            writer.Write(pngData);
        }
    }

    private static async Task GenerateAssetAsync(Bitmap sourceImage, string outputPath, int targetWidth, int targetHeight, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            var sourceAspect = (float)sourceImage.Width / sourceImage.Height;
            var targetAspect = (float)targetWidth / targetHeight;

            int scaledWidth;
            int scaledHeight;
            if (sourceAspect > targetAspect)
            {
                scaledWidth = targetWidth;
                scaledHeight = (int)(targetWidth / sourceAspect);
            }
            else
            {
                scaledHeight = targetHeight;
                scaledWidth = (int)(targetHeight * sourceAspect);
            }

            using var targetBitmap = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(targetBitmap);

            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.CompositingMode = CompositingMode.SourceOver;

            graphics.Clear(Color.Transparent);

            var x = (targetWidth - scaledWidth) / 2f;
            var y = (targetHeight - scaledHeight) / 2f;
            var destRect = new RectangleF(x, y, scaledWidth, scaledHeight);

            graphics.DrawImage(sourceImage, destRect);

            targetBitmap.Save(outputPath, ImageFormat.Png);
        }, cancellationToken);
    }
}
