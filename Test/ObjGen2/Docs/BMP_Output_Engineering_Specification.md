# BMP Output Engineering Specification
## Field Leveling Simulator - ObjGen

### Document Information
- **Version**: 1.0
- **Date**: 2024
- **Project**: Field Leveling Simulator
- **Component**: BMP Output Generation

---

## 1. Overview

The BMP output system generates elevation visualization images in Windows Bitmap format, displaying the evolving terrain surface after each earthmoving operation. The output combines elevation data with grid overlays, text annotations, and color-coded elevation mapping.

## 2. Technical Specifications

### 2.1 File Format
- **Format**: Windows Bitmap (BMP)
- **Color Depth**: 24-bit RGB (8 bits per channel)
- **Compression**: None (uncompressed)
- **Byte Order**: BGR (Blue, Green, Red) pixel storage
- **Row Padding**: 4-byte boundary alignment

### 2.2 Grid System
- **Base Grid Size**: 0.6096m × 0.6096m (2ft × 2ft bins)
- **Projection Origin**: Site centroid (calculated from AGD data)
- **Grid Origin**: Southwest corner (MinBx, MinBy) - bottom-left of the grid
- **Coordinate System**: Local equirectangular projection
- **Earth Radius**: 6,371,000m (for coordinate conversion)

### 2.3 Image Dimensions and Scaling

#### Base Grid Dimensions
```
Width = mesh.MaxBx - mesh.MinBx + 1
Height = mesh.MaxBy - mesh.MinBy + 1
```

#### Output Scaling
- **Scale Factor**: 12x (each grid cell = 12×12 pixels)
- **Text Area Height**: 200 pixels (additional space for annotations)
- **Final Dimensions**:
  - `scaledWidth = width × 12`
  - `scaledHeight = (height × 12) + 200`

#### Example Calculation
For a 50×30 grid:
- Base grid: 50×30 bins
- Scaled image: 600×560 pixels (600×360 + 200 text area)

## 3. Elevation Data Processing

### 3.1 Elevation Values
- **Source**: Current surface elevation (`vertex.ZCur`)
- **Vertical Exaggeration**: Applied via `VE` parameter (default: 10.0)
- **Formula**: `elevationGrid[y,x] = vertex.ZCur × VE`
- **Units**: Meters (with vertical exaggeration applied)

### 3.2 Elevation Range Calculation
```csharp
// Dynamic range calculation for each frame
minElevation = MIN(elevationGrid[all_valid_cells])
maxElevation = MAX(elevationGrid[all_valid_cells])
```

### 3.3 Color Mapping Algorithm

#### Normalization
```csharp
normalizedElevation = (elevation - minElevation) / (maxElevation - minElevation)
colorIndex = CLAMP(normalizedElevation × 255, 0, 255)
```

#### Color Palette Generation
- **Method**: HSV color space interpolation
- **Hue Range**: 240° (blue) to 0° (red)
- **Saturation**: 100%
- **Value**: 100%
- **Palette Size**: 256 colors (0-255)

#### HSV to RGB Conversion
The system uses a complete HSV-to-RGB conversion algorithm supporting all hue ranges:
- **Hue 0-60°**: Red to Yellow
- **Hue 60-120°**: Yellow to Green  
- **Hue 120-180°**: Green to Cyan
- **Hue 180-240°**: Cyan to Blue
- **Hue 240-300°**: Blue to Magenta
- **Hue 300-360°**: Magenta to Red

## 4. Visual Elements

### 4.1 Grid Overlay
- **Grid Lines**: Dark gray (RGB: 64, 64, 64)
- **Grid Pattern**: Every 12th pixel (matching scale factor)
- **Grid Logic**: `(x % 12 == 0) || (y % 12 == 0)`

### 4.2 Data Visualization
- **Valid Data**: Color-coded elevation mapping
- **No Data**: Black (RGB: 0, 0, 0)
- **Grid Lines**: Overlaid on elevation data

### 4.3 Text Annotations

#### Text Rendering System
- **Font**: Custom 8×12 pixel bitmap font
- **Character Set**: Full ASCII (numbers, letters, punctuation, symbols)
- **Text Color**: White (RGB: 255, 255, 255)
- **Background**: Black (RGB: 0, 0, 0)
- **Line Height**: 16 pixels
- **Character Spacing**: 8 pixels width
- **Margin**: 10 pixels from edges

#### Text Content by Trip Type

**Initial State (Trip 0)**:
```
INITIAL STATE
Grid: [width]x[height] bins ([width] wide, [height] long)
No earthmoving operations
```

**Regular Trips**:
```
Trip: [trip_index]
BCY: [bcy_value]

CUT:
  Equipment: [width]x[height] bins (estimated)
  Start: ([start_lat], [start_lon])
  End: ([end_lat], [end_lon])
  Detail: ([cut_start_lat], [cut_start_lon]) -> ([cut_stop_lat], [cut_stop_lon])

FILL:
  Equipment: [width]x[height] bins (estimated)
  Start: ([start_lat], [start_lon])
  End: ([end_lat], [end_lon])
  Detail: ([fill_start_lat], [fill_start_lon]) -> ([fill_stop_lat], [fill_stop_lon])
```

## 5. Equipment and Operation Specifications

### 5.1 Equipment Dimensions
- **Scraper Width**: 4.572m (15ft)
- **Equipment Width in Bins**: 8 bins (4.572m ÷ 0.6096m = 7.5, rounded up)
- **Maximum Cut Depth**: 0.06096m (2ft)

### 5.2 Operation Calculations
- **Cut Dimensions**: Equipment width × calculated length
- **Fill Dimensions**: Equipment width × calculated length
- **Length Calculation**: Distance between start/stop coordinates
- **Fallback**: General start/end coordinates if detailed coordinates unavailable

## 6. File Structure

### 6.1 BMP Header (54 bytes)
```
Offset  Size  Description
0       2     Signature ("BM")
2       4     File size
6       4     Reserved (0)
10      4     Offset to pixel data (54)
14      4     DIB header size (40)
18      4     Image width
22      4     Image height
26      2     Planes (1)
28      2     Bits per pixel (24)
30      4     Compression (0)
34      4     Image size (0 for uncompressed)
38      4     X pixels per meter (0)
42      4     Y pixels per meter (0)
46      4     Colors in color table (0)
50      4     Important color count (0)
```

### 6.2 Pixel Data
- **Storage**: Bottom-to-top (BMP standard)
- **Format**: BGR (Blue, Green, Red)
- **Row Padding**: Aligned to 4-byte boundaries
- **Total Size**: `width × height × 3 + padding`

## 7. Performance Characteristics

### 7.1 Memory Usage
- **Elevation Grid**: `height × width × 8 bytes` (double precision)
- **Data Mask**: `height × width × 1 byte` (boolean)
- **Color Palette**: 256 × 3 × 4 bytes = 3,072 bytes
- **Total RAM**: ~8 bytes per grid cell + overhead

### 7.2 File Size Estimation
```
File Size = 54 + (scaledWidth × scaledHeight × 3) + padding
Example: 600×560 = 54 + (600 × 560 × 3) = 1,008,054 bytes ≈ 984 KB
```

## 8. Quality Assurance

### 8.1 Color Accuracy
- **Color Space**: RGB with HSV interpolation
- **Gradient Smoothness**: 256-level color ramp
- **Contrast**: Full dynamic range utilization

### 8.2 Grid Accuracy
- **Grid Alignment**: Pixel-perfect alignment with scale factor
- **Grid Visibility**: High contrast dark gray on elevation colors
- **Grid Consistency**: Uniform 12×12 pixel cells

### 8.3 Text Readability
- **Font Clarity**: High contrast white on black
- **Character Spacing**: Consistent 8×12 pixel characters
- **Line Spacing**: 16-pixel line height for readability

## 9. Integration Points

### 9.1 Input Dependencies
- **Mesh Data**: Vertex positions and elevations
- **Trip Information**: Operation details and coordinates
- **Vertical Exaggeration**: User-configurable parameter

### 9.2 Output Integration
- **File Naming**: `surface_trip_[index]_VE[value]_[mode]_[state].bmp`
- **Directory**: `out/` subdirectory
- **Format**: Standard Windows BMP compatible with all image viewers

## 10. Error Handling

### 10.1 Data Validation
- **Missing Data**: Rendered as black pixels
- **Invalid Coordinates**: Clamped to grid boundaries
- **Empty Grids**: Minimal 1×1 pixel output

### 10.2 File System
- **Directory Creation**: Automatic `out/` directory creation
- **File Overwrite**: Existing files are replaced
- **Error Recovery**: Graceful handling of write failures

---

## Appendix A: Color Palette Reference

The elevation color ramp transitions through the following key colors:
- **Index 0**: Blue (RGB: 0, 0, 255)
- **Index 64**: Cyan (RGB: 0, 255, 255)  
- **Index 128**: Green (RGB: 0, 255, 0)
- **Index 192**: Yellow (RGB: 255, 255, 0)
- **Index 255**: Red (RGB: 255, 0, 0)

## Appendix B: Coordinate System

The system uses a local equirectangular projection:
- **Projection Origin**: `(lat0, lon0)` = average of all AGD points (site centroid)
- **Grid Origin**: Southwest corner at `(MinBx, MinBy)` - the bottom-left of the grid
- **X-axis**: East-West (longitude-based)
- **Y-axis**: North-South (latitude-based)
- **Units**: Meters from projection origin
- **Grid Alignment**: Integer bin coordinates (Bx, By) with origin at southwest corner

## Appendix C: Font Character Set

The custom bitmap font supports:
- **Numbers**: 0-9
- **Uppercase**: A-Z
- **Lowercase**: a-z
- **Punctuation**: .,:;!?()[]{}-_=+*/\\|<>@#$%&^~`'" 
- **Special**: Space character

Each character is rendered as an 8×12 pixel bitmap with consistent spacing and alignment.
