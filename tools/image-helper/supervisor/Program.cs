using System.Text.Json;
using Wayfarer.ImagePrototype;

/// <summary>Standalone invocation demonstration; source and destination paths are operator input.</summary>
internal static class Program
{
    /// <summary>Only a successful verified derivative is saved to the requested local file.</summary>
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: ImageHelper.Supervisor HELPER INPUT OUTPUT");
            return 2;
        }
        var supervisor = new ImageHelperSupervisor(Path.GetFullPath(args[0]));
        var source = await File.ReadAllBytesAsync(args[1]);
        var result = await supervisor.TransformAsync(source, new ImageTransform(), TimeSpan.FromSeconds(30));
        if (result.Status == ImageTransformStatus.Success)
            await File.WriteAllBytesAsync(args[2], result.Bytes!);
        Console.WriteLine(JsonSerializer.Serialize(result with { Bytes = null }));
        return result.Status == ImageTransformStatus.Success ? 0 : 1;
    }
}
