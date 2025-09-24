namespace ObjGen2
{
    // Equipment specifications and operational parameters
    public class Equipment
    {
        public double SoilSwell { get; set; }
        public double SoilShrink { get; set; }
        public double EquipmentLoadLCY { get; set; }
        public double CuttingWidthFt { get; set; }
        public double CuttingWidthM { get; set; }
        public double MaxCutDepthM { get; set; }
        public double MaxLooseDepositInches { get; set; }
        public double MaxLooseDepositM { get; set; }
        public double FieldEfficiency { get; set; }
        public SpeedModel SpeedModel { get; set; }
    }
}
