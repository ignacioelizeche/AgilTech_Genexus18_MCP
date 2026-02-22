using System;
using System.Reflection;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Artech.Genexus.Common.Rules;

public class InspectSDK
{
    public static void Main()
    {
        Console.WriteLine("Inspecting SDTStructure...");
        var sdtType = typeof(SDTStructure);
        foreach (var prop in sdtType.GetProperties()) Console.WriteLine($"Property: {prop.Name} ({prop.PropertyType})");
        
        Console.WriteLine("\nInspecting SDTStructure.Root...");
        // Assuming there is a Root property
        var rootProp = sdtType.GetProperty("Root");
        if (rootProp != null) {
            var rootType = rootProp.PropertyType;
            Console.WriteLine($"Root Type: {rootType}");
            foreach (var prop in rootType.GetProperties()) Console.WriteLine($"Root Property: {prop.Name} ({prop.PropertyType})");
        }

        Console.WriteLine("\nInspecting ParmRule...");
        var parmType = typeof(ParmRule);
        foreach (var prop in parmType.GetProperties()) Console.WriteLine($"Parm Property: {prop.Name} ({prop.PropertyType})");
    }
}
