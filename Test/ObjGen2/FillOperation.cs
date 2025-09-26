using System.Collections.Generic;

namespace ObjGen2
{
    // Fill operation details
    public class FillOperation
    {
        public OperationCoordinates Coordinates { get; set; }
        public double DepthM { get; set; }
        public double LengthM { get; set; }
        public double HeadingDeg { get; set; }
        public List<BinOperation> Bins { get; set; } = new List<BinOperation>();
    }
}
