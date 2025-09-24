using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ObjGen2
{
    public class ErrorChecker
    {
        private const int ScaleFactor = 12;
        private const int TextAreaHeight = 200;
        private const int GridLineColor = 0x404040; // Dark gray RGB(64, 64, 64)
        
        // Color constants for error visualization
        private const int RedColor = 0xFF0000;    // Red for too high
        private const int BlueColor = 0x0000FF;  // Blue for too low
        private const int GreenColor = 0x00FF00; // Green for within tolerance
        private const int BlackColor = 0x000000; // Black for no data

        public void GenerateErrorCheckBMP(FieldPlan fieldPlan, string outputFolder, double toleranceMeters)
        {
            if (fieldPlan?.Bins?.BinTable?.Bins == null || fieldPlan.Bins.BinTable.Bins.Count == 0)
            {
                throw new InvalidOperationException("No bin data available for error check BMP generation");
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

            // Create error grid
            var errorGrid = CreateErrorGrid(bins, minX, maxX, minY, maxY, gridWidth, gridHeight, toleranceMeters);

            // Create BMP data
            var bmpData = CreateBMPData(errorGrid, gridWidth, gridHeight, scaledWidth, scaledHeight);

            // Write BMP file
            var fileName = Path.Combine(outputFolder, "ErrorCheck.bmp");
            WriteBMPFile(fileName, scaledWidth, scaledHeight, bmpData);
        }

        private int[,] CreateErrorGrid(List<Bin> bins, int minX, int maxX, int minY, int maxY, 
            int gridWidth, int gridHeight, double toleranceMeters)
        {
            var errorGrid = new int[gridHeight, gridWidth];

            foreach (var bin in bins)
            {
                var x = bin.IndexX - minX;
                var y = bin.IndexY - minY;

                if (x >= 0 && x < gridWidth && y >= 0 && y < gridHeight)
                {
                    // Calculate elevation difference
                    var elevationDifference = bin.ExistingElevationM - bin.TargetElevationM;

                    // Determine color based on tolerance
                    if (elevationDifference > toleranceMeters)
                    {
                        // Too high - Red
                        errorGrid[y, x] = RedColor;
                    }
                    else if (elevationDifference < -toleranceMeters)
                    {
                        // Too low - Blue
                        errorGrid[y, x] = BlueColor;
                    }
                    else
                    {
                        // Within tolerance - Green
                        errorGrid[y, x] = GreenColor;
                    }
                }
            }

            return errorGrid;
        }

        private byte[] CreateBMPData(int[,] errorGrid, int gridWidth, int gridHeight, 
            int scaledWidth, int scaledHeight)
        {
            var imageData = new byte[scaledWidth * scaledHeight * 3];

            for (int y = 0; y < scaledHeight - TextAreaHeight; y++)
            {
                for (int x = 0; x < scaledWidth; x++)
                {
                    var gridX = x / ScaleFactor;
                    var gridY = y / ScaleFactor;

                    int pixelIndex = (y * scaledWidth + x) * 3;

                    if (gridX < gridWidth && gridY < gridHeight)
                    {
                        var color = errorGrid[gridY, gridX];
                        
                        // Extract RGB components
                        imageData[pixelIndex + 2] = (byte)((color >> 16) & 0xFF); // Red
                        imageData[pixelIndex + 1] = (byte)((color >> 8) & 0xFF);  // Green
                        imageData[pixelIndex + 0] = (byte)(color & 0xFF);        // Blue
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
