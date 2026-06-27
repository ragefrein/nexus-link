package com.nexuslink.client

import android.os.Bundle
import android.util.Log
import androidx.appcompat.app.AppCompatActivity
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.Socket
import kotlin.concurrent.thread

class MainActivity : AppCompatActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        thread { temukanPcDanHubungkan() }
    }

    private fun temukanPcDanHubungkan() {
        try {
            Log.d("NEXUS", "Mulai mencari PC via UDP...")
            val udpSocket = DatagramSocket()
            udpSocket.broadcast = true
            udpSocket.soTimeout = 10000 
            val pesan = "NEXUS_DISCOVER".toByteArray()
            val broadcastAddress = InetAddress.getByName("255.255.255.255")
            val paketKirim = DatagramPacket(pesan, pesan.size, broadcastAddress, 5051)
            udpSocket.send(paketKirim)
            val buffer = ByteArray(1024)
            val paketTerima = DatagramPacket(buffer, buffer.size)
            udpSocket.receive(paketTerima) 

            val balasan = String(paketTerima.data, 0, paketTerima.length)
            val ipPc = paketTerima.address.hostAddress

            Log.d("NEXUS", "Dapat balasan: '$balasan' dari IP PC: $ipPc")
            udpSocket.close()
            if (balasan == "NEXUS_SERVER_HERE" && ipPc != null) {
                Log.d("NEXUS", "Berhasil menemukan PC! Menyambungkan TCP...")
                val tcpSocket = Socket(ipPc, 5050)

                if (tcpSocket.isConnected) {
                    Log.d("NEXUS", "HORE! Sukses terhubung ke PC!")
                    tcpSocket.close()
                }
            }
        } catch (e: Exception) {
            Log.e("NEXUS", "Terjadi error jaringan: ${e.message}")
            e.printStackTrace()
        }
    }
}
