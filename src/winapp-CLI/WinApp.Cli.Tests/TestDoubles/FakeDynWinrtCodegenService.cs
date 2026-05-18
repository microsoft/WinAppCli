// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests.TestDoubles;

// In-memory IDynWinrtCodegenService — orchestration tests use it instead
// of spawning the real codegen binary.
internal sealed class FakeDynWinrtCodegenService : IDynWinrtCodegenService
{
    public List<CallRecord> Calls { get; } = new();

    // When non-null, RunAsync throws after recording the call.
    public Exception? FailWith { get; set; }

    // Stub files written into the output dir on success.
    public IReadOnlyDictionary<string, string> StubFilesPerCall { get; set; }
        = new Dictionary<string, string> { ["index.js"] = "// fake codegen output" };

    public Task<DirectoryInfo> RunAsync(
        JsBindingsConfig config,
        IReadOnlyList<FileInfo> winmds,
        FileInfo? windowsSdkWinmd,
        DirectoryInfo workspaceDir,
        DirectoryInfo winappDir,
        TaskContext taskContext,
        IReadOnlyList<FileInfo>? userAdditionalWinmds = null,
        IReadOnlyList<FileInfo>? userAdditionalRefs = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new CallRecord
        {
            Config = config,
            EmitWinmds = winmds.Select(f => f.FullName).ToArray(),
            UserAdditionalWinmds = (userAdditionalWinmds ?? Array.Empty<FileInfo>()).Select(f => f.FullName).ToArray(),
            UserAdditionalRefs = (userAdditionalRefs ?? Array.Empty<FileInfo>()).Select(f => f.FullName).ToArray(),
            WorkspaceDir = workspaceDir.FullName,
            WinappDir = winappDir.FullName,
        });

        if (FailWith is not null)
        {
            throw FailWith;
        }

        // Mirror the real success contract: output dir + stub files + marker.
        var outputDir = DynWinrtCodegenService.ResolveOutputDir(workspaceDir, config.Output);
        outputDir.Create();
        foreach (var (relPath, content) in StubFilesPerCall)
        {
            var fullPath = Path.Combine(outputDir.FullName, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }
        File.WriteAllText(
            Path.Combine(outputDir.FullName, DynWinrtCodegenService.ManagedMarkerFileName),
            "# fake managed marker\n");
        return Task.FromResult(outputDir);
    }

    public sealed class CallRecord
    {
        public JsBindingsConfig Config { get; set; } = null!;
        public string[] EmitWinmds { get; set; } = Array.Empty<string>();
        public string[] UserAdditionalWinmds { get; set; } = Array.Empty<string>();
        public string[] UserAdditionalRefs { get; set; } = Array.Empty<string>();
        public string WorkspaceDir { get; set; } = string.Empty;
        public string WinappDir { get; set; } = string.Empty;
    }
}
