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
    // Define the required asset specifications for MSIX packages (used for fallback/default generation)
    private static readonly (string FileName, int Width, int Height)[] AssetSpecifications =
    [
        ("Square44x44Logo.png", 44, 44),
        ("Square44x44Logo.scale-200.png", 88, 88),
        ("Square44x44Logo.targetsize-24_altform-unplated.png", 24, 24),
        ("Square150x150Logo.png", 150, 150),
        ("Square150x150Logo.scale-200.png", 300, 300),
        ("Wide310x150Logo.png", 310, 150),
        ("Wide310x150Logo.scale-200.png", 620, 300),
        ("StoreLogo.png", 50, 50),
    ];

    // Scale factors to generate for each asset
    private static readonly (string Suffix, float Scale)[] ScaleVariants =
    [
        ("", 1.0f),                 // Base (scale-100)
        (".scale-200", 2.0f),       // scale-200
    ];

    // Target size variants for square assets (for taskbar, Start menu, etc.)
    private static readonly int[] TargetSizes = [16, 24, 32, 48, 256];

    public async Task GenerateAssetsAsync(FileInfo sourceImagePath, DirectoryInfo outputDirectory, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        if (!sourceImagePath.Exists)
        {
            throw new FileNotFoundException($"Source image not found: {sourceImagePath.FullName}");
        }

        taskContext.AddStatusMessage($"{UiSymbols.Info} Generating MSIX image assets from: {sourceImagePath.FullName}");

        // Load the source image
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
            taskContext.AddDebugMessage($"Source image size: {sourceImage.Width}x{sourceImage.Height}");

            // Ensure output directory exists
            if (!outputDirectory.Exists)
            {
                outputDirectory.Create();
            }

            // Generate each required asset
            var successCount = 0;
            foreach (var (fileName, width, height) in AssetSpecifications)
            {
                try
                {
                    var outputPath = Path.Combine(outputDirectory.FullName, fileName);
                    await GenerateAssetAsync(sourceImage, outputPath, width, height, cancellationToken);
                    successCount++;
                    taskContext.AddDebugMessage($"  {UiSymbols.Check} Generated: {fileName} ({width}x{height})");
                }
                catch (Exception ex)
                {
                    taskContext.AddDebugMessage($"  {UiSymbols.Warning} Failed to generate {fileName}: {ex.Message}");
                }
            }
            if (successCount == AssetSpecifications.Length)
            {
                taskContext.AddStatusMessage($"{UiSymbols.Info} Successfully generated {AssetSpecifications.Length} image assets in: {outputDirectory.FullName}");
            }
            else
            {
                taskContext.AddStatusMessage($"{UiSymbols.Info} Successfully generated {successCount} of {AssetSpecifications.Length} image assets in: {outputDirectory.FullName}");
            }
        }
    }

    public async Task GenerateAssetsFromManifestAsync(
        FileInfo sourceImagePath,
        DirectoryInfo manifestDirectory,
        IReadOnlyList<ManifestAssetReference> assetReferences,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        if (!sourceImagePath.Exists)
        {
            throw new FileNotFoundException($"Source image not found: {sourceImagePath.FullName}");
        }

        if (assetReferences.Count == 0)
        {
            taskContext.AddStatusMessage($"{UiSymbols.Warning} No asset references found in manifest. No assets generated.");
            return;
        }

        taskContext.AddStatusMessage($"{UiSymbols.Info} Generating MSIX image assets from manifest references: {sourceImagePath.FullName}");

        // Load the source image
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
            taskContext.AddDebugMessage($"Source image size: {sourceImage.Width}x{sourceImage.Height}");

            var successCount = 0;
            var totalCount = 0;

            foreach (var assetRef in assetReferences)
            {
                // Get the directory and base filename
                var assetFullPath = Path.Combine(manifestDirectory.FullName, assetRef.RelativePath);
                var assetDirectory = Path.GetDirectoryName(assetFullPath);
                var assetFileName = Path.GetFileNameWithoutExtension(assetRef.RelativePath);
                var assetExtension = Path.GetExtension(assetRef.RelativePath);

                // Ensure asset directory exists
                if (!string.IsNullOrEmpty(assetDirectory) && !Directory.Exists(assetDirectory))
                {
                    Directory.CreateDirectory(assetDirectory);
                }

                // Generate scale variants
                foreach (var (suffix, scale) in ScaleVariants)
                {
                    totalCount++;
                    var scaledWidth = (int)(assetRef.BaseWidth * scale);
                    var scaledHeight = (int)(assetRef.BaseHeight * scale);
                    var scaledFileName = $"{assetFileName}{suffix}{assetExtension}";
                    var scaledPath = Path.Combine(assetDirectory ?? manifestDirectory.FullName, scaledFileName);

                    try
                    {
                        await GenerateAssetAsync(sourceImage, scaledPath, scaledWidth, scaledHeight, cancellationToken);
                        successCount++;
                        taskContext.AddDebugMessage($"  {UiSymbols.Check} Generated: {scaledFileName} ({scaledWidth}x{scaledHeight})");
                    }
                    catch (Exception ex)
                    {
                        taskContext.AddDebugMessage($"  {UiSymbols.Warning} Failed to generate {scaledFileName}: {ex.Message}");
                    }
                }

                // Generate targetsize variants for square assets (used for taskbar icons, etc.)
                if (assetRef.BaseWidth == assetRef.BaseHeight && assetFileName.Contains("44x44", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var targetSize in TargetSizes)
                    {
                        totalCount++;
                        var targetFileName = $"{assetFileName}.targetsize-{targetSize}_altform-unplated{assetExtension}";
                        var targetPath = Path.Combine(assetDirectory ?? manifestDirectory.FullName, targetFileName);

                        try
                        {
                            await GenerateAssetAsync(sourceImage, targetPath, targetSize, targetSize, cancellationToken);
                            successCount++;
                            taskContext.AddDebugMessage($"  {UiSymbols.Check} Generated: {targetFileName} ({targetSize}x{targetSize})");
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"  {UiSymbols.Warning} Failed to generate {targetFileName}: {ex.Message}");
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

    private static async Task GenerateAssetAsync(Bitmap sourceImage, string outputPath, int targetWidth, int targetHeight, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            // Calculate scaling to fit target dimensions while maintaining aspect ratio
            var sourceAspect = (float)sourceImage.Width / sourceImage.Height;
            var targetAspect = (float)targetWidth / targetHeight;

            int scaledWidth, scaledHeight;
            if (sourceAspect > targetAspect)
            {
                // Source is wider - fit to width
                scaledWidth = targetWidth;
                scaledHeight = (int)(targetWidth / sourceAspect);
            }
            else
            {
                // Source is taller - fit to height
                scaledHeight = targetHeight;
                scaledWidth = (int)(targetHeight * sourceAspect);
            }

            // Create the target bitmap with the required dimensions
            using var targetBitmap = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(targetBitmap);

            // Set high-quality rendering options
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.CompositingMode = CompositingMode.SourceOver;

            // Fill with transparent background
            graphics.Clear(Color.Transparent);

            // Calculate position to center the scaled image
            var x = (targetWidth - scaledWidth) / 2f;
            var y = (targetHeight - scaledHeight) / 2f;
            var destRect = new RectangleF(x, y, scaledWidth, scaledHeight);

            // Draw the scaled image
            graphics.DrawImage(sourceImage, destRect);

            // Save as PNG
            targetBitmap.Save(outputPath, ImageFormat.Png);
        }, cancellationToken);
    }
}
