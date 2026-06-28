package com.nexuslink.client

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.util.Log
import androidx.appcompat.app.AppCompatActivity
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.Socket
import kotlin.concurrent.thread

class MainActivity : AppCompatActivity() {

    private var tcpSocket: Socket? = null
    private var lastClipboardText = ""
    private var pendingTextToSend: String? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        // Handle text shared from other apps
        if (intent?.action == Intent.ACTION_SEND && intent.type == "text/plain") {
            pendingTextToSend = intent.getStringExtra(Intent.EXTRA_TEXT)
        }

        thread {
            discoverAndConnectPc()
        }

        // Monitor local clipboard changes
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.addPrimaryClipChangedListener {
            val clip = clipboard.primaryClip
            if (clip != null && clip.itemCount > 0) {
                val text = clip.getItemAt(0).text?.toString()
                if (text != null && text != lastClipboardText) {
                    lastClipboardText = text 
                    sendTextToPc(text)
                }
            }
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            if (clipboard.hasPrimaryClip()) {
                val clip = clipboard.primaryClip
                if (clip != null && clip.itemCount > 0) {
                    val text = clip.getItemAt(0).text?.toString()
                    if (text != null && text != lastClipboardText) {
                        lastClipboardText = text
                        sendTextToPc(text)
                    }
                }
            }
        }
    }

    private fun sendTextToPc(text: String) {
        thread {
            try {
                if (tcpSocket?.isConnected == true) {
                    val out = tcpSocket!!.getOutputStream()
                    val packet = "CLIP:$text"
                    out.write(packet.toByteArray())
                    out.flush()
                    Log.d("NEXUS", "Sent to PC: $text")
                }
            } catch (e: Exception) {
                Log.e("NEXUS", "Failed to send: ${e.message}")
            }
        }
    }

    private fun discoverAndConnectPc() {
        try {
            // UDP Discovery
            val udpSocket = DatagramSocket()
            udpSocket.broadcast = true
            udpSocket.soTimeout = 10000 

            val message = "NEXUS_DISCOVER".toByteArray()
            val broadcastAddress = InetAddress.getByName("255.255.255.255")
            val sendPacket = DatagramPacket(message, message.size, broadcastAddress, 5051)
            udpSocket.send(sendPacket)

            val buffer = ByteArray(1024)
            val receivePacket = DatagramPacket(buffer, buffer.size)
            udpSocket.receive(receivePacket)

            val reply = String(receivePacket.data, 0, receivePacket.length)
            val pcIp = receivePacket.address.hostAddress
            udpSocket.close()

            if (reply == "NEXUS_SERVER_HERE" && pcIp != null) {
                // Establish TCP Connection
                tcpSocket = Socket(pcIp, 5050) 
                
                if (tcpSocket!!.isConnected) {
                    Log.d("NEXUS", "Connected to PC!")
                    
                    if (pendingTextToSend != null) {
                        sendTextToPc(pendingTextToSend!!)
                        pendingTextToSend = null 
                        runOnUiThread { finish() } 
                    }

                    listenForPcMessages()
                }
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    private fun listenForPcMessages() {
        try {
            val inputStream = tcpSocket!!.getInputStream()
            val buffer = ByteArray(4096)
            
            while (true) {
                val bytesRead = inputStream.read(buffer)
                if (bytesRead == -1) break 
                
                val incomingMsg = String(buffer, 0, bytesRead)
                
                if (incomingMsg.startsWith("CLIP:")) {
                    val clipboardContent = incomingMsg.substring(5)
                    lastClipboardText = clipboardContent
                    
                    runOnUiThread {
                        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                        val clip = ClipData.newPlainText("Nexus", clipboardContent)
                        clipboard.setPrimaryClip(clip)
                        Log.d("NEXUS", "Clipboard synced: $clipboardContent")
                    }
                }
            }
        } catch (e: Exception) {
            Log.e("NEXUS", "Disconnected: ${e.message}")
        }
    }
}
