# .NET MAUI Sample Application

This sample is a real .NET MAUI Windows project (similar to how `samples/dotnet-app` is a real .NET project) plus a Pester validation script for the MAUI + `winapp` packaging flow.

Project files include:

- `maui-app.csproj`
- `App.xaml`, `AppShell.xaml`, `MainPage.xaml` (+ code-behind)
- `Platforms/Windows/Package.appxmanifest` (with MAUI `$placeholder$` tokens)
- `Resources/*` assets used by MAUI resizetizer

The test script validates the workflow documented in [docs/guides/maui.md](../../docs/guides/maui.md):

1. from-scratch MAUI project packaging flow;
2. existing sample project packaging flow;
3. generated resizetizer manifest usage with `winapp package --manifest ... --executable ...`;
4. unpackaged executable signing via `winapp sign`.

## Run locally

```powershell
.\scripts\test-samples.ps1 -Samples maui-app
```

If needed, point to a local winapp npm artifact:

```powershell
.\scripts\test-samples.ps1 -Samples maui-app -WinappPath .\artifacts\npm -Verbose
```
