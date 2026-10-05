using System.Diagnostics;
using System.Text;
using MadModStudio.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynLanguageVersion = Microsoft.CodeAnalysis.CSharp.LanguageVersion;

namespace MadModStudio.Compiler;

/// <summary>
/// Compiles mod source in-process with Roslyn. References are passed as metadata-only references (the referenced game
/// assemblies are read as data, never loaded for execution). No Visual Studio or MSBuild is required.
/// The output targets whatever runtime the referenced core library (e.g. the game's mscorlib) represents, not the
/// runtime Mad Mod Studio itself runs on.
/// </summary>
public sealed class RoslynModCompiler : IModCompiler
{
    private static readonly string[] CoreLibraryNames = { "mscorlib.dll", "netstandard.dll", "System.Runtime.dll", "System.Private.CoreLib.dll" };
    private readonly ILogger<RoslynModCompiler> _log;

    public RoslynModCompiler(ILogger<RoslynModCompiler>? log = null) => _log = log ?? NullLogger<RoslynModCompiler>.Instance;

    public Task<CompileResult> CompileAsync(CompileRequest request, CancellationToken ct = default) =>
        Task.Run(() => Compile(request, ct), ct);

    private CompileResult Compile(CompileRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var log = new StringBuilder();
        var diagnostics = new List<CompilerDiagnostic>();
        log.AppendLine($"Mad Mod Studio C# compiler (Roslyn {typeof(CSharpCompilation).Assembly.GetName().Version})");
        log.AppendLine($"Assembly: {request.AssemblyName}  Configuration: {request.Configuration}  LangVersion: {request.LanguageVersion}");

        if (request.SourceFiles.Count == 0)
        {
            diagnostics.Add(new CompilerDiagnostic("MMS0001", Severity.Error, "No C# source files were found to compile.", null, null, null, null, null));
            return Fail(diagnostics, log, sw, Array.Empty<string>());
        }

        if (!LanguageVersionFacts.TryParse(request.LanguageVersion, out var langVersion))
        {
            diagnostics.Add(new CompilerDiagnostic("MMS0002", Severity.Warning, $"Unknown C# language version '{request.LanguageVersion}'; using 9.0.", null, null, null, null, null));
            langVersion = RoslynLanguageVersion.CSharp9;
        }

        var symbols = request.PreprocessorSymbols.ToList();
        if (request.Configuration == BuildConfiguration.Debug) symbols.Add("DEBUG");
        symbols.Add("TRACE");
        var parseOptions = new CSharpParseOptions(langVersion, DocumentationMode.None, SourceCodeKind.Regular, symbols.Distinct());

        var trees = new List<SyntaxTree>();
        foreach (var file in request.SourceFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var text = File.ReadAllText(file);
                var display = request.SourceRoot != null ? Path.GetRelativePath(request.SourceRoot, file).Replace('\\', '/') : file;
                trees.Add(CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(text, Encoding.UTF8), parseOptions, display, ct));
            }
            catch (IOException ex)
            {
                diagnostics.Add(new CompilerDiagnostic("MMS0003", Severity.Error, $"Could not read source file: {ex.Message}", file, null, null, null, null));
            }
        }

        var references = new List<MetadataReference>();
        var used = new List<string>();
        foreach (var r in request.ReferencePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(r))
            {
                diagnostics.Add(new CompilerDiagnostic("MMS0004", Severity.Warning, $"Reference not found: {r}", null, null, null, null, null));
                continue;
            }
            try
            {
                // Read into memory instead of CreateFromFile: a memory-mapped reference keeps the game's DLLs locked
                // (blocking game updates) until the GC happens to release it.
                var image = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(File.ReadAllBytes(r));
                references.Add(MetadataReference.CreateFromImage(image, filePath: r));
                used.Add(r);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
            {
                diagnostics.Add(new CompilerDiagnostic("MMS0005", Severity.Warning, $"Reference could not be read ({ex.Message}): {r}", null, null, null, null, null));
            }
        }
        log.AppendLine($"References ({used.Count}):");
        foreach (var r in used) log.AppendLine("  " + r);

        if (!used.Any(u => CoreLibraryNames.Contains(Path.GetFileName(u), StringComparer.OrdinalIgnoreCase)))
        {
            diagnostics.Add(new CompilerDiagnostic("MMS0006", Severity.Error,
                "No core library (mscorlib.dll / netstandard.dll) was found among the references. Check the Game Profile's managed assembly path.",
                null, null, null, null, null));
            return Fail(diagnostics, log, sw, used);
        }
        if (diagnostics.Any(d => d.Severity == Severity.Error)) return Fail(diagnostics, log, sw, used);

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(request.Configuration == BuildConfiguration.Release ? OptimizationLevel.Release : OptimizationLevel.Debug)
            .WithAllowUnsafe(request.AllowUnsafe)
            .WithDeterministic(true)
            .WithNullableContextOptions(request.NullableEnabled ? NullableContextOptions.Enable : NullableContextOptions.Disable)
            .WithPlatform(Platform.AnyCpu)
            // Unity's Mono references many facade versions; unify silently like MSBuild would.
            .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);

        var compilation = CSharpCompilation.Create(request.AssemblyName, trees, references, options);
        Directory.CreateDirectory(request.OutputDirectory);
        var dllPath = Path.Combine(request.OutputDirectory, request.AssemblyName + ".dll");
        var pdbPath = Path.Combine(request.OutputDirectory, request.AssemblyName + ".pdb");

        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emitOptions = new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: request.AssemblyName + ".pdb");
        EmitResult result = compilation.Emit(peStream, request.EmitPdb ? pdbStream : null, options: emitOptions, cancellationToken: ct);

        diagnostics.AddRange(result.Diagnostics
            .Where(d => d.Severity != DiagnosticSeverity.Hidden)
            .Select(Convert));

        foreach (var d in diagnostics) log.AppendLine(d.ToString());

        if (!result.Success)
        {
            log.AppendLine($"Build FAILED: {diagnostics.Count(d => d.Severity == Severity.Error)} error(s), {diagnostics.Count(d => d.Severity == Severity.Warning)} warning(s).");
            _log.LogInformation("Compilation of {Assembly} failed with {Errors} errors", request.AssemblyName, diagnostics.Count(d => d.Severity == Severity.Error));
            return Fail(diagnostics, log, sw, used, alreadyLogged: true);
        }

        // Only write files after a successful emit so a failed build never leaves a broken DLL behind.
        File.WriteAllBytes(dllPath, peStream.ToArray());
        string? writtenPdb = null;
        if (request.EmitPdb)
        {
            File.WriteAllBytes(pdbPath, pdbStream.ToArray());
            writtenPdb = pdbPath;
        }
        sw.Stop();
        log.AppendLine($"Build succeeded: {dllPath} ({new FileInfo(dllPath).Length:N0} bytes) in {sw.Elapsed.TotalSeconds:F2}s, {diagnostics.Count(d => d.Severity == Severity.Warning)} warning(s).");
        _log.LogInformation("Compiled {Assembly} -> {Path}", request.AssemblyName, dllPath);
        return new CompileResult
        {
            Success = true,
            OutputAssemblyPath = dllPath,
            PdbPath = writtenPdb,
            Diagnostics = diagnostics,
            ReferencesUsed = used,
            Duration = sw.Elapsed,
            Log = log.ToString(),
        };
    }

    private static CompileResult Fail(List<CompilerDiagnostic> diagnostics, StringBuilder log, Stopwatch sw, IReadOnlyList<string> used, bool alreadyLogged = false)
    {
        if (!alreadyLogged)
        {
            foreach (var d in diagnostics) log.AppendLine(d.ToString());
            log.AppendLine($"Build FAILED: {diagnostics.Count(d => d.Severity == Severity.Error)} error(s).");
        }
        sw.Stop();
        return new CompileResult { Success = false, Diagnostics = diagnostics, ReferencesUsed = used, Duration = sw.Elapsed, Log = log.ToString() };
    }

    private static CompilerDiagnostic Convert(Diagnostic d)
    {
        var sev = d.Severity switch
        {
            DiagnosticSeverity.Error => Severity.Error,
            DiagnosticSeverity.Warning => Severity.Warning,
            _ => Severity.Info,
        };
        if (d.Location.IsInSource)
        {
            var span = d.Location.GetLineSpan();
            return new CompilerDiagnostic(d.Id, sev, d.GetMessage(), span.Path,
                span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
                span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
        }
        return new CompilerDiagnostic(d.Id, sev, d.GetMessage(), null, null, null, null, null);
    }
}
