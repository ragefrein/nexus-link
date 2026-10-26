cmake_minimum_required(VERSION 3.10)

# Nama proyek dan bahasa yang digunakan (C++)
project(NexusServer VERSION 1.0 LANGUAGES CXX)

option(NEXUSLINK_ENABLE_UNSAFE_LEGACY_SERVER "Build the archived C++ server without TLS/pairing" OFF)
if(NOT NEXUSLINK_ENABLE_UNSAFE_LEGACY_SERVER)
    message(FATAL_ERROR "The C++ server is archived and has no authentication. Use pc-server-wpf/NexusLink.csproj. For isolated legacy development only, explicitly set NEXUSLINK_ENABLE_UNSAFE_LEGACY_SERVER=ON.")
endif()

# Tentukan versi standar C++ (kita gunakan C++17 yang modern)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED True)

# Disable gamepad support to avoid missing xinput.h in MinGW
add_definitions(-DIMGUI_IMPL_WIN32_DISABLE_GAMEPAD)

# ImGui directories
set(IMGUI_DIR thirdparty/imgui-1.90.8)

# Include directories
include_directories(${IMGUI_DIR} ${IMGUI_DIR}/backends)

# Source files
set(SOURCES
    main.cpp
    ${IMGUI_DIR}/imgui.cpp
    ${IMGUI_DIR}/imgui_demo.cpp
    ${IMGUI_DIR}/imgui_draw.cpp
    ${IMGUI_DIR}/imgui_tables.cpp
    ${IMGUI_DIR}/imgui_widgets.cpp
    ${IMGUI_DIR}/backends/imgui_impl_win32.cpp
    ${IMGUI_DIR}/backends/imgui_impl_opengl3.cpp
)

# Memberitahu CMake: "Tolong buatkan program bernama 'nexus-server' dari file-file di atas"
add_executable(nexus-server ${SOURCES})

# Memberitahu CMake: "Tolong tempelkan/link library Windows Socket (ws2_32) dan OpenGL ke program nexus-server"
target_link_libraries(nexus-server ws2_32 opengl32)
