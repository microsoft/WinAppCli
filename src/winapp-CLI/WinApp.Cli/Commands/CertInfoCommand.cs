// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal class CertInfoCommand : Command, IShortDescription
{
    public string ShortDescription => "Display certificate details.";
    public static Argument<FileInfo> CertPathArgument { get; }
    public static Option<string> PasswordOption { get; }

    static CertInfoCommand()
    {
        CertPathArgument = new Argument<FileInfo>("cert-path")
        {
            Description = "Path to the certificate file (PFX)"
        };
        CertPathArgument.AcceptExistingOnly();
        PasswordOption = new Option<string>("--password")
        {
            Description = "Password for the PFX file",
            DefaultValueFactory = (argumentResult) => "password",
        };
    }

    public CertInfoCommand()
        : base("info", "Display certificate details (subject, thumbprint, expiry). Useful for verifying a certificate matches your manifest before signing.")
    {
        Arguments.Add(CertPathArgument);
        Options.Add(PasswordOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var certPath = parseResult.GetRequiredValue(CertPathArgument);
            var password = parseResult.GetRequiredValue(PasswordOption);
            var json = parseResult.GetRequiredValue(WinAppRootCommand.JsonOption);

            certPath.Refresh();
            if (!certPath.Exists)
            {
                if (json)
                {
                    return Task.FromResult(JsonErrorOutput.Write($"Certificate file not found: {certPath}"));
                }
                Console.Error.WriteLine($"{UiSymbols.Error} Certificate file not found: {certPath}");
                return Task.FromResult(1);
            }

            try
            {
                using var cert = X509CertificateLoader.LoadPkcs12FromFile(
                    certPath.FullName, password, X509KeyStorageFlags.Exportable);

                if (json)
                {
                    var jsonOutput = new CertInfoJsonOutput
                    {
                        Subject = cert.Subject,
                        Issuer = cert.Issuer,
                        Thumbprint = cert.Thumbprint,
                        SerialNumber = cert.SerialNumber,
                        NotBefore = cert.NotBefore.ToString("yyyy-MM-dd HH:mm:ss"),
                        NotAfter = cert.NotAfter.ToString("yyyy-MM-dd HH:mm:ss"),
                        HasPrivateKey = cert.HasPrivateKey,
                    };
                    Console.WriteLine(JsonSerializer.Serialize(jsonOutput, WinAppJsonContext.Default.CertInfoJsonOutput));
                }
                else
                {
                    Console.WriteLine($"Subject:         {cert.Subject}");
                    Console.WriteLine($"Issuer:          {cert.Issuer}");
                    Console.WriteLine($"Thumbprint:      {cert.Thumbprint}");
                    Console.WriteLine($"Serial Number:   {cert.SerialNumber}");
                    Console.WriteLine($"Not Before:      {cert.NotBefore:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"Not After:       {cert.NotAfter:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"Has Private Key: {cert.HasPrivateKey}");
                }

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                if (json)
                {
                    return Task.FromResult(JsonErrorOutput.Write($"Failed to read certificate: {ex.Message}"));
                }
                Console.Error.WriteLine($"{UiSymbols.Error} Failed to read certificate: {ex.Message}");
                return Task.FromResult(1);
            }
        }
    }
}
