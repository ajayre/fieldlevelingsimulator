using System.Collections.Generic;

namespace ObjGen2
{
    // Main container for the field plan data
    public class FieldPlan
    {
        public Equipment Equipment { get; set; }
        public Bins Bins { get; set; }
        public List<Trip> Trips { get; set; } = new List<Trip>();
    }
}
