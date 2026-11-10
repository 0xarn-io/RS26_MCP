using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static void AddEngineeringTests(List<(string Name, Action Run)> tests)
    {
        tests.Add(("configuration browsing preserves selectors and limits on both routes", () =>
        {
            var all = (ConfigurationRequest)RequestParser.ParseJson("configuration", "{}").Engineering!;
            True(all.Domain is null && all.Type is null && all.Action == "read" && all.Limit == 50);
            var io = (IoProjectRequest)RequestParser.ParseJson("io_project", """{"type":"EIO_SIGNAL","offset":12,"limit":200}""").Engineering!;
            True(io.Domain == "EIO" && io.Type == "EIO_SIGNAL" && io.Offset == 12 && io.Limit == 200);
            Reject("io_project", """{"domain":"MOC"}""");
            Reject("configuration", """{"type":"A"}""");
            Reject("io_project", """{"instance":"a"}""");
            Reject("configuration", """{"limit":201}""");
        }));
        tests.Add(("parameter changes require exact selectors and a typed expected value", () =>
        {
            const string selection = """{"action":"set","type":"EIO_SIGNAL","instance":"diTest","attribute":"Label","value":"example" """;
            var set = (IoProjectRequest)RequestParser.ParseJson("io_project", selection + ""","expected":""}""").Engineering!;
            Equal("example", set.Value!.Value.GetString()!);
            Equal("", set.Expected!.Value.GetString()!);
            foreach (var suffix in new[] { "}", ""","expected":null}""", ""","expected":{}}""",
                ""","expected":1e999}""", ""","expected":false,"limit":1}""" })
                Reject("io_project", selection + suffix);
            Reject("io_project", """{"action":"read","value":0}""");
            Reject("configuration", """{"action":"set","value":1,"expected":0,"attribute":"mass"}""");
        }));
        tests.Add(("SDK parameter edits retain integer precision and exclude bindings or safety topics", () =>
        {
            True(ConfigurationValues.CanEdit("MOC", "ARM_LOAD", "mass", TypeCode.Double));
            True(ConfigurationValues.CanEdit("EIO", "EIO_SIGNAL", "Label", TypeCode.String));
            True(!ConfigurationValues.CanEdit("EIO", "EIO_SIGNAL", "DeviceMap", TypeCode.String));
            True(!ConfigurationValues.CanEdit("SIO", "ARM_LOAD", "mass", TypeCode.Double));
            Equal(ulong.MaxValue, (ulong)ConfigurationValues.Scalar(JsonSerializer.SerializeToElement(ulong.MaxValue), TypeCode.UInt64));
            Throws<BridgeException>(() => ConfigurationValues.Scalar(JsonSerializer.SerializeToElement(1.5), TypeCode.Int32));
            Throws<BridgeException>(() => ConfigurationValues.Scalar(JsonSerializer.SerializeToElement("1"), TypeCode.Int32));
        }));
        tests.Add(("simulated signal defaults and widths require confirmed empty device and map", () =>
        {
            True(ConfigurationValues.CanEditSignalSetting("Default", TypeCode.Single, "DI", "", ""));
            True(ConfigurationValues.CanEditSignalSetting("Size", TypeCode.Int32, "GO", "", ""));
            True(!ConfigurationValues.CanEditSignalSetting("Size", TypeCode.Int32, "DI", "", ""));
            foreach (var binding in new[] { (null, ""), ("", (string?)null), ("fieldbus", ""), ("", "0") })
                True(!ConfigurationValues.CanEditSignalSetting("Default", TypeCode.Single, "DO", binding.Item1, binding.Item2));
            ConfigurationValues.ValidateSignalSetting("Default", JsonSerializer.SerializeToElement(0));
            ConfigurationValues.ValidateSignalSetting("Size", JsonSerializer.SerializeToElement(32));
            foreach (var (attr, value) in new[] { ("Default", 2), ("Default", 1e-30), ("Size", 33), ("Size", 0), ("Size", 1.5) })
                Throws<BridgeException>(() => ConfigurationValues.ValidateSignalSetting(attr, JsonSerializer.SerializeToElement(value)));
        }));
        tests.Add(("simulation setup discriminates read sections and preserves false and zero edits", () =>
        {
            SimulationSetupRequest Parse(string json) => (SimulationSetupRequest)RequestParser.ParseJson("simulation_setup", json).Engineering!;
            Equal("overview", Parse("{}").Section);
            Equal("draft", Parse("""{"section":"participants","configuration":"draft"}""").Configuration!);
            Equal("eligibleParticipants", Parse("""{"section":"eligibleParticipants","configuration":"draft","limit":2}""").Section);
            var world = Parse("""{"action":"set_world","floorEnabled":false,"floorHeightMm":0,"gravityMps2":-9.81}""");
            True(world.FloorEnabled == false && world.FloorHeightMm == 0 && world.GravityMps2 == -9.81);
            Equal("Dynamic", Parse("""{"action":"set_part","part":"box","motionControl":"Dynamic"}""").MotionControl!);
            var conveyor = Parse("""{"action":"set_part","part":"belt","surfaceSpeedMps":-0.5,"surfaceVelocityEnabled":false,"restitution":0}""");
            True(conveyor.SurfaceSpeedMps == -0.5 && conveyor.SurfaceVelocityEnabled == false && conveyor.Restitution == 0);
            True(Parse("""{"action":"set_part","part":"belt","surfaceDirection":[0,-1,0]}""").SurfaceDirection == new Vector3D(0,-1,0));
            Reject("simulation_setup", """{"action":"set_part","part":"belt","surfaceDirection":[0,0,0]}""");
            True(Parse("""{"action":"set_part","part":"belt","detectableBySensors":false}""").DetectableBySensors == false);
            Equal("VirtualTimeSlice", Parse("""{"action":"set_configuration","virtualTimeMode":"VirtualTimeSlice"}""").VirtualTimeMode!);
            True(Parse("""{"action":"set_participant","participant":"feeder","included":false}""").Included == false);
            Reject("simulation_setup", """{"action":"set_participant","participant":"feeder"}""");
            foreach (var bad in new[] { """{"limit":3}""", """{"section":"parts","configuration":"unused"}""",
                """{"action":"set_part","motionControl":"Static"}""", """{"action":"set_world"}""",
                """{"action":"set_part","part":"box","motionControl":"mobile"}""", """{"action":"set_world","gravityMps2":1e999}""",
                """{"action":"set_world","floorEnabled":1}""", """{"action":"set_part","part":"a","densityKgPerM3":0}""",
                """{"action":"set_part","part":"a","restitution":1.01}""", """{"action":"set_configuration","initialState":"s","floorEnabled":true}""" })
                Reject("simulation_setup", bad);
        }));
        tests.Add(("SafeMove transport accepts only local XML and bounded, explicitly selected zone edits", () =>
        {
            const string file = """{"file":"C:\\example\\geometry.xml" """;
            var read = (SafeMoveRequest)RequestParser.ParseJson("safemove", file + "}").Engineering!;
            True(read.Edit == null && read.File == @"C:\example\geometry.xml");
            const string edit = ""","action":"edit_zone_copy","outputName":"preview.xml","driveModuleId":1,"zoneId":3""";
            var changed = (SafeMoveRequest)RequestParser.ParseJson("safemove", file + edit + ""","topM":1.25}""").Engineering!;
            True(changed.Edit?.TopM == 1.25 && changed.Edit.BottomM == null);
            Reject("safemove", """{"file":"relative.xml"}""");
            Reject("safemove", """{"file":"C:\\example\\geometry.cfg"}""");
            Reject("safemove", file + edit + "}");
            Reject("safemove", file + edit + ""","pointsM":[[0,0]]}""");
            Reject("safemove", file + edit + ""","topM":1,"bottomM":2}""");
            Reject("safemove", file + edit.Replace("preview.xml", "../preview.xml") + ""","bottomM":0}""");
        }));
        tests.Add(("engineering tools reject remote controller selectors before any SDK or file access", () =>
        {
            foreach (var operation in new[] { "configuration", "io_project", "simulation_setup", "safemove" })
                Reject(operation, """{"controllerAddress":"192.0.2.10"}""", "real_controller_permission_required");
        }));
    }
}
