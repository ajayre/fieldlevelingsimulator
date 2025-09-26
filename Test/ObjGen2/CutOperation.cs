using System.Collections.Generic;

namespace ObjGen2
{
    // Cut operation details
    public class CutOperation
    {
        public OperationCoordinates Coordinates { get; set; }
        public double DepthM { get; set; }
        public double LengthM { get; set; }
        public double HeadingDeg { get; set; }
        public List<BinOperation> Bins { get; set; } = new List<BinOperation>();
    }
}
