using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace ObjGen2
{
    public class FieldPlanParser
    {
        public FieldPlan Parse(string filePath)
        {
            var doc = XDocument.Load(filePath);
            var fieldPlanElement = doc.Root;

            if (fieldPlanElement?.Name != "FieldPlan")
            {
                throw new InvalidOperationException("Invalid field plan file format. Root element must be 'FieldPlan'.");
            }

            var fieldPlan = new FieldPlan();

            // Parse Equipment section
            var equipmentElement = fieldPlanElement.Element("Equipment");
            if (equipmentElement != null)
            {
                fieldPlan.Equipment = ParseEquipment(equipmentElement);
            }

            // Parse Bins section
            var binsElement = fieldPlanElement.Element("Bins");
            if (binsElement != null)
            {
                fieldPlan.Bins = ParseBins(binsElement);
            }

            // Parse Trips section
            var tripsElement = fieldPlanElement.Element("Trips");
            if (tripsElement != null)
            {
                fieldPlan.Trips = ParseTrips(tripsElement);
            }

            return fieldPlan;
        }

        private Equipment ParseEquipment(XElement equipmentElement)
        {
            var equipment = new Equipment();

            equipment.SoilSwell = GetDoubleValue(equipmentElement, "SoilSwell");
            equipment.SoilShrink = GetDoubleValue(equipmentElement, "SoilShrink");
            equipment.EquipmentLoadLCY = GetDoubleValue(equipmentElement, "EquipmentLoadLCY");
            equipment.CuttingWidthFt = GetDoubleValue(equipmentElement, "CuttingWidthFt");
            equipment.CuttingWidthM = GetDoubleValue(equipmentElement, "CuttingWidthM");
            equipment.MaxCutDepthM = GetDoubleValue(equipmentElement, "MaxCutDepthM");
            equipment.MaxLooseDepositInches = GetDoubleValue(equipmentElement, "MaxLooseDepositInches");
            equipment.MaxLooseDepositM = GetDoubleValue(equipmentElement, "MaxLooseDepositM");
            equipment.FieldEfficiency = GetDoubleValue(equipmentElement, "FieldEfficiency");

            var speedModelElement = equipmentElement.Element("SpeedModel");
            if (speedModelElement != null)
            {
                equipment.SpeedModel = new SpeedModel
                {
                    VLoadMs = GetDoubleValue(speedModelElement, "VLoadMs"),
                    VHaulMs = GetDoubleValue(speedModelElement, "VHaulMs"),
                    VEmptyMs = GetDoubleValue(speedModelElement, "VEmptyMs"),
                    VDumpMs = GetDoubleValue(speedModelElement, "VDumpMs")
                };
            }

            return equipment;
        }

        private Bins ParseBins(XElement binsElement)
        {
            var bins = new Bins();

            bins.BinSizeM = GetDoubleValue(binsElement, "BinSizeM");

            var binDimensionsElement = binsElement.Element("BinDimensions");
            if (binDimensionsElement != null)
            {
                bins.BinDimensions = new BinDimensions
                {
                    Width = GetIntValue(binDimensionsElement, "Width"),
                    Height = GetIntValue(binDimensionsElement, "Height"),
                    TotalBins = GetIntValue(binDimensionsElement, "TotalBins")
                };
            }

            var fieldBoundsElement = binsElement.Element("FieldBounds");
            if (fieldBoundsElement != null)
            {
                bins.FieldBounds = new FieldBounds
                {
                    MinLatitude = GetDoubleValue(fieldBoundsElement, "MinLatitude"),
                    MaxLatitude = GetDoubleValue(fieldBoundsElement, "MaxLatitude"),
                    MinLongitude = GetDoubleValue(fieldBoundsElement, "MinLongitude"),
                    MaxLongitude = GetDoubleValue(fieldBoundsElement, "MaxLongitude")
                };
            }

            var binTableElement = binsElement.Element("BinTable");
            if (binTableElement != null)
            {
                bins.BinTable = ParseBinTable(binTableElement);
                // Calculate initial elevation range after parsing all bins
                bins.BinTable.CalculateInitialElevationRange();
            }

            return bins;
        }

        private BinTable ParseBinTable(XElement binTableElement)
        {
            var binTable = new BinTable();

            var binElements = binTableElement.Elements("Bin");
            foreach (var binElement in binElements)
            {
                var bin = new Bin
                {
                    IndexX = GetIntValue(binElement, "IndexX"),
                    IndexY = GetIntValue(binElement, "IndexY"),
                    CutAmountM = GetDoubleValue(binElement, "CutAmountM"),
                    FillAmountM = GetDoubleValue(binElement, "FillAmountM"),
                    ExistingElevationM = GetDoubleValue(binElement, "ExistingElevationM")
                };

                var southwestElement = binElement.Element("SouthwestCorner");
                if (southwestElement != null)
                {
                    bin.SouthwestCorner = new Coordinate
                    {
                        Latitude = GetDoubleValue(southwestElement, "Latitude"),
                        Longitude = GetDoubleValue(southwestElement, "Longitude")
                    };
                }

                var northeastElement = binElement.Element("NortheastCorner");
                if (northeastElement != null)
                {
                    bin.NortheastCorner = new Coordinate
                    {
                        Latitude = GetDoubleValue(northeastElement, "Latitude"),
                        Longitude = GetDoubleValue(northeastElement, "Longitude")
                    };
                }

                var centroidElement = binElement.Element("Centroid");
                if (centroidElement != null)
                {
                    bin.Centroid = new Coordinate
                    {
                        Latitude = GetDoubleValue(centroidElement, "Latitude"),
                        Longitude = GetDoubleValue(centroidElement, "Longitude")
                    };
                }

                // Calculate target elevation: existing elevation - cut amount + fill amount
                bin.TargetElevationM = bin.ExistingElevationM - bin.CutAmountM + bin.FillAmountM;

                binTable.Bins.Add(bin);
            }

            return binTable;
        }

        private List<Trip> ParseTrips(XElement tripsElement)
        {
            var trips = new List<Trip>();

            var tripElements = tripsElement.Elements("Trip");
            foreach (var tripElement in tripElements)
            {
                var trip = new Trip
                {
                    TripIndex = GetIntValue(tripElement, "TripIndex")
                };

                var startCoordsElement = tripElement.Element("StartCoordinates");
                if (startCoordsElement != null)
                {
                    trip.StartCoordinates = new Coordinate
                    {
                        Latitude = GetDoubleValue(startCoordsElement, "Latitude"),
                        Longitude = GetDoubleValue(startCoordsElement, "Longitude")
                    };
                }

                var endCoordsElement = tripElement.Element("EndCoordinates");
                if (endCoordsElement != null)
                {
                    trip.EndCoordinates = new Coordinate
                    {
                        Latitude = GetDoubleValue(endCoordsElement, "Latitude"),
                        Longitude = GetDoubleValue(endCoordsElement, "Longitude")
                    };
                }

                var cutElement = tripElement.Element("Cut");
                if (cutElement != null)
                {
                    trip.Cut = ParseCutOperation(cutElement);
                }

                var fillElement = tripElement.Element("Fill");
                if (fillElement != null)
                {
                    trip.Fill = ParseFillOperation(fillElement);
                }

                var metricsElement = tripElement.Element("Metrics");
                if (metricsElement != null)
                {
                    trip.Metrics = new TripMetrics
                    {
                        BCY = GetDoubleValue(metricsElement, "BCY"),
                        DistanceM = GetDoubleValue(metricsElement, "DistanceM"),
                        TripTimeSeconds = GetDoubleValue(metricsElement, "TripTimeSeconds"),
                        TotalDistanceM = GetDoubleValue(metricsElement, "TotalDistanceM"),
                        TotalCutBins = GetIntValue(metricsElement, "TotalCutBins"),
                        TotalFillBins = GetIntValue(metricsElement, "TotalFillBins")
                    };
                }

                trips.Add(trip);
            }

            return trips;
        }

        private CutOperation ParseCutOperation(XElement cutElement)
        {
            var cut = new CutOperation
            {
                DepthM = GetDoubleValue(cutElement, "DepthM"),
                LengthM = GetDoubleValue(cutElement, "LengthM"),
                HeadingDeg = GetDoubleValue(cutElement, "HeadingDeg") // Keep operation-level heading
            };

            var coordsElement = cutElement.Element("Coordinates");
            if (coordsElement != null)
            {
                cut.Coordinates = new OperationCoordinates
                {
                    StartLatitude = GetDoubleValue(coordsElement, "StartLatitude"),
                    StartLongitude = GetDoubleValue(coordsElement, "StartLongitude"),
                    StopLatitude = GetDoubleValue(coordsElement, "StopLatitude"),
                    StopLongitude = GetDoubleValue(coordsElement, "StopLongitude")
                };
            }

            var profileElement = cutElement.Element("Profile");
            if (profileElement != null)
            {
                var profileEntries = profileElement.Elements("ProfileEntry");
                foreach (var entryElement in profileEntries)
                {
                    cut.Profile.Add(new ProfileEntry
                    {
                        DistanceM = GetDoubleValue(entryElement, "DistanceM"),
                        DepthM = GetDoubleValue(entryElement, "DepthM")
                    });
                }
            }

            var binsElement = cutElement.Element("Bins");
            if (binsElement != null)
            {
                var binElements = binsElement.Elements("Bin");
                foreach (var binElement in binElements)
                {
                    cut.Bins.Add(new BinOperation
                    {
                        IndexX = GetIntValue(binElement, "IndexX"),
                        IndexY = GetIntValue(binElement, "IndexY"),
                        CutAmountM = GetDoubleValue(binElement, "CutAmountM"),
                        FillAmountM = GetDoubleValue(binElement, "FillAmountM")
                    });
                }
            }

            return cut;
        }

        private FillOperation ParseFillOperation(XElement fillElement)
        {
            var fill = new FillOperation
            {
                DepthM = GetDoubleValue(fillElement, "DepthM"),
                LengthM = GetDoubleValue(fillElement, "LengthM"),
                HeadingDeg = GetDoubleValue(fillElement, "HeadingDeg") // Keep operation-level heading
            };

            var coordsElement = fillElement.Element("Coordinates");
            if (coordsElement != null)
            {
                fill.Coordinates = new OperationCoordinates
                {
                    StartLatitude = GetDoubleValue(coordsElement, "StartLatitude"),
                    StartLongitude = GetDoubleValue(coordsElement, "StartLongitude"),
                    StopLatitude = GetDoubleValue(coordsElement, "StopLatitude"),
                    StopLongitude = GetDoubleValue(coordsElement, "StopLongitude")
                };
            }

            var profileElement = fillElement.Element("Profile");
            if (profileElement != null)
            {
                var profileEntries = profileElement.Elements("ProfileEntry");
                foreach (var entryElement in profileEntries)
                {
                    fill.Profile.Add(new ProfileEntry
                    {
                        DistanceM = GetDoubleValue(entryElement, "DistanceM"),
                        DepthM = GetDoubleValue(entryElement, "DepthM")
                    });
                }
            }

            var binsElement = fillElement.Element("Bins");
            if (binsElement != null)
            {
                var binElements = binsElement.Elements("Bin");
                foreach (var binElement in binElements)
                {
                    fill.Bins.Add(new BinOperation
                    {
                        IndexX = GetIntValue(binElement, "IndexX"),
                        IndexY = GetIntValue(binElement, "IndexY"),
                        CutAmountM = GetDoubleValue(binElement, "CutAmountM"),
                        FillAmountM = GetDoubleValue(binElement, "FillAmountM")
                    });
                }
            }

            return fill;
        }

        private double GetDoubleValue(XElement parent, string elementName)
        {
            var element = parent.Element(elementName);
            return element != null && double.TryParse(element.Value, out double result) ? result : 0.0;
        }

        private int GetIntValue(XElement parent, string elementName)
        {
            var element = parent.Element(elementName);
            return element != null && int.TryParse(element.Value, out int result) ? result : 0;
        }
    }
}
