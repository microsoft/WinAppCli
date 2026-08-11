// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Reads the on-disk API metadata cache and answers <c>find-api</c> queries
/// (search, members, check-property, types, enums, namespaces, packages,
/// stats, projects) as structured results. Pure read side: it never writes to
/// the console, so command handlers own text vs. <c>--json</c> rendering.
/// </summary>
internal static class ApiQueryEngine
{
    public static ApiQueryResult<ApiSearchOutput> Search(string query, int maxResults, string cacheDir, ProjectManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return ApiQueryResult<ApiSearchOutput>.InvalidInput("A search query is required.");
        }

        List<string> packageCacheDirs = GetPackageCacheDirs(cacheDir, manifest);
        var namespaceHits = new Dictionary<string, (int BestScore, List<string> Files, List<ApiTypeHit> Matches)>();

        // Track high-confidence type matches for namespace disambiguation.
        var typeMatches = new List<(string Name, string FullName, TypeKind Kind, int Score, string? Description, List<string>? EnumValues)>();

        foreach (string dir in packageCacheDirs)
        {
            string nsPath = Path.Combine(dir, "namespaces.json");
            if (!File.Exists(nsPath))
            {
                continue;
            }
            foreach (string ns in Deserialize(nsPath, ApiSearchJsonContext.Default.ListString) ?? new List<string>())
            {
                string typesFile = Path.Combine(dir, "types", ApiCachePaths.NamespaceFileName(ns));
                if (!File.Exists(typesFile))
                {
                    continue;
                }
                var types = Deserialize(typesFile, ApiSearchJsonContext.Default.ListWinMdTypeInfo);
                if (types == null)
                {
                    continue;
                }
                foreach (WinMdTypeInfo type in types)
                {
                    int typeScore = Scoring.GetMatchScore(type.Name, type.FullName, query);
                    int bestMemberScore = 0;
                    string? memberSignature = null;
                    if (type.Members != null)
                    {
                        foreach (WinMdMemberInfo member in type.Members)
                        {
                            int memberScore = Scoring.GetMatchScore(member.Name, type.FullName + "." + member.Name, query);
                            if (memberScore > bestMemberScore)
                            {
                                bestMemberScore = memberScore;
                                memberSignature = member.Signature;
                            }
                        }
                    }
                    int combined = Math.Max(typeScore, bestMemberScore);
                    if (combined <= 0)
                    {
                        continue;
                    }

                    if (!namespaceHits.TryGetValue(ns, out var group))
                    {
                        group = (0, new List<string>(), new List<ApiTypeHit>());
                        namespaceHits[ns] = group;
                    }
                    if (combined > group.BestScore)
                    {
                        group.BestScore = combined;
                    }
                    if (!group.Files.Contains(typesFile))
                    {
                        group.Files.Add(typesFile);
                    }
                    string display = typeScore >= bestMemberScore
                        ? $"{type.Kind} {type.FullName}"
                        : $"{type.Kind} {type.FullName} -> {memberSignature}";
                    group.Matches.Add(new ApiTypeHit { Display = display, Score = typeScore >= bestMemberScore ? typeScore : bestMemberScore });
                    namespaceHits[ns] = group;

                    if (typeScore >= 60)
                    {
                        typeMatches.Add((type.Name, type.FullName, type.Kind, typeScore, type.Description, type.EnumValues));
                    }
                }
            }
        }

        // Ambiguity: the same short name resolving to more than one namespace.
        var ambiguousGroups = typeMatches
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g
                .Select(t => NamespacePrefix(t.FullName, t.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1)
            .Select(g => new ApiAmbiguityGroup
            {
                Name = g.Key,
                Candidates = g.OrderByDescending(t => t.Score).Select(t => new ApiAmbiguityCandidate
                {
                    FullName = t.FullName,
                    Kind = t.Kind.ToString(),
                    Score = t.Score,
                    Description = t.Description,
                    EnumValues = t.Kind == TypeKind.Enum ? t.EnumValues : null,
                }).ToList(),
            })
            .ToList();

        var results = namespaceHits
            .OrderByDescending(kv => kv.Value.BestScore)
            .Take(maxResults)
            .Select(kv => new ApiNamespaceHit
            {
                Namespace = kv.Key,
                Score = kv.Value.BestScore,
                Files = kv.Value.Files,
                Matches = kv.Value.Matches.OrderByDescending(m => m.Score).Take(5).ToList(),
            })
            .ToList();

        return ApiQueryResult<ApiSearchOutput>.Ok(new ApiSearchOutput
        {
            Query = query,
            Ambiguous = ambiguousGroups.Count > 0 ? ambiguousGroups : null,
            Results = results,
        });
    }

    /// <summary>
    /// Returns the namespace portion of a fully-qualified type name (everything
    /// before the trailing <c>.Name</c>), or an empty string for a type in the
    /// global namespace where <c>FullName == Name</c>. Guards the substring math so
    /// global-namespace types can't drive the length negative.
    /// </summary>
    private static string NamespacePrefix(string fullName, string name)
    {
        int cut = fullName.Length - name.Length - 1;
        return cut > 0 ? fullName.Substring(0, cut) : string.Empty;
    }

    public static ApiQueryResult<ApiMembersOutput> Members(string typeName, string? filter, string cacheDir, ProjectManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return ApiQueryResult<ApiMembersOutput>.InvalidInput("A type name is required.");
        }

        List<string> packageCacheDirs = GetPackageCacheDirs(cacheDir, manifest);
        var allTypes = LoadAllTypes(packageCacheDirs);
        var type = ResolveType(typeName, allTypes);
        if (type == null)
        {
            return ApiQueryResult<ApiMembersOutput>.NotFound($"Type not found: {typeName}");
        }

        var members = CollectMembersWithInheritance(type, allTypes);

        List<ApiMemberOutput> Project(MemberKind kind) => members
            .Where(m => m.Member.Kind == kind)
            .Select(m => ToMemberOutput(m.Member, m.DeclaringType, type.FullName))
            .ToList();

        var allProperties = Project(MemberKind.Property);
        var allEvents = Project(MemberKind.Event);
        var allMethods = Project(MemberKind.Method);

        // The GetForCurrentView warning describes the type, not the filtered view,
        // so it must be computed before filtering or a filter would suppress it.
        bool getForCurrentView = allMethods.Any(m => m.Name.Equals("GetForCurrentView", StringComparison.Ordinal));

        bool filtered = !string.IsNullOrWhiteSpace(filter);
        List<ApiMemberOutput> Filter(List<ApiMemberOutput> source) => filtered
            ? source.Where(m => MatchesFilter(m.Name, filter)).ToList()
            : source;

        return ApiQueryResult<ApiMembersOutput>.Ok(new ApiMembersOutput
        {
            FullName = type.FullName,
            Kind = type.Kind.ToString(),
            Description = type.Description,
            BaseType = type.BaseType,
            Deprecated = type.DeprecatedMessage,
            Filter = filtered ? filter : null,
            // Totals are reported only when filtering, so a caller can see how much
            // of the type was hidden and never mistake a narrow view for the whole API.
            TotalProperties = filtered ? allProperties.Count : null,
            TotalEvents = filtered ? allEvents.Count : null,
            TotalMethods = filtered ? allMethods.Count : null,
            Properties = Filter(allProperties),
            Events = Filter(allEvents),
            Methods = Filter(allMethods),
            GetForCurrentViewWarning = getForCurrentView,
        });
    }

    /// <summary>
    /// Case-insensitive substring match used by the <c>--filter</c> option on
    /// <c>members</c> and <c>enums</c>. Substring (not prefix) matching is deliberate:
    /// callers filter by concept ("folder", "background"), which rarely starts the name.
    /// </summary>
    private static bool MatchesFilter(string name, string? filter) =>
        string.IsNullOrWhiteSpace(filter) || name.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public static ApiQueryResult<ApiTypesOutput> Types(string ns, string cacheDir, ProjectManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(ns))
        {
            return ApiQueryResult<ApiTypesOutput>.InvalidInput("A namespace is required.");
        }
        string typesFileName = ApiCachePaths.NamespaceFileName(ns);
        List<string> packageCacheDirs = GetPackageCacheDirs(cacheDir, manifest);
        bool found = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var types = new List<ApiTypeSummary>();
        foreach (string dir in packageCacheDirs)
        {
            string typesFile = Path.Combine(dir, "types", typesFileName);
            if (!File.Exists(typesFile))
            {
                continue;
            }
            found = true;
            var list = Deserialize(typesFile, ApiSearchJsonContext.Default.ListWinMdTypeInfo);
            if (list == null)
            {
                continue;
            }
            foreach (WinMdTypeInfo type in list)
            {
                if (seen.Add(type.FullName))
                {
                    types.Add(new ApiTypeSummary { FullName = type.FullName, Kind = type.Kind.ToString(), BaseType = type.BaseType });
                }
            }
        }
        if (!found)
        {
            return ApiQueryResult<ApiTypesOutput>.NotFound($"Namespace not found: {ns}");
        }
        return ApiQueryResult<ApiTypesOutput>.Ok(new ApiTypesOutput { Namespace = ns, Types = types });
    }

    public static ApiQueryResult<ApiEnumsOutput> Enums(string typeName, string? filter, string cacheDir, ProjectManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return ApiQueryResult<ApiEnumsOutput>.InvalidInput("A type name is required.");
        }
        var type = ResolveType(typeName, LoadAllTypes(GetPackageCacheDirs(cacheDir, manifest)));
        if (type == null)
        {
            return ApiQueryResult<ApiEnumsOutput>.NotFound($"Type not found: {typeName}");
        }
        if (type.Kind != TypeKind.Enum)
        {
            return ApiQueryResult<ApiEnumsOutput>.NotAnEnum($"{type.FullName} is not an Enum (kind: {type.Kind}).");
        }
        List<string> allValues = type.EnumValues ?? new List<string>();
        bool filtered = !string.IsNullOrWhiteSpace(filter);
        return ApiQueryResult<ApiEnumsOutput>.Ok(new ApiEnumsOutput
        {
            FullName = type.FullName,
            Filter = filtered ? filter : null,
            TotalValues = filtered ? allValues.Count : null,
            Values = filtered
                ? allValues.Where(v => MatchesFilter(v, filter)).ToList()
                : allValues,
        });
    }

    public static ApiQueryResult<ApiNamespacesOutput> Namespaces(string? filter, string cacheDir, ProjectManifest manifest)
    {
        var sorted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string dir in GetPackageCacheDirs(cacheDir, manifest))
        {
            string nsPath = Path.Combine(dir, "namespaces.json");
            if (!File.Exists(nsPath))
            {
                continue;
            }
            var list = Deserialize(nsPath, ApiSearchJsonContext.Default.ListString);
            if (list == null)
            {
                continue;
            }
            foreach (string ns in list)
            {
                sorted.Add(ns);
            }
        }
        var filtered = sorted
            .Where(ns => filter == null || ns.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return ApiQueryResult<ApiNamespacesOutput>.Ok(new ApiNamespacesOutput { Namespaces = filtered });
    }

    public static ApiQueryResult<ApiPackagesOutput> Packages(string cacheDir, ProjectManifest manifest)
    {
        var summaries = new List<ApiPackageSummary>();
        foreach (ProjectPackageRef package in manifest.Packages)
        {
            string metaPath = Path.Combine(cacheDir, "packages", package.Id, package.Version, "meta.json");
            if (!File.Exists(metaPath))
            {
                summaries.Add(new ApiPackageSummary { Id = package.Id, Version = package.Version, Status = "cache-missing" });
                continue;
            }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                summaries.Add(new ApiPackageSummary
                {
                    Id = package.Id,
                    Version = package.Version,
                    TotalTypes = doc.RootElement.GetProperty("totalTypes").GetInt32(),
                    TotalMembers = doc.RootElement.GetProperty("totalMembers").GetInt32(),
                    Status = "ok",
                });
            }
            catch
            {
                summaries.Add(new ApiPackageSummary { Id = package.Id, Version = package.Version, Status = "meta-unreadable" });
            }
        }
        return ApiQueryResult<ApiPackagesOutput>.Ok(new ApiPackagesOutput
        {
            Packages = summaries,
        });
    }

    public static ApiQueryResult<ApiStatsOutput> Stats(string cacheDir, ProjectManifest manifest)
    {
        int types = 0, members = 0, namespaces = 0, winmds = 0;
        foreach (ProjectPackageRef package in manifest.Packages)
        {
            string metaPath = Path.Combine(cacheDir, "packages", package.Id, package.Version, "meta.json");
            if (!File.Exists(metaPath))
            {
                continue;
            }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                types += doc.RootElement.GetProperty("totalTypes").GetInt32();
                members += doc.RootElement.GetProperty("totalMembers").GetInt32();
                namespaces += doc.RootElement.GetProperty("totalNamespaces").GetInt32();
                if (doc.RootElement.TryGetProperty("winMdFiles", out var winMdFiles))
                {
                    winmds += winMdFiles.GetArrayLength();
                }
            }
            catch
            {
            }
        }
        return ApiQueryResult<ApiStatsOutput>.Ok(new ApiStatsOutput
        {
            Packages = manifest.Packages.Count,
            Namespaces = namespaces,
            Types = types,
            Members = members,
            WinMdFiles = winmds,
        });
    }

    public static ApiProjectsOutput Projects(string cacheDir)
    {
        var projects = new List<ApiProjectSummary>();
        string projectsDir = Path.Combine(cacheDir, "projects");
        if (Directory.Exists(projectsDir))
        {
            foreach (string file in Directory.GetFiles(projectsDir, "*.json"))
            {
                int count = DeserializeManifest(file)?.Packages.Count ?? 0;
                projects.Add(new ApiProjectSummary { Name = Path.GetFileNameWithoutExtension(file), PackageCount = count });
            }
        }
        return new ApiProjectsOutput { Projects = projects };
    }

    public static ApiQueryResult<ApiCheckPropertyOutput> CheckProperty(string typeName, string propertyName, string cacheDir, ProjectManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(propertyName))
        {
            return ApiQueryResult<ApiCheckPropertyOutput>.InvalidInput("Usage: check-property <TypeName> <PropertyName>");
        }

        List<string> packageCacheDirs = GetPackageCacheDirs(cacheDir, manifest);
        var allTypes = LoadAllTypes(packageCacheDirs);

        var targetType = ResolveType(typeName, allTypes);
        if (targetType == null)
        {
            return ApiQueryResult<ApiCheckPropertyOutput>.NotFound($"Type not found: {typeName}");
        }

        var members = CollectMembersWithInheritance(targetType, allTypes);

        // 1. Direct or inherited member. Only a Property counts as "found" — a
        // method or event with the same name is not a settable property.
        var exact = members.FirstOrDefault(m =>
            m.Member.Kind == MemberKind.Property &&
            m.Member.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
        if (exact.Member != null)
        {
            return ApiQueryResult<ApiCheckPropertyOutput>.Ok(new ApiCheckPropertyOutput
            {
                Found = true,
                Type = targetType.FullName,
                Property = exact.Member.Name,
                Match = ToMemberOutput(exact.Member, exact.DeclaringType, targetType.FullName),
                SimilarOnType = new List<ApiMemberOutput>(),
                TypesWithProperty = new List<ApiCrossTypeMember>(),
                TypesWithSimilar = new List<ApiCrossTypeMember>(),
            });
        }

        // 2. Attached property pattern (static GetXxx/SetXxx).
        var attached = DetectAttachedProperty(targetType, propertyName);
        if (attached != null)
        {
            return ApiQueryResult<ApiCheckPropertyOutput>.Ok(new ApiCheckPropertyOutput
            {
                Found = true,
                Type = targetType.FullName,
                Property = propertyName,
                Attached = true,
                AttachedInfo = attached,
                SimilarOnType = new List<ApiMemberOutput>(),
                TypesWithProperty = new List<ApiCrossTypeMember>(),
                TypesWithSimilar = new List<ApiCrossTypeMember>(),
            });
        }

        // 3. Not found — build suggestions.
        var similarOnType = members
            .Where(m => m.Member.Kind == MemberKind.Property)
            .Select(m => (Member: m.Member, Score: Scoring.GetMatchScore(m.Member.Name, m.Member.Name, propertyName)))
            .Where(x => x.Score >= 40)
            .OrderByDescending(x => x.Score)
            .Take(5)
            .Select(x => ToMemberOutput(x.Member, targetType.FullName, targetType.FullName))
            .ToList();

        var typesWithProperty = allTypes
            .Where(t => t.FullName != targetType.FullName)
            .SelectMany(t => t.Members
                .Where(m => m.Kind == MemberKind.Property && m.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                .Select(m => new ApiCrossTypeMember { TypeName = t.Name, Signature = m.Signature, Description = m.Description }))
            .Take(5)
            .ToList();

        var typesWithSimilar = allTypes
            .Where(t => t.FullName != targetType.FullName)
            .SelectMany(t => t.Members
                .Where(m => m.Kind == MemberKind.Property)
                .Select(m => (Type: t, Member: m, Score: Scoring.GetMatchScore(m.Name, m.Name, propertyName)))
                .Where(x => x.Score >= 60 && !x.Member.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Score)
            .Take(3)
            .Select(x => new ApiCrossTypeMember { TypeName = x.Type.Name, Signature = x.Member.Signature, Description = x.Member.Description })
            .ToList();

        return ApiQueryResult<ApiCheckPropertyOutput>.Ok(new ApiCheckPropertyOutput
        {
            Found = false,
            Type = targetType.FullName,
            Property = propertyName,
            SimilarOnType = similarOnType,
            TypesWithProperty = typesWithProperty,
            TypesWithSimilar = typesWithSimilar,
        });
    }

    private static ApiMemberOutput ToMemberOutput(WinMdMemberInfo member, string? declaringType, string ownerFullName)
    {
        bool inherited = declaringType != null && declaringType != ownerFullName;
        return new ApiMemberOutput
        {
            Name = member.Name,
            Kind = member.Kind.ToString(),
            Signature = member.Signature,
            ReturnType = member.ReturnType,
            Description = member.Description,
            Deprecated = member.DeprecatedMessage,
            DeclaringType = inherited ? declaringType : null,
            Inherited = inherited,
        };
    }

    private static WinMdTypeInfo? ResolveType(string typeName, List<WinMdTypeInfo> allTypes)
    {
        var exact = allTypes.FirstOrDefault(t => t.FullName.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        var shortMatches = allTypes.Where(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (shortMatches.Count == 1)
        {
            return shortMatches[0];
        }
        if (shortMatches.Count > 1)
        {
            var winuiMatch = shortMatches.FirstOrDefault(t => t.Namespace.StartsWith("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase));
            return winuiMatch ?? shortMatches[0];
        }
        return null;
    }

    private static List<(WinMdMemberInfo Member, string? DeclaringType)> CollectMembersWithInheritance(
        WinMdTypeInfo type, List<WinMdTypeInfo> allTypes)
    {
        var result = new List<(WinMdMemberInfo Member, string? DeclaringType)>();
        foreach (var m in type.Members)
        {
            result.Add((m, type.FullName));
        }

        string? baseTypeName = type.BaseType;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { type.FullName };
        while (!string.IsNullOrEmpty(baseTypeName) && visited.Add(baseTypeName))
        {
            var baseType = allTypes.FirstOrDefault(t => t.FullName.Equals(baseTypeName, StringComparison.OrdinalIgnoreCase));
            if (baseType == null)
            {
                break;
            }
            foreach (var m in baseType.Members)
            {
                if (!result.Any(r => r.Member.Name == m.Name && r.Member.Kind == m.Kind))
                {
                    result.Add((m, baseType.FullName));
                }
            }
            baseTypeName = baseType.BaseType;
        }
        return result;
    }

    private static string? DetectAttachedProperty(WinMdTypeInfo type, string propertyName)
    {
        string getName = "Get" + propertyName;
        string setName = "Set" + propertyName;

        var getter = type.Members.FirstOrDefault(m => m.Kind == MemberKind.Method && m.Name.Equals(getName, StringComparison.OrdinalIgnoreCase));
        var setter = type.Members.FirstOrDefault(m => m.Kind == MemberKind.Method && m.Name.Equals(setName, StringComparison.OrdinalIgnoreCase));

        if (getter != null && getter.Parameters != null && getter.Parameters.Count >= 1)
        {
            string paramType = getter.Parameters[0].Type;
            if (paramType.Contains("DependencyObject") || paramType.Contains("UIElement") || paramType.Contains("FrameworkElement"))
            {
                string returnType = getter.ReturnType ?? "unknown";
                string accessors = setter != null
                    ? $"via {type.Name}.{getName}() / {type.Name}.{setName}()"
                    : $"via {type.Name}.{getName}() (read-only)";
                return $"{returnType} — {accessors}";
            }
        }
        return null;
    }

    private static List<WinMdTypeInfo> LoadAllTypes(List<string> packageCacheDirs)
    {
        var allTypes = new List<WinMdTypeInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in packageCacheDirs)
        {
            string nsPath = Path.Combine(dir, "namespaces.json");
            if (!File.Exists(nsPath))
            {
                continue;
            }
            var namespaces = Deserialize(nsPath, ApiSearchJsonContext.Default.ListString);
            if (namespaces == null)
            {
                continue;
            }
            foreach (string ns in namespaces)
            {
                string typesFile = Path.Combine(dir, "types", ApiCachePaths.NamespaceFileName(ns));
                if (!File.Exists(typesFile))
                {
                    continue;
                }
                var types = Deserialize(typesFile, ApiSearchJsonContext.Default.ListWinMdTypeInfo);
                if (types == null)
                {
                    continue;
                }
                foreach (var type in types)
                {
                    if (seen.Add(type.FullName))
                    {
                        allTypes.Add(type);
                    }
                }
            }
        }
        return allTypes;
    }

    private static List<string> GetPackageCacheDirs(string cacheDir, ProjectManifest manifest)
    {
        var dirs = new List<string>();
        foreach (ProjectPackageRef package in manifest.Packages)
        {
            string dir = Path.Combine(cacheDir, "packages", package.Id, package.Version);
            if (Directory.Exists(dir))
            {
                dirs.Add(dir);
            }
        }
        return dirs;
    }

    private static T? Deserialize<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo);
        }
        catch
        {
            return null;
        }
    }

    private static ProjectManifest? DeserializeManifest(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ApiSearchJsonContext.Default.ProjectManifest);
        }
        catch
        {
            return null;
        }
    }
}
