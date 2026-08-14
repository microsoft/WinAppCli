// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Security.Cryptography;

namespace WinApp.Cli.Services;

/// <summary>
/// Extracts the embedded WinUI analyzer DLL and its MSBuild hook props into a
/// per-user, content-hash-keyed cache. See <see cref="IAnalyzerInjectionService"/>.
/// </summary>
internal sealed class AnalyzerInjectionService(IWinappDirectoryService winappDirectoryService)
    : IAnalyzerInjectionService
{
    /// <summary>LogicalName of the analyzer DLL embedded by WinApp.Cli.csproj.</summary>
    internal const string AnalyzerResourceName = "Microsoft.WindowsAppSDK.Analyzers.dll";

    /// <summary>File name of the extracted analyzer DLL in the cache.</summary>
    internal const string AnalyzerDllFileName = "Microsoft.WindowsAppSDK.Analyzers.dll";

    /// <summary>File name of the generated MSBuild hook props in the cache.</summary>
    internal const string HookPropsFileName = "winapp-winui-analyzer.props";

    /// <summary>Cache sub-directory (under the global .winapp directory) for analyzer assets.</summary>
    internal const string CacheSubdirectory = "winui-analyzer";

    private readonly IWinappDirectoryService _winappDirectoryService = winappDirectoryService;

    public AnalyzerInjection? PrepareInjection()
    {
        byte[]? analyzerBytes = ReadEmbeddedAnalyzer();
        if (analyzerBytes is null)
        {
            return null;
        }

        string contentHash = Convert.ToHexStringLower(SHA256.HashData(analyzerBytes));

        DirectoryInfo cacheDir = new(Path.Combine(
            _winappDirectoryService.GetGlobalWinappDirectory().FullName,
            CacheSubdirectory,
            contentHash));

        string analyzerDllPath = Path.Combine(cacheDir.FullName, AnalyzerDllFileName);
        string hookPropsPath = Path.Combine(cacheDir.FullName, HookPropsFileName);

        // Content-hash cache: if both artifacts already exist for this hash, reuse them.
        if (!File.Exists(analyzerDllPath) || !File.Exists(hookPropsPath))
        {
            cacheDir.Create();
            WriteFileAtomic(analyzerDllPath, analyzerBytes);
            WriteFileAtomic(hookPropsPath, System.Text.Encoding.UTF8.GetBytes(BuildHookProps()));
        }

        return new AnalyzerInjection(hookPropsPath, analyzerDllPath, contentHash);
    }

    private static byte[]? ReadEmbeddedAnalyzer()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        using Stream? stream = asm.GetManifestResourceStream(AnalyzerResourceName);
        if (stream is null)
        {
            return null;
        }

        using MemoryStream ms = new();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// The injected MSBuild hook. Adds the analyzer as a restore-invisible
    /// <c>&lt;Analyzer&gt;</c> item (picked up at CoreCompile, so it needs no restore),
    /// surfaces XAML files to the analyzer the way the analyzer package's own
    /// .targets does, and re-chains any pre-existing CustomAfterMicrosoftCommonTargets
    /// the caller threads in via <c>-p:_WinAppChainedCustomAfter=</c>.
    /// </summary>
    internal static string BuildHookProps() =>
        """
        <Project>
          <!--
            Injected by 'winapp run' to surface WinUI analyzer diagnostics (issue #634).
            This file lives in the per-user winapp cache and is threaded in via
            -p:CustomAfterMicrosoftCommonTargets= on the build pass only.
          -->
          <ItemGroup>
            <Analyzer Include="$(MSBuildThisFileDirectory)Microsoft.WindowsAppSDK.Analyzers.dll" />
          </ItemGroup>

          <!-- Surface XAML files as AdditionalFiles so the XAML rules can inspect them
               (mirrors Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer's packaged .targets). -->
          <Target Name="WinAppInjectXamlFilesForAnalyzer" BeforeTargets="CoreCompile">
            <ItemGroup>
              <AdditionalFiles Include="@(Page)" Condition="'%(Extension)' == '.xaml'" />
              <AdditionalFiles Include="@(ApplicationDefinition)" Condition="'%(Extension)' == '.xaml'" />
            </ItemGroup>
          </Target>

          <!-- Re-chain the user's original CustomAfterMicrosoftCommonTargets, which the
               global -p: injection would otherwise shadow. The value is passed
               out-of-band as -p:_WinAppChainedCustomAfter=<original>. -->
          <Import Project="$(_WinAppChainedCustomAfter)"
                  Condition="'$(_WinAppChainedCustomAfter)' != '' and Exists('$(_WinAppChainedCustomAfter)')" />
        </Project>
        """;

    private static void WriteFileAtomic(string destinationPath, byte[] bytes)
    {
        // Write to a unique temp file in the same directory, then move into place so a
        // concurrent winapp run never observes a half-written file.
        string tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        try
        {
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            // Another process won the race and wrote identical (content-hashed) bytes.
            File.Delete(tempPath);
        }
    }
}
