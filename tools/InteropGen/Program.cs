// Mirrors BepInEx.Unity.IL2CPP.Il2CppInteropManager (be.788) so the output is what BepInEx expects.
// Usage: InteropGen <GameAssembly.dll> <global-metadata.dat> <unity-libs dir> <out interop dir> <unity version>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AssetRipper.Primitives;
using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using Il2CppInterop.Common;
using Il2CppInterop.Generator;
using Il2CppInterop.Generator.Runners;
using LibCpp2IL;
using Microsoft.Extensions.Logging;

if (args.Length != 5)
{
    Console.Error.WriteLine("usage: InteropGen <GameAssembly.dll> <global-metadata.dat> <unity-libs> <out-dir> <unity-version>");
    return 2;
}
var (gameAssembly, metadata, unityLibs, outDir, unityVersion) = (args[0], args[1], args[2], args[3], args[4]);

InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_32);
InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64);
LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();

Cpp2IL.Core.Logging.Logger.InfoLog += (m, s) => Console.WriteLine($"[Cpp2IL/{s}] {m.Trim()}");
Cpp2IL.Core.Logging.Logger.WarningLog += (m, s) => Console.WriteLine($"[Cpp2IL/{s}] WARN {m.Trim()}");
Cpp2IL.Core.Logging.Logger.ErrorLog += (m, s) => Console.WriteLine($"[Cpp2IL/{s}] ERROR {m.Trim()}");

var sw = Stopwatch.StartNew();
Cpp2IlApi.InitializeLibCpp2Il(gameAssembly, metadata, UnityVersion.Parse(unityVersion), false);

var layers = new List<Cpp2IlProcessingLayer> { new AttributeInjectorProcessingLayer() };
foreach (var l in layers) l.PreProcess(Cpp2IlApi.CurrentAppContext, layers);
foreach (var l in layers) l.Process(Cpp2IlApi.CurrentAppContext);
var assemblies = new AsmResolverDllOutputFormatDefault().BuildAssemblies(Cpp2IlApi.CurrentAppContext);
LibCpp2IlMain.Reset();
Cpp2IlApi.CurrentAppContext = null;
Console.WriteLine($"Cpp2IL finished in {sw.Elapsed} ({assemblies.Count} assemblies)");

Directory.CreateDirectory(outDir);
foreach (var f in Directory.EnumerateFiles(outDir, "*.dll")) File.Delete(f);

using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new ConsoleLoggerProvider()).SetMinimumLevel(LogLevel.Information));
Il2CppInteropGenerator.Create(new GeneratorOptions
                      {
                          GameAssemblyPath = gameAssembly, // ScanMethodRefs = true (64-bit default)
                          Source = assemblies,
                          OutputDir = outDir,
                          UnityBaseLibsDir = unityLibs,
                      })
                      .AddLogger(loggerFactory.CreateLogger("Il2CppInteropGen"))
                      .AddInteropAssemblyGenerator()
                      .Run();
Console.WriteLine($"Done in {sw.Elapsed}. Output: {outDir}");
return 0;

sealed class ConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string name) => new ConsoleLogger(name);
    public void Dispose() { }

    sealed class ConsoleLogger(string name) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception e, Func<TState, Exception, string> fmt)
            => Console.WriteLine($"[{name}/{level}] {fmt(state, e)}{(e != null ? "\n" + e : "")}");
    }
}
