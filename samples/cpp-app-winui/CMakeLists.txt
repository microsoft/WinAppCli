cmake_minimum_required(VERSION 3.20)
project(cpp-app-winui)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Download winapp CLI if not available in PATH
find_program(WINAPP_CLI winapp)
if(NOT WINAPP_CLI)
    set(WINAPP_DIR "${CMAKE_CURRENT_SOURCE_DIR}/.winapp-tools")
    set(WINAPP_CLI "${WINAPP_DIR}/winapp.exe")
    
    if(NOT EXISTS "${WINAPP_CLI}")
        message(STATUS "Downloading winapp CLI...")
        
        # Determine architecture
        if(CMAKE_SYSTEM_PROCESSOR MATCHES "ARM64|aarch64")
            set(WINAPP_ARCH "arm64")
        else()
            set(WINAPP_ARCH "x64")
        endif()
        
        # Download and extract
        set(WINAPP_ZIP "${CMAKE_CURRENT_BINARY_DIR}/winappcli.zip")
        file(DOWNLOAD 
            "https://github.com/microsoft/WinAppCli/releases/latest/download/winappcli-${WINAPP_ARCH}.zip"
            "${WINAPP_ZIP}"
            SHOW_PROGRESS
        )
        
        file(ARCHIVE_EXTRACT INPUT "${WINAPP_ZIP}" DESTINATION "${WINAPP_DIR}")
        file(REMOVE "${WINAPP_ZIP}")
        message(STATUS "winapp CLI downloaded to ${WINAPP_DIR}")
    endif()
endif()

# Automatically restore Windows App SDK headers and generate certificate if needed
# This runs once during CMake configuration, not on every build
if(NOT EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/.winapp/include")
    message(STATUS "Restoring Windows App SDK headers...")
    execute_process(
        COMMAND "${WINAPP_CLI}" restore
        WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
        RESULT_VARIABLE RESTORE_RESULT
    )
    if(NOT RESTORE_RESULT EQUAL 0)
        message(WARNING "Failed to restore Windows App SDK. Run 'winapp restore' manually.")
    endif()
endif()

if(NOT EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/devcert.pfx")
    message(STATUS "Generating development certificate...")
    execute_process(
        COMMAND "${WINAPP_CLI}" cert generate --if-exists skip
        WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
        RESULT_VARIABLE CERT_RESULT
    )
    if(NOT CERT_RESULT EQUAL 0)
        message(WARNING "Failed to generate certificate. Run 'winapp cert generate' manually.")
    endif()
endif()

# WinUI 3 windowed application (WIN32 = Windows subsystem, no console)
add_executable(cpp-app-winui WIN32 main.cpp)

# C++/WinRT + WinUI headers are large; need /bigobj for MSVC
if(MSVC)
    target_compile_options(cpp-app-winui PRIVATE /bigobj)
endif()

# Link Windows Runtime and bootstrapper libraries
target_link_libraries(cpp-app-winui PRIVATE WindowsApp.lib OneCoreUap.lib Microsoft.WindowsAppRuntime.Bootstrap.lib)

# Add Windows App SDK include and lib directories
target_include_directories(cpp-app-winui PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/.winapp/include)

# Determine build architecture for lib/bin paths
if(CMAKE_SYSTEM_PROCESSOR MATCHES "ARM64|aarch64")
    set(WINAPP_LIB_ARCH "arm64")
else()
    set(WINAPP_LIB_ARCH "x64")
endif()

target_link_directories(cpp-app-winui PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/.winapp/lib/${WINAPP_LIB_ARCH})

# Embed the application manifest for DPI awareness
if(MSVC)
    target_sources(cpp-app-winui PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/app.manifest")
endif()

# Copy Windows App SDK runtime DLLs to the build output directory
set(WINAPP_BIN_DIR "${CMAKE_CURRENT_SOURCE_DIR}/.winapp/bin/${WINAPP_LIB_ARCH}")
if(EXISTS "${WINAPP_BIN_DIR}")
    add_custom_command(TARGET cpp-app-winui POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${WINAPP_BIN_DIR}"
            "$<TARGET_FILE_DIR:cpp-app-winui>"
        COMMENT "Copying Windows App SDK runtime DLLs..."
    )
endif()
