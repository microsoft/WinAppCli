{
  "targets": [
    {
      "target_name": "{addon-name}",
      "sources": ["{addon-name}.cc"],
      "include_dirs": [
        "<!@(node -p \"require('node-addon-api').include\")",
        "<!(node -e \"require('nan')\")",
        "<(module_root_dir)/../.winapp/include"
      ],
      "msvs_settings": {
        "VCCLCompilerTool": {
          "ExceptionHandling": 1,
          "DebugInformationFormat": 1,
          "AdditionalOptions": [
            "/FS"
          ]
        },
        "VCLinkerTool": {
          "GenerateDebugInformation": "true"
        }
      },
      "defines": [
        "NODE_ADDON_API_CPP_EXCEPTIONS",
        "WINVER=0x0A00",
        "_WIN32_WINNT=0x0A00"
      ],
      "library_dirs": [
        "<(module_root_dir)/../.winapp/lib/<(target_arch)",
        "../build/<(target_arch)/Release"
      ],
      "libraries": [
        "comctl32.lib",
        "shcore.lib",
        "WindowsApp.lib",
        "Microsoft.WindowsAppRuntime.Bootstrap.lib"
      ],
      "dependencies": [
        "<!(node -p \"require('node-addon-api').gyp\")"
      ],
    }
  ]
}