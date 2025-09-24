using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ObjGen2
{
    public class CutSheet
    {
        private const int ScaleFactor = 12;
        private const int TextAreaHeight = 200;
        private const int GridLineColor = 0x404040; // Dark gray RGB(64, 64, 64)
        private const int NoDataColor = 0x000000; // Black RGB(0, 0, 0)

        public void GenerateCutSheet(FieldPlan fieldPlan, string outputFolder, string fileName)
        {
            if (fieldPlan?.Bins?.BinTable?.Bins == null || fieldPlan.Bins.BinTable.Bins.Count == 0)
            {
                throw new InvalidOperationException("No bin data available for cut sheet generation");
            }

            var bins = fieldPlan.Bins.BinTable.Bins;
            var minX = bins.Min(b => b.IndexX);
            var maxX = bins.Max(b => b.IndexX);
            var minY = bins.Min(b => b.IndexY);
            var maxY = bins.Max(b => b.IndexY);

            var gridWidth = maxX - minX + 1;
            var gridHeight = maxY - minY + 1;

            // Calculate image dimensions
            var scaledWidth = gridWidth * ScaleFactor;
            var scaledHeight = (gridHeight * ScaleFactor) + TextAreaHeight;

            // Create cut/fill grid (ignoring bins with zero elevation)
            var cutFillGrid = CreateCutFillGrid(bins, minX, maxX, minY, maxY, gridWidth, gridHeight);

            // Calculate cut/fill range for normalization
            var validCutFillValues = cutFillGrid.Cast<double?>().Where(v => v.HasValue).Select(v => v.Value).ToList();
            if (!validCutFillValues.Any())
            {
                throw new InvalidOperationException("No valid cut/fill data found");
            }

            var minCutFill = validCutFillValues.Min();
            var maxCutFill = validCutFillValues.Max();

            // If cut/fill range is too small, add some artificial range for visualization
            if (maxCutFill - minCutFill < 0.001) // Less than 1mm difference
            {
                var centerValue = (minCutFill + maxCutFill) / 2;
                minCutFill = centerValue - 0.01; // 1cm below center
                maxCutFill = centerValue + 0.01; // 1cm above center
                Console.WriteLine($"Small cut/fill range detected. Using artificial range for better visualization.");
            }

            // Generate color palette
            var colorPalette = GenerateColorPalette();

            // Create BMP data
            var bmpData = CreateBMPData(cutFillGrid, gridWidth, gridHeight, scaledWidth, scaledHeight, 
                minCutFill, maxCutFill, colorPalette);

            // Write BMP file
            var fullPath = Path.Combine(outputFolder, fileName);
            WriteBMPFile(fullPath, scaledWidth, scaledHeight, bmpData);
        }

        private double?[,] CreateCutFillGrid(List<Bin> bins, int minX, int maxX, int minY, int maxY, 
            int gridWidth, int gridHeight)
        {
            var cutFillGrid = new double?[gridHeight, gridWidth];

            foreach (var bin in bins)
            {
                // Skip bins with zero elevation
                if (bin.ExistingElevationM == 0.0)
                    continue;

                var x = bin.IndexX - minX;
                var y = bin.IndexY - minY;

                if (x >= 0 && x < gridWidth && y >= 0 && y < gridHeight)
                {
                    // Calculate net cut/fill amount (positive = net cut, negative = net fill)
                    cutFillGrid[y, x] = bin.CutAmountM - bin.FillAmountM;
                }
            }

            return cutFillGrid;
        }

        private int[,] GenerateColorPalette()
        {
            var palette = new int[256, 3]; // 256 colors, RGB values

            for (int i = 0; i < 256; i++)
            {
                var hue = 240.0 - (i / 255.0) * 240.0; // Blue (240°) to Red (0°)
                var rgb = HsvToRgb(hue, 1.0, 1.0);
                
                palette[i, 0] = (int)(rgb[0] * 255); // Red
                palette[i, 1] = (int)(rgb[1] * 255); // Green
                palette[i, 2] = (int)(rgb[2] * 255); // Blue
            }

            return palette;
        }

        private double[] HsvToRgb(double h, double s, double v)
        {
            h = h % 360.0;
            if (h < 0) h += 360.0;

            var c = v * s;
            var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            var m = v - c;

            double r, g, b;

            if (h >= 0 && h < 60)
            {
                r = c; g = x; b = 0;
            }
            else if (h >= 60 && h < 120)
            {
                r = x; g = c; b = 0;
            }
            else if (h >= 120 && h < 180)
            {
                r = 0; g = c; b = x;
            }
            else if (h >= 180 && h < 240)
            {
                r = 0; g = x; b = c;
            }
            else if (h >= 240 && h < 300)
            {
                r = x; g = 0; b = c;
            }
            else
            {
                r = c; g = 0; b = x;
            }

            return new double[] { r + m, g + m, b + m };
        }

        private byte[] CreateBMPData(double?[,] cutFillGrid, int gridWidth, int gridHeight, 
            int scaledWidth, int scaledHeight, double minCutFill, double maxCutFill, int[,] colorPalette)
        {
            var imageData = new byte[scaledWidth * scaledHeight * 3];
            var rowPadding = (4 - (scaledWidth * 3) % 4) % 4;

            for (int y = 0; y < scaledHeight - TextAreaHeight; y++)
            {
                for (int x = 0; x < scaledWidth; x++)
                {
                    var gridX = x / ScaleFactor;
                    var gridY = y / ScaleFactor;

                    int pixelIndex = (y * scaledWidth + x) * 3;

                    if (gridX < gridWidth && gridY < gridHeight)
                    {
                        var cutFillValue = cutFillGrid[gridY, gridX];
                        
                        if (cutFillValue.HasValue)
                        {
                            // Normalize cut/fill value to 0-255 range
                            var normalizedValue = (cutFillValue.Value - minCutFill) / (maxCutFill - minCutFill);
                            var colorIndex = Math.Max(0, Math.Min(255, (int)(normalizedValue * 255)));

                            var r = (byte)colorPalette[colorIndex, 0];
                            var g = (byte)colorPalette[colorIndex, 1];
                            var b = (byte)colorPalette[colorIndex, 2];

                            // Apply color from palette
                            imageData[pixelIndex + 2] = (byte)colorPalette[colorIndex, 0]; // Red
                            imageData[pixelIndex + 1] = (byte)colorPalette[colorIndex, 1]; // Green
                            imageData[pixelIndex + 0] = (byte)colorPalette[colorIndex, 2]; // Blue
                        }
                        else
                        {
                            // No data - black
                            imageData[pixelIndex + 2] = 0; // Red
                            imageData[pixelIndex + 1] = 0; // Green
                            imageData[pixelIndex + 0] = 0; // Blue
                        }
                    }
                    else
                    {
                        // Outside grid - black
                        imageData[pixelIndex + 2] = 0; // Red
                        imageData[pixelIndex + 1] = 0; // Green
                        imageData[pixelIndex + 0] = 0; // Blue
                    }

                    // Draw grid lines
                    if ((x % ScaleFactor == 0) || (y % ScaleFactor == 0))
                    {
                        imageData[pixelIndex + 2] = 0x40; // Red component of dark gray
                        imageData[pixelIndex + 1] = 0x40; // Green component of dark gray
                        imageData[pixelIndex + 0] = 0x40; // Blue component of dark gray
                    }
                }
            }

            // Fill text area with black
            for (int y = scaledHeight - TextAreaHeight; y < scaledHeight; y++)
            {
                for (int x = 0; x < scaledWidth; x++)
                {
                    int pixelIndex = (y * scaledWidth + x) * 3;
                    imageData[pixelIndex + 2] = 0; // Red
                    imageData[pixelIndex + 1] = 0; // Green
                    imageData[pixelIndex + 0] = 0; // Blue
                }
            }

            return imageData;
        }

        private void WriteBMPFile(string fileName, int width, int height, byte[] imageData)
        {
            var rowPadding = (4 - (width * 3) % 4) % 4;
            var imageSize = width * height * 3 + height * rowPadding;
            var fileSize = 54 + imageSize;

            using (var writer = new BinaryWriter(File.Create(fileName)))
            {
                // BMP Header (54 bytes)
                writer.Write((byte)'B'); // Signature
                writer.Write((byte)'M');
                writer.Write(fileSize); // File size
                writer.Write(0); // Reserved
                writer.Write(54); // Offset to pixel data
                writer.Write(40); // DIB header size
                writer.Write(width); // Image width
                writer.Write(height); // Image height
                writer.Write((short)1); // Planes
                writer.Write((short)24); // Bits per pixel
                writer.Write(0); // Compression
                writer.Write(imageSize); // Image size
                writer.Write(0); // X pixels per meter
                writer.Write(0); // Y pixels per meter
                writer.Write(0); // Colors in color table
                writer.Write(0); // Important color count

                // Write pixel data (bottom-to-top for BMP format)
                for (int y = height - 1; y >= 0; y--)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Flip Y coordinate to account for BMP bottom-to-top storage
                        int flippedY = height - 1 - y;
                        int pixelIndex = (flippedY * width + x) * 3;
                        writer.Write(imageData[pixelIndex + 0]); // Blue
                        writer.Write(imageData[pixelIndex + 1]); // Green
                        writer.Write(imageData[pixelIndex + 2]); // Red
                    }

                    // Write row padding
                    for (int p = 0; p < rowPadding; p++)
                    {
                        writer.Write((byte)0);
                    }
                }
            }
        }
    }
}
