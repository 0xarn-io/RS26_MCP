using System.Collections;
using System.Reflection;

namespace RobotStudioOffline.Core;

public sealed record RapidSyntaxDiagnostic(string Code, string? Argument, int? Line, int? Column,
    int? EndLine, int? EndColumn, int? Utf16Length, string Category = "syntax");
public sealed record RapidSourceInspection(string File, string? Module, string Sha256, string Engine,
    string? EngineVersion, bool Available, bool? Correct, int? ErrorCount, RapidSyntaxDiagnostic[] Errors,
    bool Truncated, string? UnavailableReason)
{
    public int IndexBase => 1;
    public string CoordinateUnits => "one-based line and UTF-16 column in the submitted source, exclusive end";
}

/// <summary>Optional RobotStudio editor service. It parses text without a station, controller, or editor window.</summary>
public static class RapidSourceDiagnostics
{
    private const string ParserAssemblyName = "RobotStudio.Services.RapidLanguage";
    private const string TypeName = ParserAssemblyName + ".Parser.RapidParser";
    public static RapidSourceInspection Inspect(RapidSource source, Assembly? installedParser = null)
    {
        string? version = null;
        var diagnostics = new List<RapidSyntaxDiagnostic>();
        if (source.Module.Length == 0) diagnostics.Add(new("ModuleHeader", "Expected one identifiable MODULE declaration.", null, null, null, null, null));
        try
        {
            // This public product service is included in the installed 2026 editor but does not
            // have the SDK's versioning contract. A missing/changed service leaves VC checks usable.
            var assembly = installedParser ?? Assembly.Load(new AssemblyName(ParserAssemblyName));
            version = assembly.GetName().Version?.ToString();
            var type = assembly.GetType(TypeName, throwOnError: true)!;
            var constructor = type.GetConstructor([typeof(string)]) ?? throw new MissingMethodException(TypeName, ".ctor(string)");
            var parser = constructor.Invoke([source.Text]);
            var parse = type.GetMethod("ParseDocument", Type.EmptyTypes) ?? throw new MissingMethodException(TypeName, "ParseDocument");
            parse.Invoke(parser, null);
            var errors = Value(parser, "Errors") as IEnumerable ?? throw new MissingMemberException(TypeName, "Errors");
            var lineStarts = new List<int> { 0 };
            for (var i = 0; i < source.Text.Length; i++) if (source.Text[i] == '\n') lineStarts.Add(i + 1);
            (int Line, int Column) Position(int offset)
            {
                var index = lineStarts.BinarySearch(offset);
                if (index < 0) index = ~index - 1;
                return (index + 1, offset - lineStarts[index] + 1);
            }
            var count = diagnostics.Count;
            var hasSyntaxError = count > 0;
            foreach (var error in errors)
            {
                count++;
                var code = Value(error!, "Code")!.ToString()!;
                // The editor shares its error enum with its semantic analyzer. Standalone files
                // can refer to symbols in other modules, so only known syntax codes block a load.
                var syntax = code is "InvalidToken" or "SyntaxError" or "ExpectedToken" or "ExpectedExpressionTerm"
                    or "UnexpectedToken" or "InvalidExpression" or "UnexpectedComment" or "TooManyArrayDimensions";
                hasSyntaxError |= syntax;
                if (diagnostics.Count >= 500) continue;
                var category = syntax ? "syntax" : "requires_task_context";
                var argument = Value(error!, "Argument") as string;
                if (argument?.Length > 256) argument = argument[..256];
                var range = Value(error!, "Range")!;
                var offset = (int)Value(range, "Offset")!;
                var length = (int)Value(range, "Length")!;
                if (offset < 0 || offset > source.Text.Length || length < 0 || length > source.Text.Length - offset)
                    diagnostics.Add(new(code, argument, null, null, null, null, null, category));
                else
                {
                    var start = Position(offset);
                    var end = Position(offset + length);
                    diagnostics.Add(new(code, argument, start.Line, start.Column, end.Line, end.Column, length, category));
                }
            }
            return new(source.File, source.Module.Length == 0 ? null : source.Module, source.Sha256, ParserAssemblyName, version,
                true, hasSyntaxError ? false : count == 0 ? true : null, count, diagnostics.ToArray(), count > diagnostics.Count, null);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            var error = ex.GetBaseException();
            return new(source.File, source.Module.Length == 0 ? null : source.Module, source.Sha256, ParserAssemblyName, version,
                false, source.Module.Length == 0 ? false : null, null, diagnostics.ToArray(), false,
                $"{error.GetType().Name}: {error.Message}");
        }
    }
    private static object? Value(object value, string name)
    {
        var property = value.GetType().GetProperty(name) ?? throw new MissingMemberException(value.GetType().FullName, name);
        return property.GetValue(value);
    }
}
