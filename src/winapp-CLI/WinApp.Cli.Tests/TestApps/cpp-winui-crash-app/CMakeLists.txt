cmake_minimum_required(VERSION 3.20)
project(cpp-winui-crash-app)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Download winapp CLI if not available in PATH
find_program(WINAPP_CLI winapp)
if(NOT WINAPP_CLI)
    set(WINAPP_DIR "${CMAKE_CURRENT_SOURCE_DIR}/.winapp-tools")
    set(WINAPP_CLI "${WINAPP_DIR}/winapp.exe")

    if(NOT EXISTS "${WINAPP_CLI}")
        message(STATUS "Downloading winapp CLI...")
        if(CMAKE_SYSTEM_PROCESSOR MATCHES "ARM64|aarch64")
            set(WINAPP_ARCH "arm64")
        else()
            set(WINAPP_ARCH "x64")
        endif()
        set(WINAPP_ZIP "${CMAKE_CURRENT_BINARY_DIR}/winappcli.zip")
        file(DOWNLOAD
            "https://github.com/microsoft/WinAppCli/releases/latest/download/winappcli-${WINAPP_ARCH}.zip"
            "${WINAPP_ZIP}"
            SHOW_PROGRESS
        )
        file(ARCHIVE_EXTRACT INPUT "${WINAPP_ZIP}" DESTINATION "${WINAPP_DIR}")
        file(REMOVE "${WINAPP_ZIP}")
    endif()
endif()

# Restore Windows App SDK headers
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

add_executable(cpp-winui-crash-app WIN32 main.cpp)

if(MSVC)
    target_compile_options(cpp-winui-crash-app PRIVATE /bigobj)
endif()

target_link_libraries(cpp-winui-crash-app PRIVATE WindowsApp.lib OneCoreUap.lib Microsoft.WindowsAppRuntime.Bootstrap.lib)
target_include_directories(cpp-winui-crash-app PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/.winapp/include)

if(CMAKE_SYSTEM_PROCESSOR MATCHES "ARM64|aarch64")
    set(WINAPP_LIB_ARCH "arm64")
else()
    set(WINAPP_LIB_ARCH "x64")
endif()

target_link_directories(cpp-winui-crash-app PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/.winapp/lib/${WINAPP_LIB_ARCH})

if(MSVC)
    target_sources(cpp-winui-crash-app PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/app.manifest")
endif()

set(WINAPP_BIN_DIR "${CMAKE_CURRENT_SOURCE_DIR}/.winapp/bin/${WINAPP_LIB_ARCH}")
if(EXISTS "${WINAPP_BIN_DIR}")
    add_custom_command(TARGET cpp-winui-crash-app POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${WINAPP_BIN_DIR}"
            "$<TARGET_FILE_DIR:cpp-winui-crash-app>"
        COMMENT "Copying Windows App SDK runtime DLLs..."
    )
endif()
