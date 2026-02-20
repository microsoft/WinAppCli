// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class MSStoreCommand : Command, IShortDescription
{
    public string ShortDescription => "Run a Microsoft Store Developer CLI command";

    public MSStoreCommand() : base("store", "Run a Microsoft Store Developer CLI command. This command will download the Microsoft Store Developer CLI if not already downloaded. Learn more about the Microsoft Store Developer CLI here: https://aka.ms/msstoredevcli")
    {
        this.TreatUnmatchedTokensAsErrors = false;
    }

    public class Handler(IMSStoreCLIService msStoreCLIService, ILogger<MSStoreCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var args = parseResult.UnmatchedTokens.ToArray();

            try
            {
                await msStoreCLIService.EnsureMSStoreCLIAvailableAsync(cancellationToken: cancellationToken);

                var processStartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = msStoreCLIService.GetMSStoreCLIPath(),
                    Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                using var process = System.Diagnostics.Process.Start(processStartInfo);
                if (process == null)
                {
                    logger.LogError("Failed to start process for MSStoreCLI.");
                    return 1;
                }

                await process.WaitForExitAsync(cancellationToken);
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                logger.LogError("Error executing MSStoreCLI: {ErrorMessage}", ex.Message);
                return 1;
            }
        }
    }
}
