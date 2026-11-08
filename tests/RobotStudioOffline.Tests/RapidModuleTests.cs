using System.Text;
using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    static partial void AddRapidModuleTests(List<(string Name, Action Run)> tests)
    {
        RapidModuleRequest Parse(string op, string json)
        {
            using var doc = JsonDocument.Parse(json);
            return RapidModuleRequests.Parse(op, doc.RootElement);
        }

        tests.Add(("RAPID diagnostics and real writes only reuse the shared owner grant", () =>
        {
            foreach (var external in new[] { false, true })
            foreach (var held in new[] { false, true })
            {
                Equal(RapidAccessDecision.Refuse, RapidWriteAccessPolicy.Decide(false, held, external, false));
                Equal(RapidAccessDecision.Reuse, RapidWriteAccessPolicy.Decide(true, held, external, false));
            }
        }));
        tests.Add(("a virtual module load requests only an enabled and unheld write grant", () =>
        {
            Equal(RapidAccessDecision.Request, RapidWriteAccessPolicy.Decide(false, false, true, true));
            Equal(RapidAccessDecision.Reuse, RapidWriteAccessPolicy.Decide(true, true, true, true));
            Equal(RapidAccessDecision.Refuse, RapidWriteAccessPolicy.Decide(false, true, true, true));
            Equal(RapidAccessDecision.Refuse, RapidWriteAccessPolicy.Decide(false, false, false, true));
        }));
        tests.Add(("RAPID module requests select an exact task and canonical local sources", () =>
        {
            var load = Parse("rapid_load", """{"robot":"TestRobot","task":"T_TEST","files":["c:/Work/Fixtures.mod","D:/RAPID/Logic.sysx"]}""");
            Equal("T_TEST", load.TaskName!);
            Equal(@"C:\Work\Fixtures.mod", load.Files[0]);
            Equal(2, load.Files.Length);
            True(!load.IncludeReal);
            var check = Parse("rapid_check", "{}");
            True(check.TaskName is null && check.Files.Length == 0 && !check.IncludeReal);
            var files = Parse("rapid_check", """{"files":["C:/Work/Fixtures.mod"]}""");
            Equal(@"C:\Work\Fixtures.mod", files.Files.Single());
            Reject("rapid_check", """{"files":[]}""");
        }));
        tests.Add(("RAPID hardware operations require an explicit selection and reject caller approval flags", () =>
        {
            var sync = Parse("rapid_sync_real", """{"task":"T_ROB1","files":["C:/Work/Fixtures.modx"]}""");
            True(sync.IncludeReal);
            Reject("rapid_sync_real", """{"files":["C:/Work/Fixtures.modx"]}""");
            Reject("rapid_sync_real", """{"task":"T_ROB1","files":["C:/Work/Fixtures.modx"],"approved":true}""");
            Reject("rapid_load", """{"files":["C:/Work/Fixtures.modx"],"includeReal":true}""");
            Reject("rapid_check", """{"controller":"192.0.2.42"}""", "real_controller_permission_required");
        }));
        tests.Add(("RAPID diff supports explicit PERS and limits selectors to exact module/name pairs", () =>
        {
            var diff = Parse("rapid_diff", """{"files":["C:/Work/Fixtures.mod"],"pers":["Fixtures/Count","Fixtures/Cmd"],"includeReal":true}""");
            Equal("Fixtures/Count", diff.Pers[0]);
            True(diff.IncludeReal);
            True(!Parse("rapid_diff", """{"files":["C:/Work/Fixtures.mod"]}""").IncludeReal);
            foreach (var selector in new[] { "Count", "Fixtures/", "/Count", "Fixtures/Count/Field", "Fixtures/Count{1}" })
                Reject("rapid_diff", JsonSerializer.Serialize(new { files = new[] { "C:/Work/Fixtures.mod" }, pers = new[] { selector } }));
            Reject("rapid_diff", """{"files":["C:/Work/Fixtures.mod"],"pers":["Fixtures/Count","FIXTURES/count"]}""");
        }));
        tests.Add(("RAPID input rejects redirected paths, non-module files and duplicate selection before disk I/O", () =>
        {
            foreach (var file in new[] { "Fixtures.mod", @"\\server\share\Fixtures.mod", @"C:\Work\..\Fixtures.mod", @"C:\Work\NUL.mod", @"C:\Work\Fixtures.txt" })
                Reject("rapid_load", JsonSerializer.Serialize(new { files = new[] { file } }));
            Reject("rapid_load", """{"files":["C:/Work/fixtures.MOD","c:/work/Fixtures.mod"]}""");
            Reject("rapid_load", """{"files":[]}""");
            Reject("rapid_load", JsonSerializer.Serialize(new { files = Enumerable.Range(0, 21).Select(i => $"C:/Work/Fixtures{i}.mod").ToArray() }));
        }));
        tests.Add(("RAPID snapshots read one module and preserve the exact selected file bytes", () =>
        {
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("MODULE Fixtures(SYSMODULE)\r\nENDMODULE\r\n")).ToArray();
            var source = RapidSource.FromBytes("Fixtures.sysx", bytes);
            Equal("Fixtures", source.Module);
            Equal("MODULE Fixtures(SYSMODULE)\nENDMODULE\n", source.Text);
            Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), source.Sha256);
            var legacy = Encoding.Latin1.GetBytes("MODULE Fixtures\n! Rhône\nENDMODULE");
            True(RapidSource.FromBytes("Fixtures.mod", legacy).Text.Contains("Rhône"));
            void InvalidSource(string file, byte[] content, string code)
            {
                try { RapidSource.FromBytes(file, content); }
                catch (BridgeException error) { Equal(code, error.Code); return; }
                throw new InvalidOperationException("Invalid RAPID source was accepted.");
            }
            InvalidSource("Fixtures.modx", legacy, "rapid_encoding");
            InvalidSource("Fixtures.mod", "MODULE Fixtures\nENDMODULE\nMODULE Another\nENDMODULE"u8.ToArray(), "rapid_header");
            InvalidSource("Fixtures.mod", "! MODULE Fixtures\nENDMODULE"u8.ToArray(), "rapid_header");
        }));
        tests.Add(("RAPID source PERS initializers preserve quoted literals and multiline records", () =>
        {
            var source = RapidSource.FromBytes("Fixtures.modx", """
                MODULE Fixtures
                ! PERS num count := 999;
                TASK PERS num count := 17; ! inline comment
                LOCAL PERS string message := "! keep ; quoted";
                PERS num offsets{2} :=
                    [1,
                    2];
                VAR num missing := 2;
                ENDMODULE
                """u8.ToArray());
            Equal("17", source.PersistentInitializer("Count")!);
            Equal("\"! keep ; quoted\"", source.PersistentInitializer("message")!);
            True(source.PersistentInitializer("offsets")!.Replace(" ", "").Replace("\n", "") == "[1,2]");
            True(source.PersistentInitializer("missing") is null);
        }));
        tests.Add(("RAPID source diff normalizes line endings and reports bounded one-based changed spans", () =>
        {
            var same = JsonSerializer.SerializeToElement(RapidSource.TextDifference("A\r\nB\r\n", "A\nB\n"));
            True(same.GetProperty("equal").GetBoolean());
            True(same.GetProperty("firstDifferenceLine").ValueKind == JsonValueKind.Null);
            var diff = JsonSerializer.SerializeToElement(RapidSource.TextDifference("A\nOld\nZ", "A\nNew\nExtra\nZ"));
            Equal(2, diff.GetProperty("firstDifferenceLine").GetInt32());
            Equal(1, diff.GetProperty("left").GetProperty("changedSpanLines").GetInt32());
            Equal(2, diff.GetProperty("right").GetProperty("changedSpanLines").GetInt32());
            Equal("New", diff.GetProperty("right").GetProperty("excerpt")[0].GetProperty("text").GetString()!);
        }));
        tests.Add(("unavailable editor parser yields an unknown syntax verdict and preserves file identity", () =>
        {
            var input = RapidSource.FromBytes("Fixture.modx", "MODULE Fixture\nENDMODULE"u8.ToArray());
            var result = RapidSourceDiagnostics.Inspect(input, typeof(string).Assembly);
            True(!result.Available && result.Correct is null && result.UnavailableReason is not null);
            Equal(input.Sha256, result.Sha256);
        }));
        var rsDirectory = Environment.GetEnvironmentVariable("RS_OFFLINE_TEST_SDK") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ABB", "RobotStudio 2026");
        var service = Path.Combine(rsDirectory, "Bin", "RobotStudio.Services.RapidLanguage.dll");
        if (File.Exists(service))
            tests.Add(("installed ABB editor parser locates malformed submitted RAPID in one-based UTF-16 coordinates", () =>
            {
                var abb = System.Reflection.Assembly.LoadFrom(service);
                const string invalid = "MODULE Fixture\r\n    ! 🧰é\r\n    PROC Main()\r\n        VAR num value;\r\n        value := ;\r\n    ENDPROC\r\nENDMODULE";
                var src = RapidSource.FromBytes("Fixture.modx", Encoding.UTF8.GetBytes(invalid));
                var bad = RapidSourceDiagnostics.Inspect(src, abb);
                True(bad.Available && bad.Correct == false);
                var expression = bad.Errors.Single(e => e.Code == "ExpectedExpressionTerm");
                Equal(5, expression.Line!.Value);
                Equal(18, expression.Column!.Value);
                Equal(19, expression.EndColumn!.Value);
                Equal(1, expression.Utf16Length!.Value);
                var valid = RapidSource.FromBytes("Fixture.modx", Encoding.UTF8.GetBytes(invalid.Replace(":= ;", ":= 1;")));
                var good = RapidSourceDiagnostics.Inspect(valid, abb);
                True(good.Available && good.Correct == true && good.ErrorCount == 0);
                var unidentified = RapidSource.FromBytes("Bad.modx", "PROC main()\nENDPROC"u8.ToArray(), allowUnidentifiedModule: true);
                var header = RapidSourceDiagnostics.Inspect(unidentified, abb);
                True(header.Correct == false && header.Errors.Any(e => e.Code == "ModuleHeader"));
            }));
    }
}
