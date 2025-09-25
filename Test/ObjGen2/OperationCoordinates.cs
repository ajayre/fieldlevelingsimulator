namespace ObjGen2
{
    // Operation coordinates (start/stop positions)
    public class OperationCoordinates
    {
        public double StartLatitude { get; set; }
        public double StartLongitude { get; set; }
        public double StopLatitude { get; set; }
        public double StopLongitude { get; set; }
        
        // New bin coordinates for markers and arrows
        public int StartBinX { get; set; }
        public int StartBinY { get; set; }
        public int EndBinX { get; set; }
        public int EndBinY { get; set; }
    }
}
