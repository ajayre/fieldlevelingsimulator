using System;
using System.IO;
using NDesk.Options;

namespace ObjGen2
{
    class Program
    {
        static void Main(string[] args)
        {
            string fieldPlanFile = null;
            string outputFolder = null;
            bool showHelp = false;
            bool annotateTrips = true; // Default to true

            var options = new OptionSet()
            {
                { "f|file=", "Path to the .fieldplan file to parse", v => fieldPlanFile = v },
                { "o|output=", "Output folder for generated files", v => outputFolder = v },
                { "a|annotate", "Annotate trip BMPs with start/end markers and arrows (default: true)", v => annotateTrips = v != null },
                { "no-annotate", "Disable trip BMP annotation", v => annotateTrips = v == null },
                { "h|help", "Show this help message", v => showHelp = v != null }
            };

            try
            {
                options.Parse(args);
            }
            catch (OptionException e)
            {
                Console.WriteLine($"Error: {e.Message}");
                Console.WriteLine("Try '--help' for more information.");
                return;
            }

            if (showHelp || string.IsNullOrEmpty(fieldPlanFile))
            {
                ShowHelp(options);
                return;
            }

            // Set default output folder if not specified
            if (string.IsNullOrEmpty(outputFolder))
            {
                outputFolder = "output";
            }

            if (!File.Exists(fieldPlanFile))
            {
                Console.WriteLine($"Error: File '{fieldPlanFile}' not found.");
                return;
            }

            try
            {
                Console.WriteLine("ObjGen2 - Field Plan Parser");
                Console.WriteLine("==========================");
                Console.WriteLine();

                // Create output folder if it doesn't exist
                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                    Console.WriteLine($"Created output folder: {outputFolder}");
                }
                else
                {
                    Console.WriteLine($"Using output folder: {outputFolder}");
                }
                Console.WriteLine();

                // Parse the field plan file
                var fieldPlan = ParseFieldPlan(fieldPlanFile);
                
                Console.WriteLine("Field plan loaded successfully!");
                Console.WriteLine($"Equipment: {fieldPlan.Equipment?.SoilSwell} soil swell factor");
                Console.WriteLine($"Bins: {fieldPlan.Bins?.BinTable?.Bins?.Count ?? 0} bins loaded");
                Console.WriteLine($"Initial elevation range: {fieldPlan.Bins?.BinTable?.InitialMinimumElevationM:F3}m to {fieldPlan.Bins?.BinTable?.InitialMaximumElevationM:F3}m");
                Console.WriteLine($"Trips: {fieldPlan.Trips?.Count ?? 0} trips loaded");
                Console.WriteLine($"Output folder: {outputFolder}");
                
                // Execute trips and generate BMP visualization
                Console.WriteLine();
                Console.WriteLine("Executing trips and generating BMP visualization...");
                var tripExecutor = new TripExecutor();
                tripExecutor.ExecuteTrips(fieldPlan, outputFolder, annotateTrips);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing field plan: {ex.Message}");
            }
        }

        static void ShowHelp(OptionSet options)
        {
            Console.WriteLine("ObjGen2 - Field Plan Parser");
            Console.WriteLine("Usage: ObjGen2 [OPTIONS]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            options.WriteOptionDescriptions(Console.Out);
        }

        static FieldPlan ParseFieldPlan(string filePath)
        {
            Console.WriteLine($"Loading field plan from: {filePath}");
            
            // Show progress bar
            ShowProgressBar("Parsing XML", 0, 100);
            
            var parser = new FieldPlanParser();
            var fieldPlan = parser.Parse(filePath);
            
            ShowProgressBar("Parsing XML", 100, 100);
            Console.WriteLine();
            
            return fieldPlan;
        }

        static void ShowProgressBar(string operation, int current, int total)
        {
            int width = 50;
            int filled = (int)((double)current / total * width);
            int percent = (int)((double)current / total * 100);
            
            string bar = new string('█', filled) + new string('░', width - filled);
            Console.Write($"\r{operation}: [{bar}] {percent}%");
            
            if (current >= total)
            {
                Console.WriteLine();
            }
        }
    }
}
