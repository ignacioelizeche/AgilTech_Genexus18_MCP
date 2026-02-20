using System;
using System.Reflection;
using System.IO;

public class Finder {
    public static void Main() {
        string gxPath = @'C:\Program Files (x86)\GeneXus\GeneXus18';
        string[] dlls = Directory.GetFiles(gxPath, 'Artech.*.dll');
        foreach (var dll in dlls) {
            try {
                var asm = Assembly.LoadFrom(dll);
                foreach (var type in asm.GetTypes()) {
                    if (type.Name == 'ContextService') {
                        Console.WriteLine($'Found ContextService in {Path.GetFileName(dll)}: {type.FullName}');
                    }
                    if (type.Name == 'UIServices') {
                        Console.WriteLine($'Found UIServices in {Path.GetFileName(dll)}: {type.FullName}');
                    }
                }
            } catch {}
        }
    }
}
