package com.nexuslink.client

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.util.Log
import android.widget.Toast
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.Socket
import kotlin.concurrent.thread

/**
 * Transparent Activity that handles ACTION_SEND seamlessly.
 * It immediately connects, sends the data in the background, and finishes without showing a UI.
 */
class ShareReceiverActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // We do NOT call setContentView() so this activity remains completely invisible/transparent.

        val intentAction = intent?.action
        val intentType = intent?.type

        if (intentAction == Intent.ACTION_SEND) {
            if (intent.hasExtra(Intent.EXTRA_STREAM)) {
                val uri = intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)
                if (uri != null) {
                    handleFileShare(uri)
                    return
                }
            } else if (intentType == "text/plain") {
                val text = intent.getStringExtra(Intent.EXTRA_TEXT)
                if (text != null) {
                    handleTextShare(text)
                    return
                }
            }
        }
        
        finish()
    }

    private fun getCachedIp(): String? {
        val prefs = getSharedPreferences("NexusPrefs", Context.MODE_PRIVATE)
        return prefs.getString("PC_IP", null)
    }

    private fun discoverPcIp(): String? {
        try {
            val udpSocket = DatagramSocket()
            udpSocket.broadcast = true
            udpSocket.soTimeout = 2000 // Quick discovery

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

            if (reply == "NEXUS_SERVER_HERE") {
                return pcIp
            }
        } catch (e: Exception) {
            Log.e("NEXUS", "Discovery failed: ${e.message}")
        }
        return null
    }

    private fun connectAndGetIp(): String? {
        var ip = getCachedIp()
        if (ip != null) {
            try {
                // Quick ping test
                val testSocket = Socket(ip, 5050)
                testSocket.close()
                return ip
            } catch (e: Exception) {
                // IP is stale
            }
        }
        
        // Fallback to UDP broadcast
        ip = discoverPcIp()
        if (ip != null) {
            val prefs = getSharedPreferences("NexusPrefs", Context.MODE_PRIVATE)
            prefs.edit().putString("PC_IP", ip).apply()
        }
        return ip
    }

    private fun handleTextShare(text: String) {
        thread {
            val pcIp = connectAndGetIp()
            if (pcIp != null) {
                try {
                    val socket = Socket(pcIp, 5050)
                    val out = socket.getOutputStream()
                    val packet = "CLIP:$text"
                    out.write(packet.toByteArray())
                    out.flush()
                    socket.close()
                    runOnUiThread { Toast.makeText(this@ShareReceiverActivity, "✅ Text sent to PC!", Toast.LENGTH_SHORT).show() }
                } catch (e: Exception) {
                    runOnUiThread { Toast.makeText(this@ShareReceiverActivity, "❌ Failed to send text", Toast.LENGTH_SHORT).show() }
                }
            } else {
                runOnUiThread { Toast.makeText(this@ShareReceiverActivity, "❌ Nexus PC not found", Toast.LENGTH_SHORT).show() }
            }
            runOnUiThread { finish() }
        }
    }

    private fun handleFileShare(uri: Uri) {
        thread {
            val pcIp = connectAndGetIp()
            if (pcIp != null) {
                try {
                    val (name, size) = getFileMetaData(uri)
                    if (size > 0) {
                        val controlSocket = Socket(pcIp, 5050)
                        val out = controlSocket.getOutputStream()
                        val req = "FILE_REQ:$name:$size"
                        out.write(req.toByteArray())
                        out.flush()

                        // Wait for acceptance
                        val input = controlSocket.getInputStream()
                        val buffer = ByteArray(1024)
                        val bytesRead = input.read(buffer)
                        val reply = String(buffer, 0, bytesRead)

                        if (reply.startsWith("FILE_ACCEPT")) {
                            controlSocket.close()
                            streamFileToPc(pcIp, uri)
                        } else {
                            controlSocket.close()
                            runOnUiThread { 
                                Toast.makeText(this@ShareReceiverActivity, "❌ PC rejected file", Toast.LENGTH_SHORT).show() 
                                finish()
                            }
                        }
                    } else {
                        runOnUiThread { 
                            Toast.makeText(this@ShareReceiverActivity, "❌ Invalid file", Toast.LENGTH_SHORT).show() 
                            finish()
                        }
                    }
                } catch (e: Exception) {
                    runOnUiThread { 
                        Toast.makeText(this@ShareReceiverActivity, "❌ Failed to start transfer", Toast.LENGTH_SHORT).show() 
                        finish()
                    }
                }
            } else {
                runOnUiThread { 
                    Toast.makeText(this@ShareReceiverActivity, "❌ Nexus PC not found", Toast.LENGTH_SHORT).show() 
                    finish()
                }
            }
        }
    }

    private fun streamFileToPc(pcIp: String, uri: Uri) {
        try {
            val dataSocket = Socket(pcIp, 5052)
            
            // Gunakan Buffered Streams agar pembacaan storage dan penulisan jaringan (TCP) di-batch dengan cerdas
            val out = java.io.BufferedOutputStream(dataSocket.getOutputStream(), 1024 * 1024)
            val baseInput = contentResolver.openInputStream(uri)
            
            if (baseInput != null) {
                val inputStream = java.io.BufferedInputStream(baseInput, 1024 * 1024)
                runOnUiThread { Toast.makeText(this, "🚀 Sending file to PC...", Toast.LENGTH_SHORT).show() }
                
                dataSocket.sendBufferSize = 2 * 1024 * 1024
                val buffer = ByteArray(1024 * 1024) 
                var bytesRead: Int
                
                while (inputStream.read(buffer).also { bytesRead = it } != -1) {
                    out.write(buffer, 0, bytesRead)
                }
                out.flush()
                inputStream.close()
                runOnUiThread { Toast.makeText(this, "✅ File successfully sent!", Toast.LENGTH_SHORT).show() }
                
                // Simpan riwayat ke SharedPreferences agar tampil di Dashboard (Aktivitas Terakhir)
                val prefs = getSharedPreferences("NexusPrefs", Context.MODE_PRIVATE)
                val filename = getFileMetaData(uri).first
                prefs.edit()
                    .putString("LAST_ACT_TITLE", filename)
                    .putString("LAST_ACT_SUB", "Successfully synchronized")
                    .apply()
            }
            dataSocket.close()
        } catch (e: Exception) {
            runOnUiThread { Toast.makeText(this, "❌ Failed to send file", Toast.LENGTH_SHORT).show() }
        }
        runOnUiThread { finish() }
    }

    private fun getFileMetaData(uri: Uri): Pair<String, Long> {
        var name = "shared_file.dat"
        var size = 0L
        try {
            contentResolver.query(uri, null, null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) {
                    val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)
                    if (nameIndex != -1) name = cursor.getString(nameIndex)
                    if (sizeIndex != -1) size = cursor.getLong(sizeIndex)
                }
            }
        } catch (e: Exception) {}
        return Pair(name, size)
    }
}
