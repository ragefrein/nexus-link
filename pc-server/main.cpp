#include <iostream>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <string>
#include <cstring>
#include <vector>
#include <fstream>
#include <direct.h>
#include <chrono>
#include <iomanip>
#include <sstream>

// ImGui
#include "imgui.h"
#include "imgui_impl_win32.h"
#include "imgui_impl_opengl3.h"
#include <GL/GL.h>
#include <tchar.h>

#pragma comment(lib, "Ws2_32.lib")

// --- Global State ---
bool g_NetworkRunning = true;
std::vector<std::string> g_LogMessages;
CRITICAL_SECTION g_LogMutex;

void AddLog(const std::string& msg) {
    EnterCriticalSection(&g_LogMutex);
    g_LogMessages.push_back(msg);
    if(g_LogMessages.size() > 100) {
        g_LogMessages.erase(g_LogMessages.begin());
    }
    LeaveCriticalSection(&g_LogMutex);
}

// --- Native Windows Clipboard API ---
void setWindowsClipboard(const std::string& text) {
    if (OpenClipboard(nullptr)) {
        EmptyClipboard();
        HGLOBAL hMem = GlobalAlloc(GMEM_MOVEABLE, text.length() + 1);
        if (hMem) {
            memcpy(GlobalLock(hMem), text.c_str(), text.length() + 1);
            GlobalUnlock(hMem);
            SetClipboardData(CF_TEXT, hMem);
        }
        CloseClipboard();
    }
}

std::string getWindowsClipboard() {
    std::string text = "";
    if (OpenClipboard(nullptr)) {
        HANDLE hData = GetClipboardData(CF_TEXT);
        if (hData != nullptr) {
            char* pszText = static_cast<char*>(GlobalLock(hData));
            if (pszText != nullptr) {
                text = pszText;
                GlobalUnlock(hData);
            }
        }
        CloseClipboard();
    }
    return text;
}

// --- Network Thread ---
struct FileTransferParams {
    SOCKET sock;
    std::string filename;
    long long filesize;
};

DWORD WINAPI receiveFileThread(LPVOID lpParam) {
    FileTransferParams* params = (FileTransferParams*)lpParam;
    SOCKET sock = params->sock;
    std::string filename = params->filename;
    long long filesize = params->filesize;
    delete params; 

    _mkdir("NexusFiles");
    std::string path = "NexusFiles\\" + filename;
    std::ofstream outfile(path, std::ios::binary);
    
    char buffer[65536]; 
    long long bytesReceivedTotal = 0;
    
    AddLog("[FILE] Receiving " + filename + " (" + std::to_string(filesize / 1024) + " KB)...");
    
    auto startTime = std::chrono::high_resolution_clock::now();
    
    while (bytesReceivedTotal < filesize && g_NetworkRunning) {
        int bytes = recv(sock, buffer, sizeof(buffer), 0);
        if (bytes > 0) {
            outfile.write(buffer, bytes);
            bytesReceivedTotal += bytes;
        } else {
            break;
        }
    }
    
    outfile.close();
    closesocket(sock);
    
    auto endTime = std::chrono::high_resolution_clock::now();
    std::chrono::duration<double> elapsed = endTime - startTime;
    double seconds = elapsed.count();
    
    if (bytesReceivedTotal == filesize) {
        double mbps = 0.0;
        if (seconds > 0.0001) mbps = (filesize / (1024.0 * 1024.0)) / seconds;
        std::stringstream ss;
        ss << "[FILE] Success! Saved to " << path << " (Speed: " << std::fixed << std::setprecision(2) << mbps << " MB/s)";
        AddLog(ss.str());
    } else {
        AddLog("[FILE] Transfer interrupted.");
    }
    
    return 0;
}

DWORD WINAPI NetworkThreadFunc(LPVOID lpParam) {
    WSADATA wsaData;
    WSAStartup(MAKEWORD(2, 2), &wsaData);

    SOCKET udpSocket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    sockaddr_in udpAddr;
    udpAddr.sin_family = AF_INET;
    udpAddr.sin_addr.s_addr = INADDR_ANY;
    udpAddr.sin_port = htons(5051);
    bind(udpSocket, (sockaddr*)&udpAddr, sizeof(udpAddr));

    SOCKET tcpSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    sockaddr_in tcpAddr;
    tcpAddr.sin_family = AF_INET;
    tcpAddr.sin_addr.s_addr = INADDR_ANY;
    tcpAddr.sin_port = htons(5050);
    bind(tcpSocket, (sockaddr*)&tcpAddr, sizeof(tcpAddr));
    listen(tcpSocket, SOMAXCONN);

    SOCKET fileListenSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    sockaddr_in fileAddr;
    fileAddr.sin_family = AF_INET;
    fileAddr.sin_addr.s_addr = INADDR_ANY;
    fileAddr.sin_port = htons(5052);
    bind(fileListenSocket, (sockaddr*)&fileAddr, sizeof(fileAddr));
    listen(fileListenSocket, SOMAXCONN);

    AddLog("NEXUS PC SERVER IS RUNNING");
    AddLog("Control Port: 5050 | Data Kargo Port: 5052");

    char buffer[4096];
    std::vector<SOCKET> controlSockets;
    std::string lastClipboardText = getWindowsClipboard();
    
    std::string expectedFilename = "";
    long long expectedFilesize = 0;

    while (g_NetworkRunning) {
        fd_set readfds;
        FD_ZERO(&readfds);
        FD_SET(udpSocket, &readfds);
        FD_SET(tcpSocket, &readfds);
        FD_SET(fileListenSocket, &readfds);
        
        for (SOCKET s : controlSockets) FD_SET(s, &readfds);

        timeval timeout;
        timeout.tv_sec = 0;
        timeout.tv_usec = 500000; // 500ms

        int activity = select(0, &readfds, NULL, NULL, &timeout);

        if (activity == SOCKET_ERROR) break;

        // Poll Clipboard
        if (activity == 0 && !controlSockets.empty()) {
            std::string currentClip = getWindowsClipboard();
            if (!currentClip.empty() && currentClip != lastClipboardText) {
                lastClipboardText = currentClip;
                std::string packet = "CLIP:" + currentClip;
                for (SOCKET s : controlSockets) {
                    send(s, packet.c_str(), packet.length(), 0);
                }
                AddLog("[CLIPBOARD] Sent to Android: " + currentClip);
            }
        }
        
        // Broadcast PC Info periodically (Battery, Hostname)
        static auto lastInfoTime = std::chrono::steady_clock::now();
        auto now = std::chrono::steady_clock::now();
        if (std::chrono::duration_cast<std::chrono::seconds>(now - lastInfoTime).count() >= 5) {
            lastInfoTime = now;
            if (!controlSockets.empty()) {
                char hostname[MAX_COMPUTERNAME_LENGTH + 1];
                DWORD size = sizeof(hostname);
                if (!GetComputerNameA(hostname, &size)) strcpy(hostname, "Windows PC");
                
                SYSTEM_POWER_STATUS status;
                int batteryLevel = 100;
                int isCharging = 1;
                std::string deviceType = "Desktop";
                if (GetSystemPowerStatus(&status)) {
                    batteryLevel = status.BatteryLifePercent;
                    isCharging = status.ACLineStatus;
                    if (status.BatteryFlag == 128 || batteryLevel == 255) {
                        deviceType = "Desktop";
                        batteryLevel = 100; // default for desktop
                    } else {
                        deviceType = "Laptop";
                    }
                }
                std::string infoPacket = "INFO:" + std::string(hostname) + ":" + std::to_string(batteryLevel) + ":" + std::to_string(isCharging) + ":" + deviceType;
                for (SOCKET s : controlSockets) {
                    send(s, infoPacket.c_str(), infoPacket.length(), 0);
                }
            }
        }

        // Handle UDP
        if (FD_ISSET(udpSocket, &readfds)) {
            sockaddr_in clientAddr;
            int clientAddrSize = sizeof(clientAddr);
            int bytesReceived = recvfrom(udpSocket, buffer, sizeof(buffer) - 1, 0, (sockaddr*)&clientAddr, &clientAddrSize);
            
            if (bytesReceived > 0) {
                buffer[bytesReceived] = '\0';
                std::string message(buffer);
                if (message == "NEXUS_DISCOVER") {
                    std::string reply = "NEXUS_SERVER_HERE";
                    sendto(udpSocket, reply.c_str(), reply.length(), 0, (sockaddr*)&clientAddr, clientAddrSize);
                }
            }
        }

        // Handle new Control Connections
        if (FD_ISSET(tcpSocket, &readfds)) {
            SOCKET clientSocket = accept(tcpSocket, NULL, NULL);
            controlSockets.push_back(clientSocket);
            AddLog("[NETWORK] Device connected.");
        }

        // Handle new File Data Connections
        if (FD_ISSET(fileListenSocket, &readfds)) {
            SOCKET fileSocket = accept(fileListenSocket, NULL, NULL);
            if (expectedFilesize > 0) {
                FileTransferParams* params = new FileTransferParams();
                params->sock = fileSocket;
                params->filename = expectedFilename;
                params->filesize = expectedFilesize;
                CreateThread(NULL, 0, receiveFileThread, params, 0, NULL);
                
                expectedFilename = "";
                expectedFilesize = 0;
            } else {
                closesocket(fileSocket);
            }
        }

        // Handle Control Data
        for (auto it = controlSockets.begin(); it != controlSockets.end(); ) {
            SOCKET s = *it;
            if (FD_ISSET(s, &readfds)) {
                int bytesReceived = recv(s, buffer, sizeof(buffer) - 1, 0);
                if (bytesReceived > 0) {
                    buffer[bytesReceived] = '\0';
                    std::string msg(buffer);
                    
                    if (msg.rfind("CLIP:", 0) == 0) {
                        std::string content = msg.substr(5);
                        setWindowsClipboard(content);
                        lastClipboardText = content; 
                        AddLog("[CLIPBOARD] Received: " + content);
                    } 
                    else if (msg.rfind("FILE_REQ:", 0) == 0) {
                        size_t firstColon = msg.find(':');
                        size_t secondColon = msg.find(':', firstColon + 1);
                        if (firstColon != std::string::npos && secondColon != std::string::npos) {
                            expectedFilename = msg.substr(firstColon + 1, secondColon - firstColon - 1);
                            try {
                                expectedFilesize = std::stoll(msg.substr(secondColon + 1));
                                std::string reply = "FILE_ACCEPT";
                                send(s, reply.c_str(), reply.length(), 0);
                            } catch (...) {}
                        }
                    }
                    ++it;
                } else {
                    closesocket(s);
                    it = controlSockets.erase(it);
                    AddLog("[NETWORK] Device disconnected.");
                }
            } else {
                ++it;
            }
        }
    }

    closesocket(udpSocket);
    closesocket(fileListenSocket);
    closesocket(tcpSocket);
    WSACleanup();
    return 0;
}


// --- OpenGL 3 Helper Functions ---
struct WGL_WindowData { HDC hDC; };
static HGLRC            g_hRC;
static WGL_WindowData   g_MainWindow;
static int              g_Width;
static int              g_Height;

bool CreateDeviceWGL(HWND hWnd, WGL_WindowData* data) {
    HDC hDc = ::GetDC(hWnd);
    PIXELFORMATDESCRIPTOR pfd = { 0 };
    pfd.nSize = sizeof(pfd);
    pfd.nVersion = 1;
    pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
    pfd.iPixelType = PFD_TYPE_RGBA;
    pfd.cColorBits = 32;

    const int pf = ::ChoosePixelFormat(hDc, &pfd);
    if (pf == 0) return false;
    if (::SetPixelFormat(hDc, pf, &pfd) == FALSE) return false;
    ::ReleaseDC(hWnd, hDc);

    data->hDC = ::GetDC(hWnd);
    if (!g_hRC)
        g_hRC = wglCreateContext(data->hDC);
    return true;
}

void CleanupDeviceWGL(HWND hWnd, WGL_WindowData* data) {
    wglMakeCurrent(nullptr, nullptr);
    ::ReleaseDC(hWnd, data->hDC);
}

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

LRESULT WINAPI WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (ImGui_ImplWin32_WndProcHandler(hWnd, msg, wParam, lParam))
        return true;

    switch (msg) {
    case WM_SIZE:
        if (wParam != SIZE_MINIMIZED) {
            g_Width = LOWORD(lParam);
            g_Height = HIWORD(lParam);
        }
        return 0;
    case WM_SYSCOMMAND:
        if ((wParam & 0xfff0) == SC_KEYMENU) return 0;
        break;
    case WM_DESTROY:
        ::PostQuitMessage(0);
        return 0;
    }
    return ::DefWindowProcW(hWnd, msg, wParam, lParam);
}

int main(int, char**) {
    InitializeCriticalSection(&g_LogMutex);

    // 1. Start Network Thread
    HANDLE hThread = CreateThread(NULL, 0, NetworkThreadFunc, NULL, 0, NULL);

    // 2. Create Window
    WNDCLASSEXW wc = { sizeof(wc), CS_OWNDC, WndProc, 0L, 0L, GetModuleHandle(nullptr), nullptr, nullptr, nullptr, nullptr, L"NexusClass", nullptr };
    ::RegisterClassExW(&wc);
    HWND hwnd = ::CreateWindowW(wc.lpszClassName, L"NexusLink - PC Server Dashboard", WS_OVERLAPPEDWINDOW, 100, 100, 800, 600, nullptr, nullptr, wc.hInstance, nullptr);

    if (!CreateDeviceWGL(hwnd, &g_MainWindow)) {
        CleanupDeviceWGL(hwnd, &g_MainWindow);
        ::DestroyWindow(hwnd);
        ::UnregisterClassW(wc.lpszClassName, wc.hInstance);
        return 1;
    }
    wglMakeCurrent(g_MainWindow.hDC, g_hRC);

    ::ShowWindow(hwnd, SW_SHOWDEFAULT);
    ::UpdateWindow(hwnd);

    // 3. Setup ImGui
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO(); (void)io;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;

    ImGui::StyleColorsDark();

    ImGui_ImplWin32_InitForOpenGL(hwnd);
    ImGui_ImplOpenGL3_Init();

    ImVec4 clear_color = ImVec4(0.08f, 0.08f, 0.08f, 1.00f);

    // 4. Main UI Loop
    bool done = false;
    while (!done) {
        MSG msg;
        while (::PeekMessage(&msg, nullptr, 0U, 0U, PM_REMOVE)) {
            ::TranslateMessage(&msg);
            ::DispatchMessage(&msg);
            if (msg.message == WM_QUIT) done = true;
        }
        if (done) break;

        ImGui_ImplOpenGL3_NewFrame();
        ImGui_ImplWin32_NewFrame();
        ImGui::NewFrame();

        // --- Dashboard UI ---
        // Make the window fill the entire screen space
        ImGui::SetNextWindowPos(ImVec2(0.0f, 0.0f));
        ImGui::SetNextWindowSize(ImGui::GetIO().DisplaySize);
        ImGui::Begin("Dashboard", nullptr, ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove);

        ImGui::TextColored(ImVec4(0.0f, 1.0f, 0.5f, 1.0f), "NexusLink PC Server is Active");
        ImGui::Separator();
        
        ImGui::Spacing();
        if (ImGui::Button("Stop Server & Exit", ImVec2(200, 40))) {
            done = true;
        }

        ImGui::Spacing();
        ImGui::Text("Activity Log:");
        
        // Log Box
        ImGui::BeginChild("LogRegion", ImVec2(0, 0), true, ImGuiWindowFlags_AlwaysVerticalScrollbar);
        {
            EnterCriticalSection(&g_LogMutex);
            for (const auto& log : g_LogMessages) {
                ImGui::TextUnformatted(log.c_str());
            }
            if (ImGui::GetScrollY() >= ImGui::GetScrollMaxY())
                ImGui::SetScrollHereY(1.0f);
            LeaveCriticalSection(&g_LogMutex);
        }
        ImGui::EndChild();
        
        ImGui::End();
        // --------------------

        ImGui::Render();
        glViewport(0, 0, g_Width, g_Height);
        glClearColor(clear_color.x, clear_color.y, clear_color.z, clear_color.w);
        glClear(GL_COLOR_BUFFER_BIT);
        ImGui_ImplOpenGL3_RenderDrawData(ImGui::GetDrawData());

        ::SwapBuffers(g_MainWindow.hDC);
    }

    // 5. Cleanup
    g_NetworkRunning = false;
    WaitForSingleObject(hThread, INFINITE);
    CloseHandle(hThread);
    DeleteCriticalSection(&g_LogMutex);

    ImGui_ImplOpenGL3_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();

    CleanupDeviceWGL(hwnd, &g_MainWindow);
    wglDeleteContext(g_hRC);
    ::DestroyWindow(hwnd);
    ::UnregisterClassW(wc.lpszClassName, wc.hInstance);

    return 0;
}