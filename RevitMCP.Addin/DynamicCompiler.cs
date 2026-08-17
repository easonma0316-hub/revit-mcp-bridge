using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
#if !NET48
// Only the CSharp namespace: Microsoft.CodeAnalysis.Location would clash with Autodesk.Revit.DB.Location.
using Microsoft.CodeAnalysis.CSharp;
using MetadataReference = Microsoft.CodeAnalysis.MetadataReference;
#endif

namespace RevitMCP.Addin
{
    /// <summary>
    /// In-memory C# compiler behind <c>execute_code</c>.
    ///
    /// Kept in its own class on purpose: on .NET 8 it references Roslyn, and the
    /// runtime only loads Microsoft.CodeAnalysis*.dll when this type is first
    /// touched - i.e. on the first execute_code call, never for ordinary tools.
    /// (Other add-ins such as pyRevit ship their own Roslyn; loading ours lazily
    /// keeps out of their way, and a same-or-newer copy already in the process
    /// simply satisfies our reference.)
    /// </summary>
    internal static class DynamicCompiler
    {
#if NET48
        // .NET Framework (Revit 2024): CodeDom drives the in-box csc.exe, so the
        // add-in stays a single DLL.
        public static System.Reflection.Assembly Compile(string source, int prefixLines)
        {
            System.CodeDom.Compiler.CompilerResults results;
            using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
            {
                var pars = new System.CodeDom.Compiler.CompilerParameters { GenerateInMemory = true };
                pars.ReferencedAssemblies.Add("System.dll");
                pars.ReferencedAssemblies.Add("System.Core.dll");
                pars.ReferencedAssemblies.Add(typeof(Document).Assembly.Location);      // RevitAPI
                pars.ReferencedAssemblies.Add(typeof(UIApplication).Assembly.Location); // RevitAPIUI
                results = provider.CompileAssemblyFromSource(pars, source);
            }
            if (results.Errors.HasErrors)
            {
                var errors = results.Errors.Cast<System.CodeDom.Compiler.CompilerError>()
                    .Where(e => !e.IsWarning)
                    .Select(e => $"line {e.Line - prefixLines}: {e.ErrorText}");
                throw new McpException(McpException.BadRequest,
                    "C# compilation failed:\n" + string.Join("\n", errors));
            }
            return results.CompiledAssembly;
        }
#else
        // .NET 8 (Revit 2025+): CodeDom cannot compile on .NET Core, so use Roslyn
        // (Microsoft.CodeAnalysis.CSharp, deployed next to the add-in DLL).
        private static readonly Lazy<List<MetadataReference>> Refs =
            new Lazy<List<MetadataReference>>(BuildRefs);

        private static List<MetadataReference> BuildRefs()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Framework: every System.* / netstandard assembly the host runtime offers.
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(name) || !File.Exists(path)) continue;
                if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase))
                    paths.Add(path);
            }

            // Revit API.
            paths.Add(typeof(Document).Assembly.Location);       // RevitAPI
            paths.Add(typeof(UIApplication).Assembly.Location);  // RevitAPIUI

            return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        }

        public static System.Reflection.Assembly Compile(string source, int prefixLines)
        {
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
            var compilation = CSharpCompilation.Create(
                "McpDynamicCode_" + Guid.NewGuid().ToString("N"),
                new[] { tree },
                Refs.Value,
                new CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                                             optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release));

            using (var ms = new MemoryStream())
            {
                var emit = compilation.Emit(ms);
                if (!emit.Success)
                {
                    var errors = emit.Diagnostics
                        .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                        .Select(d => $"line {d.Location.GetLineSpan().StartLinePosition.Line + 1 - prefixLines}: {d.GetMessage()}");
                    throw new McpException(McpException.BadRequest,
                        "C# compilation failed:\n" + string.Join("\n", errors));
                }
                return System.Reflection.Assembly.Load(ms.ToArray());
            }
        }
#endif
    }
}
