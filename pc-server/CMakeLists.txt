cmake_minimum_required(VERSION 3.10)

# Nama proyek dan bahasa yang digunakan (C++)
project(NexusServer VERSION 1.0 LANGUAGES CXX)

# Tentukan versi standar C++ (kita gunakan C++17 yang modern)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED True)

# Memberitahu CMake: "Tolong buatkan program bernama 'nexus-server' dari file 'main.cpp'"
add_executable(nexus-server main.cpp)

# Memberitahu CMake: "Tolong tempelkan/link library Windows Socket (ws2_32) ke program nexus-server"
target_link_libraries(nexus-server ws2_32)
