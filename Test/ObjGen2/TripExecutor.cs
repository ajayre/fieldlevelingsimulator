using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ObjGen2
{
    public class TripExecutor
    {
        private const int ScaleFactor = 12;
        private const int TextAreaHeight = 200;
        private const int GridLineColor = 0x404040; // Dark gray RGB(64, 64, 64)
        private const int NoDataColor = 0x000000; // Black RGB(0, 0, 0)

        public void ExecuteTrips(FieldPlan fieldPlan, string outputFolder, bool annotateTrips = true)
        {
            if (fieldPlan?.Bins?.BinTable?.Bins == null || fieldPlan.Bins.BinTable.Bins.Count == 0)
            {
                throw new InvalidOperationException("No bin data available for trip execution");
            }

            // Generate initial BMP (Trip_0000.bmp)
            GenerateBMP(fieldPlan, outputFolder, "Trip_0000.bmp", null, annotateTrips);
            
            // Generate cut sheet BMP
            var cutSheet = new CutSheet();
            cutSheet.GenerateCutSheet(fieldPlan, outputFolder, "CutSheet.bmp");
            Console.WriteLine("CutSheet.bmp generated successfully!");

            // Execute each trip
            if (fieldPlan.Trips != null && fieldPlan.Trips.Count > 0)
            {
                foreach (var trip in fieldPlan.Trips)
                {
                    ExecuteTrip(fieldPlan, trip);
                    
                    // Generate BMP after each trip
                    var tripNumber = trip.TripIndex.ToString("D4");
                    var fileName = $"Trip_{tripNumber}.bmp";
                    GenerateBMP(fieldPlan, outputFolder, fileName, trip, annotateTrips);
                    
                    Console.WriteLine($"Trip {trip.TripIndex} executed. Generated {fileName}");
                }
            }

            // Generate error check BMP at the end of trip execution
            var errorChecker = new ErrorChecker();
            var toleranceMeters = 0.1 * 0.3048; // Convert 0.1 feet to meters
            errorChecker.GenerateErrorCheckBMP(fieldPlan, outputFolder, toleranceMeters);
            Console.WriteLine("ErrorCheck.bmp generated successfully!");
        }

        private void ExecuteTrip(FieldPlan fieldPlan, Trip trip)
        {
            // Process cut operations from trip
            if (trip.Cut?.Bins != null)
            {
                foreach (var binOperation in trip.Cut.Bins)
                {
                    if (binOperation.CutAmountM > 0)
                    {
                        // Find the corresponding bin in the BinTable
                        var bin = fieldPlan.Bins.BinTable.Bins.FirstOrDefault(b => 
                            b.IndexX == binOperation.IndexX && b.IndexY == binOperation.IndexY);
                        
                        if (bin != null)
                        {
                            // Remove the cut amount from the existing elevation
                            bin.ExistingElevationM -= binOperation.CutAmountM;
                            
                            // Ensure elevation doesn't go below zero
                            if (bin.ExistingElevationM < 0)
                            {
                                bin.ExistingElevationM = 0;
                            }
                        }
                    }
                }
            }

            // Process fill operations from trip
            if (trip.Fill?.Bins != null)
            {
                foreach (var binOperation in trip.Fill.Bins)
                {
                    if (binOperation.FillAmountM > 0)
                    {
                        // Find the corresponding bin in the BinTable
                        var bin = fieldPlan.Bins.BinTable.Bins.FirstOrDefault(b => 
                            b.IndexX == binOperation.IndexX && b.IndexY == binOperation.IndexY);
                        
                        if (bin != null)
                        {
                            // Add the fill amount to the existing elevation
                            bin.ExistingElevationM += binOperation.FillAmountM;
                        }
                    }
                }
            }
        }

        private void GenerateBMP(FieldPlan fieldPlan, string outputFolder, string fileName, Trip currentTrip = null, bool annotateTrips = true)
        {
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

            // Create elevation grid
            var elevationGrid = CreateElevationGrid(bins, minX, maxX, minY, maxY, gridWidth, gridHeight);

            // Use the initial elevation range from BinTable instead of recalculating
            var minElevation = fieldPlan.Bins.BinTable.InitialMinimumElevationM;
            var maxElevation = fieldPlan.Bins.BinTable.InitialMaximumElevationM;
            
            if (minElevation == 0.0 && maxElevation == 0.0)
            {
                throw new InvalidOperationException("No valid non-zero elevation data found in initial elevation range");
            }
            
            // If elevation range is too small, add some artificial range for visualization
            if (maxElevation - minElevation < 0.01) // Less than 1cm difference
            {
                var centerElevation = (minElevation + maxElevation) / 2;
                minElevation = centerElevation - 0.1; // 10cm below center
                maxElevation = centerElevation + 0.1; // 10cm above center
                Console.WriteLine($"Small elevation range detected. Using artificial range for better visualization.");
            }

            // Generate color palette
            var colorPalette = GenerateColorPalette();

            // Create BMP data
            var bmpData = CreateBMPData(elevationGrid, gridWidth, gridHeight, scaledWidth, scaledHeight, 
                minElevation, maxElevation, colorPalette, currentTrip, annotateTrips, minX, minY, fieldPlan);

            // Write BMP file
            var fullPath = Path.Combine(outputFolder, fileName);
            WriteBMPFile(fullPath, scaledWidth, scaledHeight, bmpData);
        }

        private double?[,] CreateElevationGrid(List<Bin> bins, int minX, int maxX, int minY, int maxY, 
            int gridWidth, int gridHeight)
        {
            var elevationGrid = new double?[gridHeight, gridWidth];

            foreach (var bin in bins)
            {
                var x = bin.IndexX - minX;
                var y = bin.IndexY - minY;

                if (x >= 0 && x < gridWidth && y >= 0 && y < gridHeight)
                {
                    elevationGrid[y, x] = bin.ExistingElevationM;
                }
            }

            return elevationGrid;
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

        private byte[] CreateBMPData(double?[,] elevationGrid, int gridWidth, int gridHeight, 
            int scaledWidth, int scaledHeight, double minElevation, double maxElevation, int[,] colorPalette,
            Trip currentTrip = null, bool annotateTrips = true, int minX = 0, int minY = 0, FieldPlan fieldPlan = null)
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
                        var elevation = elevationGrid[gridY, gridX];
                        
                        if (elevation.HasValue && elevation.Value != 0.0)
                        {
                            // Normalize elevation to 0-255 range
                            var normalizedElevation = (elevation.Value - minElevation) / (maxElevation - minElevation);
                            var colorIndex = Math.Max(0, Math.Min(255, (int)(normalizedElevation * 255)));

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

            // Draw annotations if enabled and trip is provided
            if (annotateTrips && currentTrip != null)
            {
                DrawAnnotations(imageData, scaledWidth, scaledHeight, currentTrip, minX, minY, fieldPlan);
            }

            return imageData;
        }

        private void DrawAnnotations(byte[] imageData, int scaledWidth, int scaledHeight, Trip trip, int minX, int minY, FieldPlan fieldPlan)
        {
            // Draw borders around bins affected by this trip
            DrawAffectedBinBorders(imageData, scaledWidth, scaledHeight, trip, minX, minY, fieldPlan);

            // Draw cut operation annotations (using operation-level coordinates)
            if (trip.Cut?.Coordinates != null)
            {
                DrawOperationAnnotations(imageData, scaledWidth, scaledHeight, trip.Cut.Coordinates, 
                    minX, minY, true, fieldPlan); // true for cut operation
            }

            // Draw fill operation annotations (using operation-level coordinates)
            if (trip.Fill?.Coordinates != null)
            {
                DrawOperationAnnotations(imageData, scaledWidth, scaledHeight, trip.Fill.Coordinates, 
                    minX, minY, false, fieldPlan); // false for fill operation
            }
        }

        private void DrawOperationAnnotations(byte[] imageData, int scaledWidth, int scaledHeight, 
            OperationCoordinates coords, int minX, int minY, bool isCutOperation, FieldPlan fieldPlan)
        {
            // Use bin coordinates directly if available, otherwise fall back to lat/lon lookup
            int startBinX, startBinY, endBinX, endBinY;
            
            if (coords.StartBinX != 0 || coords.StartBinY != 0 || coords.EndBinX != 0 || coords.EndBinY != 0)
            {
                // Use the new bin coordinates directly
                startBinX = coords.StartBinX;
                startBinY = coords.StartBinY;
                endBinX = coords.EndBinX;
                endBinY = coords.EndBinY;
            }
            else
            {
                // Fallback to lat/lon lookup for backward compatibility
                var startBin = FindBinByCoordinates(coords.StartLatitude, coords.StartLongitude, fieldPlan);
                var endBin = FindBinByCoordinates(coords.StopLatitude, coords.StopLongitude, fieldPlan);
                
                if (startBin == null || endBin == null) return;
                
                startBinX = startBin.IndexX;
                startBinY = startBin.IndexY;
                endBinX = endBin.IndexX;
                endBinY = endBin.IndexY;
            }
            
            // Convert bin indices to grid positions
            var startX = (startBinX - minX) * ScaleFactor + (ScaleFactor / 2);
            var startY = (startBinY - minY) * ScaleFactor + (ScaleFactor / 2);
            var endX = (endBinX - minX) * ScaleFactor + (ScaleFactor / 2);
            var endY = (endBinY - minY) * ScaleFactor + (ScaleFactor / 2);

            // Ensure coordinates are within bounds
            if (startX >= 0 && startX < scaledWidth && startY >= 0 && startY < scaledHeight - TextAreaHeight)
            {
                // Draw green start dot
                DrawDot(imageData, scaledWidth, scaledHeight, startX, startY, 0x00FF00); // Green
            }

            if (endX >= 0 && endX < scaledWidth && endY >= 0 && endY < scaledHeight - TextAreaHeight)
            {
                // Draw red end dot
                DrawDot(imageData, scaledWidth, scaledHeight, endX, endY, 0xFF0000); // Red
            }

            // Draw arrow from start to end
            if (startX >= 0 && startX < scaledWidth && startY >= 0 && startY < scaledHeight - TextAreaHeight &&
                endX >= 0 && endX < scaledWidth && endY >= 0 && endY < scaledHeight - TextAreaHeight)
            {
                DrawArrow(imageData, scaledWidth, scaledHeight, startX, startY, endX, endY, isCutOperation);
            }
        }

        private void DrawDot(byte[] imageData, int scaledWidth, int scaledHeight, int x, int y, int color)
        {
            int radius = 6; // Dot radius
            
            // Draw black border first (1 pixel wider)
            for (int dy = -radius - 1; dy <= radius + 1; dy++)
            {
                for (int dx = -radius - 1; dx <= radius + 1; dx++)
                {
                    int pixelX = x + dx;
                    int pixelY = y + dy;
                    
                    if (pixelX >= 0 && pixelX < scaledWidth && pixelY >= 0 && pixelY < scaledHeight - TextAreaHeight)
                    {
                        // Check if pixel is within outer circle (border)
                        if (dx * dx + dy * dy <= (radius + 1) * (radius + 1))
                        {
                            int pixelIndex = (pixelY * scaledWidth + pixelX) * 3;
                            imageData[pixelIndex + 2] = 0; // Black border
                            imageData[pixelIndex + 1] = 0;
                            imageData[pixelIndex + 0] = 0;
                        }
                    }
                }
            }
            
            // Draw colored dot on top
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int pixelX = x + dx;
                    int pixelY = y + dy;
                    
                    if (pixelX >= 0 && pixelX < scaledWidth && pixelY >= 0 && pixelY < scaledHeight - TextAreaHeight)
                    {
                        // Check if pixel is within inner circle (colored part)
                        if (dx * dx + dy * dy <= radius * radius)
                        {
                            int pixelIndex = (pixelY * scaledWidth + pixelX) * 3;
                            imageData[pixelIndex + 2] = (byte)((color >> 16) & 0xFF); // Red
                            imageData[pixelIndex + 1] = (byte)((color >> 8) & 0xFF);  // Green
                            imageData[pixelIndex + 0] = (byte)(color & 0xFF);        // Blue
                        }
                    }
                }
            }
        }

        private void DrawArrow(byte[] imageData, int scaledWidth, int scaledHeight, int startX, int startY, int endX, int endY, bool isCutOperation)
        {
            // Use black color for all arrows
            int arrowColor = 0x000000; // Black for all arrows

            // Draw line from start to end
            DrawLine(imageData, scaledWidth, scaledHeight, startX, startY, endX, endY, arrowColor);

            // Draw arrowhead at end
            DrawArrowhead(imageData, scaledWidth, scaledHeight, startX, startY, endX, endY, arrowColor);
        }

        private void DrawLine(byte[] imageData, int scaledWidth, int scaledHeight, int x1, int y1, int x2, int y2, int color)
        {
            // Draw thick line by drawing multiple parallel lines
            int thickness = 6; // Line thickness
            
            // Calculate perpendicular direction for thickness
            double dx = x2 - x1;
            double dy = y2 - y1;
            double length = Math.Sqrt(dx * dx + dy * dy);
            
            if (length == 0) return;
            
            // Normalize direction
            dx /= length;
            dy /= length;
            
            // Perpendicular vector for thickness
            double perpX = -dy;
            double perpY = dx;
            
            // Draw multiple parallel lines for thickness
            for (int i = -thickness / 2; i <= thickness / 2; i++)
            {
                int offsetX = (int)(i * perpX);
                int offsetY = (int)(i * perpY);
                
                DrawSingleLine(imageData, scaledWidth, scaledHeight, 
                    x1 + offsetX, y1 + offsetY, x2 + offsetX, y2 + offsetY, color);
            }
        }

        private void DrawSingleLine(byte[] imageData, int scaledWidth, int scaledHeight, int x1, int y1, int x2, int y2, int color)
        {
            int dx = Math.Abs(x2 - x1);
            int dy = Math.Abs(y2 - y1);
            int sx = x1 < x2 ? 1 : -1;
            int sy = y1 < y2 ? 1 : -1;
            int err = dx - dy;

            int x = x1;
            int y = y1;

            while (true)
            {
                if (x >= 0 && x < scaledWidth && y >= 0 && y < scaledHeight - TextAreaHeight)
                {
                    int pixelIndex = (y * scaledWidth + x) * 3;
                    imageData[pixelIndex + 2] = (byte)((color >> 16) & 0xFF); // Red
                    imageData[pixelIndex + 1] = (byte)((color >> 8) & 0xFF);  // Green
                    imageData[pixelIndex + 0] = (byte)(color & 0xFF);        // Blue
                }

                if (x == x2 && y == y2) break;

                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y += sy;
                }
            }
        }

        private void DrawArrowhead(byte[] imageData, int scaledWidth, int scaledHeight, int startX, int startY, int endX, int endY, int color)
        {
            // Calculate direction vector
            double dx = endX - startX;
            double dy = endY - startY;
            double length = Math.Sqrt(dx * dx + dy * dy);
            
            if (length == 0) return;

            // Normalize direction vector
            dx /= length;
            dy /= length;

            // Arrowhead size
            int arrowSize = 10; // Increased from 5 to 10
            
            // Calculate arrowhead points
            double angle = Math.PI / 6; // 30 degrees
            double cosAngle = Math.Cos(angle);
            double sinAngle = Math.Sin(angle);

            // Left arrowhead point
            int leftX = (int)(endX - arrowSize * (dx * cosAngle + dy * sinAngle));
            int leftY = (int)(endY - arrowSize * (dy * cosAngle - dx * sinAngle));

            // Right arrowhead point
            int rightX = (int)(endX - arrowSize * (dx * cosAngle - dy * sinAngle));
            int rightY = (int)(endY - arrowSize * (dy * cosAngle + dx * sinAngle));

            // Draw arrowhead lines
            DrawLine(imageData, scaledWidth, scaledHeight, endX, endY, leftX, leftY, color);
            DrawLine(imageData, scaledWidth, scaledHeight, endX, endY, rightX, rightY, color);
        }

        private Bin FindBinByCoordinates(double latitude, double longitude, FieldPlan fieldPlan)
        {
            if (fieldPlan?.Bins?.BinTable?.Bins == null) return null;

            // Find the bin that contains the given coordinates
            foreach (var bin in fieldPlan.Bins.BinTable.Bins)
            {
                if (bin.SouthwestCorner != null && bin.NortheastCorner != null)
                {
                    // Check if the coordinates fall within this bin's bounds
                    if (latitude >= bin.SouthwestCorner.Latitude && latitude <= bin.NortheastCorner.Latitude &&
                        longitude >= bin.SouthwestCorner.Longitude && longitude <= bin.NortheastCorner.Longitude)
                    {
                        return bin;
                    }
                }
            }

            return null;
        }

        private void DrawAffectedBinBorders(byte[] imageData, int scaledWidth, int scaledHeight, Trip trip, int minX, int minY, FieldPlan fieldPlan)
        {
            // Get all bins affected by this trip
            var affectedBins = GetAffectedBins(trip, fieldPlan);
            
            foreach (var bin in affectedBins)
            {
                // Calculate bin position in the image
                var binX = (bin.IndexX - minX) * ScaleFactor;
                var binY = (bin.IndexY - minY) * ScaleFactor;
                
                // Draw black border around the bin
                DrawBinBorder(imageData, scaledWidth, scaledHeight, binX, binY);
            }
        }

        private List<Bin> GetAffectedBins(Trip trip, FieldPlan fieldPlan)
        {
            var affectedBins = new List<Bin>();
            
            if (fieldPlan?.Bins?.BinTable?.Bins == null) return affectedBins;

            // Get bins affected by cut operations
            if (trip.Cut?.Bins != null)
            {
                foreach (var binOperation in trip.Cut.Bins)
                {
                    var bin = fieldPlan.Bins.BinTable.Bins.FirstOrDefault(b => 
                        b.IndexX == binOperation.IndexX && b.IndexY == binOperation.IndexY);
                    if (bin != null && !affectedBins.Contains(bin))
                    {
                        affectedBins.Add(bin);
                    }
                }
            }

            // Get bins affected by fill operations
            if (trip.Fill?.Bins != null)
            {
                foreach (var binOperation in trip.Fill.Bins)
                {
                    var bin = fieldPlan.Bins.BinTable.Bins.FirstOrDefault(b => 
                        b.IndexX == binOperation.IndexX && b.IndexY == binOperation.IndexY);
                    if (bin != null && !affectedBins.Contains(bin))
                    {
                        affectedBins.Add(bin);
                    }
                }
            }

            return affectedBins;
        }

        private void DrawBinBorder(byte[] imageData, int scaledWidth, int scaledHeight, int binX, int binY)
        {
            int borderThickness = 2; // Bold border thickness
            int blackColor = 0x000000; // Black color

            // Draw top border
            for (int x = binX; x < binX + ScaleFactor; x++)
            {
                for (int t = 0; t < borderThickness; t++)
                {
                    int pixelY = binY + t;
                    if (x >= 0 && x < scaledWidth && pixelY >= 0 && pixelY < scaledHeight - TextAreaHeight)
                    {
                        int pixelIndex = (pixelY * scaledWidth + x) * 3;
                        imageData[pixelIndex + 2] = 0; // Red
                        imageData[pixelIndex + 1] = 0; // Green
                        imageData[pixelIndex + 0] = 0; // Blue
                    }
                }
            }

            // Draw bottom border
            for (int x = binX; x < binX + ScaleFactor; x++)
            {
                for (int t = 0; t < borderThickness; t++)
                {
                    int pixelY = binY + ScaleFactor - 1 - t;
                    if (x >= 0 && x < scaledWidth && pixelY >= 0 && pixelY < scaledHeight - TextAreaHeight)
                    {
                        int pixelIndex = (pixelY * scaledWidth + x) * 3;
                        imageData[pixelIndex + 2] = 0; // Red
                        imageData[pixelIndex + 1] = 0; // Green
                        imageData[pixelIndex + 0] = 0; // Blue
                    }
                }
            }

            // Draw left border
            for (int y = binY; y < binY + ScaleFactor; y++)
            {
                for (int t = 0; t < borderThickness; t++)
                {
                    int pixelX = binX + t;
                    if (pixelX >= 0 && pixelX < scaledWidth && y >= 0 && y < scaledHeight - TextAreaHeight)
                    {
                        int pixelIndex = (y * scaledWidth + pixelX) * 3;
                        imageData[pixelIndex + 2] = 0; // Red
                        imageData[pixelIndex + 1] = 0; // Green
                        imageData[pixelIndex + 0] = 0; // Blue
                    }
                }
            }

            // Draw right border
            for (int y = binY; y < binY + ScaleFactor; y++)
            {
                for (int t = 0; t < borderThickness; t++)
                {
                    int pixelX = binX + ScaleFactor - 1 - t;
                    if (pixelX >= 0 && pixelX < scaledWidth && y >= 0 && y < scaledHeight - TextAreaHeight)
                    {
                        int pixelIndex = (y * scaledWidth + pixelX) * 3;
                        imageData[pixelIndex + 2] = 0; // Red
                        imageData[pixelIndex + 1] = 0; // Green
                        imageData[pixelIndex + 0] = 0; // Blue
                    }
                }
            }
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
