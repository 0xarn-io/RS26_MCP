using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace RobotStudioOffline.Core;

/// <summary>Local Visual SafeMove geometry interchange, as documented in 3HAC098577-001 §10.5.</summary>
public static class SafeMoveInspection
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly XNamespace Geometry = "urn:abb-robotics-simplified-safety-controller-configuration";
    private static readonly XNamespace Instance = XmlSchema.InstanceNamespace;
    private const string Source = "Visual SafeMove geometry interchange XML";

    public static object Read(string file, int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 200)
            throw Invalid("offset must be nonnegative and limit must be between 1 and 200.");
        file = InputPath(file);
        var (document, _) = Load(file);
        var root = document.Root!;
        var items = new List<object>();
        foreach (var module in root.Elements(Geometry + "DriveModuleConfiguration"))
        {
            var driveModuleId = (int)module.Attribute("driveModuleId")!;
            items.Add(new { driveModuleId, geometry = Describe(module, includeChildren: false) });
            items.AddRange(module.Elements().Select(child => new { driveModuleId, geometry = Describe(child) }));
        }
        var page = items.Skip(offset).Take(limit).ToArray();
        return new
        {
            source = Source, file, version = (string?)root.Attribute("version"),
            schemaChecked = true, activeConfigurationVerified = false, safetyValidationVerified = false,
            lengthUnit = "m", speedUnit = "m/s", zoneFrame = "controllerWorld", quaternionOrder = "q1(real),q2,q3,q4",
            total = items.Count, offset, nextOffset = offset + (long)page.Length < items.Count ? offset + page.Length : (int?)null,
            items = page
        };
    }

    private static object Describe(XElement element, bool includeChildren = true)
    {
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.Namespace == XNamespace.None)
            {
                var name = attribute.Name.LocalName;
                attributes.Add(name, name switch
                {
                    "id" or "driveModuleId" => (int)attribute,
                    _ when IsMeasurement(name) => (double)attribute,
                    "jointSupervisionOnly" => (bool)attribute,
                    _ => attribute.Value
                });
            }
            else if (attribute.Name == Instance + "type") attributes.Add("xsi:type", attribute.Value.Split(':')[^1]);
        }
        return new { kind = element.Name.LocalName, attributes, children = includeChildren ? element.Elements().Select(e => Describe(e)).ToArray() : [] };
    }

    public static object EditZoneCopy(string file, string outputName, int driveModuleId, int zoneId,
        double? bottomM, double? topM, double[][]? pointsM)
    {
        file = InputPath(file);
        if (string.IsNullOrWhiteSpace(outputName) || outputName.Length > 255 || outputName.IndexOfAny(['/', '\\', ':']) >= 0 ||
            !outputName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw Invalid("outputName must be a new sibling .xml filename.");
        var directory = Path.GetDirectoryName(file)!;
        var destination = LocalInputFile.CanonicalPath(Path.Combine(directory, outputName));
        if (driveModuleId is < 1 or > 4 || zoneId is < 1 or > 16 || (bottomM is null && topM is null && pointsM is null))
            throw Invalid("Select a driveModuleId (1–4) and zoneId (1–16) and supply bottomM, topM or pointsM.");
        if (File.Exists(destination) || Directory.Exists(destination) || StringComparer.OrdinalIgnoreCase.Equals(destination, file))
            throw Exists();

        var (document, schema) = Load(file);
        var modules = document.Root!.Elements(Geometry + "DriveModuleConfiguration")
            .Where(m => (int)m.Attribute("driveModuleId")! == driveModuleId).ToArray();
        if (modules.Length > 1) throw Invalid("The export has duplicate driveModuleId values for this selection.");
        var zones = modules.SelectMany(m => m.Elements(Geometry + "SafeZone"))
            .Where(z => (int)z.Attribute("id")! == zoneId).ToArray();
        if (zones.Length == 0) throw new BridgeException("safemove_zone_missing", "The selected drive module and zone were not found.", 404);
        if (zones.Length != 1) throw Invalid("The selected drive module has duplicate zone IDs.");
        var zone = zones[0];
        var bottom = bottomM ?? (double)zone.Attribute("bottom")!;
        var top = topM ?? (double)zone.Attribute("top")!;
        if (!double.IsFinite(bottom) || !double.IsFinite(top) || bottom < -30 || top > 30 || bottom >= top)
            throw Invalid("The resulting bottom and top must be finite, ordered heights in [-30,30] metres.");
        if (bottomM.HasValue) zone.SetAttributeValue("bottom", bottom);
        if (topM.HasValue) zone.SetAttributeValue("top", top);
        if (pointsM is not null)
        {
            SafeMovePolygon.Check(pointsM);
            zone.Elements(Geometry + "Point").Remove();
            zone.Add(pointsM.Select(p => new XElement(Geometry + "Point", new XAttribute("x", p[0]), new XAttribute("y", p[1]))));
        }
        CheckSchema(document, schema);
        SaveNewSibling(document, directory, destination);
        return new
        {
            source = Source, file, outputFile = destination, driveModuleId, zoneId, zone = Describe(zone),
            schemaChecked = true, appliedToController = false, activeConfigurationVerified = false, safetyValidationVerified = false
        };
    }

    private static string InputPath(string file)
    {
        var path = LocalInputFile.CanonicalPath(file);
        if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) throw Invalid("Select a SafeMove geometry .xml file.");
        LocalInputFile.RequireAvailable(path, "safemove");
        return path;
    }

    private static (XDocument, XmlSchemaSet) Load(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes) throw Invalid("SafeMove geometry exports are limited to 2 MiB.");
            var settings = ReaderSettings();
            // Bound hierarchy before materializing an untrusted document. The documented format needs fewer than ten levels.
            using (var reader = XmlReader.Create(stream, settings))
            {
                reader.MoveToContent();
                if (reader.LocalName != "SimplifiedSafetyConfiguration" || reader.NamespaceURI != Geometry.NamespaceName)
                    throw new BridgeException("safemove_format_unsupported", "Export a geometry configuration XML from Visual SafeMove.");
                do
                {
                    if (reader.Depth > 12) throw Invalid("The geometry XML nesting exceeds the supported format.");
                } while (reader.Read());
            }
            stream.Position = 0;
            using var input = XmlReader.Create(stream, settings);
            var document = XDocument.Load(input, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            if (document.DescendantNodes().OfType<XProcessingInstruction>().Any())
                throw Invalid("Geometry XML with processing instructions is unsupported.");
            var schema = InstalledSchema();
            CheckSchema(document, schema);
            return (document, schema);
        }
        catch (XmlException ex)
        {
            throw Invalid($"Invalid geometry XML at line {ex.LineNumber}, column {ex.LinePosition}.");
        }
        catch (UnauthorizedAccessException) { throw new BridgeException("safemove_file_unreadable", "Cannot read the local file.", 403); }
        catch (IOException) { throw new BridgeException("safemove_file_unreadable", "Cannot read the local file.", 409); }
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes
    };

    private static XmlSchemaSet InstalledSchema()
    {
        // RobotWare registers installed RobotControl packages here. Never resolve schemas or includes from the request XML.
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ABB", "RobotWare");
        if (Directory.Exists(directory))
        {
            var candidates = Directory.EnumerateDirectories(directory, "RobotControl_8.*")
                .Select(d => (directory: d, version: Version.TryParse(Path.GetFileName(d)["RobotControl_".Length..], out var v) ? v : null))
                .Where(d => d.version is not null).OrderByDescending(d => d.version);
            foreach (var candidate in candidates)
            {
                var schemaPath = Path.Combine(candidate.directory, "utility", "SafeMove2", "SimplifiedSafetyControllerSchema.xsd");
                if (!File.Exists(schemaPath)) continue;
                LocalInputFile.RequireAvailable(schemaPath, "safemove_schema");
                using var file = File.OpenRead(schemaPath);
                using var schemaReader = XmlReader.Create(file, ReaderSettings());
                var schemas = new XmlSchemaSet { XmlResolver = null };
                try
                {
                    schemas.Add(Geometry.NamespaceName, schemaReader);
                    schemas.Compile();
                }
                catch (XmlSchemaException) { throw new BridgeException("safemove_schema_unavailable", "The installed RobotWare geometry XSD could not be loaded.", 409); }
                return schemas;
            }
        }
        throw new BridgeException("safemove_schema_unavailable", "Install the RobotWare 8 RobotControl package containing utility/SafeMove2/SimplifiedSafetyControllerSchema.xsd.", 409);
    }

    private static void CheckSchema(XDocument document, XmlSchemaSet schema)
    {
        try { document.Validate(schema, null); }
        catch (XmlSchemaValidationException ex)
        {
            throw new BridgeException("safemove_schema_mismatch", $"The geometry XML does not match ABB's installed schema at line {ex.LineNumber}, column {ex.LinePosition}.");
        }
        // xs:double admits NaN. Check the full export, including geometry outside the selected page or edited zone.
        foreach (var attribute in document.Descendants().Attributes())
        {
            if (attribute.Name.Namespace == XNamespace.None && IsMeasurement(attribute.Name.LocalName) && !double.IsFinite((double)attribute))
                throw Invalid($"SafeMove geometry requires a finite {attribute.Name.LocalName} at line {((IXmlLineInfo)attribute).LineNumber}.");
        }
    }

    private static bool IsMeasurement(string name) => name is "x" or "y" or "z" or "q1" or "q2" or "q3" or "q4" or
        "radius" or "width" or "height" or "bottom" or "top" or "startSpeedOffset" or "maxSpeedManualMode";

    private static void SaveNewSibling(XDocument document, string directory, string destination)
    {
        var temporary = Path.Combine(directory, $".safemove-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                document.Save(stream, SaveOptions.DisableFormatting);
            }
            File.Move(temporary, destination);
            created = false;
        }
        catch (UnauthorizedAccessException) { throw new BridgeException("safemove_copy_denied", "Cannot create a sibling file in this directory.", 403); }
        catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { throw Exists(); }
        catch (IOException) { throw new BridgeException("safemove_copy_failed", "The new geometry file could not be saved.", 409); }
        finally { if (created) File.Delete(temporary); }
    }

    private static BridgeException Invalid(string message) => new("invalid_request", message);
    private static BridgeException Exists() => new("safemove_output_exists", "Choose a new output filename. Existing files are preserved.", 409);
}
