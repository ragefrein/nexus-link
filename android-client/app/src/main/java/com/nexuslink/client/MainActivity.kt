package com.nexuslink.client

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.util.Log
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.graphics.Color
import android.graphics.PixelFormat
import androidx.appcompat.app.AppCompatActivity
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.Socket
import kotlin.concurrent.thread

class MainActivity : AppCompatActivity() {

    private var tcpSocket: Socket? = null
    private var lastClipboardText = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        // Request SYSTEM_ALERT_WINDOW permission if not granted
        if (!Settings.canDrawOverlays(this)) {
            val intentOverlay = Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName"))
            startActivity(intentOverlay)
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

    // --- Floating Bubble for Android 10+ Clipboard Injection ---
    private fun showFloatingClipboardButton(text: String) {
        if (!Settings.canDrawOverlays(this)) {
            return
        }

        runOnUiThread {
            val windowManager = getSystemService(Context.WINDOW_SERVICE) as WindowManager
            
            val bubble = android.widget.Button(this)
            bubble.text = "📋"
            bubble.textSize = 24f
            bubble.setBackgroundColor(Color.parseColor("#333333"))
            bubble.setTextColor(Color.WHITE)

            val params = WindowManager.LayoutParams(
                WindowManager.LayoutParams.WRAP_CONTENT,
                WindowManager.LayoutParams.WRAP_CONTENT,
                WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
                PixelFormat.TRANSLUCENT
            )
            params.gravity = Gravity.CENTER_VERTICAL or Gravity.END

            bubble.setOnClickListener {
                val intent = Intent(this, TransparentActivity::class.java)
                intent.flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
                intent.putExtra("CLIP_TEXT", text)
                startActivity(intent)

                if (bubble.parent != null) {
                    windowManager.removeView(bubble)
                }
            }

            windowManager.addView(bubble, params)

            bubble.postDelayed({
                if (bubble.parent != null) {
                    bubble.performClick()
                }
            }, 1000)
        }
    }
    // -----------------------------------------------------------

    private fun sendTextToPc(text: String) {
        thread {
            try {
                if (tcpSocket?.isConnected == true) {
                    val out = tcpSocket!!.getOutputStream()
                    val packet = "CLIP:$text"
                    out.write(packet.toByteArray())
                    out.flush()
                }
            } catch (e: Exception) {
                Log.e("NEXUS", "Failed to send: ${e.message}")
            }
        }
    }

    private fun discoverAndConnectPc() {
        try {
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
                // Cache the IP to allow seamless transparent sharing later!
                val prefs = getSharedPreferences("NexusPrefs", Context.MODE_PRIVATE)
                prefs.edit().putString("PC_IP", pcIp).apply()

                tcpSocket = Socket(pcIp, 5050) 
                
                if (tcpSocket!!.isConnected) {
                    Log.d("NEXUS", "Connected to PC!")
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
                    
                    showFloatingClipboardButton(clipboardContent)
                }
            }
        } catch (e: Exception) {
            Log.e("NEXUS", "Disconnected: ${e.message}")
        }
    }
}
