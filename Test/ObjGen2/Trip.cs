namespace ObjGen2
{
    // Trip with cut/fill operations and metrics
    public class Trip
    {
        public int TripIndex { get; set; }
        public Coordinate StartCoordinates { get; set; }
        public Coordinate EndCoordinates { get; set; }
        public CutOperation Cut { get; set; }
        public FillOperation Fill { get; set; }
        public TripMetrics Metrics { get; set; }
    }
}
