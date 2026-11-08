using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobotStudioOffline.Core;

public sealed record RapidModuleRequest(string Operation, string? Robot, string? TaskName, string[] Files, string[] Pers, bool IncludeReal);

public static class RapidModuleRequests
{
    public static readonly string[] Operations = ["rapid_check", "rapid_load", "rapid_diff", "rapid_sync_real"];

    public static RapidModuleRequest Parse(string operation, JsonElement arguments)
    {
        RequestParser.RejectControllerAccess(arguments);
        string[] fields = operation switch
        {
            "rapid_check" => ["robot", "task", "files"],
            "rapid_load" or "rapid_sync_real" => ["robot", "task", "files"],
            "rapid_diff" => ["robot", "task", "files", "pers", "includeReal"],
            _ => throw new BridgeException("unknown_operation", "Unknown RAPID module operation.", 404)
        };
        RequestParser.CheckProperties(arguments, fields, "arguments");
        var task = RequestParser.ReadName(arguments, "task");
        if (task is not null && !Identifier(task)) throw Invalid("task must be an exact RAPID task identifier.");
        if (operation == "rapid_sync_real" && task is null) throw Invalid("Select the real task explicitly.");
        var files = operation == "rapid_check" && !arguments.TryGetProperty("files", out _)
            ? [] : Strings(arguments, "files", 20, LocalModulePath);
        var pers = arguments.TryGetProperty("pers", out _) ? Strings(arguments, "pers", 100, PersistentSelector) : [];
        var real = operation == "rapid_sync_real";
        if (arguments.TryGetProperty("includeReal", out var include))
        {
            if (include.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("includeReal must be boolean.");
            real = include.GetBoolean();
        }
        return new(operation, RequestParser.ReadName(arguments, "robot"), task, files, pers, real);
    }

    private static string[] Strings(JsonElement args, string key, int max, Func<string, string> validate)
    {
        if (!args.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() < 1 || list.GetArrayLength() > max)
            throw Invalid($"{key} must contain 1..{max} entries.");
        var result = list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? validate(item.GetString()!) : throw Invalid($"{key} entries must be strings.")).ToArray();
        if (result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length) throw Invalid($"Duplicate {key} selection.");
        return result;
    }

    private static string LocalModulePath(string path)
    {
        var canonical = LocalInputFile.CanonicalPath(path);
        if (Path.GetExtension(canonical).ToLowerInvariant() is not (".mod" or ".sys" or ".modx" or ".sysx"))
            throw Invalid("Select local .mod, .sys, .modx or .sysx source files.");
        return canonical;
    }

    private static string PersistentSelector(string selector)
    {
        var parts = selector.Split('/');
        if (parts.Length != 2 || !parts.All(Identifier)) throw Invalid("Select PERS using Module/name.");
        return selector;
    }

    internal static bool Identifier(string value) => value.Length is > 0 and <= 128 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static BridgeException Invalid(string message) => new("invalid_request", message);
}

public sealed record RapidSource(string File, string Module, byte[] Bytes, string Text, string Sha256)
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public static RapidSource Read(string path, bool allowUnidentifiedModule = false)
    {
        LocalInputFile.RequireAvailable(path, "rapid");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > MaxBytes)
            throw new BridgeException("rapid_source_size", "Each module must contain 1 byte through 2 MiB of source.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return FromBytes(path, bytes, allowUnidentifiedModule);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static RapidSource FromBytes(string path, byte[] bytes, bool allowUnidentifiedModule = false)
    {
        var body = bytes.AsSpan();
        if (body.StartsWith(Encoding.UTF8.GetPreamble())) body = body[3..];
        string text;
        try { text = StrictUtf8.GetString(body); }
        catch (DecoderFallbackException) when (Path.GetExtension(path).ToLowerInvariant() is ".mod" or ".sys")
        {
            text = Encoding.Latin1.GetString(body);
        }
        catch (DecoderFallbackException) { throw new BridgeException("rapid_encoding", "Unicode RAPID modules require UTF-8."); }
        if (text.Contains('\0')) throw new BridgeException("rapid_encoding", "Expected readable RAPID module source.");
        text = text.ReplaceLineEndings("\n");
        var headers = Regex.Matches(text, @"^[\t ]*MODULE[\t ]+([a-z_][a-z0-9_]*)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var identified = headers.Count == 1 && RapidModuleRequests.Identifier(headers[0].Groups[1].Value);
        if (!identified && !allowUnidentifiedModule)
            throw new BridgeException("rapid_header", "The selected source must contain exactly one identifiable MODULE declaration.");
        return new(path, identified ? headers[0].Groups[1].Value : "", bytes, text, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static object TextDifference(string first, string second)
    {
        first = first.ReplaceLineEndings("\n");
        second = second.ReplaceLineEndings("\n");
        var left = first.Split('\n');
        var right = second.Split('\n');
        var start = 0;
        while (start < left.Length && start < right.Length && left[start] == right[start]) start++;
        var commonTail = 0;
        while (commonTail < left.Length - start && commonTail < right.Length - start && left[^(commonTail + 1)] == right[^(commonTail + 1)]) commonTail++;
        var leftCount = left.Length - start - commonTail;
        var rightCount = right.Length - start - commonTail;
        object Side(string[] lines, string value, int count) => new
        {
            sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))), totalLines = lines.Length,
            changedSpanLines = count, excerpt = lines.Skip(start).Take(Math.Min(8, count)).Select((line, i) =>
                new { line = start + i + 1, text = line[..Math.Min(line.Length, 240)], truncated = line.Length > 240 }).ToArray()
        };
        return new { equal = first == second, firstDifferenceLine = first == second ? (int?)null : start + 1,
            left = Side(left, first, leftCount), right = Side(right, second, rightCount), normalization = "UTF-8 text with LF line endings" };
    }

    // Extract source initializers without evaluating RAPID. Ignore comments and preserve quoted
    // literals, including whitespace, comment markers, semicolons and ABB backslash escapes.
    public string? PersistentInitializer(string name)
    {
        var chars = Text.ToCharArray();
        var quoted = false;
        for (var i = 0; i < chars.Length; i++)
        {
            if (quoted && chars[i] == '\\') { i++; continue; }
            if (chars[i] == '"') { quoted = !quoted; continue; }
            if (!quoted && chars[i] == '!')
                while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
        }
        var source = new string(chars);
        var declaration = Regex.Match(source, @"^[\t ]*(?:(?:LOCAL|TASK)\s+)?PERS\s+[a-z_][a-z0-9_]*\s+" + Regex.Escape(name) +
            @"\b(?:\s*\{[^{};]*\})?\s*:=\s*", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!declaration.Success) return null;
        var start = declaration.Index + declaration.Length;
        quoted = false;
        for (var i = start; i < source.Length; i++)
        {
            if (quoted && source[i] == '\\') { i++; continue; }
            if (source[i] == '"') quoted = !quoted;
            if (!quoted && source[i] == ';') return source[start..i].Trim();
        }
        return null;
    }
}
