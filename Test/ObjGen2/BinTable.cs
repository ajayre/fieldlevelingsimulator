using System.Collections.Generic;
using System.Linq;

namespace ObjGen2
{
    // Container for all bins in the field
    public class BinTable
    {
        public List<Bin> Bins { get; set; } = new List<Bin>();
        public double InitialMinimumElevationM { get; set; }
        public double InitialMaximumElevationM { get; set; }
        
        // Calculate and set the initial elevation range (ignoring zero elevations)
        public void CalculateInitialElevationRange()
        {
            var nonZeroElevations = Bins
                .Where(bin => bin.ExistingElevationM != 0.0)
                .Select(bin => bin.ExistingElevationM)
                .ToList();
                
            if (nonZeroElevations.Any())
            {
                InitialMinimumElevationM = nonZeroElevations.Min();
                InitialMaximumElevationM = nonZeroElevations.Max();
            }
            else
            {
                InitialMinimumElevationM = 0.0;
                InitialMaximumElevationM = 0.0;
            }
        }
    }
}
