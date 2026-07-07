using System.IO;
using System.Reflection;
#if NET8_0_OR_GREATER
using System.Runtime.Loader;
#endif

namespace C_toolsPlugin;

internal static class CtoolRuntimeAssemblyResolver
{
    private static readonly HashSet<string> KnownDependencyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "C_toolsJson",
        "C_toolsShared",
        "Microsoft.Bcl.AsyncInterfaces",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "PdfSharp",
        "PdfSharp.BarCodes",
        "PdfSharp.Charting",
        "PdfSharp.Cryptography",
        "PdfSharp.Quality",
        "PdfSharp.Shared",
        "PdfSharp.Snippets",
        "PdfSharp.System",
        "PdfSharp.WPFonts",
        "System.Buffers",
        "System.IO.Pipelines",
        "System.Memory",
        "System.Numerics.Vectors",
        "System.Runtime.CompilerServices.Unsafe",
        "System.Security.Cryptography.Pkcs",
        "System.Text.Encodings.Web",
        "System.Text.Json",
        "System.Threading.Tasks.Extensions",
        "System.ValueTuple"
    };

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static int s_initialized;
    private static string[]? s_assemblySearchDirectories;

    internal static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref s_initialized, 1) != 0)
            return;

        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

#if NET8_0_OR_GREATER
        var loadContext = AssemblyLoadContext.GetLoadContext(typeof(CtoolRuntimeAssemblyResolver).Assembly);
        if (loadContext != null)
            loadContext.Resolving += OnAssemblyLoadContextResolving;

        if (!ReferenceEquals(loadContext, AssemblyLoadContext.Default))
            AssemblyLoadContext.Default.Resolving += OnAssemblyLoadContextResolving;

        PreloadKnownDependencies(loadContext ?? AssemblyLoadContext.Default);
#else
        PreloadKnownDependencies();
#endif
    }

    private static string BundleDirectory
    {
        get
        {
            var assemblyPath = typeof(CtoolRuntimeAssemblyResolver).Assembly.Location;
            var directory = Path.GetDirectoryName(assemblyPath);
            return string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory;
        }
    }

    private static IEnumerable<string> EnumerateAssemblySearchDirectories()
    {
        if (s_assemblySearchDirectories == null)
        {
            var dirs = new List<string> { BundleDirectory };

            var runtimesDirectory = Path.Combine(BundleDirectory, "runtimes");
            if (Directory.Exists(runtimesDirectory))
            {
                foreach (var directory in Directory.EnumerateDirectories(runtimesDirectory, "*", SearchOption.AllDirectories))
                    dirs.Add(directory);
            }

            s_assemblySearchDirectories = dirs.ToArray();
        }

        foreach (var dir in s_assemblySearchDirectories)
            yield return dir;
    }

    private static string? TryResolveAssemblyPath(string? simpleName)
    {
        if (string.IsNullOrWhiteSpace(simpleName))
            return null;

        foreach (var directory in EnumerateAssemblySearchDirectories().Distinct(PathComparer))
        {
            var candidatePath = Path.Combine(directory, simpleName + ".dll");
            if (File.Exists(candidatePath))
                return candidatePath;
        }

        return null;
    }

    private static Assembly? TryGetLoadedAssembly(string? simpleName)
    {
        if (string.IsNullOrWhiteSpace(simpleName))
            return null;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                return assembly;
        }

        return null;
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            var requestedAssemblyName = new AssemblyName(args.Name);
            return TryGetLoadedAssembly(requestedAssemblyName.Name) ??
                   TryLoadAssembly(requestedAssemblyName.Name, static path => Assembly.LoadFrom(path));
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("C_TOOL 主插件解析运行时依赖失败（NETFX）", ex);
            return null;
        }
    }

#if NET8_0_OR_GREATER
    private static Assembly? OnAssemblyLoadContextResolving(AssemblyLoadContext loadContext, AssemblyName assemblyName)
    {
        try
        {
            return TryGetLoadedAssembly(assemblyName.Name) ??
                   TryLoadAssembly(assemblyName.Name, path => loadContext.LoadFromAssemblyPath(path));
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("C_TOOL 主插件解析运行时依赖失败（NET8）", ex);
            return null;
        }
    }

    private static void PreloadKnownDependencies(AssemblyLoadContext loadContext)
    {
        foreach (var dependencyName in KnownDependencyNames)
        {
            if (TryGetLoadedAssembly(dependencyName) != null)
                continue;

            try
            {
                var path = TryResolveAssemblyPath(dependencyName);
                if (!string.IsNullOrWhiteSpace(path))
                    loadContext.LoadFromAssemblyPath(path);
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal($"C_TOOL 主插件预加载依赖失败：{dependencyName}", ex);
            }
        }
    }
#else
    private static void PreloadKnownDependencies()
    {
        foreach (var dependencyName in KnownDependencyNames)
        {
            if (TryGetLoadedAssembly(dependencyName) != null)
                continue;

            try
            {
                var path = TryResolveAssemblyPath(dependencyName);
                if (!string.IsNullOrWhiteSpace(path))
                    Assembly.LoadFrom(path);
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal($"C_TOOL 主插件预加载依赖失败：{dependencyName}", ex);
            }
        }
    }
#endif

    private static Assembly? TryLoadAssembly(string? simpleName, Func<string, Assembly> loader)
    {
        var path = TryResolveAssemblyPath(simpleName);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return loader(path!);
    }
}
