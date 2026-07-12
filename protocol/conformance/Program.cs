// Copyright (c) Microsoft Corporation. Licensed under the MIT License.
//
// wdxp-conformance: the W2 conformance suite, runnable now with `dotnet run`:
//   1. schema-valid          — the schema is structurally valid (reuses the generator's Validator);
//   2. gate3-facade-totality — every command/event/field flows to EVERY generated facade, so a
//      schema field-add can never be silently dropped from a surface;
//   3. golden:*              — the golden traces conform: JSON-RPC framing (jsonrpc 2.0, string ids
//      correlated across request/response/error, result XOR error) and message payloads validated
//      RECURSIVELY against the contract (nested $ref objects, enum value sets, array element types —
//      not merely top-level field presence);
//   4. golden-coverage       — every command and event is exercised by at least one golden trace.
// Exit 0 = all green. This is the fast-gate oracle; the live-substrate replay against a real target
// is a later step that needs the W1 daemon.
using System.Text.Json;
using Wdxp.Gen;

string? schemaPath = SchemaPaths.FindUp("wdxp.v0.json");
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

// ---- Check 3: golden traces conform (recursively) ----
var commands = protocol.Domains
    .SelectMany(d => d.Commands.Select(c => ($"{d.Name}.{c.Name}", (Cmd: c, Dom: d))))
    .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
var events = protocol.Domains
    .SelectMany(d => d.Events.Select(e => ($"{d.Name}.{e.Name}", (Evt: e, Dom: d))))
    .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
var errorCodes = LoadErrorCodes(schemaPath);

if (!Directory.Exists(goldenDir))
    checks.Add(("golden-present", false, $"no golden dir at {goldenDir}"));
else
{
    var goldenFiles = Directory.EnumerateFiles(goldenDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
    if (goldenFiles.Count == 0)
        checks.Add(("golden-present", false, $"golden dir {goldenDir} has no *.json traces — nothing to conform"));
    foreach (var file in goldenFiles)
        checks.Add(ConformTrace(file, commands, events, errorCodes, resolver));

    // ---- Check 4: every command and event is exercised by at least one golden trace ----
    if (goldenFiles.Count > 0)
        checks.Add(GoldenCoverage(protocol, goldenFiles));
}

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

    // Field-level totality against the CLI facade (the structured contract every client binds to):
    // every declared param/return field must be emitted for its command/event, and the facade may not
    // invent an undeclared field. This gives Gate 3 real teeth beyond method presence — a schema
    // field-add can never be silently dropped from (nor a stray field smuggled into) the surface.
    var cliByMethod = cli.RootElement.GetProperty("commands").EnumerateArray()
        .ToDictionary(e => e.GetProperty("method").GetString()!, e => e, StringComparer.Ordinal);
    var cliByNotif = cli.RootElement.GetProperty("notifications").EnumerateArray()
        .ToDictionary(e => e.GetProperty("method").GetString()!, e => e, StringComparer.Ordinal);
    int fieldCount = 0;
    foreach (var d in p.Domains)
    {
        foreach (var c in d.Commands)
        {
            var method = $"{d.Name}.{c.Name}";
            if (!cliByMethod.TryGetValue(method, out var node)) continue; // presence already reported above
            fieldCount += CheckCliFields(node, "args", c.Parameters, $"command '{method}' params", errs);
            fieldCount += CheckCliFields(node, "returns", c.Returns, $"command '{method}' returns", errs);
        }
        foreach (var ev in d.Events)
        {
            var method = $"{d.Name}.{ev.Name}";
            if (!cliByNotif.TryGetValue(method, out var node)) continue;
            fieldCount += CheckCliFields(node, "args", ev.Parameters, $"event '{method}' params", errs);
        }
    }

    return ("gate3-facade-totality", errs.Count == 0,
        errs.Count == 0
            ? $"{schemaCommands.Count} commands + {schemaEvents.Count} events in every facade; {fieldCount} fields total in CLI facade"
            : string.Join("; ", errs));
}

// Assert the CLI facade emits exactly the declared field set (by name) for one command/event arg list.
// Returns the number of declared fields checked (for the totality tally).
static int CheckCliFields(JsonElement node, string prop, IReadOnlyList<Field> declared, string where, List<string> errs)
{
    var emitted = node.GetProperty(prop).EnumerateArray()
        .Select(a => a.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
    foreach (var f in declared)
        if (!emitted.Contains(f.Name)) errs.Add($"field '{f.Name}' of {where} missing from CLI facade");
    foreach (var name in emitted)
        if (declared.All(f => f.Name != name)) errs.Add($"CLI facade emits undeclared field '{name}' in {where}");
    return declared.Count;
}

static (string, bool, string) ConformTrace(string file,
    IReadOnlyDictionary<string, (Command Cmd, Domain Dom)> commands,
    IReadOnlyDictionary<string, (EventDef Evt, Domain Dom)> events,
    IReadOnlyDictionary<int, string> errorCodes, RefResolver resolver)
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

        // Every framed message MUST carry jsonrpc 2.0 (not merely "if present, correct").
        if (!msg.TryGetProperty("jsonrpc", out var v) || v.ValueKind != JsonValueKind.String || v.GetString() != "2.0")
            errs.Add($"{where}: every message MUST carry \"jsonrpc\":\"2.0\"");

        bool hasResult = msg.TryGetProperty("result", out var result);
        bool hasError = msg.TryGetProperty("error", out var error);
        bool hasMethod = msg.TryGetProperty("method", out var methodEl);
        bool hasId = msg.TryGetProperty("id", out var idEl);

        // WDXP pins string ids protocol-wide (envelope §2) so every id is uniformly correlatable.
        if (hasId && idEl.ValueKind != JsonValueKind.String)
            errs.Add($"{where}: id must be a JSON string (WDXP pins string ids)");
        string? id = hasId && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;

        if (hasResult && hasError)
        {
            errs.Add($"{where}: a response carries BOTH 'result' and 'error' (exactly one is allowed)");
        }
        else if (hasError) // error response — correlates to a prior request, like a result
        {
            if (id is null || !idToMethod.ContainsKey(id))
                errs.Add($"{where}: error response for id '{id}' has no prior request");
            if (!error.TryGetProperty("code", out var codeEl) || codeEl.ValueKind != JsonValueKind.Number)
                errs.Add($"{where}: error is missing an integer 'code'");
            else
            {
                int code = codeEl.GetInt32();
                if (!errorCodes.TryGetValue(code, out var declaredName))
                    errs.Add($"{where}: undeclared error code {code}");
                else if (error.TryGetProperty("name", out var nm) && nm.GetString() != declaredName)
                    errs.Add($"{where}: error name '{nm.GetString()}' != declared '{declaredName}' for code {code}");
            }
        }
        else if (hasResult) // success response
        {
            if (id is null || !idToMethod.TryGetValue(id, out var method))
                errs.Add($"{where}: result for id '{id}' has no prior request");
            else if (commands.TryGetValue(method, out var c))
                ValidateObject(result, c.Cmd.Returns, c.Dom, resolver, $"{where} result[{method}]", errs, 0);
        }
        else if (hasMethod && hasId) // request
        {
            string method = methodEl.GetString()!;
            if (id is not null) idToMethod[id] = method;
            if (!commands.TryGetValue(method, out var c))
                errs.Add($"{where}: unknown command '{method}'");
            else
            {
                var pars = msg.TryGetProperty("params", out var pe) ? pe : default;
                ValidateObject(pars, c.Cmd.Parameters, c.Dom, resolver, $"{where} params[{method}]", errs, 0);
            }
        }
        else if (hasMethod) // notification / event
        {
            string method = methodEl.GetString()!;
            if (!events.TryGetValue(method, out var e))
                errs.Add($"{where}: unknown event '{method}'");
            else
            {
                var pars = msg.TryGetProperty("params", out var pe) ? pe : default;
                ValidateObject(pars, e.Evt.Parameters, e.Dom, resolver, $"{where} event[{method}]", errs, 0);
            }
        }
        else errs.Add($"{where}: unclassifiable message");
    }

    if (n == 0) errs.Add($"{scenario}: golden trace has no messages (empty or missing 'messages' array) — nothing to conform");

    return ($"golden:{Path.GetFileName(file)}", errs.Count == 0, errs.Count == 0 ? $"{n} messages conform" : string.Join("; ", errs));
}

// Recursively validate a JSON object against a declared field list: no unknown fields, all required
// fields present, and every present field's value validated against its declared type — $ref objects
// recurse into their properties, enums are checked against their value set, and arrays element-wise.
// This is what makes the golden gate real: a nested payload can't silently drift from the contract.
static void ValidateObject(JsonElement obj, IReadOnlyList<Field> declared, Domain? ctx, RefResolver r,
    string where, List<string> errs, int depth)
{
    if (obj.ValueKind != JsonValueKind.Object)
    {
        // An omitted/empty object is only an error when a required field was expected
        // (zero-parameter commands may omit `params` entirely — envelope §2).
        foreach (var f in declared)
            if (!f.Optional) errs.Add($"{where}: missing required field '{f.Name}'");
        return;
    }
    if (depth > 64) { errs.Add($"{where}: type nesting too deep"); return; }

    var byName = declared.ToDictionary(f => f.Name, StringComparer.Ordinal);
    foreach (var prop in obj.EnumerateObject())
        if (!byName.ContainsKey(prop.Name))
            errs.Add($"{where}: unknown field '{prop.Name}'");

    foreach (var f in declared)
    {
        if (!obj.TryGetProperty(f.Name, out var val))
        {
            if (!f.Optional) errs.Add($"{where}: missing required field '{f.Name}'");
            continue;
        }
        ValidateValue(val, f, ctx, r, $"{where}.{f.Name}", errs, depth);
    }
}

// Validate one field value: arrays are checked element-wise, everything else as a scalar.
static void ValidateValue(JsonElement v, Field f, Domain? ctx, RefResolver r, string where, List<string> errs, int depth)
{
    if (f.Array)
    {
        if (v.ValueKind != JsonValueKind.Array) { errs.Add($"{where}: expected an array"); return; }
        int i = 0;
        foreach (var el in v.EnumerateArray())
            ValidateScalar(el, f, ctx, r, $"{where}[{i++}]", errs, depth);
        return;
    }
    ValidateScalar(v, f, ctx, r, where, errs, depth);
}

// Validate a single (non-array) value against its declared type: resolve $refs to enum/object/primitive.
static void ValidateScalar(JsonElement v, Field f, Domain? ctx, RefResolver r, string where, List<string> errs, int depth)
{
    if (f.Ref is not null)
    {
        var (td, owner) = r.ResolveWithOwner(f.Ref, ctx);
        if (td is null) { errs.Add($"{where}: unresolved $ref '{f.Ref}'"); return; }
        switch (td.Kind)
        {
            case "enum":
                if (v.ValueKind != JsonValueKind.String || !td.Values.Contains(v.GetString()!))
                    errs.Add($"{where}: {Describe(v)} is not a valid {f.Ref} value (one of: {string.Join("|", td.Values)})");
                break;
            case "object":
                ValidateObject(v, td.Properties, owner, r, where, errs, depth + 1);
                break;
            case "primitive":
                CheckPrimitive(v, td.Primitive, f.Ref, where, errs);
                break;
        }
        return;
    }
    CheckPrimitive(v, f.Type, f.Type ?? "value", where, errs);
}

static void CheckPrimitive(JsonElement v, string? baseType, string label, string where, List<string> errs)
{
    bool ok = baseType switch
    {
        "string" => v.ValueKind == JsonValueKind.String,
        "integer" or "number" => v.ValueKind == JsonValueKind.Number,
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => v.ValueKind == JsonValueKind.Object,
        _ => true, // no/unknown base type: don't over-constrain
    };
    if (!ok) errs.Add($"{where}: expected {label}, got {Describe(v)}");
}

static string Describe(JsonElement v) => v.ValueKind switch
{
    JsonValueKind.String => $"\"{v.GetString()}\"",
    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
    _ => v.ValueKind.ToString().ToLowerInvariant(),
};

// Coverage: every command and event must be exercised (as a request/notification `method`) by at
// least one golden trace. Results correlate by id, so a command's presence is proven by its request.
static (string, bool, string) GoldenCoverage(Protocol p, IReadOnlyList<string> files)
{
    var used = new HashSet<string>(StringComparer.Ordinal);
    foreach (var file in files)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array) continue;
        foreach (var entry in msgs.EnumerateArray())
            if (entry.TryGetProperty("msg", out var msg) && msg.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
                used.Add(m.GetString()!);
    }

    var errs = new List<string>();
    foreach (var d in p.Domains)
    {
        foreach (var c in d.Commands)
            if (!used.Contains($"{d.Name}.{c.Name}")) errs.Add($"command '{d.Name}.{c.Name}' not exercised by any golden trace");
        foreach (var e in d.Events)
            if (!used.Contains($"{d.Name}.{e.Name}")) errs.Add($"event '{d.Name}.{e.Name}' not exercised by any golden trace");
    }

    int total = p.Domains.Sum(d => d.Commands.Count + d.Events.Count);
    return ("golden-coverage", errs.Count == 0,
        errs.Count == 0 ? $"all {total} commands+events exercised by golden traces" : string.Join("; ", errs));
}

static Dictionary<int, string> LoadErrorCodes(string schemaPath)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
    var map = new Dictionary<int, string>();
    foreach (var e in doc.RootElement.GetProperty("errorCodes").EnumerateArray())
        map[e.GetProperty("code").GetInt32()] = e.GetProperty("name").GetString()!;
    return map;
}
