using MadModStudio.Compiler;
using MadModStudio.Core.History;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Deploy;
using MadModStudio.Game7DTD.Index;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Logs;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.Game7DTD.Repair;
using MadModStudio.Game7DTD.Scanner;
using MadModStudio.Game7DTD.Validation;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Comparison;
using MadModStudio.ModAnalysis.Decompilation;
using MadModStudio.ModAnalysis.Harmony;
using MadModStudio.Packaging;
using Microsoft.Extensions.DependencyInjection;

namespace MadModStudio.Game7DTD;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers generic analysis/compile/packaging services plus the 7 Days to Die game module.</summary>
    public static IServiceCollection AddGame7DTD(this IServiceCollection services)
    {
        services.AddSingleton<AssemblyInspector>();
        services.AddSingleton<DecompilerService>();
        services.AddSingleton<SourceHarmonyScanner>();
        services.AddSingleton<VersionComparer>();
        services.AddSingleton<IModCompiler, RoslynModCompiler>();
        services.AddSingleton<ModPackager>();
        services.AddSingleton<ProjectHistoryService>();

        services.AddSingleton<GameInstallLocator>();
        services.AddSingleton<GameVersionDetector>();
        services.AddSingleton<RuntimeProfileDetector>();
        services.AddSingleton<GameIndexBuilder>();
        services.AddSingleton<GameProfileService>();
        services.AddSingleton<GameReferenceResolver>();
        services.AddSingleton<CompileInputResolver>();
        services.AddSingleton<ModAnalyzer>();
        services.AddSingleton<ModImporter>();
        services.AddSingleton<ProjectService>();
        services.AddSingleton<ModBuildPipeline>();
        services.AddSingleton<LogParser>();
        services.AddSingleton<RepairService>();
        services.AddSingleton<SafeFixService>();
        services.AddSingleton<BatchModScanner>();
        services.AddSingleton<ModDeployService>();

        // Validators run in registration order.
        services.AddSingleton<IModValidator, ModInfoValidator>();
        services.AddSingleton<IModValidator, FolderStructureValidator>();
        services.AddSingleton<IModValidator, XmlWellFormedValidator>();
        services.AddSingleton<IModValidator, XmlPatchTargetValidator>();
        services.AddSingleton<IModValidator, GameLayoutCompatibilityValidator>();
        services.AddSingleton<IModValidator, XmlReferenceValidator>();
        services.AddSingleton<IModValidator, DuplicateFileValidator>();
        services.AddSingleton<IModValidator, LocalizationValidator>();
        services.AddSingleton<IModValidator, DllDependencyValidator>();
        services.AddSingleton<IModValidator, GameApiReferenceValidator>();
        services.AddSingleton<IModValidator, CompilerResultValidator>();
        services.AddSingleton<IModValidator, HarmonyTargetValidator>();
        services.AddSingleton<IModValidator, ServerSideValidator>();
        services.AddSingleton<IModValidator, PackageStructureValidator>();
        return services;
    }
}
