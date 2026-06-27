#include <iostream>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <string>

#pragma comment(lib, "Ws2_32.lib")

int main()
{
    WSADATA wsaData;
    WSAStartup(MAKEWORD(2, 2), &wsaData);
    SOCKET udpSocket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    sockaddr_in udpAddr;
    udpAddr.sin_family = AF_INET;
    udpAddr.sin_addr.s_addr = INADDR_ANY;
    udpAddr.sin_port = htons(5051);
    bind(udpSocket, (sockaddr *)&udpAddr, sizeof(udpAddr));
    SOCKET tcpSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    sockaddr_in tcpAddr;
    tcpAddr.sin_family = AF_INET;
    tcpAddr.sin_addr.s_addr = INADDR_ANY;
    tcpAddr.sin_port = htons(5050);
    bind(tcpSocket, (sockaddr *)&tcpAddr, sizeof(tcpAddr));
    listen(tcpSocket, SOMAXCONN);

    std::cout << "[SERVER] Menunggu koneksi(UDP 5051) dan koneksi utama (TCP 5050)...\n";

    char buffer[1024];
    while (true)
    {
        fd_set readfds;
        FD_ZERO(&readfds);
        FD_SET(udpSocket, &readfds);
        FD_SET(tcpSocket, &readfds);
        int activity = select(0, &readfds, NULL, NULL, NULL);

        if (activity == SOCKET_ERROR)
        {
            std::cerr << "Error pada Radar jaringan.\n";
            break;
        }
        if (FD_ISSET(udpSocket, &readfds))
        {
            sockaddr_in clientAddr;
            int clientAddrSize = sizeof(clientAddr);
            int bytesReceived = recvfrom(udpSocket, buffer, sizeof(buffer) - 1, 0, (sockaddr *)&clientAddr, &clientAddrSize);

            if (bytesReceived > 0)
            {
                buffer[bytesReceived] = '\0';
                std::string message(buffer);

                if (message == "NEXUS_DISCOVER")
                {
                    std::string clientIp = inet_ntoa(clientAddr.sin_addr);
                    std::cout << "[UDP] HP Android mencari PC dari IP: " << clientIp << std::endl;

                    std::string reply = "NEXUS_SERVER_HERE";
                    sendto(udpSocket, reply.c_str(), reply.length(), 0, (sockaddr *)&clientAddr, clientAddrSize);
                }
            }
        }
        if (FD_ISSET(tcpSocket, &readfds))
        {
            SOCKET clientSocket = accept(tcpSocket, NULL, NULL);
            if (clientSocket != INVALID_SOCKET)
            {
                std::cout << "HP Android berhasil tersambung secara otomatis!\n";
                closesocket(clientSocket);
            }
        }
    }

    closesocket(udpSocket);
    closesocket(tcpSocket);
    WSACleanup();
    return 0;
}