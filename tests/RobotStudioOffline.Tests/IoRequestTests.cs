using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static void AddIoRequestTests(List<(string Name, Action Run)> tests)
    {
        tests.Add(("I/O reads default to bounded paging and preserve explicit selection", () =>
        {
            var page = (IoReadRequest)ParseIo("io_read", "{}");
            True(page.Names is null);
            Equal(0, page.Offset);
            Equal(100, page.Limit);
            var selected = (IoReadRequest)ParseIo("io_read", "{\"robot\":\"TestRobot\",\"names\":[\"diReady\",\"doTestValve\"]}");
            Equal("TestRobot", selected.Robot!);
            True(selected.Names!.SequenceEqual(new[] { "diReady", "doTestValve" }));
            var lastPage = (IoReadRequest)ParseIo("io_read", "{\"offset\":2147483647,\"limit\":500}");
            Equal(int.MaxValue, lastPage.Offset);
            Equal(500, lastPage.Limit);
        }));
        tests.Add(("named I/O reads reject paging even when values equal defaults", () =>
        {
            Reject("io_read", "{\"names\":[\"diReady\"],\"offset\":0}");
            Reject("io_read", "{\"names\":[\"diReady\"],\"limit\":100}");
            foreach (var json in new[] { "{\"offset\":-1}", "{\"offset\":2147483648}", "{\"offset\":1.5}",
                         "{\"limit\":0}", "{\"limit\":501}", "{\"limit\":null}" }) Reject("io_read", json);
        }));
        tests.Add(("I/O selected names enforce bounds uniqueness and string constraints", () =>
        {
            foreach (var names in new[] { "[]", "null", "[\"same\",\"same\"]", "[null]", "[1]", "[\" \" ]", "[\"bad\\nname\"]",
                         JsonSerializer.Serialize(new[] { new string('a', 257) }),
                         JsonSerializer.Serialize(Enumerable.Range(0, 101).Select(index => "signal" + index)) })
                Reject("io_read", "{\"names\":" + names + "}");
            var maximum = (IoReadRequest)ParseIo("io_read", "{\"names\":" +
                JsonSerializer.Serialize(Enumerable.Range(0, 100).Select(index => "signal" + index)) + "}");
            Equal(100, maximum.Names!.Length);
        }));
        tests.Add(("I/O writes preserve full group integers and explicit input simulation", () =>
        {
            var write = (IoWriteRequest)ParseIo("io_write", "{\"name\":\"goWord\",\"value\":16777217}");
            Near(16777217, write.Value);
            True(!write.SimulateInput);
            var input = (IoWriteRequest)ParseIo("io_write", "{\"name\":\"giWord\",\"value\":4294967295,\"simulateInput\":true}");
            Near(uint.MaxValue, input.Value);
            True(input.SimulateInput);
            foreach (var json in new[] { "{\"name\":\"doTestValve\"}", "{\"value\":1}", "{\"name\":null,\"value\":1}",
                         "{\"name\":\"doTestValve\",\"value\":\"1\"}", "{\"name\":\"aoValue\",\"value\":1e400}",
                         "{\"name\":\"doTestValve\",\"value\":1,\"simulateInput\":1}" }) Reject("io_write", json);
        }));
        tests.Add(("I/O pulses enforce integer duration bounds and explicit booleans", () =>
        {
            var pulse = (IoPulseRequest)ParseIo("io_pulse", "{\"name\":\"doTestValve\"}");
            Equal(100, pulse.DurationMs);
            True(!pulse.SimulateInput);
            foreach (var duration in new[] { 1, 5000 })
                Equal(duration, ((IoPulseRequest)ParseIo("io_pulse", "{\"name\":\"diStart\",\"simulateInput\":true,\"durationMs\":" + duration + "}")).DurationMs);
            foreach (var duration in new[] { "0", "5001", "-1", "1.5", "null", "\"100\"" })
                Reject("io_pulse", "{\"name\":\"doTestValve\",\"durationMs\":" + duration + "}");
            Reject("io_pulse", "{\"name\":\"doTestValve\",\"simulateInput\":null}");
        }));
        tests.Add(("I/O rejects duplicates unknown force flags and controller destinations", () =>
        {
            Reject("io_read", "{\"offset\":0,\"offset\":1}");
            Reject("io_write", "{\"name\":\"doTestValve\",\"name\":\"doOther\",\"value\":1}");
            Reject("io_write", "{\"name\":\"doTestValve\",\"value\":1,\"force\":true}");
            Reject("io_pulse", "{\"name\":\"doTestValve\",\"force\":true}");
            Reject("io_read", "{\"controller\":\"192.0.2.42\"}", "real_controller_permission_required");
            Reject("io_write", "{\"name\":\"doTestValve\",\"value\":1,\"nested\":{\"address\":\"192.0.2.42\"}}", "real_controller_permission_required");
            Reject("io_read", "{\"names\":[\"192.0.2.42\"]}", "real_controller_permission_required");
            Reject("io_read", "[]");
            Equal("unknown_operation", Throws<BridgeException>(() => ParseIo("io_force", "{}")).Code);
        }));
        tests.Add(("I/O digital values are exactly zero or one within metadata bounds", () =>
        {
            IoValueValidation.Validate("digital", 0, 0, 1);
            IoValueValidation.Validate("digital", 1, 0, 1);
            foreach (var value in new[] { -1.0, 0.5, 1.0000001, 2, double.NaN, double.PositiveInfinity })
                Equal("invalid_io_value", Throws<BridgeException>(() => IoValueValidation.Validate("digital", value, 0, 1)).Code);
            Throws<BridgeException>(() => IoValueValidation.Validate("digital", 1, 0, 0));
        }));
        tests.Add(("I/O groups retain integer precision above float range through uint maximum", () =>
        {
            foreach (var value in new uint[] { 0, 1, 16777217, 2147483648, uint.MaxValue })
            {
                IoValueValidation.Validate("group", value, 0, uint.MaxValue);
                Equal(value, IoValueValidation.ToGroupValue(value));
            }
            foreach (var value in new[] { -1.0, 0.5, 4294967296, double.NaN, double.NegativeInfinity })
                Equal("invalid_io_value", Throws<BridgeException>(() => IoValueValidation.ToGroupValue(value)).Code);
            Throws<BridgeException>(() => IoValueValidation.Validate("group", 256, 0, 255));
        }));
        tests.Add(("I/O analog values check finite float representation and configured range", () =>
        {
            IoValueValidation.Validate("analog", 1.25, -10, 10);
            Equal(1.25f, IoValueValidation.ToAnalogValue(1.25));
            IoValueValidation.Validate("analog", float.MaxValue, -float.MaxValue, float.MaxValue);
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.MaxValue, -double.MaxValue })
                Equal("invalid_io_value", Throws<BridgeException>(() => IoValueValidation.ToAnalogValue(value)).Code);
            Throws<BridgeException>(() => IoValueValidation.Validate("analog", 10.01, -10, 10));
            // A valid double can round to a float outside a tight configured bound.
            Throws<BridgeException>(() => IoValueValidation.Validate("analog", 0.1, 0, 0.1));
        }));
        tests.Add(("I/O refuses unavailable or inconsistent metadata and unsupported kinds", () =>
        {
            foreach (var limits in new[] { (double.NaN, 1.0), (0.0, double.PositiveInfinity), (2.0, 1.0) })
                Equal("invalid_signal_metadata", Throws<BridgeException>(() => IoValueValidation.Validate("analog", 0, limits.Item1, limits.Item2)).Code);
            Equal("unsupported_signal_type", Throws<BridgeException>(() => IoValueValidation.Validate("unknown", 0, 0, 1)).Code);
        }));
    }

    private static IoRequest ParseIo(string operation, string json)
    {
        using var document = JsonDocument.Parse(json);
        return IoRequests.Parse(operation, document.RootElement);
    }
}
