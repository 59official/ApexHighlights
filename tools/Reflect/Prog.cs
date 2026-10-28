using System;
using System.Linq;
using System.Reflection;

internal static class Prog
{
    private static void Main(string[] args)
    {
        var path = args.Length > 0 ? args[0]
            : @"C:\Users\59official\.nuget\packages\vortice.mediafoundation\3.8.3\lib\net8.0\Vortice.MediaFoundation.dll";
        var asm = Assembly.LoadFrom(path);
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }

        Console.WriteLine($"asm={asm.GetName().Name} types={types.Length}");
        foreach (var n in new[] { "MediaTypeAttributeKeys", "AudioFormatGuids", "TMessageType", "TransformCategoryGuids", "MediaTypeGuids", "ProcessOutputFlags", "AudioStreamInfoFlags", "MediaEventTypes" })
            Show(types, n);
    }

    private static void Show(Type[] types, string typeName)
    {
        var t = types.FirstOrDefault(x => x.Name == typeName);
        if (t == null) { Console.WriteLine($"!! {typeName} 未找到"); return; }
        Console.WriteLine($"== {t.FullName}");
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(f => f.Name))
            Console.WriteLine($"   {f.Name} = {Val(f.GetValue(null))}");
    }

    private static string Val(object? v) => v is Guid g ? g.ToString() : v?.ToString() ?? "null";
}
