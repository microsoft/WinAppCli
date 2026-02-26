// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography.Catalog;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.System.SystemServices;

namespace WinApp.Cli.Services;

internal class CodeIntegrityCatalogService(ILogger<CodeIntegrityCatalogService> logger) : ICodeIntegrityCatalogService
{
    public const string DefaultCatalogFileName = "CodeIntegrityExternal.cat";
    public const string CatalogFileExtension = ".cat";
    private const string CatalogVersion = "2";
    private const string PublicVersion = "1";
    private const string HashAlgorithms = "SHA256";
    private const string CatAttr1 = "0x10010001:OSAttr:2:6.2";
    private static readonly byte[] SECTION_NAME_TEXT = { (byte)'.', (byte)'t', (byte)'e', (byte)'x', (byte)'t', 0, 0, 0 };

    [ThreadStatic]
    private static ILogger? t_callbackLogger;

    public static T ReadBytes<T>(BinaryReader reader) where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        Span<byte> buffer = stackalloc byte[size];
        var bytesRead = reader.Read(buffer);

        if (bytesRead != size)
        {
            throw new EndOfStreamException($"Unable to read {size} bytes for {typeof(T).Name}.");
        }

        return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(buffer));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ParseErrorCallback(uint errorArea, uint localError, PWSTR line)
    {
        string lineStr = line.ToString();

        string areaDescription = errorArea switch
        {
            PInvoke.CRYPTCAT_E_AREA_HEADER => "The header section of the CDF",
            PInvoke.CRYPTCAT_E_AREA_MEMBER => "A member file entry in the CatalogFiles section of the CDF",
            PInvoke.CRYPTCAT_E_AREA_ATTRIBUTE => "An attribute entry in the CDF",
            _ => $"Unknown ({errorArea})"
        };

        string errorDescription = localError switch
        {
            PInvoke.CRYPTCAT_E_CDF_MEMBER_FILE_PATH => "The member file name or path is missing.",
            PInvoke.CRYPTCAT_E_CDF_MEMBER_INDIRECTDATA => "The function failed to create a hash of the member subject.",
            PInvoke.CRYPTCAT_E_CDF_MEMBER_FILENOTFOUND => "The function failed to find the member file.",
            PInvoke.CRYPTCAT_E_CDF_BAD_GUID_CONV => "The function failed to convert the subject string to a GUID.",
            PInvoke.CRYPTCAT_E_CDF_ATTR_TYPECOMBO => "The attribute contains an invalid OID, or the combination of type, name or OID, and value is not valid.",
            PInvoke.CRYPTCAT_E_CDF_ATTR_TOOFEWVALUES => "The attribute line is missing one or more elements of its composition including type, object identifier (OID) or name, or value.",
            PInvoke.CRYPTCAT_E_CDF_UNSUPPORTED => "The function does not support the attribute.",
            PInvoke.CRYPTCAT_E_CDF_DUPLICATE => "The file member already exists.",
            PInvoke.CRYPTCAT_E_CDF_TAGNOTFOUND => "The CatalogHeader or Name tag is missing.",
            _ => $"Unknown ({localError})"
        };

        t_callbackLogger?.LogError("CDF Parsing Error - Area: {ErrorArea} : {AreaDescription}, Error: {LocalError} : {ErrorDescription}, Line: {Line}",
            errorArea, areaDescription, localError, errorDescription, lineStr);
    }

    private List<string> CollectExecutableFiles(IReadOnlyCollection<string> directories, SearchOption searchOption)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(directory);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"Directory not found: {fullPath}");
            }

            foreach (var file in Directory.EnumerateFiles(fullPath, "*.*", searchOption))
            {
                if (IsExecutable(file))
                {
                    logger.LogInformation("{UISymbol} Adding executable file: {File}", UiSymbols.Info, file);
                    files.Add(file);
                }
            }
        }
        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsExecutable(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        if (stream.Length < Unsafe.SizeOf<IMAGE_DOS_HEADER>())
        {
            return false;
        }

        var dosHeader = ReadBytes<IMAGE_DOS_HEADER>(reader);
        if (dosHeader.e_magic != PInvoke.IMAGE_DOS_SIGNATURE)
        {
            return false;
        }

        if ((dosHeader.e_lfanew <= 0) || (dosHeader.e_lfanew > stream.Length - 4))
        {
            return false;
        }

        stream.Position = dosHeader.e_lfanew;
        var signature = reader.ReadUInt32();
        if (signature != PInvoke.IMAGE_NT_SIGNATURE)
        {
            return false;
        }

        if (stream.Length < (stream.Position + Unsafe.SizeOf<IMAGE_FILE_HEADER>()))
        {
            return false;
        }

        var header = ReadBytes<IMAGE_FILE_HEADER>(reader);

        if (stream.Length < stream.Position + header.SizeOfOptionalHeader)
        {
            return false;
        }

        // IMAGE_SECTION_HEADERs are after the optional header.
        stream.Position += header.SizeOfOptionalHeader;

        if (stream.Length < (stream.Position + (header.NumberOfSections * Unsafe.SizeOf<IMAGE_SECTION_HEADER>())))
        {
            return false;
        }

        for (var i = 0; i < header.NumberOfSections; i++)
        {
            var sectionHeader = ReadBytes<IMAGE_SECTION_HEADER>(reader);
            if ((sectionHeader.Characteristics & (IMAGE_SECTION_CHARACTERISTICS.IMAGE_SCN_CNT_CODE | IMAGE_SECTION_CHARACTERISTICS.IMAGE_SCN_MEM_EXECUTE)) != 0)
            {
                return true;
            }
            unsafe
            {
                var nameSpan = new ReadOnlySpan<byte>(&sectionHeader.Name, 8);
                if (nameSpan.SequenceEqual(SECTION_NAME_TEXT))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static string CreateCatalogDefinitionFile(string outputCatalogPath, List<string> files, bool usePageHashes, bool computeFlatHashes)
    {
        var cdfPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cdf");
        var builder = new StringBuilder();

        builder.AppendLine("[CatalogHeader]");
        builder.AppendLine($"Name={outputCatalogPath}");
        builder.AppendLine($"PublicVersion={PublicVersion}");
        builder.AppendLine($"CatalogVersion={CatalogVersion}");
        builder.AppendLine($"HashAlgorithms={HashAlgorithms}");
        builder.AppendLine($"PageHashes={(usePageHashes ? "true" : "false")}");
        builder.AppendLine($"CATATTR1={CatAttr1}");
        builder.AppendLine();
        builder.AppendLine("[CatalogFiles]");

        for (var i = 0; i < files.Count; i++)
        {
            builder.AppendLine($"<HASH>{files[i]}={files[i]}");
            if (computeFlatHashes)
            {
                // GUID {DE351A42-8E59-11d0-8C47-00C04FC295EE} corresponds to CRYPT_SUBJTYPE_FLAT_IMAGE
                builder.AppendLine($"<HASH>{files[i]}ALTSIPID={{DE351A42-8E59-11d0-8C47-00C04FC295EE}}");
            }
        }

        File.WriteAllText(cdfPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return cdfPath;
    }

    private static unsafe void EnumerateCdfAttributes(CRYPTCATCDF* cdfHandle)
    {
        CRYPTCATATTRIBUTE* catalogAttr = null;

        while ((catalogAttr = PInvoke.CryptCATCDFEnumCatAttributes(cdfHandle, catalogAttr, &ParseErrorCallback)) != null) { }
    }

    private static unsafe void EnumerateCdfMembers(CRYPTCATCDF* cdfHandle)
    {
        PWSTR memberTag = default;
        CRYPTCATMEMBER* catalogMember = null;

        while ((memberTag = PInvoke.CryptCATCDFEnumMembersByCDFTagEx(cdfHandle, memberTag, &ParseErrorCallback, &catalogMember, true, null)) != default)
        {
            CRYPTCATATTRIBUTE* memberAttr = null;
            while ((memberAttr = PInvoke.CryptCATCDFEnumAttributesWithCDFTag(cdfHandle, memberTag, catalogMember, memberAttr, &ParseErrorCallback)) != null) { }
        }
    }

    public unsafe Task CreateExternalCatalogAsync(List<string> directories, bool recursive, bool usePageHashes, bool computeFlatHashes, IfExists ifExists, FileInfo output)
    {
        string? cdfOutputPath = null;
        return CreateExternalCatalogAsync(directories, recursive, usePageHashes, computeFlatHashes, ifExists, output, ref cdfOutputPath);
    }

    public unsafe Task CreateExternalCatalogAsync(List<string> directories, bool recursive, bool usePageHashes, bool computeFlatHashes, IfExists ifExists, FileInfo output, ref string? cdfOutputPath)
    {
        if ((directories == null) || (directories.Count == 0))
        {
            throw new ArgumentException("At least one directory path must be provided.", nameof(directories));
        }

        var outputCatalogPath = output.FullName;
        if (File.Exists(outputCatalogPath))
        {
            if (ifExists == IfExists.Skip)
            {
                logger.LogInformation("{UISymbol} Output catalog already exists, skipping generation: {File}", UiSymbols.Info, outputCatalogPath);
                return Task.CompletedTask;
            }
            if (ifExists == IfExists.Error)
            {
                throw new IOException($"Output catalog already exists: {outputCatalogPath}");
            }
        }

        logger.LogInformation("{UISymbol} Collecting executable files...", UiSymbols.Info);
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = CollectExecutableFiles(directories, searchOption);

        if (files.Count == 0)
        {
            throw new InvalidOperationException("No executable files were found in the specified directories.");
        }

        logger.LogInformation("{UISymbol} Generating {File}...", UiSymbols.Info, outputCatalogPath);

        var cdfPath = CreateCatalogDefinitionFile(outputCatalogPath, files, usePageHashes, computeFlatHashes);

        t_callbackLogger = logger;
        try
        {
            fixed (char* pCdfPath = cdfPath)
            {
                var cdfHandle = PInvoke.CryptCATCDFOpen(pCdfPath, &ParseErrorCallback);

                if (cdfHandle == null)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open the generated CDF.");
                }

                try
                {
                    EnumerateCdfAttributes(cdfHandle);
                    EnumerateCdfMembers(cdfHandle);
                }
                finally
                {
                    PInvoke.CryptCATCDFClose(cdfHandle);
                }
            }
        }
        catch (Exception)
        {
            cdfOutputPath = null;
            throw;
        }
        finally
        {
            t_callbackLogger = null;
            if (cdfOutputPath == null)
            {
                try
                {
                    File.Delete(cdfPath);
                }
                catch { }
            }
            else
            {
                cdfOutputPath = cdfPath;
            }
        }

        return Task.CompletedTask;
    }
}
