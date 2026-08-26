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
        var namespaceHits = new Dictionary<string, (int BestScore, bool HasExact, List<string> Files, List<(ApiTypeHit Hit, bool Exact)> Matches)>();

        // The symbol the caller actually asked for, when the query names one. Exact
        // matches sort ahead of merely-related types, and ambiguity is reported for
        // this name only.
        string exactTarget = query.Trim();

        // Track high-confidence type matches for namespace disambiguation.
        var typeMatches = new List<(string Name, string FullName, TypeKind Kind, int Score, string? Description, List<string>? EnumValues)>();

        // The same type routinely ships in several packages in one graph (for example
        // Microsoft.UI.Xaml.Controls.Button in both WinAppSdkRuntime and
        // Microsoft.WindowsAppSDK.WinUI). Without this, each copy is scored and emitted
        // separately: duplicates pad the ambiguity candidate lists and crowd real hits out
        // of the per-namespace Matches cap below. LoadAllTypes already dedupes this way for
        // every other verb; Search was the only path missing it.
        var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    // CsWinRT emits an ABI.* projection twin for each WinRT type. They are
                    // marshalling internals no one writes code against, and because
                    // "ABI.Microsoft.UI.Xaml.Controls" is a distinct namespace prefix they can
                    // also manufacture a bogus CS0104 ambiguity warning for a type that really
                    // lives in only one namespace. Excluded from discovery only — LoadAllTypes
                    // is deliberately left unfiltered so 'find-api members ABI.Foo' still resolves.
                    if (IsProjectionInternal(type.FullName))
                    {
                        continue;
                    }
                    if (!seenTypes.Add(type.FullName))
                    {
                        continue;
                    }
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
                        group = (0, false, new List<string>(), new List<(ApiTypeHit, bool)>());
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
                    bool isExactName = type.Name.Equals(exactTarget, StringComparison.OrdinalIgnoreCase);
                    if (isExactName)
                    {
                        group.HasExact = true;
                    }
                    string display = typeScore >= bestMemberScore
                        ? $"{type.Kind} {type.FullName}"
                        : $"{type.Kind} {type.FullName} -> {memberSignature}";
                    group.Matches.Add((new ApiTypeHit { Display = display, Score = typeScore >= bestMemberScore ? typeScore : bestMemberScore }, isExactName));
                    namespaceHits[ns] = group;

                    if (typeScore >= 60)
                    {
                        typeMatches.Add((type.Name, type.FullName, type.Kind, typeScore, type.Description, type.EnumValues));
                    }
                }
            }
        }

        // Ambiguity: the same short name resolving to more than one namespace.
        // Only the requested symbol is worth warning about. A query for
        // "NavigationView" used to emit a group for every related match it happened to
        // score — NavigationViewItem, NavigationViewItemAutomationPeer and so on — none
        // of which the caller asked to disambiguate, and none of which --max bounded.
        var allAmbiguous = typeMatches
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g
                .Select(t => NamespacePrefix(t.FullName, t.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1)
            .ToList();

        var exactAmbiguous = allAmbiguous
            .Where(g => g.Key.Equals(exactTarget, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // When the query names a symbol, report that symbol's ambiguity and nothing
        // else. A fuzzy query ("acrylic brush") names no symbol, so fall back to the
        // highest-scoring groups — still bounded by --max, which never applied here before.
        var ambiguousGroups = (exactAmbiguous.Count > 0 ? exactAmbiguous : allAmbiguous)
            .OrderByDescending(g => g.Max(t => t.Score))
            .Take(maxResults)
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
            .OrderByDescending(kv => kv.Value.HasExact)
            .ThenByDescending(kv => kv.Value.BestScore)
            .Take(maxResults)
            .Select(kv => new ApiNamespaceHit
            {
                Namespace = kv.Key,
                Score = kv.Value.BestScore,
                Files = kv.Value.Files,
                Matches = kv.Value.Matches
                    .OrderByDescending(m => m.Exact)
                    .ThenByDescending(m => m.Hit.Score)
                    .Select(m => m.Hit)
                    .Take(5)
                    .ToList(),
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

    /// <summary>
    /// True for CsWinRT's generated <c>ABI.*</c> projection twins, which mirror real WinRT
    /// types purely as marshalling internals. They are filtered out of search results because
    /// they are never something a caller writes code against, and because their distinct
    /// namespace prefix would otherwise fabricate ambiguity warnings for types that live in a
    /// single real namespace. Exact lookups (<c>members</c>, <c>enums</c>, <c>check-property</c>)
    /// go through <see cref="LoadAllTypes"/> and can still reach them by fully-qualified name.
    /// </summary>
    private static bool IsProjectionInternal(string fullName) =>
        fullName.StartsWith("ABI.", StringComparison.Ordinal);

    public static ApiQueryResult<ApiMembersOutput> Members(string typeName, string? filter, string cacheDir, ProjectManifest manifest, bool includeAll = false)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return ApiQueryResult<ApiMembersOutput>.InvalidInput("A type name is required.");
        }

        List<string> packageCacheDirs = GetPackageCacheDirs(cacheDir, manifest);
        var allTypes = LoadAllTypes(packageCacheDirs);
        var (type, ambiguous) = ResolveType(typeName, allTypes);
        if (ambiguous is not null)
        {
            return ApiQueryResult<ApiMembersOutput>.InvalidInput(AmbiguousTypeMessage(typeName, ambiguous));
        }
        if (type == null)
        {
            return ApiQueryResult<ApiMembersOutput>.NotFound(WithIncompleteNote($"Type not found: {typeName}", cacheDir, manifest));
        }

        var members = CollectMembersWithInheritance(type, allTypes);

        bool filtered = !string.IsNullOrWhiteSpace(filter);

        // An unfiltered listing without --all is a bulk dump — the one shape that is
        // routinely expensive (Button: 400 members, ~46k tokens of JSON). Trim the two
        // parts of it that no caller writes code from, and leave everything else alone:
        // a targeted --filter query is already small, and its descriptions are the most
        // useful text in the payload.
        bool bulk = !filtered && !includeAll;

        List<ApiMemberOutput> Project(MemberKind kind) => members
            .Where(m => m.Member.Kind == kind)
            .Select(m => ToMemberOutput(m.Member, m.DeclaringType, type.FullName, includeDescription: !bulk))
            .ToList();

        var allProperties = Project(MemberKind.Property);
        var allEvents = Project(MemberKind.Event);
        var allMethods = Project(MemberKind.Method);

        // The GetForCurrentView warning describes the type, not the filtered view,
        // so it must be computed before filtering or a filter would suppress it.
        bool getForCurrentView = allMethods.Any(m => m.Name.Equals("GetForCurrentView", StringComparison.Ordinal));

        // Dependency-property identifier statics (BackgroundProperty) are 28% of a
        // WinUI control's members and are never what you write in XAML or in a property
        // assignment — they exist to be passed to GetValue/SetValue/RegisterCallback.
        // A --filter query still reaches them, so code-behind work is unaffected.
        int hiddenDependencyProperties = 0;
        if (bulk)
        {
            int before = allProperties.Count;
            allProperties = allProperties.Where(m => !IsDependencyPropertyIdentifier(m)).ToList();
            hiddenDependencyProperties = before - allProperties.Count;
        }

        List<ApiMemberOutput> Filter(List<ApiMemberOutput> source) => filtered
            ? source.Where(m => MatchesFilter(m.Name, filter)).ToList()
            : source;

        // Totals describe the type, not the view, so anything hidden — by a filter or by
        // the bulk-dump trim — must be visible as a count or a narrow view reads as a
        // small API. When nothing is hidden they stay null and cost nothing.
        bool anythingHidden = filtered || hiddenDependencyProperties > 0;

        return ApiQueryResult<ApiMembersOutput>.Ok(new ApiMembersOutput
        {
            FullName = type.FullName,
            Kind = type.Kind.ToString(),
            Description = type.Description,
            BaseType = type.BaseType,
            Deprecated = type.DeprecatedMessage,
            Filter = filtered ? filter : null,
            TotalProperties = anythingHidden ? allProperties.Count + hiddenDependencyProperties : null,
            TotalEvents = anythingHidden ? allEvents.Count : null,
            TotalMethods = anythingHidden ? allMethods.Count : null,
            HiddenDependencyProperties = hiddenDependencyProperties > 0 ? hiddenDependencyProperties : null,
            DescriptionsOmitted = bulk ? true : null,
            Hint = bulk
                ? "Dependency-property identifiers and member descriptions are omitted from an unfiltered listing. Use --filter <text> to search the full surface, or --all for the complete listing."
                : null,
            Properties = Filter(allProperties),
            Events = Filter(allEvents),
            Methods = Filter(allMethods),
            GetForCurrentViewWarning = getForCurrentView,
        });
    }

    /// <summary>
    /// Whether a member is a dependency-property identifier static — a property named
    /// <c>XxxProperty</c> whose type is <c>DependencyProperty</c>. Both conditions are
    /// required so an ordinary member that merely ends in "Property" is never hidden.
    /// </summary>
    private static bool IsDependencyPropertyIdentifier(ApiMemberOutput member) =>
        member.Kind == nameof(MemberKind.Property)
        && member.Name.EndsWith("Property", StringComparison.Ordinal)
        && member.ReturnType is not null
        && (member.ReturnType.EndsWith(".DependencyProperty", StringComparison.Ordinal)
            || member.ReturnType.Equals("DependencyProperty", StringComparison.Ordinal));

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
            return ApiQueryResult<ApiTypesOutput>.NotFound(WithIncompleteNote($"Namespace not found: {ns}", cacheDir, manifest));
        }
        return ApiQueryResult<ApiTypesOutput>.Ok(new ApiTypesOutput { Namespace = ns, Types = types });
    }

    public static ApiQueryResult<ApiEnumsOutput> Enums(string typeName, string? filter, string cacheDir, ProjectManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return ApiQueryResult<ApiEnumsOutput>.InvalidInput("A type name is required.");
        }
        var (type, ambiguous) = ResolveType(typeName, LoadAllTypes(GetPackageCacheDirs(cacheDir, manifest)));
        if (ambiguous is not null)
        {
            return ApiQueryResult<ApiEnumsOutput>.InvalidInput(AmbiguousTypeMessage(typeName, ambiguous));
        }
        if (type == null)
        {
            return ApiQueryResult<ApiEnumsOutput>.NotFound(WithIncompleteNote($"Type not found: {typeName}", cacheDir, manifest));
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
            sorted.UnionWith(list);
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
                bool incomplete = doc.RootElement.TryGetProperty("incomplete", out var incompleteEl)
                    && incompleteEl.ValueKind == JsonValueKind.True;
                summaries.Add(new ApiPackageSummary
                {
                    Id = package.Id,
                    Version = package.Version,
                    TotalTypes = doc.RootElement.GetProperty("totalTypes").GetInt32(),
                    TotalMembers = doc.RootElement.GetProperty("totalMembers").GetInt32(),
                    Status = incomplete ? "incomplete" : "ok",
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // Unreadable or malformed meta.json is reported per package rather than
                // failing the whole listing.
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // Skip a package whose meta.json cannot be read or parsed and keep
                // accumulating totals from the rest.
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
                // Report the manifest's own project name, not its file name: the file
                // name carries a path hash (App_ab12cd34) that '--project' does not match.
                ProjectManifest? manifest = DeserializeManifest(file);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.ProjectName))
                {
                    continue;
                }
                projects.Add(new ApiProjectSummary { Name = manifest.ProjectName, PackageCount = manifest.Packages.Count });
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

        var (targetType, ambiguous) = ResolveType(typeName, allTypes);
        if (ambiguous is not null)
        {
            return ApiQueryResult<ApiCheckPropertyOutput>.InvalidInput(AmbiguousTypeMessage(typeName, ambiguous));
        }
        if (targetType == null)
        {
            return ApiQueryResult<ApiCheckPropertyOutput>.NotFound(WithIncompleteNote($"Type not found: {typeName}", cacheDir, manifest));
        }

        var members = CollectMembersWithInheritance(targetType, allTypes);

        // 1. Direct or inherited member. Only a Property counts as "found" — a
        // method or event with the same name is not a settable property.
        var exact = members.FirstOrDefault(m =>
            m.Member.Kind == MemberKind.Property &&
            m.Member.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
        if (exact.Member != null)
        {
            ApiMemberOutput match = ToMemberOutput(exact.Member, exact.DeclaringType, targetType.FullName);
            return ApiQueryResult<ApiCheckPropertyOutput>.Ok(new ApiCheckPropertyOutput
            {
                Found = true,
                Type = targetType.FullName,
                Property = exact.Member.Name,
                Match = match,
                Writable = match.Writable,
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
            // A miss is the only outcome a partial index can get wrong, so the caveat
            // rides on the negative answer rather than on every result.
            Warning = IncompleteIndexNote(cacheDir, manifest),
            SimilarOnType = NullIfEmpty(similarOnType),
            TypesWithProperty = NullIfEmpty(typesWithProperty),
            TypesWithSimilar = NullIfEmpty(typesWithSimilar),
        });
    }

    private static ApiMemberOutput ToMemberOutput(WinMdMemberInfo member, string? declaringType, string ownerFullName, bool includeDescription = true)
    {
        bool inherited = declaringType != null && declaringType != ownerFullName;
        return new ApiMemberOutput
        {
            Name = member.Name,
            Kind = member.Kind.ToString(),
            Signature = member.Signature,
            ReturnType = member.ReturnType,
            Description = includeDescription ? member.Description : null,
            Deprecated = member.DeprecatedMessage,
            DeclaringType = inherited ? declaringType : null,
            Inherited = inherited,
            Writable = PropertyWritable(member),
        };
    }

    /// <summary>
    /// Whether a property can be assigned, read from the accessor block the parser
    /// bakes into the signature (<c>{ get; }</c>, <c>{ get; set; }</c>, <c>{ set; }</c>).
    /// Returns <c>null</c> for non-properties, and for a property whose type blob could
    /// not be decoded and therefore carries no accessor block — so "unknown" is never
    /// reported as read-only.
    /// </summary>
    private static bool? PropertyWritable(WinMdMemberInfo member)
    {
        if (member.Kind != MemberKind.Property)
        {
            return null;
        }
        if (member.Signature.EndsWith("{ get; set; }", StringComparison.Ordinal)
            || member.Signature.EndsWith("{ set; }", StringComparison.Ordinal))
        {
            return true;
        }
        return member.Signature.EndsWith("{ get; }", StringComparison.Ordinal) ? false : null;
    }

    /// <summary>
    /// Resolve a type by fully-qualified name, or by short name when that resolves
    /// deterministically. A short name shared by a modern <c>Microsoft.*</c> type and its
    /// legacy <c>Windows.*</c> UWP twin resolves to the <c>Microsoft.*</c> one — that is
    /// the projection a Windows App SDK caller means, and it is the pair almost every
    /// duplicated name in the SDK scope forms. Anything still ambiguous after that
    /// returns its candidates instead of a type: picking one would depend on file
    /// enumeration order, and validating an API against the wrong type is worse than
    /// being told to qualify it.
    /// </summary>
    private static (WinMdTypeInfo? Type, List<WinMdTypeInfo>? Candidates) ResolveType(string typeName, List<WinMdTypeInfo> allTypes)
    {
        var exact = allTypes.FirstOrDefault(t => t.FullName.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return (exact, null);
        }

        var shortMatches = allTypes.Where(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)).ToList();

        // An ABI.* twin mirrors a real type, so it must never manufacture ambiguity.
        // Only fall back to them when they are the sole matches, which keeps a
        // deliberate 'members ABI.Foo'-style short-name lookup working.
        var candidates = shortMatches.Where(t => !IsProjectionInternal(t.FullName)).ToList();
        if (candidates.Count == 0)
        {
            candidates = shortMatches;
        }

        var distinct = candidates
            .GroupBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (distinct.Count > 1)
        {
            // Restricted to an actual WinUI-3-vs-UWP twin set: every candidate must be
            // Microsoft.*/Windows.* and exactly one Microsoft.*. A broader "prefer
            // Microsoft.*" rule would silently answer for the Microsoft type when a
            // third-party package ships a same-named control, which is the very bug
            // this ambiguity check exists to prevent.
            bool twinSet = distinct.All(t =>
                t.FullName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                t.FullName.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase));
            if (twinSet)
            {
                var modern = distinct
                    .Where(t => t.FullName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (modern.Count == 1)
                {
                    return (modern[0], null);
                }
            }
        }

        return distinct.Count switch
        {
            1 => (distinct[0], null),
            > 1 => (null, distinct),
            _ => (null, null),
        };
    }

    /// <summary>
    /// Message for a short name that matches several types, listing every candidate so
    /// the caller can re-run with a fully-qualified name.
    /// </summary>
    private static string AmbiguousTypeMessage(string typeName, List<WinMdTypeInfo> candidates) =>
        $"'{typeName}' is ambiguous — {candidates.Count} indexed types share that name: " +
        string.Join(", ", candidates
            .Select(c => $"'{c.FullName}'")
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) +
        ". Re-run with the fully-qualified type name.";

    private static List<(WinMdMemberInfo Member, string? DeclaringType)> CollectMembersWithInheritance(
        WinMdTypeInfo type, List<WinMdTypeInfo> allTypes)
    {
        var result = new List<(WinMdMemberInfo Member, string? DeclaringType)>();

        // Keyed by kind + full signature, not by name: a base type's overload set
        // (ListViewBase.ScrollIntoView(object) and ScrollIntoView(object, ScrollIntoViewAlignment))
        // collapses to a single arbitrary entry under a name-only key, so a derived type
        // such as GridView silently loses every overload but one. Signature keying also
        // still hides a true override, because an override repeats the base signature
        // verbatim and the derived member is added first.
        var seenSignatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in type.Members)
        {
            result.Add((m, type.FullName));
            seenSignatures.Add(MemberDedupKey(m));
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
                if (seenSignatures.Add(MemberDedupKey(m)))
                {
                    result.Add((m, baseType.FullName));
                }
            }
            baseTypeName = baseType.BaseType;
        }
        return result;
    }

    private static string MemberDedupKey(WinMdMemberInfo member) =>
        member.Kind + "|" + member.Signature;

    /// <summary>
    /// Collapses an empty suggestion list to <see langword="null"/> so it is omitted
    /// from JSON rather than serialized as <c>[]</c>. Empty arrays measured ~10% of a
    /// batched <c>check-property</c> payload while carrying no information.
    /// </summary>
    private static List<T>? NullIfEmpty<T>(List<T> list) => list.Count > 0 ? list : null;

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

    /// <summary>
    /// Describes any packages in the scope whose index is known to be partial (a
    /// metadata file failed to parse), or <see langword="null"/> when the index is
    /// whole. Negative answers are qualified with this: from a partial index,
    /// "no such type/property" would otherwise be indistinguishable from "that
    /// package was never read", which is exactly the false negative a caller
    /// validating an API before writing code must not act on.
    /// </summary>
    private static string? IncompleteIndexNote(string cacheDir, ProjectManifest manifest)
    {
        var incomplete = new List<string>();
        foreach (ProjectPackageRef package in manifest.Packages)
        {
            string metaPath = Path.Combine(cacheDir, "packages", package.Id, package.Version, "meta.json");
            if (!File.Exists(metaPath))
            {
                continue;
            }
            PackageMeta? meta = Deserialize(metaPath, ApiSearchJsonContext.Default.PackageMeta);
            if (meta is { Incomplete: true })
            {
                incomplete.Add(package.Id);
            }
        }
        if (incomplete.Count == 0)
        {
            return null;
        }
        return $"The index is incomplete — metadata for {string.Join(", ", incomplete)} could not be fully read, " +
            "so this result may be a false negative. Run 'winapp find-api refresh' to rebuild it.";
    }

    /// <summary>Appends the incomplete-index note to a negative answer, when there is one.</summary>
    private static string WithIncompleteNote(string message, string cacheDir, ProjectManifest manifest)
    {
        string? note = IncompleteIndexNote(cacheDir, manifest);
        return note is null ? message : message + " " + note;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or corrupt cache file reads as "no data" so the caller can
            // fall back to reindexing instead of crashing.
            return null;
        }
    }

    private static ProjectManifest? DeserializeManifest(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ApiSearchJsonContext.Default.ProjectManifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or corrupt manifest reads as "no manifest" so the caller can
            // report an unindexed project instead of crashing.
            return null;
        }
    }
}
