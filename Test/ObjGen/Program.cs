using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HaulFrames
{
    // Build: .NET 8 Console
    // Usage: HaulFrames.exe "The Shop 2.agd" "trips_v4_1.csv" [everyN=1] [maxTrips=0] [format=obj|ply|dem|bmp] [ve=10] [mode=bin|blade]
    // Exports a frame after each Nth trip. Evolution can be classic per-bin or 4.572 m blade footprint.
    internal static class Program
    {
        // ===== Spec constants (v4.1) =====
        const double B = 0.6096;                           // bin size (m) - 2ft x 2ft
        const double Aeff = B * B;                         // bin area (m²)
        const double Swell = 1.30;
        const double Shrink = 0.64;
        const double NET = Swell * Shrink;                 // bank -> loose -> compacted
        const double YD3_PER_M3 = 1.30795061931439;        // yd³ per m³
        const double R = 6371000.0;                        // Earth radius (m)
        const double SCRAPER_WIDTH = 4.572;                // blade width (m)
        const double MAX_CUT_DEPTH = 0.06096;              // hard cap (m)
        const double DUMP_TRAVEL_M = 5.0;                  // dump spread length (m)

        enum OutFormat { Obj, Ply, Dem, Bmp }
        enum EvolMode  { Bin, Blade }

        // ===== Data models =====
        record AgdPoint(double Lat, double Lon, double? ZExist, double? ZProp);

        sealed class Bin
        {
            public int Bx, By;
            public List<AgdPoint> Samples = new();
            public double LatCenter, LonCenter;
            public double? ZExistMean, ZPropMean;
            public double X, Y;         // local XY (m)
            public double ZCur;         // evolving surface
            public double ZProp;        // clamp to proposed
        }

        // Profile data structure for cut/fill cross-sections
        sealed class ProfileData
        {
            public List<(double Distance, double Depth)> Points = new();
            
            // Get depth at a specific distance along the profile
            public double GetDepthAtDistance(double distance)
            {
                if (Points.Count == 0) return 0.0;
                if (Points.Count == 1) return Points[0].Depth;
                
                // Find the two points that bracket the distance
                for (int i = 0; i < Points.Count - 1; i++)
                {
                    if (distance >= Points[i].Distance && distance <= Points[i + 1].Distance)
                    {
                        // Linear interpolation
                        double t = (distance - Points[i].Distance) / (Points[i + 1].Distance - Points[i].Distance);
                        return Points[i].Depth + t * (Points[i + 1].Depth - Points[i].Depth);
                    }
                }
                
                // If distance is beyond the profile, use the last point
                return Points[Points.Count - 1].Depth;
            }
        }

        sealed class TripRec
        {
            public int TripIndex;
            public double StartLat, StartLon;
            public double EndLat, EndLon;
            public double BCY;

            // Optional detailed geometry (used by blade mode when present)
            public double? CutStartLat, CutStartLon, CutStopLat, CutStopLon;
            public double? FillStartLat, FillStartLon, FillStopLat, FillStopLon;
            public double? CutLengthM;   // from CSV if present
            public double? HeadingDeg;   // from CSV if present
            
            // New profile data for enhanced accuracy
            public ProfileData? CutProfile;   // cut profile data
            public ProfileData? FillProfile;  // fill profile data
        }

        sealed class Mesh
        {
            public List<Bin> Vertices = new();
            public List<(int a, int b, int c)> Faces = new();   // 1-based indices (OBJ-style)
            public Dictionary<(int bx, int by), int> IndexByKey = new();
            public double Lat0, Lon0;                           // projection origin
            public int MinBx, MaxBx, MinBy, MaxBy;              // lattice bounds (DEM)
        }

        static void Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

            string agdPath = args.Length > 0 ? args[0] : "ShopB4.agd";
            string tripsCsv = args.Length > 1 ? args[1] : "trips_output.csv";
            int everyN = args.Length > 2 ? Math.Max(1, ParseInt(args[2], 1)) : 1;
            int maxTrips = args.Length > 3 ? Math.Max(0, ParseInt(args[3], 0)) : 0;
            OutFormat format = args.Length > 4 ? ParseFormat(args[4]) : OutFormat.Bmp;
            double VE = args.Length > 5 ? ParseDouble(args[5], 10.0) : 1.0;
            EvolMode mode = args.Length > 6 ? ParseMode(args[6]) : EvolMode.Blade;

            if (!File.Exists(agdPath)) { Console.Error.WriteLine($"AGD not found: {agdPath}"); return; }
            if (!File.Exists(tripsCsv)) { Console.Error.WriteLine($"Trips CSV not found: {tripsCsv}"); return; }

            Console.WriteLine($"AGD: {agdPath}");
            Console.WriteLine($"Trips: {tripsCsv}");
            Console.WriteLine($"everyN={everyN}, maxTrips={(maxTrips==0?"ALL":maxTrips)}, format={format}, VE={VE}, mode={mode}");
            
            // Calculate and display dimensions for the first trip
            CalculateFirstTripDimensions();

            var agd = LoadAgd(agdPath);
            if (agd.Count == 0) { Console.Error.WriteLine("AGD empty."); return; }

            // Projection anchor = site centroid
            (double lat0, double lon0) = (agd.Average(p => p.Lat), agd.Average(p => p.Lon));

            var allBins = BuildBins(agd, lat0, lon0);
            var bins = allBins.Where(b => b.ZExistMean.HasValue && b.ZPropMean.HasValue).ToList();
            if (bins.Count == 0) { Console.Error.WriteLine("No bins with both existing and proposed."); return; }

            foreach (var b in bins) { b.ZCur = b.ZExistMean!.Value; b.ZProp = b.ZPropMean!.Value; }

            var mesh = BuildMesh(bins, lat0, lon0);

            var trips = LoadTrips(tripsCsv);
            if (trips.Count == 0) { Console.Error.WriteLine("Trips CSV empty."); return; }
            int toApply = (maxTrips > 0) ? Math.Min(maxTrips, trips.Count) : trips.Count;

            Console.WriteLine($"Exporting initial state and applying {toApply} trips...");

            // Export initial state (trip 0)
            string veTag = VE.ToString("0.###", CultureInfo.InvariantCulture);
            string initialBaseName = $"surface_trip_0000_VE{veTag}_{mode.ToString().ToLower()}_initial";
            switch (format)
            {
                case OutFormat.Obj: WriteObj(mesh, @"out\" + initialBaseName + ".obj", VE); break;
                case OutFormat.Ply: WritePly(mesh, @"out\" + initialBaseName + ".ply", VE); break;
                case OutFormat.Dem: WriteDemAscii(mesh, @"out\" + initialBaseName + ".asc", VE); break;
                case OutFormat.Bmp: WriteBmp(mesh, @"out\" + initialBaseName + ".bmp", VE, CreateInitialTrip()); break;
            }
            Console.WriteLine($"  wrote {initialBaseName}.{(format==OutFormat.Obj ? "obj" : format==OutFormat.Ply ? "ply" : format==OutFormat.Dem ? "asc" : "bmp")}");

            Console.WriteLine($"Applying {toApply} trips and exporting {format} after each {(everyN==1 ? "" : $"{everyN}th ")}trip...");

            for (int k = 0; k < toApply; k++)
            {
                if (mode == EvolMode.Bin) ApplyTrip_Bin(mesh, trips[k]);
                else if (HasDetailedGeometry(trips[k])) 
                {
                    Console.WriteLine($"Using ApplyTrip_BladeWithProfiles for trip {trips[k].TripIndex}");
                    ApplyTrip_BladeWithProfiles(mesh, trips[k], trips[k].BCY / YD3_PER_M3, (trips[k].BCY / YD3_PER_M3) * NET);
                }
                else                      
                {
                    Console.WriteLine($"Using ApplyTrip_Blade for trip {trips[k].TripIndex}");
                    ApplyTrip_Blade(mesh, trips[k]);
                }

                if (((k + 1) % everyN) == 0)
                {
                    string baseName = $"surface_trip_{trips[k].TripIndex:D4}_VE{veTag}_{mode.ToString().ToLower()}";
                    switch (format)
                    {
                        case OutFormat.Obj: WriteObj(mesh, @"out\" + baseName + ".obj", VE); break;
                        case OutFormat.Ply: WritePly(mesh, @"out\" + baseName + ".ply", VE); break;
                        case OutFormat.Dem: WriteDemAscii(mesh, @"out\" + baseName + ".asc", VE); break;
                        case OutFormat.Bmp: WriteBmp(mesh, @"out\" + baseName + ".bmp", VE, trips[k]); break;
                    }
                    Console.WriteLine($"  wrote {baseName}.{(format==OutFormat.Obj ? "obj" : format==OutFormat.Ply ? "ply" : format==OutFormat.Dem ? "asc" : "bmp")}");
                }
            }

            Console.WriteLine("Done.");
        }
        
        // Create initial trip record for trip 0 (initial state)
        static TripRec CreateInitialTrip()
        {
            return new TripRec
            {
                TripIndex = 0,
                StartLat = 0.0,
                StartLon = 0.0,
                EndLat = 0.0,
                EndLon = 0.0,
                BCY = 0.0,
                CutStartLat = null,
                CutStartLon = null,
                CutStopLat = null,
                CutStopLon = null,
                FillStartLat = null,
                FillStartLon = null,
                FillStopLat = null,
                FillStopLon = null,
                CutLengthM = null,
                HeadingDeg = null,
                CutProfile = null,
                FillProfile = null
            };
        }
        
        // ===== Parse helpers =====
        static int    ParseInt(string s, int defVal)    => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : defVal;
        static double ParseDouble(string s, double defVal) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : defVal;
        static OutFormat ParseFormat(string s) { if (s.Equals("ply", StringComparison.OrdinalIgnoreCase)) return OutFormat.Ply; if (s.Equals("dem", StringComparison.OrdinalIgnoreCase)) return OutFormat.Dem; if (s.Equals("bmp", StringComparison.OrdinalIgnoreCase)) return OutFormat.Bmp; return OutFormat.Obj; }
        static EvolMode  ParseMode(string s)   { if (s.Equals("blade", StringComparison.OrdinalIgnoreCase)) return EvolMode.Blade; return EvolMode.Bin; }
        
        // Check if trip has detailed geometry for rectangular footprint processing
        static bool HasDetailedGeometry(TripRec trip)
        {
            bool hasCut = trip.CutStartLat.HasValue && trip.CutStartLon.HasValue && trip.CutStopLat.HasValue && trip.CutStopLon.HasValue;
            bool hasFill = trip.FillStartLat.HasValue && trip.FillStartLon.HasValue && trip.FillStopLat.HasValue && trip.FillStopLon.HasValue;
            
            Console.WriteLine($"Trip {trip.TripIndex} detailed geometry check:");
            Console.WriteLine($"  Cut: {hasCut} (Start: {trip.CutStartLat.HasValue}, {trip.CutStartLon.HasValue}, Stop: {trip.CutStopLat.HasValue}, {trip.CutStopLon.HasValue})");
            Console.WriteLine($"  Fill: {hasFill} (Start: {trip.FillStartLat.HasValue}, {trip.FillStartLon.HasValue}, Stop: {trip.FillStopLat.HasValue}, {trip.FillStopLon.HasValue})");
            
            return hasCut && hasFill;
        }
        
        // Calculate distance between two lat/lon coordinates in meters
        static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
        {
            // Convert to radians
            double lat1Rad = lat1 * Math.PI / 180.0;
            double lon1Rad = lon1 * Math.PI / 180.0;
            double lat2Rad = lat2 * Math.PI / 180.0;
            double lon2Rad = lon2 * Math.PI / 180.0;
            
            // Haversine formula for great circle distance
            double dLat = lat2Rad - lat1Rad;
            double dLon = lon2Rad - lon1Rad;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                      Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                      Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            
            return R * c; // Earth radius * central angle
        }
        
        // Calculate and display dimensions for the first trip
        static void CalculateFirstTripDimensions()
        {
            Console.WriteLine("=== FIRST TRIP DIMENSIONS CALCULATION ===");
            
            // Load trips data
            var trips = LoadTrips("trips_output.csv");
            if (trips.Count == 0)
            {
                Console.WriteLine("No trips found in CSV file.");
                return;
            }
            
            var firstTrip = trips[0];
            Console.WriteLine($"First Trip Index: {firstTrip.TripIndex}");
            Console.WriteLine($"BCY: {firstTrip.BCY:F2}");
            Console.WriteLine();
            
            // Calculate equipment width in bins
            int equipmentWidthBins = (int)Math.Ceiling(SCRAPER_WIDTH / B);
            Console.WriteLine($"Equipment Width: {SCRAPER_WIDTH}m = {equipmentWidthBins} bins (15ft ÷ 2ft = 7.5, rounded up to 8)");
            Console.WriteLine();
            
            // Calculate CUT dimensions
            Console.WriteLine("CUT OPERATION:");
            if (firstTrip.CutStartLat.HasValue && firstTrip.CutStartLon.HasValue && 
                firstTrip.CutStopLat.HasValue && firstTrip.CutStopLon.HasValue)
            {
                double cutLengthM = CalculateDistance(firstTrip.CutStartLat.Value, firstTrip.CutStartLon.Value, 
                                                     firstTrip.CutStopLat.Value, firstTrip.CutStopLon.Value);
                int cutLengthBins = Math.Max(1, (int)Math.Ceiling(cutLengthM / B));
                
                Console.WriteLine($"  Cut Start: ({firstTrip.CutStartLat:F6}, {firstTrip.CutStartLon:F6})");
                Console.WriteLine($"  Cut Stop:  ({firstTrip.CutStopLat:F6}, {firstTrip.CutStopLon:F6})");
                Console.WriteLine($"  Cut Length: {cutLengthM:F2}m = {cutLengthBins} bins");
                Console.WriteLine($"  Cut Dimensions: {equipmentWidthBins} x {cutLengthBins} bins");
            }
            else
            {
                Console.WriteLine("  No detailed cut coordinates available");
                Console.WriteLine($"  Cut Dimensions: {equipmentWidthBins} x 1 bins (default)");
            }
            Console.WriteLine();
            
            // Calculate FILL dimensions
            Console.WriteLine("FILL OPERATION:");
            if (firstTrip.FillStartLat.HasValue && firstTrip.FillStartLon.HasValue && 
                firstTrip.FillStopLat.HasValue && firstTrip.FillStopLon.HasValue)
            {
                double fillLengthM = CalculateDistance(firstTrip.FillStartLat.Value, firstTrip.FillStartLon.Value, 
                                                     firstTrip.FillStopLat.Value, firstTrip.FillStopLon.Value);
                int fillLengthBins = Math.Max(1, (int)Math.Ceiling(fillLengthM / B));
                
                Console.WriteLine($"  Fill Start: ({firstTrip.FillStartLat:F6}, {firstTrip.FillStartLon:F6})");
                Console.WriteLine($"  Fill Stop:  ({firstTrip.FillStopLat:F6}, {firstTrip.FillStopLon:F6})");
                Console.WriteLine($"  Fill Length: {fillLengthM:F2}m = {fillLengthBins} bins");
                Console.WriteLine($"  Fill Dimensions: {equipmentWidthBins} x {fillLengthBins} bins");
            }
            else
            {
                Console.WriteLine("  No detailed fill coordinates available");
                Console.WriteLine($"  Fill Dimensions: {equipmentWidthBins} x 1 bins (default)");
            }
            Console.WriteLine();
            
            // Fallback to general coordinates if detailed coordinates not available
            if ((!firstTrip.CutStartLat.HasValue || !firstTrip.FillStartLat.HasValue))
            {
                Console.WriteLine("FALLBACK (using general start/end coordinates):");
                double generalLengthM = CalculateDistance(firstTrip.StartLat, firstTrip.StartLon, firstTrip.EndLat, firstTrip.EndLon);
                int generalLengthBins = Math.Max(1, (int)Math.Ceiling(generalLengthM / B));
                
                Console.WriteLine($"  General Start: ({firstTrip.StartLat:F6}, {firstTrip.StartLon:F6})");
                Console.WriteLine($"  General End:   ({firstTrip.EndLat:F6}, {firstTrip.EndLon:F6})");
                Console.WriteLine($"  General Length: {generalLengthM:F2}m = {generalLengthBins} bins");
                Console.WriteLine($"  General Dimensions: {equipmentWidthBins} x {generalLengthBins} bins");
            }
        }

        // ===== AGD parsing & binning =====
        static List<AgdPoint> LoadAgd(string path)
        {
            var rows = new List<AgdPoint>(1 << 15);
            using var sr = new StreamReader(path);
            string? header = sr.ReadLine();
            if (header == null) return rows;

            string[] h = header.Split(',');
            int iLat = IndexOf(h, "Latitude");
            int iLon = IndexOf(h, "Longitude");
            int iEx  = IndexOf(h, "Existing");
            int iPr  = IndexOf(h, "Proposed");

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = SplitCsv(line);
                if (!TryD(parts, iLat, out double lat) || !TryD(parts, iLon, out double lon)) continue;

                double? zEx = TryDNullable(parts, iEx);
                double? zPr = TryDNullable(parts, iPr);
                if (zEx.HasValue || zPr.HasValue)
                    rows.Add(new AgdPoint(lat, lon, zEx, zPr));
            }
            return rows;
        }
        static int IndexOf(string[] headers, string contains){ for (int i = 0; i < headers.Length; i++) if (headers[i].IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) return i; return -1; }
        static string[] SplitCsv(string line) => line.Split(',');
        static bool TryD(string[] fields, int idx, out double value){ value=0; if (idx<0||idx>=fields.Length) return false; return double.TryParse(fields[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out value); }
        static double? TryDNullable(string[] fields, int idx){ if (idx<0||idx>=fields.Length) return null; return double.TryParse(fields[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null; }

        // Parse profile data from key=value;key=value format
        static ProfileData? ParseProfileData(string? profileString)
        {
            if (string.IsNullOrWhiteSpace(profileString)) return null;
            
            var profile = new ProfileData();
            var pairs = profileString.Split(';', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var pair in pairs)
            {
                var keyValue = pair.Split('=', 2);
                if (keyValue.Length == 2)
                {
                    if (double.TryParse(keyValue[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double distance) &&
                        double.TryParse(keyValue[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double depth))
                    {
                        profile.Points.Add((distance, depth));
                    }
                }
            }
            
            // Sort by distance to ensure proper interpolation
            profile.Points.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            
            return profile.Points.Count > 0 ? profile : null;
        }

        static (double x, double y) ToLocalXY(double latDeg, double lonDeg, double lat0, double lon0)
        {
            double lat = Deg2Rad(latDeg), lon = Deg2Rad(lonDeg);
            double la0 = Deg2Rad(lat0),   lo0 = Deg2Rad(lon0);
            double x = R * Math.Cos(la0) * (lon - lo0);
            double y = R * (lat - la0);
            return (x, y);
        }
        static double Deg2Rad(double d) => d * Math.PI / 180.0;

        static List<Bin> BuildBins(List<AgdPoint> pts, double lat0, double lon0)
        {
            var map = new Dictionary<(int, int), Bin>(1 << 16);
            foreach (var p in pts)
            {
                var (x, y) = ToLocalXY(p.Lat, p.Lon, lat0, lon0);
                int bx = (int)Math.Floor(x / B);
                int by = (int)Math.Floor(y / B);
                var key = (bx, by);
                if (!map.TryGetValue(key, out var bin)) { bin = new Bin { Bx = bx, By = by }; map[key] = bin; }
                bin.Samples.Add(p);
            }

            foreach (var bin in map.Values)
            {
                bin.LatCenter = bin.Samples.Average(s => s.Lat);
                bin.LonCenter = bin.Samples.Average(s => s.Lon);
                var exist = bin.Samples.Where(s => s.ZExist.HasValue).Select(s => s.ZExist!.Value).ToList();
                var prop  = bin.Samples.Where(s => s.ZProp.HasValue).Select(s => s.ZProp!.Value).ToList();
                bin.ZExistMean = exist.Count > 0 ? exist.Average() : null;
                bin.ZPropMean  = prop.Count  > 0 ? prop.Average()  : null;

                var (cx, cy) = ToLocalXY(bin.LatCenter, bin.LonCenter, lat0, lon0);
                bin.X = cx; bin.Y = cy;
            }

            return map.Values.ToList();
        }


        // ===== Mesh (grid-lattice faces) =====
        static Mesh BuildMesh(List<Bin> bins, double lat0, double lon0)
        {
            var mesh = new Mesh { Lat0 = lat0, Lon0 = lon0 };

            for (int i = 0; i < bins.Count; i++)
            {
                mesh.Vertices.Add(bins[i]);
                mesh.IndexByKey[(bins[i].Bx, bins[i].By)] = i;
            }

            var keys = mesh.IndexByKey.Keys.ToList();
            mesh.MinBx = keys.Min(k => k.Item1);
            mesh.MaxBx = keys.Max(k => k.Item1);
            mesh.MinBy = keys.Min(k => k.Item2);
            mesh.MaxBy = keys.Max(k => k.Item2);

            // Create mesh faces - handle missing bins gracefully
            for (int bx = mesh.MinBx; bx <= mesh.MaxBx - 1; bx++)
            {
                for (int by = mesh.MinBy; by <= mesh.MaxBy - 1; by++)
                {
                    // Try to get all four corners
                    bool has00 = mesh.IndexByKey.TryGetValue((bx, by), out int i00);
                    bool has10 = mesh.IndexByKey.TryGetValue((bx + 1, by), out int i10);
                    bool has01 = mesh.IndexByKey.TryGetValue((bx, by + 1), out int i01);
                    bool has11 = mesh.IndexByKey.TryGetValue((bx + 1, by + 1), out int i11);

                    // Create triangles based on what's available
                    if (has00 && has10 && has01)
                    {
                        mesh.Faces.Add((i00 + 1, i10 + 1, i01 + 1));
                    }
                    if (has10 && has01 && has11)
                    {
                        mesh.Faces.Add((i10 + 1, i11 + 1, i01 + 1));
                    }
                }
            }

            return mesh;
        }

        // ===== Trips parsing (includes optional blade geometry) =====
        static List<TripRec> LoadTrips(string path)
        {
            var trips = new List<TripRec>(1 << 16);
            using var sr = new StreamReader(path);
            string? header = sr.ReadLine();
            if (header == null) return trips;
            var h = header.Split(',');

            int I(string name) => Array.FindIndex(h, s => s.Equals(name, StringComparison.OrdinalIgnoreCase));

            int iIdx = I("trip_index");
            int iSlat = I("start_lat");
            int iSlon = I("start_lon");
            int iElat = I("end_lat");
            int iElon = I("end_lon");
            int iBCY = I("BCY");

            // Optional blade-geometry columns
            int iCSLa = I("cut_start_lat"), iCSLo = I("cut_start_lon");
            int iCELa = I("cut_stop_lat"), iCELo = I("cut_stop_lon");
            int iFSLa = I("fill_start_lat"), iFSLo = I("fill_start_lon");
            int iFELa = I("fill_stop_lat"), iFELo = I("fill_stop_lon");
            int iCLen = I("cut_length_m"), iHead = I("heading_deg");
            
            // New profile columns for enhanced accuracy
            int iCutProfile = I("cut_profile");
            int iFillProfile = I("fill_profile");

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var p = line.Split(',');

                // Required fields
                if (iIdx < 0 || iSlat < 0 || iSlon < 0 || iElat < 0 || iElon < 0 || iBCY < 0) continue;
                if (!int.TryParse(p[iIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) continue;
                if (!TryD(p, iSlat, out double slat)) continue;
                if (!TryD(p, iSlon, out double slon)) continue;
                if (!TryD(p, iElat, out double elat)) continue;
                if (!TryD(p, iElon, out double elon)) continue;
                if (!TryD(p, iBCY, out double bcy)) continue;

                trips.Add(new TripRec
                {
                    TripIndex = idx,
                    StartLat = slat,
                    StartLon = slon,
                    EndLat = elat,
                    EndLon = elon,
                    BCY = bcy,

                    // Optional fields (null if column missing or value unparsable)
                    CutStartLat = TryDNullable(p, iCSLa),
                    CutStartLon = TryDNullable(p, iCSLo),
                    CutStopLat = TryDNullable(p, iCELa),
                    CutStopLon = TryDNullable(p, iCELo),
                    FillStartLat = TryDNullable(p, iFSLa),
                    FillStartLon = TryDNullable(p, iFSLo),
                    FillStopLat = TryDNullable(p, iFELa),
                    FillStopLon = TryDNullable(p, iFELo),
                    CutLengthM = TryDNullable(p, iCLen),
                    HeadingDeg = TryDNullable(p, iHead),
                    
                    // New profile data for enhanced accuracy
                    CutProfile = ParseProfileData(iCutProfile >= 0 && iCutProfile < p.Length ? p[iCutProfile] : null),
                    FillProfile = ParseProfileData(iFillProfile >= 0 && iFillProfile < p.Length ? p[iFillProfile] : null)
                });
            }

            trips.Sort((a, b) => a.TripIndex.CompareTo(b.TripIndex));
            return trips;
        }

        // ===== Evolution: equipment width strip processing =====
        static void ApplyTrip_Bin(Mesh mesh, TripRec t)
        {
            // Calculate equipment width in bins (15ft = 4.572m)
            int equipmentWidthBins = (int)Math.Ceiling(SCRAPER_WIDTH / B); // 4.572m / 0.6096m = 7.5, rounded to 8 bins
            
            // Process CUT strip at origin
            ProcessEquipmentStrip(mesh, t.StartLat, t.StartLon, t.BCY / YD3_PER_M3, equipmentWidthBins, true);
            
            // Process FILL strip at destination  
            ProcessEquipmentStrip(mesh, t.EndLat, t.EndLon, (t.BCY / YD3_PER_M3) * NET, equipmentWidthBins, false);
        }
        
        // Process equipment width strip at given location
        static void ProcessEquipmentStrip(Mesh mesh, double lat, double lon, double volumeM3, int equipmentWidthBins, bool isCut)
        {
            // Find center bin
            int centerIdx = FindBinIndex(mesh, lat, lon);
            if (centerIdx < 0 || centerIdx >= mesh.Vertices.Count) return;
            
            var centerBin = mesh.Vertices[centerIdx];
            
            // Calculate direction from start to end for strip orientation
            // For now, use a default direction (can be enhanced later)
            double stripDirection = 0.0; // North-South for now
            
            // Calculate perpendicular direction for strip
            double perpX = -Math.Sin(stripDirection * Math.PI / 180.0);
            double perpY = Math.Cos(stripDirection * Math.PI / 180.0);
            
            // Generate strip bins
            var stripBins = new List<int>();
            for (int w = -equipmentWidthBins / 2; w <= equipmentWidthBins / 2; w++)
            {
                double stripX = centerBin.X + (w * perpX * B);
                double stripY = centerBin.Y + (w * perpY * B);
                
                // Find nearest bin to strip position
                int stripIdx = FindNearestBinIndex(mesh, stripX, stripY);
                if (stripIdx >= 0 && stripIdx < mesh.Vertices.Count)
                {
                    stripBins.Add(stripIdx);
                }
            }
            
            if (stripBins.Count == 0) return;
            
            // Distribute volume across strip bins
            double volumePerBin = volumeM3 / stripBins.Count;
            double dzPerBin = volumePerBin / Aeff;
            
            foreach (int binIdx in stripBins)
            {
                var bin = mesh.Vertices[binIdx];
                if (isCut)
                {
                    bin.ZCur = Math.Max(bin.ZCur - dzPerBin, bin.ZProp);
                }
                else
                {
                    bin.ZCur = Math.Min(bin.ZCur + dzPerBin, bin.ZProp);
                }
            }
        }
        
        // Find nearest bin index to given XY coordinates
        static int FindNearestBinIndex(Mesh mesh, double x, double y)
        {
            int best = -1;
            double bestD2 = double.MaxValue;
            
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                double dx = mesh.Vertices[i].X - x;
                double dy = mesh.Vertices[i].Y - y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = i;
                }
            }
            return best;
        }

        // ===== Evolution: blade footprint with equipment width strips =====
        static void ApplyTrip_Blade(Mesh mesh, TripRec t)
        {
            // Calculate equipment width in bins (15ft = 4.572m)
            int equipmentWidthBins = (int)Math.Ceiling(SCRAPER_WIDTH / B); // 4.572m / 0.6096m = 7.5, rounded to 8 bins
            
            // Calculate cutting direction from start to end
            double cutDirection = CalculateDirection(t.StartLat, t.StartLon, t.EndLat, t.EndLon);
            
            // Process CUT strip at origin with cutting direction
            ProcessEquipmentStripWithDirection(mesh, t.StartLat, t.StartLon, t.BCY / YD3_PER_M3, equipmentWidthBins, cutDirection, true);
            
            // Process FILL strip at destination with cutting direction
            ProcessEquipmentStripWithDirection(mesh, t.EndLat, t.EndLon, (t.BCY / YD3_PER_M3) * NET, equipmentWidthBins, cutDirection, false);
        }
        
        // Calculate direction from start to end point
        static double CalculateDirection(double startLat, double startLon, double endLat, double endLon)
        {
            double dLat = endLat - startLat;
            double dLon = endLon - startLon;
            return Math.Atan2(dLon, dLat) * 180.0 / Math.PI; // Convert to degrees
        }
        
        // Process equipment width strip with specific direction
        static void ProcessEquipmentStripWithDirection(Mesh mesh, double lat, double lon, double volumeM3, int equipmentWidthBins, double stripDirection, bool isCut)
        {
            // Find center bin
            int centerIdx = FindBinIndex(mesh, lat, lon);
            if (centerIdx < 0 || centerIdx >= mesh.Vertices.Count) return;
            
            var centerBin = mesh.Vertices[centerIdx];
            
            // Calculate perpendicular direction for strip (perpendicular to cutting direction)
            double perpX = -Math.Sin(stripDirection * Math.PI / 180.0);
            double perpY = Math.Cos(stripDirection * Math.PI / 180.0);
            
            // Generate strip bins
            var stripBins = new List<int>();
            for (int w = -equipmentWidthBins / 2; w <= equipmentWidthBins / 2; w++)
            {
                double stripX = centerBin.X + (w * perpX * B);
                double stripY = centerBin.Y + (w * perpY * B);
                
                // Find nearest bin to strip position
                int stripIdx = FindNearestBinIndex(mesh, stripX, stripY);
                if (stripIdx >= 0 && stripIdx < mesh.Vertices.Count)
                {
                    stripBins.Add(stripIdx);
                }
            }
            
            if (stripBins.Count == 0) return;
            
            // Distribute volume across strip bins
            double volumePerBin = volumeM3 / stripBins.Count;
            double dzPerBin = volumePerBin / Aeff;
            
            foreach (int binIdx in stripBins)
            {
                var bin = mesh.Vertices[binIdx];
                if (isCut)
                {
                    bin.ZCur = Math.Max(bin.ZCur - dzPerBin, bin.ZProp);
                }
                else
                {
                    bin.ZCur = Math.Min(bin.ZCur + dzPerBin, bin.ZProp);
                }
            }
        }

        // ===== Enhanced blade algorithm with profile data =====
        static void ApplyTrip_BladeWithProfiles(Mesh mesh, TripRec t, double bank_m3, double comp_m3)
        {
            Console.WriteLine($"ApplyTrip_BladeWithProfiles: bank_m3={bank_m3:F2}, comp_m3={comp_m3:F2}");
            
            // CUT with profile data
            if (t.CutProfile != null)
            {
                Console.WriteLine("Using cut profile data");
                var (xcs, ycs, xce, yce) = CutSegmentXY(mesh, t);
                Console.WriteLine($"Cut segment: ({xcs:F2}, {ycs:F2}) to ({xce:F2}, {yce:F2})");
                DistributeVolumeRectangleWithProfile(mesh, xcs, ycs, xce, yce, SCRAPER_WIDTH, bank_m3, t.CutProfile,
                    perBinCapM3: i => Math.Max(0.0, Math.Min(MAX_CUT_DEPTH, mesh.Vertices[i].ZCur - mesh.Vertices[i].ZProp)) * Aeff,
                    apply: (i, vol_i) => mesh.Vertices[i].ZCur = Math.Max(mesh.Vertices[i].ZCur - vol_i / Aeff, mesh.Vertices[i].ZProp)
                );
            }
            else
            {
                Console.WriteLine("Using standard cut distribution (no profile)");
                // Fallback to standard cut distribution
                var (xcs, ycs, xce, yce) = CutSegmentXY(mesh, t);
                Console.WriteLine($"Cut segment: ({xcs:F2}, {ycs:F2}) to ({xce:F2}, {yce:F2})");
                DistributeVolumeRectangle(mesh, xcs, ycs, xce, yce, SCRAPER_WIDTH, bank_m3,
                    perBinCapM3: i => Math.Max(0.0, Math.Min(MAX_CUT_DEPTH, mesh.Vertices[i].ZCur - mesh.Vertices[i].ZProp)) * Aeff,
                    apply: (i, vol_i) => mesh.Vertices[i].ZCur = Math.Max(mesh.Vertices[i].ZCur - vol_i / Aeff, mesh.Vertices[i].ZProp)
                );
            }

            // FILL with profile data
            if (t.FillProfile != null)
            {
                Console.WriteLine("Using fill profile data");
                var (xcs, ycs, xce, yce) = CutSegmentXY(mesh, t);
                var (xfs, yfs, xfe, yfe) = FillSegmentXY(mesh, t, xcs, ycs, xce, yce);
                Console.WriteLine($"Fill segment: ({xfs:F2}, {yfs:F2}) to ({xfe:F2}, {yfe:F2})");
                DistributeVolumeRectangleWithProfile(mesh, xfs, yfs, xfe, yfe, SCRAPER_WIDTH, comp_m3, t.FillProfile,
                    perBinCapM3: i => Math.Max(0.0, mesh.Vertices[i].ZProp - mesh.Vertices[i].ZCur) * Aeff,
                    apply: (i, vol_i) => mesh.Vertices[i].ZCur = Math.Min(mesh.Vertices[i].ZCur + vol_i / Aeff, mesh.Vertices[i].ZProp)
                );
            }
            else
            {
                Console.WriteLine("Using standard fill distribution (no profile)");
                // Fallback to standard fill distribution
                var (xcs, ycs, xce, yce) = CutSegmentXY(mesh, t);
                var (xfs, yfs, xfe, yfe) = FillSegmentXY(mesh, t, xcs, ycs, xce, yce);
                Console.WriteLine($"Fill segment: ({xfs:F2}, {yfs:F2}) to ({xfe:F2}, {yfe:F2})");
                DistributeVolumeRectangle(mesh, xfs, yfs, xfe, yfe, SCRAPER_WIDTH, comp_m3,
                    perBinCapM3: i => Math.Max(0.0, mesh.Vertices[i].ZProp - mesh.Vertices[i].ZCur) * Aeff,
                    apply: (i, vol_i) => mesh.Vertices[i].ZCur = Math.Min(mesh.Vertices[i].ZCur + vol_i / Aeff, mesh.Vertices[i].ZProp)
                );
            }
        }

        // Choose cut segment (prefer explicit CSV; otherwise derive from start/heading/length)
        static (double x0, double y0, double x1, double y1) CutSegmentXY(Mesh mesh, TripRec t)
        {
            if (t.CutStartLat.HasValue && t.CutStartLon.HasValue && t.CutStopLat.HasValue && t.CutStopLon.HasValue)
            {
                var a = ToLocalXY(t.CutStartLat.Value, t.CutStartLon.Value, mesh.Lat0, mesh.Lon0);
                var b = ToLocalXY(t.CutStopLat.Value,  t.CutStopLon.Value,  mesh.Lat0, mesh.Lon0);
                return (a.x, a.y, b.x, b.y);
            }
            // Fallback: cut_stop = start bin center; cut_length from CSV; heading from start→end
            var start = ToLocalXY(t.StartLat, t.StartLon, mesh.Lat0, mesh.Lon0);
            var end   = ToLocalXY(t.EndLat,   t.EndLon,   mesh.Lat0, mesh.Lon0);
            double dx = end.x - start.x, dy = end.y - start.y;
            double L = Math.Sqrt(dx * dx + dy * dy); if (L < 1e-9) L = 1.0;
            double ux = dx / L, uy = dy / L;

            double cutLen = t.CutLengthM ?? (bankLengthEstimate(t) ); // final resort estimate
            double x1 = start.x, y1 = start.y;                        // cut_stop at start
            double x0 = x1 - ux * cutLen, y0 = y1 - uy * cutLen;      // move back along heading
            return (x0, y0, x1, y1);

            double bankLengthEstimate(TripRec tr) => (tr.BCY / YD3_PER_M3) / (SCRAPER_WIDTH * Math.Min(MAX_CUT_DEPTH, 0.06096)); // safe fallback
        }

        // Choose fill segment (prefer explicit CSV; otherwise 5 m back from end along approach)
        static (double x0, double y0, double x1, double y1) FillSegmentXY(Mesh mesh, TripRec t, double xcs, double ycs, double xce, double yce)
        {
            if (t.FillStartLat.HasValue && t.FillStartLon.HasValue && t.FillStopLat.HasValue && t.FillStopLon.HasValue)
            {
                var a = ToLocalXY(t.FillStartLat.Value, t.FillStartLon.Value, mesh.Lat0, mesh.Lon0);
                var b = ToLocalXY(t.FillStopLat.Value,  t.FillStopLon.Value,  mesh.Lat0, mesh.Lon0);
                return (a.x, a.y, b.x, b.y);
            }
            // Fallback: use a 5 m segment ending at end bin center, oriented along approach (start->end)
            var start = ToLocalXY(t.StartLat, t.StartLon, mesh.Lat0, mesh.Lon0);
            var end   = ToLocalXY(t.EndLat,   t.EndLon,   mesh.Lat0, mesh.Lon0);
            double dx = end.x - start.x, dy = end.y - start.y;
            double L = Math.Sqrt(dx * dx + dy * dy); if (L < 1e-9) { return (end.x, end.y, end.x, end.y); }
            double ux = dx / L, uy = dy / L;
            double x1 = end.x, y1 = end.y;                          // fill_stop at end
            double x0 = x1 - ux * DUMP_TRAVEL_M, y0 = y1 - uy * DUMP_TRAVEL_M;
            return (x0, y0, x1, y1);
        }

        // Volume distribution over a rotated rectangle footprint (center-in-rectangle bin test).
        static void DistributeVolumeRectangle(
            Mesh mesh, double x0, double y0, double x1, double y1, double width,
            double totalVolumeM3,
            Func<int, double> perBinCapM3, Action<int, double> apply)
        {
            // Handle degenerate (zero-length) rectangle
            double dx = x1 - x0, dy = y1 - y0;
            double L = Math.Sqrt(dx * dx + dy * dy);
            if (L < 1e-6)
            {
                int i = NearestIndex(mesh, x0, y0);
                double cap = perBinCapM3(i);
                double vol = Math.Min(totalVolumeM3, cap);
                if (vol > 0) apply(i, vol);
                return;
            }
            double ux = dx / L, uy = dy / L;            // along
            double nx = -uy, ny = ux;                  // normal
            double halfW = width * 0.5;

            // Bounding box in XY
            double minX = Math.Min(x0, x1) - halfW, maxX = Math.Max(x0, x1) + halfW;
            double minY = Math.Min(y0, y1) - halfW, maxY = Math.Max(y0, y1) + halfW;

            int bxMin = (int)Math.Floor(minX / B), bxMax = (int)Math.Floor(maxX / B);
            int byMin = (int)Math.Floor(minY / B), byMax = (int)Math.Floor(maxY / B);

            var inside = new List<int>(256);
            for (int bx = bxMin; bx <= bxMax; bx++)
            for (int by = byMin; by <= byMax; by++)
            {
                if (!mesh.IndexByKey.TryGetValue((bx, by), out int idx)) continue;
                var v = mesh.Vertices[idx];
                double rx = v.X - x0, ry = v.Y - y0;
                double s = rx * ux + ry * uy;          // along distance
                double t = rx * nx + ry * ny;          // signed lateral
                if (s >= 0 && s <= L && Math.Abs(t) <= halfW) inside.Add(idx);
            }

            if (inside.Count == 0) { return; }

            // Capacities per bin (m³)
            var caps = new double[inside.Count];
            double sumCap = 0.0;
            for (int k = 0; k < inside.Count; k++)
            {
                caps[k] = perBinCapM3(inside[k]);
                sumCap += caps[k];
            }
            if (sumCap <= 0) return;

            // Distribute proportionally to capacity (respecting caps)
            double remaining = Math.Min(totalVolumeM3, sumCap);
            // Single pass proportional allocation (since we never exceed cap by construction)
            for (int k = 0; k < inside.Count; k++)
            {
                if (caps[k] <= 0) continue;
                double take = remaining * (caps[k] / sumCap);
                if (take > 0) apply(inside[k], take);
            }
        }

        // Enhanced volume distribution using profile data for more accurate depth calculations
        static void DistributeVolumeRectangleWithProfile(
            Mesh mesh, double x0, double y0, double x1, double y1, double width,
            double totalVolumeM3, ProfileData profile,
            Func<int, double> perBinCapM3, Action<int, double> apply)
        {
            // Handle degenerate (zero-length) rectangle
            double dx = x1 - x0, dy = y1 - y0;
            double L = Math.Sqrt(dx * dx + dy * dy);
            if (L < 1e-6)
            {
                int i = NearestIndex(mesh, x0, y0);
                double cap = perBinCapM3(i);
                double vol = Math.Min(totalVolumeM3, cap);
                if (vol > 0) apply(i, vol);
                return;
            }
            double ux = dx / L, uy = dy / L;            // along
            double nx = -uy, ny = ux;                  // normal
            double halfW = width * 0.5;

            // Bounding box in XY
            double minX = Math.Min(x0, x1) - halfW, maxX = Math.Max(x0, x1) + halfW;
            double minY = Math.Min(y0, y1) - halfW, maxY = Math.Max(y0, y1) + halfW;

            int bxMin = (int)Math.Floor(minX / B), bxMax = (int)Math.Floor(maxX / B);
            int byMin = (int)Math.Floor(minY / B), byMax = (int)Math.Floor(maxY / B);

            var inside = new List<(int Index, double Distance)>(256);
            for (int bx = bxMin; bx <= bxMax; bx++)
            for (int by = byMin; by <= byMax; by++)
            {
                if (!mesh.IndexByKey.TryGetValue((bx, by), out int idx)) continue;
                var v = mesh.Vertices[idx];
                double rx = v.X - x0, ry = v.Y - y0;
                double s = rx * ux + ry * uy;          // along distance
                double t = rx * nx + ry * ny;          // signed lateral
                if (s >= 0 && s <= L && Math.Abs(t) <= halfW) 
                    inside.Add((idx, s));
            }

            if (inside.Count == 0) { return; }

            // Calculate profile-based capacities and volumes
            var profileData = new List<(int Index, double Distance, double ProfileDepth, double Capacity, double Volume)>();
            double totalCapacity = 0.0;

            foreach (var (idx, distance) in inside)
            {
                double profileDepth = profile.GetDepthAtDistance(distance);
                double baseCapacity = perBinCapM3(idx);
                // Scale capacity by profile depth (deeper cuts/fills have more capacity)
                double profileCapacity = baseCapacity * Math.Max(0.1, profileDepth / 0.1); // Normalize to 0.1m depth
                double volume = Math.Min(profileCapacity, totalVolumeM3 * (profileCapacity / (baseCapacity * inside.Count)));
                
                profileData.Add((idx, distance, profileDepth, profileCapacity, volume));
                totalCapacity += profileCapacity;
            }

            if (totalCapacity <= 0) return;

            // Distribute remaining volume proportionally based on profile-weighted capacity
            double remaining = Math.Min(totalVolumeM3, totalCapacity);
            for (int k = 0; k < profileData.Count; k++)
            {
                var (idx, distance, profileDepth, capacity, volume) = profileData[k];
                if (capacity <= 0) continue;
                
                double take = remaining * (capacity / totalCapacity);
                if (take > 0) apply(idx, take);
            }
        }

        static int NearestIndex(Mesh mesh, double x, double y)
        {
            int best = 0; double bestD2 = double.MaxValue;
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                double dx = mesh.Vertices[i].X - x, dy = mesh.Vertices[i].Y - y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = i; }
            }
            return best;
        }

        static int FindBinIndex(Mesh mesh, double lat, double lon)
        {
            var (x, y) = ToLocalXY(lat, lon, mesh.Lat0, mesh.Lon0);
            int bx = (int)Math.Floor(x / B);
            int by = (int)Math.Floor(y / B);
            if (mesh.IndexByKey.TryGetValue((bx, by), out int idx))
                return idx;
            return NearestIndex(mesh, x, y);
        }

        // ===== Writers =====
        static void WriteObj(Mesh mesh, string filePath, double ve)
        {
            var sb = new StringBuilder(1 << 23);
            sb.AppendLine("# Wavefront OBJ - surface after applied trips");
            sb.AppendLine("# Coordinates: local equirectangular XY (meters)");
            sb.AppendLine($"# Origin (lat0, lon0): {mesh.Lat0:F8}, {mesh.Lon0:F8}");
            sb.AppendLine("# Z units: meters (exported with vertical exaggeration)");
            sb.AppendLine($"# Bin size: {B} m; A_eff={Aeff} m^2; NET={NET}; YD3_PER_M3={YD3_PER_M3}; VE={ve}");
            foreach (var v in mesh.Vertices)
                sb.AppendLine($"v {v.X.ToString("F3", CultureInfo.InvariantCulture)} {v.Y.ToString("F3", CultureInfo.InvariantCulture)} {(v.ZCur * ve).ToString("F3", CultureInfo.InvariantCulture)}");
            foreach (var (a, b, c) in mesh.Faces)
                sb.AppendLine($"f {a} {b} {c}");
            File.WriteAllText(filePath, sb.ToString(), Encoding.ASCII);
        }

        // PLY in EPSG:4326 (degrees): x=lon, y=lat, z=meters*VE
        static void WritePly(Mesh mesh, string filePath, double ve)
        {
            var inv = CultureInfo.InvariantCulture;
            int vCount = mesh.Vertices.Count;
            int fCount = mesh.Faces.Count;

            using var sw = new StreamWriter(filePath, false, new UTF8Encoding(false));
            sw.WriteLine("ply");
            sw.WriteLine("format ascii 1.0");
            sw.WriteLine("comment PLY - surface after applied trips");
            sw.WriteLine("comment CRS: EPSG:4326 (WGS-84). x=longitude (deg), y=latitude (deg)");
            sw.WriteLine("comment Z units: meters (exported with vertical exaggeration)");
            sw.WriteLine($"comment Origin (lat0, lon0): {mesh.Lat0:F8}, {mesh.Lon0:F8}");
            sw.WriteLine($"comment Bin size: {B} m; A_eff={Aeff} m^2; NET={NET}; YD3_PER_M3={YD3_PER_M3}; VE={ve}");
            sw.WriteLine($"element vertex {vCount}");
            sw.WriteLine("property double x"); // longitude
            sw.WriteLine("property double y"); // latitude
            sw.WriteLine("property double z"); // elevation*VE
            sw.WriteLine($"element face {fCount}");
            sw.WriteLine("property list uchar int vertex_indices");
            sw.WriteLine("end_header");

            foreach (var v in mesh.Vertices)
                sw.WriteLine($"{v.LonCenter.ToString("G17", inv)} {v.LatCenter.ToString("G17", inv)} {(v.ZCur * ve).ToString("G17", inv)}");

            foreach (var (a, b, c) in mesh.Faces)
                sw.WriteLine($"3 {a - 1} {b - 1} {c - 1}");
        }

        // DEM (ESRI ASCII Grid) in EPSG:4326 degrees; VE applied to Z values
        static void WriteDemAscii(Mesh mesh, string filePath, double ve)
        {
            const string NODATA = "-9999";

            // 5 m -> degrees latitude
            double cellsizeDeg = B * ((180.0 / Math.PI) / R);
            int ncols = mesh.MaxBx - mesh.MinBx + 1;
            int nrows = mesh.MaxBy - mesh.MinBy + 1;

            // Lower-left corner: take the lower-left bin center and subtract half cell
            int llIdx = mesh.IndexByKey.TryGetValue((mesh.MinBx, mesh.MinBy), out var idx0) ? idx0 : 0;
            double llCenterLat = mesh.Vertices[llIdx].LatCenter;
            double llCenterLon = mesh.Vertices[llIdx].LonCenter;
            double xllcorner = llCenterLon - 0.5 * cellsizeDeg;
            double yllcorner = llCenterLat - 0.5 * cellsizeDeg;

            // Helper: degrees -> local XY
            (double x, double y) ToLocalXYDeg(double latDeg, double lonDeg) => ToLocalXY(latDeg, lonDeg, mesh.Lat0, mesh.Lon0);

            using var sw = new StreamWriter(filePath, false, new UTF8Encoding(false));
            sw.WriteLine("ncols " + ncols);
            sw.WriteLine("nrows " + nrows);
            sw.WriteLine("xllcorner " + xllcorner.ToString("G9", CultureInfo.InvariantCulture));
            sw.WriteLine("yllcorner " + yllcorner.ToString("G9", CultureInfo.InvariantCulture));
            sw.WriteLine("cellsize " + cellsizeDeg.ToString("G9", CultureInfo.InvariantCulture));
            sw.WriteLine("NODATA_value " + NODATA);
            sw.WriteLine("# CRS: EPSG:4326 (degrees). Values are elevations in meters with vertical exaggeration applied.");
            sw.WriteLine($"# lat0={mesh.Lat0:F8}, lon0={mesh.Lon0:F8}, VE={ve}");

            var vx = mesh.Vertices.Select(v => v.X).ToArray();
            var vy = mesh.Vertices.Select(v => v.Y).ToArray();

            for (int r = 0; r < nrows; r++)
            {
                int rr = nrows - 1 - r; // write top row first
                double latCenter = (yllcorner + 0.5 * cellsizeDeg) + rr * cellsizeDeg;
                var line = new StringBuilder(ncols * 12);
                for (int c = 0; c < ncols; c++)
                {
                    double lonCenter = (xllcorner + 0.5 * cellsizeDeg) + c * cellsizeDeg;
                    var (x, y) = ToLocalXYDeg(latCenter, lonCenter);

                    // nearest lattice vertex
                    int best = 0; double bestD2 = double.MaxValue;
                    for (int i = 0; i < vx.Length; i++)
                    {
                        double dx = vx[i] - x, dy = vy[i] - y;
                        double d2 = dx * dx + dy * dy;
                        if (d2 < bestD2) { bestD2 = d2; best = i; }
                    }

                    double z = mesh.Vertices[best].ZCur * ve;
                    line.Append(z.ToString("G9", CultureInfo.InvariantCulture));
                    if (c < ncols - 1) line.Append(' ');
                }
                sw.WriteLine(line.ToString());
            }
        }

        // ===== BMP Writer =====
        static void WriteBmp(Mesh mesh, string filePath, double ve, TripRec tripInfo)
        {
            // Calculate grid dimensions
            int width = mesh.MaxBx - mesh.MinBx + 1;
            int height = mesh.MaxBy - mesh.MinBy + 1;
            
            // Create elevation grid
            var elevationGrid = new double[height, width];
            var hasData = new bool[height, width];
            
            // Fill elevation grid
            foreach (var vertex in mesh.Vertices)
            {
                int gridX = vertex.Bx - mesh.MinBx;
                int gridY = vertex.By - mesh.MinBy;
                
                if (gridX >= 0 && gridX < width && gridY >= 0 && gridY < height)
                {
                    elevationGrid[gridY, gridX] = vertex.ZCur * ve;
                    hasData[gridY, gridX] = true;
                }
            }
            
            // Calculate elevation range for consistent color mapping
            double minElevation = double.MaxValue;
            double maxElevation = double.MinValue;
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (hasData[y, x])
                    {
                        minElevation = Math.Min(minElevation, elevationGrid[y, x]);
                        maxElevation = Math.Max(maxElevation, elevationGrid[y, x]);
                    }
                }
            }
            
            // Create upscaled BMP file with grid
            CreateUpscaledBmpFile(mesh, filePath, elevationGrid, hasData, minElevation, maxElevation, width, height, tripInfo);
        }
        
        // Create BMP file for elevation visualization
        static void CreateBmpFile(Mesh mesh, string filePath, double[,] elevationGrid, bool[,] hasData, 
            double minElevation, double maxElevation, int width, int height)
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            
            // BMP Header (14 bytes)
            bw.Write((byte)'B');
            bw.Write((byte)'M');
            int fileSize = 54 + (width * height * 3); // 54 byte header + 3 bytes per pixel
            bw.Write(fileSize);
            bw.Write((int)0); // Reserved
            bw.Write(54); // Offset to pixel data
            
            // DIB Header (40 bytes)
            bw.Write(40); // Header size
            bw.Write(width);
            bw.Write(height);
            bw.Write((short)1); // Planes
            bw.Write((short)24); // Bits per pixel
            bw.Write(0); // Compression
            bw.Write(0); // Image size (0 for uncompressed)
            bw.Write(0); // X pixels per meter
            bw.Write(0); // Y pixels per meter
            bw.Write(0); // Colors in color table
            bw.Write(0); // Important color count
            
            // Create color palette
            var palette = CreateElevationColorPalette();
            
            // Write pixel data (BMP is stored bottom-to-top)
            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++)
                {
                    if (hasData[y, x])
                    {
                        // Map elevation to color index (0-255)
                        double normalizedElevation = (elevationGrid[y, x] - minElevation) / (maxElevation - minElevation);
                        int colorIndex = Math.Max(0, Math.Min(255, (int)(normalizedElevation * 255)));
                        
                        // BMP stores pixels as BGR (Blue, Green, Red)
                        bw.Write((byte)palette[colorIndex].B);
                        bw.Write((byte)palette[colorIndex].G);
                        bw.Write((byte)palette[colorIndex].R);
                    }
                    else
                    {
                        // No data - use black
                        bw.Write((byte)0); // Blue
                        bw.Write((byte)0); // Green
                        bw.Write((byte)0); // Red
                    }
                }
                
                // Pad row to 4-byte boundary if necessary
                int padding = (4 - (width * 3) % 4) % 4;
                for (int p = 0; p < padding; p++)
                {
                    bw.Write((byte)0);
                }
            }
        }
        
        // Create upscaled BMP file with grid overlay and cut/fill highlighting
        static void CreateUpscaledBmpFile(Mesh mesh, string filePath, double[,] elevationGrid, bool[,] hasData, 
            double minElevation, double maxElevation, int width, int height, TripRec? tripInfo)
        {
            const int SCALE_FACTOR = 12;
            const int TEXT_HEIGHT = 200; // Extra space for text at bottom
            int scaledWidth = width * SCALE_FACTOR;
            int scaledHeight = height * SCALE_FACTOR + TEXT_HEIGHT;
            
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            
            // BMP Header (14 bytes)
            bw.Write((byte)'B');
            bw.Write((byte)'M');
            int fileSize = 54 + (scaledWidth * scaledHeight * 3); // 54 byte header + 3 bytes per pixel
            bw.Write(fileSize);
            bw.Write((int)0); // Reserved
            bw.Write(54); // Offset to pixel data
            
            // DIB Header (40 bytes)
            bw.Write(40); // Header size
            bw.Write(scaledWidth);
            bw.Write(scaledHeight);
            bw.Write((short)1); // Planes
            bw.Write((short)24); // Bits per pixel
            bw.Write(0); // Compression
            bw.Write(0); // Image size (0 for uncompressed)
            bw.Write(0); // X pixels per meter
            bw.Write(0); // Y pixels per meter
            bw.Write(0); // Colors in color table
            bw.Write(0); // Important color count
            
            // Create color palette
            var palette = CreateElevationColorPalette();
            
            // Grid color (dark gray)
            const byte GRID_R = 64, GRID_G = 64, GRID_B = 64;
            
            // Write pixel data (BMP is stored bottom-to-top)
            for (int y = scaledHeight - 1; y >= 0; y--)
            {
                for (int x = 0; x < scaledWidth; x++)
                {
                    // Check if we're in the text area at the bottom
                    int imageHeight = height * SCALE_FACTOR;
                    if (y < TEXT_HEIGHT)
                    {
                        // Text area - render text or background
                        if (tripInfo != null && IsTextPixel(x, y, tripInfo, scaledWidth, width, height))
                        {
                            // Text pixel - white
                            bw.Write((byte)255); // Blue
                            bw.Write((byte)255); // Green
                            bw.Write((byte)255); // Red
                        }
                        else
                        {
                            // Background - black
                            bw.Write((byte)0); // Blue
                            bw.Write((byte)0); // Green
                            bw.Write((byte)0); // Red
                        }
                    }
                    else
                    {
                        // Main image area
                        int imageY = y - TEXT_HEIGHT;
                        int origX = x / SCALE_FACTOR;
                        int origY = imageY / SCALE_FACTOR;
                        
                        // Check if this is a grid line
                        bool isGridLine = (x % SCALE_FACTOR == 0) || (imageY % SCALE_FACTOR == 0);
                        
                        if (isGridLine)
                        {
                            // Draw grid line
                            bw.Write(GRID_B); // Blue
                            bw.Write(GRID_G); // Green
                            bw.Write(GRID_R); // Red
                        }
                        else if (origX >= 0 && origX < width && origY >= 0 && origY < height && hasData[origY, origX])
                        {
                            // Map elevation to color index (0-255) - natural color changes from elevation
                            double normalizedElevation = (elevationGrid[origY, origX] - minElevation) / (maxElevation - minElevation);
                            int colorIndex = Math.Max(0, Math.Min(255, (int)(normalizedElevation * 255)));
                            
                            // BMP stores pixels as BGR (Blue, Green, Red)
                            bw.Write((byte)palette[colorIndex].B);
                            bw.Write((byte)palette[colorIndex].G);
                            bw.Write((byte)palette[colorIndex].R);
                        }
                        else
                        {
                            // No data - use black
                            bw.Write((byte)0); // Blue
                            bw.Write((byte)0); // Green
                            bw.Write((byte)0); // Red
                        }
                    }
                }
                
                // Pad row to 4-byte boundary if necessary
                int padding = (4 - (scaledWidth * 3) % 4) % 4;
                for (int p = 0; p < padding; p++)
                {
                    bw.Write((byte)0);
                }
            }
        }
        
        // Simple text rendering for BMP
        static bool IsTextPixel(int x, int y, TripRec trip, int imageWidth, int gridWidth, int gridHeight)
        {
            const int CHAR_WIDTH = 8;
            const int CHAR_HEIGHT = 12;
            const int LINE_HEIGHT = 16;
            const int MARGIN = 10;
            
            // Calculate which line we're on
            int lineIndex = y / LINE_HEIGHT;
            int charY = y % LINE_HEIGHT;
            
            if (charY >= CHAR_HEIGHT) return false; // Between lines
            
            // Build text lines
            var lines = new List<string>();
            
            if (trip.TripIndex == 0)
            {
                // Initial state - minimal text
                lines.Add("INITIAL STATE");
                lines.Add($"Grid: {gridWidth}x{gridHeight} bins ({gridWidth} wide, {gridHeight} long)");
                lines.Add("No earthmoving operations");
            }
            else
            {
                // Regular trip with operations
                lines.Add($"Trip: {trip.TripIndex}");
                lines.Add($"BCY: {trip.BCY:F2}");
                
                // Calculate cut and fill dimensions
                var cutDimensions = CalculateOperationDimensions(trip, true, gridWidth, gridHeight);
                var fillDimensions = CalculateOperationDimensions(trip, false, gridWidth, gridHeight);
                
                lines.Add("");
                lines.Add("CUT:");
                lines.Add($"  Equipment: {cutDimensions.width}x{cutDimensions.height} bins (estimated)");
                lines.Add($"  Start: ({trip.StartLat:F6}, {trip.StartLon:F6})");
                lines.Add($"  End: ({trip.EndLat:F6}, {trip.EndLon:F6})");
                
                if (trip.CutStartLat.HasValue && trip.CutStartLon.HasValue && trip.CutStopLat.HasValue && trip.CutStopLon.HasValue)
                {
                    lines.Add($"  Detail: ({trip.CutStartLat:F6}, {trip.CutStartLon:F6}) -> ({trip.CutStopLat:F6}, {trip.CutStopLon:F6})");
                }
                
                lines.Add("");
                lines.Add("FILL:");
                lines.Add($"  Equipment: {fillDimensions.width}x{fillDimensions.height} bins (estimated)");
                lines.Add($"  Start: ({trip.StartLat:F6}, {trip.StartLon:F6})");
                lines.Add($"  End: ({trip.EndLat:F6}, {trip.EndLon:F6})");
                
                if (trip.FillStartLat.HasValue && trip.FillStartLon.HasValue && trip.FillStopLat.HasValue && trip.FillStopLon.HasValue)
                {
                    lines.Add($"  Detail: ({trip.FillStartLat:F6}, {trip.FillStartLon:F6}) -> ({trip.FillStopLat:F6}, {trip.FillStopLon:F6})");
                }
            }
            
            if (lineIndex >= lines.Count) return false;
            
            string line = lines[lineIndex];
            
            // Calculate character position
            int charX = (x - MARGIN) / CHAR_WIDTH;
            int pixelX = (x - MARGIN) % CHAR_WIDTH;
            
            if (charX < 0 || charX >= line.Length || pixelX < 0 || pixelX >= CHAR_WIDTH) return false;
            
            char c = line[charX];
            return GetCharPixel(c, pixelX, charY);
        }
        
        // Calculate dimensions of cut or fill operation in bins
        static (int width, int height) CalculateOperationDimensions(TripRec trip, bool isCut, int gridWidth, int gridHeight)
        {
            // Equipment width: 15ft = 4.572m, Bin size: 2ft = 0.6096m
            // Equipment width in bins = 15ft ÷ 2ft = 7.5 bins, rounded up = 8 bins
            int equipmentWidthBins = (int)Math.Ceiling(SCRAPER_WIDTH / B); // 4.572m / 0.6096m = 7.5, rounded to 8 bins
            
            // Calculate the actual length of the cut or fill operation using start/end coordinates
            int operationLengthBins = 1; // Default to 1 bin if no coordinates
            
            if (isCut && trip.CutStartLat.HasValue && trip.CutStartLon.HasValue && trip.CutStopLat.HasValue && trip.CutStopLon.HasValue)
            {
                // Calculate cut operation length using cut start/stop coordinates
                double cutLengthM = CalculateDistance(trip.CutStartLat.Value, trip.CutStartLon.Value, 
                                                     trip.CutStopLat.Value, trip.CutStopLon.Value);
                operationLengthBins = Math.Max(1, (int)Math.Ceiling(cutLengthM / B));
            }
            else if (!isCut && trip.FillStartLat.HasValue && trip.FillStartLon.HasValue && trip.FillStopLat.HasValue && trip.FillStopLon.HasValue)
            {
                // Calculate fill operation length using fill start/stop coordinates
                double fillLengthM = CalculateDistance(trip.FillStartLat.Value, trip.FillStartLon.Value, 
                                                      trip.FillStopLat.Value, trip.FillStopLon.Value);
                operationLengthBins = Math.Max(1, (int)Math.Ceiling(fillLengthM / B));
            }
            else
            {
                // Fallback: use general start/end coordinates if detailed coordinates not available
                double generalLengthM = CalculateDistance(trip.StartLat, trip.StartLon, trip.EndLat, trip.EndLon);
                operationLengthBins = Math.Max(1, (int)Math.Ceiling(generalLengthM / B));
            }
            
            // Width = equipment width, Height = operation length
            int width = equipmentWidthBins;
            int height = operationLengthBins;
            
            // Cap at reasonable limits
            if (isCut && trip.CutStartLat.HasValue && trip.CutStartLon.HasValue && trip.CutStopLat.HasValue && trip.CutStopLon.HasValue)
            {
                double dx = trip.CutStopLat.Value - trip.CutStartLat.Value;
                double dy = trip.CutStopLon.Value - trip.CutStartLon.Value;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                int pathBins = Math.Max(1, (int)Math.Ceiling(distance * 111000 / B));
                // height = Math.Max(height, pathBins); // DISABLED: Simulation creates single strip, not long path
            }
            
            if (!isCut && trip.FillStartLat.HasValue && trip.FillStartLon.HasValue && trip.FillStopLat.HasValue && trip.FillStopLon.HasValue)
            {
                double dx = trip.FillStopLat.Value - trip.FillStartLat.Value;
                double dy = trip.FillStopLon.Value - trip.FillStartLon.Value;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                int pathBins = Math.Max(1, (int)Math.Ceiling(distance * 111000 / B));
                // height = Math.Max(height, pathBins); // DISABLED: Simulation creates single strip, not long path
            }
            
            // Cap at reasonable limits
            width = Math.Min(width, gridWidth);
            height = Math.Min(height, gridHeight);
            
            return (width, height);
        }
        
        
        // Use the BMPFont class for character rendering
        static bool GetCharPixel(char c, int x, int y)
        {
            return BMPFont.GetCharPixel(c, x, y);
        }
        
        // Create consistent color palette for elevation mapping
        static (int R, int G, int B)[] CreateElevationColorPalette()
        {
            var palette = new (int R, int G, int B)[256];
            
            // Create a smooth color ramp from blue (low) to red (high)
            // Using HSV color space for smoother transitions
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0;
                
                // HSV to RGB conversion for smooth color transitions
                // Hue: 240° (blue) to 0° (red) - going through the spectrum
                double hue = 240.0 * (1.0 - t);  // Blue to red through the spectrum
                double saturation = 1.0;
                double value = 1.0;
                
                var rgb = HsvToRgb(hue, saturation, value);
                palette[i] = ((int)(rgb.R * 255), (int)(rgb.G * 255), (int)(rgb.B * 255));
            }
            
            return palette;
        }
        
        // Convert HSV to RGB for smooth color transitions
        static (double R, double G, double B) HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            
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
            
            return (r + m, g + m, b + m);
        }
    }
}

