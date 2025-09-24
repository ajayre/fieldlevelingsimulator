namespace ObjGen2
{
    // Individual bin with spatial and operational data
    public class Bin
    {
        public int IndexX { get; set; }
        public int IndexY { get; set; }
        public Coordinate SouthwestCorner { get; set; }
        public Coordinate NortheastCorner { get; set; }
        public Coordinate Centroid { get; set; }
        public double CutAmountM { get; set; }
        public double FillAmountM { get; set; }
        public double ExistingElevationM { get; set; }
        public double TargetElevationM { get; set; }
    }
}
