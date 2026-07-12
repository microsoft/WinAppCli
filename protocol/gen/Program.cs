// Copyright (c) Microsoft Corporation. Licensed under the MIT License.
//
// wdxp-gen: the DAP-style code generator. Loads the one hand-authored source of truth
// (wdxp.v0.json), structurally validates it, and emits every downstream surface by walking the
// same model with ZERO per-command special-casing. That property is what makes Gate 3 real:
// a schema field-add flows to all generated surfaces with no hand edits.
using System.Text;
using System.Text.Json;
using Wdxp.Gen;

var schemaPath = ArgValue(args, "--schema") ?? FindSchema();
if (schemaPath is null) { Console.Error.WriteLine("error: could not locate wdxp.v0.json (pass --schema <path>)"); return 2; }
schemaPath = Path.GetFullPath(schemaPath);
var outDir = Path.GetFullPath(ArgValue(args, "--out") ?? Path.Combine(Path.GetDirectoryName(schemaPath)!, "gen", "out"));

Console.WriteLine($"wdxp-gen: schema = {schemaPath}");
var protocol = SchemaLoader.Load(schemaPath);
var resolver = new RefResolver(protocol);

var errors = Validator.Validate(protocol, resolver);
if (errors.Count > 0)
{
    Console.Error.WriteLine($"SCHEMA INVALID — {errors.Count} error(s):");
    foreach (var e in errors) Console.Error.WriteLine("  - " + e);
    return 1;
}
Console.WriteLine($"schema valid: {protocol.Domains.Count} domains, "
    + $"{protocol.Domains.Sum(d => d.Commands.Count)} commands, {protocol.Domains.Sum(d => d.Events.Count)} events.");

Directory.CreateDirectory(outDir);
var opts = new JsonSerializerOptions { WriteIndented = true };

var cli = Emit.Cli(protocol);
File.WriteAllText(Path.Combine(outDir, "cli-commands.json"), JsonSerializer.Serialize(cli, opts));

File.WriteAllText(Path.Combine(outDir, "protocol-reference.md"), Emit.Docs(protocol));

Console.WriteLine($"emitted 2 surfaces -> {outDir}");
Console.WriteLine("  cli-commands.json  protocol-reference.md");
return 0;

static string? ArgValue(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static string? FindSchema()
{
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, "protocol", "wdxp.v0.json"), Path.Combine(dir.FullName, "wdxp.v0.json") })
                if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
    }
    return null;
}

namespace Wdxp.Gen
{
    /// <summary>Structural gate enforced in CI. The JSON Schema (wdxp.schema.json) is the authoring aid;
    /// these checks are the ones we actually fail the build on.</summary>
    public static class Validator
    {
        public static List<string> Validate(Protocol p, RefResolver r)
        {
            var errors = new List<string>();
            var tiers = new HashSet<string> { "read", "mutate-ephemeral", "structural", "persist", "privileged" };
            var caps = new HashSet<string>(StringComparer.Ordinal);
            var methods = new HashSet<string>(StringComparer.Ordinal);

            foreach (var d in p.Domains)
            {
                if (!caps.Add(d.Capability)) errors.Add($"duplicate capability '{d.Capability}' on domain {d.Name}");

                foreach (var t in d.Types) CheckType(t, d, errors);

                foreach (var c in d.Commands)
                {
                    var m = $"{d.Name}.{c.Name}";
                    if (!methods.Add(m)) errors.Add($"duplicate method '{m}'");
                    if (!tiers.Contains(c.Risk)) errors.Add($"{m}: unknown risk tier '{c.Risk}'");
                    foreach (var f in c.Parameters) CheckField(f, d, $"{m} param '{f.Name}'", errors, r);
                    foreach (var f in c.Returns) CheckField(f, d, $"{m} return '{f.Name}'", errors, r);
                }

                foreach (var e in d.Events)
                    foreach (var f in e.Parameters) CheckField(f, d, $"{d.Name}.{e.Name} param '{f.Name}'", errors, r);
            }

            // Cross-cutting invariants the whole architecture leans on.
            RequireEnum(p, r, "Source", "SourceKind", "source-backed", errors);
            RequireEnum(p, r, "Property", "ValueSource", "local", errors);
            RequireEnum(p, r, "HotReload", "TransactionState", "refused-unsafe", errors);
            RequireEnum(p, r, "Diagnostics", "ReasonCode", "release-no-line-info", errors);
            if (r.Resolve("Outcome", null) is null) errors.Add("missing protocol-level enum 'Outcome' (the four-outcome classifier)");
            if (r.Resolve("RiskTier", null) is null) errors.Add("missing protocol-level enum 'RiskTier'");
            return errors;
        }

        private static void CheckType(TypeDef t, Domain d, List<string> errors)
        {
            switch (t.Kind)
            {
                case "enum" when t.Values.Count == 0: errors.Add($"{d.Name}.{t.Id}: enum has no values"); break;
                case "primitive" when t.Primitive is null: errors.Add($"{d.Name}.{t.Id}: primitive has no base type"); break;
                case "object" when t.Properties.Count == 0: errors.Add($"{d.Name}.{t.Id}: object has no properties"); break;
            }
        }

        private static void CheckField(Field f, Domain d, string where, List<string> errors, RefResolver r)
        {
            if (f.Type is null && f.Ref is null) errors.Add($"{where}: needs either 'type' or '$ref'");
            if (f.Type is not null && f.Ref is not null) errors.Add($"{where}: has both 'type' and '$ref'");
            if (f.Ref is not null && r.Resolve(f.Ref, d) is null) errors.Add($"{where}: unresolved $ref '{f.Ref}'");
        }

        private static void RequireEnum(Protocol p, RefResolver r, string domain, string type, string mustContain, List<string> errors)
        {
            var td = r.Resolve($"{domain}.{type}", null);
            if (td is null) { errors.Add($"missing normative enum {domain}.{type}"); return; }
            if (!td.Values.Contains(mustContain)) errors.Add($"{domain}.{type} must contain '{mustContain}'");
        }
    }

    /// <summary>The emitters. Each walks the model generically — the map from a schema field to a
    /// CLI arg / doc row is type-driven, never command-driven.</summary>
    public static class Emit
    {
        // ---- CLI facade: the `winapp --cli-schema`-shaped command graph (+ events as notifications) ----
        public static object Cli(Protocol p)
        {
            var commands = new List<object?>();
            var notifications = new List<object?>();
            foreach (var d in p.Domains)
            {
                foreach (var c in d.Commands)
                    commands.Add(new Dictionary<string, object?>
                    {
                        ["cliPath"] = $"{d.Capability} {Kebab(c.Name)}",
                        ["method"] = $"{d.Name}.{c.Name}",
                        ["capability"] = d.Capability,
                        ["risk"] = c.Risk,
                        ["stability"] = c.Stability ?? d.Stability,
                        ["summary"] = c.Summary,
                        ["args"] = c.Parameters.Select(ArgDescriptor).ToList(),
                        ["returns"] = c.Returns.Select(ArgDescriptor).ToList(),
                    });
                foreach (var e in d.Events)
                    notifications.Add(new Dictionary<string, object?>
                    {
                        ["method"] = $"{d.Name}.{e.Name}",
                        ["capability"] = d.Capability,
                        ["stability"] = e.Stability ?? d.Stability,
                        ["summary"] = e.Summary,
                        ["args"] = e.Parameters.Select(ArgDescriptor).ToList(),
                    });
            }
            return new Dictionary<string, object?>
            {
                ["protocol"] = p.Name,
                ["version"] = p.Version,
                ["generatedBy"] = "wdxp-gen",
                ["surface"] = "cli-command-graph",
                ["commandCount"] = commands.Count,
                ["commands"] = commands,
                ["notificationCount"] = notifications.Count,
                ["notifications"] = notifications,
            };
        }

        private static object ArgDescriptor(Field f) => new Dictionary<string, object?>
        {
            ["name"] = f.Name,
            ["type"] = f.Ref ?? f.Type,
            ["isRef"] = f.Ref is not null,
            ["array"] = f.Array,
            ["optional"] = f.Optional,
            ["summary"] = f.Summary,
        };

        // ---- Docs facade: a human-readable reference generated from the same model ----
        public static string Docs(Protocol p)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {p.Name} v{p.Version} — protocol reference (generated)");
            sb.AppendLine();
            sb.AppendLine("> Generated by `wdxp-gen` from `wdxp.v0.json`. Do not edit by hand.");
            sb.AppendLine();
            foreach (var d in p.Domains)
            {
                sb.AppendLine($"## {d.Name}  `{d.Capability}`  ({d.Stability})");
                sb.AppendLine();
                sb.AppendLine(d.Summary);
                sb.AppendLine();
                if (d.Commands.Count > 0)
                {
                    sb.AppendLine("| Command | Risk | Params → Returns |");
                    sb.AppendLine("|---|---|---|");
                    foreach (var c in d.Commands)
                        sb.AppendLine($"| `{d.Name}.{c.Name}` | {c.Risk} | {Sig(c.Parameters)} → {Sig(c.Returns)} |");
                    sb.AppendLine();
                }
                if (d.Events.Count > 0)
                {
                    sb.AppendLine("Events: " + string.Join(", ", d.Events.Select(e => $"`{d.Name}.{e.Name}`")) + ".");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        private static string Sig(IReadOnlyList<Field> fs) =>
            fs.Count == 0 ? "()" : "(" + string.Join(", ", fs.Select(f => $"{f.Name}: {(f.Ref ?? f.Type)}{(f.Array ? "[]" : "")}{(f.Optional ? "?" : "")}")) + ")";

        private static string[] Words(string name)
        {
            var words = new List<string>();
            var cur = new StringBuilder();
            foreach (var ch in name)
            {
                if (char.IsUpper(ch) && cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); }
                cur.Append(char.ToLowerInvariant(ch));
            }
            if (cur.Length > 0) words.Add(cur.ToString());
            return words.ToArray();
        }

        private static string Kebab(string name) => string.Join("-", Words(name));
    }
}
