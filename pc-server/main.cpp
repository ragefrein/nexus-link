#include <iostream>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <string>

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

int main() {
    WSADATA wsaData;
    WSAStartup(MAKEWORD(2, 2), &wsaData);

    // Initialize UDP Discovery Socket
    SOCKET udpSocket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    sockaddr_in udpAddr;
    udpAddr.sin_family = AF_INET;
    udpAddr.sin_addr.s_addr = INADDR_ANY;
    udpAddr.sin_port = htons(5051);
    bind(udpSocket, (sockaddr*)&udpAddr, sizeof(udpAddr));

    // Initialize TCP Data Socket
    SOCKET tcpSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    sockaddr_in tcpAddr;
    tcpAddr.sin_family = AF_INET;
    tcpAddr.sin_addr.s_addr = INADDR_ANY;
    tcpAddr.sin_port = htons(5050);
    bind(tcpSocket, (sockaddr*)&tcpAddr, sizeof(tcpAddr));
    listen(tcpSocket, SOMAXCONN);

    std::cout << "[SERVER] Waiting for UDP discovery and TCP connections...\n";

    char buffer[4096];
    SOCKET activeTcpSocket = INVALID_SOCKET;
    std::string lastClipboardText = getWindowsClipboard();

    while (true) {
        fd_set readfds;
        FD_ZERO(&readfds);
        FD_SET(udpSocket, &readfds);
        FD_SET(tcpSocket, &readfds);
        
        if (activeTcpSocket != INVALID_SOCKET) {
            FD_SET(activeTcpSocket, &readfds);
        }

        // Set 1 second timeout for clipboard polling
        timeval timeout;
        timeout.tv_sec = 1;
        timeout.tv_usec = 0;

        int activity = select(0, &readfds, NULL, NULL, &timeout);

        if (activity == SOCKET_ERROR) {
            std::cerr << "[ERROR] Select failed.\n";
            break;
        }

        // Poll PC Clipboard every second
        if (activity == 0 && activeTcpSocket != INVALID_SOCKET) {
            std::string currentClip = getWindowsClipboard();
            if (!currentClip.empty() && currentClip != lastClipboardText) {
                lastClipboardText = currentClip;
                std::string packet = "CLIP:" + currentClip;
                send(activeTcpSocket, packet.c_str(), packet.length(), 0);
                std::cout << "[CLIPBOARD] Sent to Android: " << currentClip << "\n";
            }
        }

        // Handle UDP Discovery from Android
        if (FD_ISSET(udpSocket, &readfds)) {
            sockaddr_in clientAddr;
            int clientAddrSize = sizeof(clientAddr);
            int bytesReceived = recvfrom(udpSocket, buffer, sizeof(buffer) - 1, 0, (sockaddr*)&clientAddr, &clientAddrSize);
            
            if (bytesReceived > 0) {
                buffer[bytesReceived] = '\0';
                std::string message(buffer);
                
                if (message == "NEXUS_DISCOVER") {
                    std::cout << "[UDP] Discovery ping from IP: " << inet_ntoa(clientAddr.sin_addr) << "\n";
                    std::string reply = "NEXUS_SERVER_HERE";
                    sendto(udpSocket, reply.c_str(), reply.length(), 0, (sockaddr*)&clientAddr, clientAddrSize);
                }
            }
        }

        // Handle incoming TCP connections
        if (FD_ISSET(tcpSocket, &readfds)) {
            SOCKET clientSocket = accept(tcpSocket, NULL, NULL);
            // Drop old connection if a new session is started (e.g. Android Share intent)
            if (activeTcpSocket != INVALID_SOCKET) {
                closesocket(activeTcpSocket);
            }
            activeTcpSocket = clientSocket;
            std::cout << "[TCP] Android connected! Clipboard sync active.\n";
        }

        // Handle TCP Data from Android
        if (activeTcpSocket != INVALID_SOCKET && FD_ISSET(activeTcpSocket, &readfds)) {
            int bytesReceived = recv(activeTcpSocket, buffer, sizeof(buffer) - 1, 0);
            if (bytesReceived > 0) {
                buffer[bytesReceived] = '\0';
                std::string msg(buffer);
                
                if (msg.rfind("CLIP:", 0) == 0) {
                    std::string content = msg.substr(5);
                    setWindowsClipboard(content);
                    lastClipboardText = content; // Prevent looping back
                    std::cout << "[CLIPBOARD] Received from Android: " << content << "\n";
                }
            } else {
                std::cout << "[TCP] Android disconnected.\n";
                closesocket(activeTcpSocket);
                activeTcpSocket = INVALID_SOCKET;
            }
        }
    }

    closesocket(udpSocket);
    if (activeTcpSocket != INVALID_SOCKET) closesocket(activeTcpSocket);
    WSACleanup();
    return 0;
}