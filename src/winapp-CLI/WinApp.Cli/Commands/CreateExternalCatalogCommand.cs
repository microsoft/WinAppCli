// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class CreateExternalCatalogCommand : Command
{
    public static Argument<string> InputFolderArgument { get; }
    public static Option<bool> RecursiveOption { get; }
    public static Option<bool> UsePageHashesOption { get; }
    public static Option<bool> ComputeFlatHashesOption { get; }
    public static Option<IfExists> IfExistsOption { get; }
    public static Option<FileInfo?> OutputOption { get; }

    static CreateExternalCatalogCommand()
    {
        InputFolderArgument = new Argument<string>("input-folder")
        {
            Description = "List of input folders with executable files to process (separated by semicolons)"
        };
        RecursiveOption = new Option<bool>("--recursive", "-r")
        {
            Description = "Include files from subdirectories"
        };
        UsePageHashesOption = new Option<bool>("--use-page-hashes")
        {
            Description = "Include page hashes when generating the catalog"
        };
        ComputeFlatHashesOption = new Option<bool>("--compute-flat-hashes")
        {
            Description = "Include flat hashes when generating the catalog"
        };
        IfExistsOption = new Option<IfExists>("--if-exists")
        {
            Description = "Behavior when an CodeIntegrityExternal.cat file already exists",
            DefaultValueFactory = _ => IfExists.Error
        };
        OutputOption = new Option<FileInfo?>("--output", "-o")
        {
            Description = "Output catalog file path. If not specified, the default CodeIntegrityExternal.cat name is used."
        };
        OutputOption.AcceptLegalFilePathsOnly();
    }

    public CreateExternalCatalogCommand() : base("create-external-catalog", "Generates CodeIntegrityExternal.cat catalog file with hashes of executable file from a given directory.")
    {
        Arguments.Add(InputFolderArgument);
        Options.Add(RecursiveOption);
        Options.Add(UsePageHashesOption);
        Options.Add(ComputeFlatHashesOption);
        Options.Add(IfExistsOption);
        Options.Add(OutputOption);
    }

    public class Handler(ICodeIntegrityCatalogService externalCatalogService, ICurrentDirectoryProvider currentDirectoryProvider, ILogger<CreateExternalCatalogCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var directories = parseResult.GetValue(InputFolderArgument)!;
            var inputDirectories = new List<string>();
            inputDirectories.AddRange(directories.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            var recursive = parseResult.GetValue(RecursiveOption);
            var usePageHashes = parseResult.GetValue(UsePageHashesOption);
            var computeFlatHashes = parseResult.GetValue(ComputeFlatHashesOption);
            var ifExists = parseResult.GetRequiredValue(IfExistsOption);
            var output = parseResult.GetValue(OutputOption) ?? new FileInfo(Path.Combine(currentDirectoryProvider.GetCurrentDirectory(), CodeIntegrityCatalogService.DefaultCatalogFileName));

            try
            {
                logger.LogInformation("{UISymbol} Generating CodeIntegrityExternal.cat for directory: {Directory}", UiSymbols.Info, directories);

                await externalCatalogService.CreateExternalCatalog(inputDirectories, recursive, usePageHashes, computeFlatHashes, ifExists, output);

                logger.LogInformation("{UISymbol} {ExternalCatalog} was generated successfully.", UiSymbols.Check, output.FullName);
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error generating {ExternalCatalog}: {ErrorMessage}", UiSymbols.Error, output.FullName, ex.Message);
                return 1;
            }
        }
    }
}
