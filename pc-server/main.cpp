#include <iostream>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <string>
#include <vector>

#include <fstream>
#include <direct.h>
#include <chrono>
#include <iomanip>

#pragma comment(lib, "Ws2_32.lib")

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
// ------------------------------------

// Data structure to pass arguments to the thread
struct FileTransferParams {
    SOCKET sock;
    std::string filename;
    long long filesize;
};

// Fast streaming in 64KB chunks using native Windows Threads
DWORD WINAPI receiveFileThread(LPVOID lpParam) {
    FileTransferParams* params = (FileTransferParams*)lpParam;
    SOCKET sock = params->sock;
    std::string filename = params->filename;
    long long filesize = params->filesize;
    delete params; // Free memory allocated in main

    _mkdir("NexusFiles");
    std::string path = "NexusFiles\\" + filename;
    std::ofstream outfile(path, std::ios::binary);
    
    char buffer[65536]; 
    long long bytesReceivedTotal = 0;
    
    std::cout << "\n[FILE] Receiving " << filename << " (" << (filesize / 1024) << " KB)...\n";
    
    auto startTime = std::chrono::high_resolution_clock::now();
    
    while (bytesReceivedTotal < filesize) {
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
        if (seconds > 0.0001) {
            mbps = (filesize / (1024.0 * 1024.0)) / seconds;
        }
        std::cout << "[FILE] \033[32mSuccess!\033[0m Saved to " << path << "\n";
        std::cout << "       Time: " << std::fixed << std::setprecision(2) << seconds << " s | Speed: " << mbps << " MB/s\n\n";
    } else {
        std::cout << "[FILE] \033[31mTransfer interrupted.\033[0m\n\n";
    }
    
    return 0;
}

int main() {
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

    std::cout << "=================================================\n";
    std::cout << "          NEXUS PC SERVER IS RUNNING             \n";
    std::cout << "  Control Port: 5050  |  Data Kargo Port: 5052   \n";
    std::cout << "=================================================\n";

    char buffer[4096];
    std::vector<SOCKET> controlSockets;
    std::string lastClipboardText = getWindowsClipboard();
    
    std::string expectedFilename = "";
    long long expectedFilesize = 0;

    while (true) {
        fd_set readfds;
        FD_ZERO(&readfds);
        FD_SET(udpSocket, &readfds);
        FD_SET(tcpSocket, &readfds);
        FD_SET(fileListenSocket, &readfds);
        
        for (SOCKET s : controlSockets) {
            FD_SET(s, &readfds);
        }

        timeval timeout;
        timeout.tv_sec = 1;
        timeout.tv_usec = 0;

        int activity = select(0, &readfds, NULL, NULL, &timeout);

        if (activity == SOCKET_ERROR) {
            break;
        }

        // Poll Clipboard
        if (activity == 0 && !controlSockets.empty()) {
            std::string currentClip = getWindowsClipboard();
            if (!currentClip.empty() && currentClip != lastClipboardText) {
                lastClipboardText = currentClip;
                std::string packet = "CLIP:" + currentClip;
                for (SOCKET s : controlSockets) {
                    send(s, packet.c_str(), packet.length(), 0);
                }
                std::cout << "[CLIPBOARD] Sent to Android: " << currentClip << "\n";
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
                        std::cout << "[CLIPBOARD] Received: " << content << "\n";
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