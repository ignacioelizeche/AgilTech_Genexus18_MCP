using System;
using System.IO;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Common.Diagnostics;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Services;
using GxMcp.Worker.Helpers;

public class TestValidation
{
    public static void Main(string[] args)
    {
        // This script is intended to be run via a tool that can load the GeneXus SDK environment
        // For now, I'll just write it to see if it compiles and what the SDK offers.
    }

    public static void RunValidation(KBObject obj)
    {
        OutputMessages output = new OutputMessages();
        obj.Validate(output);

        foreach (OutputError error in output.Errors)
        {
            Console.WriteLine($"Error: {error.ErrorCode} - {error.Text}");
            if (error.Position is Artech.Common.Location.TextPosition textPos)
            {
                Console.WriteLine($"  At Line: {textPos.Line}, Char: {textPos.Char}");
            }
        }
    }
}
