// Copyright (c) Microsoft Corporation. Licensed under the MIT License.
//
// wdxp-conformance: the W2 conformance suite. Three checks, runnable now with `dotnet run`:
//   1. the schema is structurally valid (reuses the generator's Validator);
//   2. Gate 3 totality — every command/event in the schema appears in EVERY generated facade
//      (the CLI command-graph + the docs reference), so a schema field-add can never be silently
//      dropped from a surface;
//   3. the golden traces conform to the schema (methods exist; message fields match the contract;
//      error codes are declared).
// Exit 0 = all green. This is the fast-gate oracle; the live-substrate replay against a real target
// is a later step that needs the W1 daemon.
using System.Text.Json;
using Wdxp.Gen;

string? schemaPath = FindUp("wdxp.v0.json");
if (schemaPath is null) { Console.Error.WriteLine("error: could not locate wdxp.v0.json"); return 2; }
string goldenDir = Path.Combine(Path.GetDirectoryName(schemaPath)!, "golden");

var protocol = SchemaLoader.Load(schemaPath);
var resolver = new RefResolver(protocol);
var checks = new List<(string Name, bool Pass, string Detail)>();

// ---- Check 1: schema structurally valid ----
var schemaErrors = Validator.Validate(protocol, resolver);
checks.Add(("schema-valid", schemaErrors.Count == 0,
    schemaErrors.Count == 0 ? $"{protocol.Domains.Count} domains, {protocol.Domains.Sum(d => d.Commands.Count)} commands"
                            : string.Join("; ", schemaErrors)));

// ---- Check 2: Gate 3 — both facades are total over the schema ----
checks.Add(Gate3(protocol));

// ---- Check 3: golden traces conform ----
var commands = protocol.Domains.SelectMany(d => d.Commands.Select(c => ($"{d.Name}.{c.Name}", c))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
var events = protocol.Domains.SelectMany(d => d.Events.Select(e => ($"{d.Name}.{e.Name}", e))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
var errorCodes = LoadErrorCodes(schemaPath);

if (!Directory.Exists(goldenDir))
    checks.Add(("golden-present", false, $"no golden dir at {goldenDir}"));
else
    foreach (var file in Directory.EnumerateFiles(goldenDir, "*.json").OrderBy(f => f))
        checks.Add(ConformTrace(file, commands, events, errorCodes));

// ---- Report ----
Console.WriteLine("WDXP conformance");
Console.WriteLine(new string('-', 60));
bool allPass = true;
foreach (var (name, pass, detail) in checks)
{
    allPass &= pass;
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-26} {detail}");
}
Console.WriteLine(new string('-', 60));
Console.WriteLine($"RESULT: {(allPass ? "PASS" : "FAIL")}  ({checks.Count(c => c.Pass)}/{checks.Count} checks)");
return allPass ? 0 : 1;

// -------------------------------------------------------------------------------------------------

static (string, bool, string) Gate3(Protocol p)
{
    var errs = new List<string>();
    var schemaCommands = p.Domains.SelectMany(d => d.Commands.Select(c => $"{d.Name}.{c.Name}")).ToHashSet(StringComparer.Ordinal);
    var schemaEvents = p.Domains.SelectMany(d => d.Events.Select(e => $"{d.Name}.{e.Name}")).ToHashSet(StringComparer.Ordinal);

    // Facade 1: the CLI command-graph (structured). Commands and events (as notifications) must both be total.
    using var cli = JsonDocument.Parse(JsonSerializer.Serialize(Emit.Cli(p)));
    var cliMethods = cli.RootElement.GetProperty("commands").EnumerateArray().Select(e => e.GetProperty("method").GetString()!).ToHashSet(StringComparer.Ordinal);
    var cliNotifs = cli.RootElement.GetProperty("notifications").EnumerateArray().Select(e => e.GetProperty("method").GetString()!).ToHashSet(StringComparer.Ordinal);

    // Facade 2: the human-readable docs reference (generated from the same model).
    var docs = Emit.Docs(p);

    foreach (var m in schemaCommands)
    {
        if (!cliMethods.Contains(m)) errs.Add($"command '{m}' missing from CLI facade");
        if (!docs.Contains($"`{m}`", StringComparison.Ordinal)) errs.Add($"command '{m}' missing from docs facade");
    }
    foreach (var e in schemaEvents)
    {
        if (!cliNotifs.Contains(e)) errs.Add($"event '{e}' missing from CLI facade notifications");
        if (!docs.Contains($"`{e}`", StringComparison.Ordinal)) errs.Add($"event '{e}' missing from docs facade");
    }

    if (cliMethods.Count != schemaCommands.Count) errs.Add($"CLI facade command count {cliMethods.Count} != schema {schemaCommands.Count}");
    if (cliNotifs.Count != schemaEvents.Count) errs.Add($"CLI facade notification count {cliNotifs.Count} != schema {schemaEvents.Count}");

    return ("gate3-facade-totality", errs.Count == 0,
        errs.Count == 0 ? $"{schemaCommands.Count} commands + {schemaEvents.Count} events present in every facade" : string.Join("; ", errs));
}

static (string, bool, string) ConformTrace(string file, IReadOnlyDictionary<string, Command> commands,
    IReadOnlyDictionary<string, EventDef> events, IReadOnlyDictionary<int, string> errorCodes)
{
    var errs = new List<string>();
    using var doc = JsonDocument.Parse(File.ReadAllText(file));
    var root = doc.RootElement;
    string scenario = root.TryGetProperty("scenario", out var s) ? s.GetString()! : Path.GetFileNameWithoutExtension(file);
    var idToMethod = new Dictionary<string, string>(StringComparer.Ordinal);
    int n = 0;

    foreach (var entry in root.GetProperty("messages").EnumerateArray())
    {
        n++;
        var msg = entry.GetProperty("msg");
        string where = $"{scenario} msg#{n}";

        if (msg.TryGetProperty("jsonrpc", out var v) && v.GetString() != "2.0")
            errs.Add($"{where}: jsonrpc must be '2.0'");

        bool hasResult = msg.TryGetProperty("result", out var result);
        bool hasError = msg.TryGetProperty("error", out var error);
        bool hasMethod = msg.TryGetProperty("method", out var methodEl);
        bool hasId = msg.TryGetProperty("id", out var idEl);
        string? id = hasId ? (idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.ToString()) : null;

        if (hasError)
        {
            int code = error.GetProperty("code").GetInt32();
            if (!errorCodes.TryGetValue(code, out var declaredName))
                errs.Add($"{where}: undeclared error code {code}");
            else if (error.TryGetProperty("name", out var nm) && nm.GetString() != declaredName)
                errs.Add($"{where}: error name '{nm.GetString()}' != declared '{declaredName}' for code {code}");
        }
        else if (hasResult)
        {
            if (id is null || !idToMethod.TryGetValue(id, out var method))
                errs.Add($"{where}: result for id '{id}' has no prior request");
            else if (commands.TryGetValue(method, out var cmd))
                ValidateFields(result, cmd.Returns, $"{where} result[{method}]", errs);
        }
        else if (hasMethod && hasId) // request
        {
            string method = methodEl.GetString()!;
            id ??= "";
            idToMethod[id] = method;
            if (!commands.TryGetValue(method, out var cmd))
                errs.Add($"{where}: unknown command '{method}'");
            else
            {
                var pars = msg.TryGetProperty("params", out var pe) && pe.ValueKind == JsonValueKind.Object ? pe : default;
                ValidateFields(pars, cmd.Parameters, $"{where} params[{method}]", errs);
            }
        }
        else if (hasMethod) // notification / event
        {
            string method = methodEl.GetString()!;
            if (!events.TryGetValue(method, out var evt))
                errs.Add($"{where}: unknown event '{method}'");
            else
            {
                var pars = msg.TryGetProperty("params", out var pe) && pe.ValueKind == JsonValueKind.Object ? pe : default;
                ValidateFields(pars, evt.Parameters, $"{where} event[{method}]", errs);
            }
        }
        else errs.Add($"{where}: unclassifiable message");
    }

    return ($"golden:{Path.GetFileName(file)}", errs.Count == 0, errs.Count == 0 ? $"{n} messages conform" : string.Join("; ", errs));
}

// Validate the TOP-LEVEL fields of a message object against the declared contract fields:
// every provided field is declared, and every required (non-optional) field is present.
static void ValidateFields(JsonElement obj, IReadOnlyList<Field> declared, string where, List<string> errs)
{
    var byName = declared.ToDictionary(f => f.Name, StringComparer.Ordinal);
    if (obj.ValueKind == JsonValueKind.Object)
        foreach (var prop in obj.EnumerateObject())
            if (!byName.ContainsKey(prop.Name))
                errs.Add($"{where}: unknown field '{prop.Name}'");

    foreach (var f in declared)
        if (!f.Optional && !(obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(f.Name, out _)))
            errs.Add($"{where}: missing required field '{f.Name}'");
}

static Dictionary<int, string> LoadErrorCodes(string schemaPath)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
    var map = new Dictionary<int, string>();
    foreach (var e in doc.RootElement.GetProperty("errorCodes").EnumerateArray())
        map[e.GetProperty("code").GetInt32()] = e.GetProperty("name").GetString()!;
    return map;
}

static string? FindUp(string fileName)
{
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            foreach (var c in new[] { Path.Combine(dir.FullName, "protocol", fileName), Path.Combine(dir.FullName, fileName) })
                if (File.Exists(c)) return c;
            dir = dir.Parent;
        }
    }
    return null;
}
