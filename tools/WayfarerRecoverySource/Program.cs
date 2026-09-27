using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Wayfarer.CommandLine;

// Resolve only from the selected immutable application's assembly directory, never the host or destination.
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    if (name.Name is null || name.Name.IndexOfAny(['/', '\\']) >= 0) return null;
    var path = Path.Combine("/app", name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
return await Inspect();

// Delay binding application types until the immutable-image resolver is installed.
[MethodImpl(MethodImplOptions.NoInlining)]
static Task<int> Inspect() => RecoverySourceCli.RunAsync(Console.Out, Console.Error);
