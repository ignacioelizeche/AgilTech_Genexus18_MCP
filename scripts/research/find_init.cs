using System;
using System.Reflection;
using System.IO;
using System.Linq;

public class Finder {
    public static void Main() {
        string gxPath = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR");
        if (string.IsNullOrEmpty(gxPath)) {
            Console.WriteLine("ERROR: GX_PROGRAM_DIR env var not set.");
            return;
        }
        var dlls = Directory.GetFiles(gxPath, "Artech.*.dll");
        foreach (var dll in dlls) {
            try {
                var asm = Assembly.LoadFrom(dll);
                foreach (var type in asm.GetTypes()) {
                    try {
                        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                        foreach (var method in methods) {
                            if ((method.Name == "Initialize" || method.Name == "Init") && method.GetParameters().Length == 0) {
                                Console.WriteLine($"{Path.GetFileName(dll)}: {type.FullName}.{method.Name}");
                            }
                        }
                    } catch {}
                }
            } catch {}
        }
    }
}

