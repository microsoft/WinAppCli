<!-- mslearn: true -->
# Getting Started: Adding Windows APIs to Your Electron App

This guide walks you through adding Windows-native capabilities to an Electron application using the Windows App Development CLI. You'll learn how to call modern Windows APIs from your Electron app, test with app identity, and package for distribution.

## What You'll Build

By the end of this guide, you'll have an Electron app that:
- ✅ Calls modern Windows APIs (Windows SDK and Windows App SDK)
- ✅ Uses a native addon with AI capabilities (Phi Silica or WinML)
- ✅ Runs with app identity for testing protected APIs
- ✅ Packages as a signed MSIX for distribution

## Prerequisites

Before starting, ensure you have:

- **Windows 11** (Copilot+ PC if using Phi Silica)
- **Node.js** - `winget install OpenJS.NodeJS --source winget`
- **.NET SDK v10** - `winget install Microsoft.DotNet.SDK.10 --source winget`
- **Visual Studio with the Native Desktop Workload** - `winget install --id Microsoft.VisualStudio.Community --source winget --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --includeRecommended --passive --wait"`

## The Process

Building a Windows-enabled Electron app involves three main phases:

### 1. [Setting Up the Development Environment](setup.md)

First, you'll set up your development environment with the necessary tools and SDKs. This includes:
- Creating or configuring an Electron app
- Installing winapp CLI
- Initializing Windows SDKs and required assets
- Setting up your build pipeline

[Get Started with Setup →](setup.md)

### 2. Calling Windows APIs

Next, choose how to call Windows APIs from your Electron app:

#### Option A: [JS/TypeScript bindings via dynwinrt](../../js-bindings.md) ✨ *new*

The simplest path — typed JS/TypeScript wrappers generated from `.winmd` metadata, no native build step required from your Electron project. One command (`npx winapp node jsbindings add --ai`) drops a `bindings/winrt/` directory next to your sources; you `import { ChatClient } from './bindings/winrt'` and call WinRT directly. Bindings are typed at compile time but use `dynwinrt`'s libffi runtime to invoke methods at runtime, so no MSBuild / `node-gyp` step is involved.

[Add JS bindings →](../../js-bindings.md)

> Native addons (Options B–D below) are still the right choice when you need C++/C# code paths — for instance, to encapsulate a stateful service or to use APIs `dynwinrt` doesn't yet drive (XAML / DispatcherQueue). For data-style WinRT APIs, jsBindings is the easier on-ramp.

#### Option B: [Creating a C++ Notification Addon](cpp-notification-addon.md)
Learn how to create a C++ addon that calls the Windows App SDK notification APIs. This is a great starting point for understanding native addons before diving into more complex scenarios.

[Create a C++ Notification Addon →](cpp-notification-addon.md)

#### Option C: [Creating a Phi Silica Addon](phi-silica-addon.md)
Learn how to create a C# addon that uses the Phi Silica AI model to summarize text on-device. Phi Silica is a small language model that runs locally on Windows 11 devices with NPUs.

[Create a Phi Silica Addon →](phi-silica-addon.md)

#### Option D: [Creating a WinML Addon](winml-addon.md)
Learn how to create a C# addon that uses Windows Machine Learning (WinML) to run custom ONNX models for image classification, object detection, and more.

[Create a WinML Addon →](winml-addon.md)

### 3. [Packaging for Distribution](packaging.md)

Finally, you'll package your app as an MSIX for distribution. This includes:
- Building your app for production
- Creating and signing an MSIX package
- Testing the installed package
- Understanding distribution options

[Package Your App →](packaging.md)

## Quick Navigation

| Phase | Guide | What You'll Learn |
|-------|-------|-------------------|
| 1️⃣ | [Setup](setup.md) | Install tools, initialize SDKs, configure build pipeline |
| 2️⃣ | [JS bindings (dynwinrt)](../../js-bindings.md) | Generate typed JS/TS WinRT wrappers, no native build step |
| 2️⃣ | [C++ Notification Addon](cpp-notification-addon.md) | Create C++ addon, call notification APIs, test with debug identity |
| 2️⃣ | [Phi Silica Addon](phi-silica-addon.md) | Create C# addon, call AI APIs, test with debug identity |
| 2️⃣ | [WinML Addon](winml-addon.md) | Create C# addon, call WinML APIs, run ONNX models, integrate ML |
| 3️⃣ | [Packaging](packaging.md) | Build production app, create MSIX, distribute |

## Additional Resources

- **[winapp CLI Documentation](../../usage.md)** - Full CLI reference
- **[Sample Electron App](../../../samples/electron/)** - Complete working example
- **[AI Dev Gallery](https://aka.ms/aidevgallery)** - Sample gallery of all AI APIs 
- **[Windows App SDK Samples](https://github.com/microsoft/WindowsAppSDK-Samples)** - Collection of Windows App SDK samples
- **[node-api-dotnet](https://github.com/microsoft/node-api-dotnet)** - C# ↔ JavaScript interop library

## Get Help

- **Found a bug?** [File an issue](https://github.com/microsoft/WinAppCli/issues)
