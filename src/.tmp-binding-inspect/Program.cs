using System.Reflection;

var dll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "mediapipetasksvision.android", "0.10.32", "lib", "net9.0-android35.0", "MediaPipeTasksVision.dll");
var asm = Assembly.LoadFrom(dll);
try
{
    foreach (var type in asm.GetTypes().OrderBy(t => t.FullName))
    {
        if (type.FullName is null)
            continue;

        if (type.FullName.Contains("BaseOptions", StringComparison.OrdinalIgnoreCase) ||
            type.FullName.Contains("Delegate", StringComparison.OrdinalIgnoreCase) ||
            type.FullName.Contains("FaceLandmarker", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(type.FullName);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.Name.Contains("Delegate", StringComparison.OrdinalIgnoreCase) ||
                    method.Name.Contains("Build", StringComparison.OrdinalIgnoreCase) ||
                    method.Name.Contains("ModelAssetPath", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  {method}");
                }
            }

            if (type.IsEnum)
            {
                foreach (var name in Enum.GetNames(type))
                {
                    Console.WriteLine($"  enum {name}");
                }
            }
        }
    }
}
catch (ReflectionTypeLoadException ex)
{
    foreach (var type in ex.Types.Where(t => t != null).OrderBy(t => t!.FullName))
    {
        if (type!.FullName is null)
            continue;

        if (type.FullName.Contains("BaseOptions", StringComparison.OrdinalIgnoreCase) ||
            type.FullName.Contains("Delegate", StringComparison.OrdinalIgnoreCase) ||
            type.FullName.Contains("FaceLandmarker", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(type.FullName);
        }
    }

    Console.WriteLine("LOADER EXCEPTIONS");
    foreach (var loader in ex.LoaderExceptions)
    {
        Console.WriteLine(loader?.Message);
    }
}
