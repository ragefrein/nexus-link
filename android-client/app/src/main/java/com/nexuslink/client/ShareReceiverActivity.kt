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
        Wire.initialize(this)
        // We do NOT call setContentView() so this activity remains completely invisible/transparent.

        val intentAction = intent?.action
        val intentType = intent?.type

        if (intentAction == Intent.ACTION_SEND) {
            if (intent.hasExtra(Intent.EXTRA_STREAM)) {
                val uri = intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)
                if (uri != null) {
                    // Exported Share target must never read caller-supplied file:// private paths.
                    if (uri.scheme != "content") { finish(); return }
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

    private fun connectAndGetIp(): String? = SyncService.connectedIp ?: getCachedIp() ?: discoverPcIp()

    private fun handleTextShare(text: String) {
        thread {
            try {
                val ip = connectAndGetIp() ?: error("PC tidak ditemukan")
                Wire.connect(ip, 5050).use { Wire.write(it.getOutputStream(), "CLIP:$text") }
                runOnUiThread { Toast.makeText(this, "Text sent to PC", Toast.LENGTH_SHORT).show() }
            } catch (e: Exception) {
                runOnUiThread { Toast.makeText(this, "Failed: ${e.message}", Toast.LENGTH_LONG).show() }
            } finally { runOnUiThread { finish() } }
        }
    }

    private fun handleFileShare(uri: Uri) {
        thread {
            var staged: java.io.File? = null
            try {
                val ip = connectAndGetIp() ?: error("PC tidak ditemukan")
                val (name, reportedSize) = getFileMetaData(uri)
                var size = reportedSize
                // Cloud/document providers may omit SIZE. Stage only these files.
                if (size < 0) {
                    staged = java.io.File.createTempFile("nexus", ".upload", cacheDir)
                    contentResolver.openInputStream(uri).use { input ->
                        requireNotNull(input).use { source -> staged!!.outputStream().use { source.copyTo(it, 256 * 1024) } }
                    }
                    size = staged!!.length()
                }
                val peer = try { Wire.connect(ip, 5052) } catch (e: Exception) {
                    val fresh = discoverPcIp() ?: throw e
                    getSharedPreferences("NexusPrefs", MODE_PRIVATE).edit().putString("PC_IP", fresh).apply()
                    Wire.connect(fresh, 5052)
                }
                peer.use { socket ->
                    val output = socket.getOutputStream().buffered(256 * 1024)
                    Wire.write(output, org.json.JSONObject().put("name", name).put("size", size).toString())
                    val source = staged?.inputStream() ?: contentResolver.openInputStream(uri) ?: error("Cannot read file")
                    source.use { input ->
                        val buffer = ByteArray(256 * 1024)
                        var remaining = size
                        while (remaining > 0) {
                            val read = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
                            if (read < 0) throw java.io.EOFException("Source file changed or was truncated")
                            output.write(buffer, 0, read)
                            remaining -= read
                        }
                    }
                    output.flush()
                    check(Wire.read(socket.getInputStream()) == "OK") { "PC did not confirm receipt" }
                }
                getSharedPreferences("NexusPrefs", MODE_PRIVATE).edit().putString("LAST_ACT_TITLE", name)
                    .putString("LAST_ACT_SUB", "Successfully synchronized").apply()
                runOnUiThread { Toast.makeText(this, "File received by PC", Toast.LENGTH_SHORT).show() }
            } catch (e: Exception) {
                runOnUiThread { Toast.makeText(this, "Transfer failed: ${e.message}", Toast.LENGTH_LONG).show() }
            } finally {
                staged?.delete()
                runOnUiThread { finish() }
            }
        }
    }

    private fun getFileMetaData(uri: Uri): Pair<String, Long> {
        var name = "shared_file.dat"
        var size = -1L
        try {
            contentResolver.query(uri, null, null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) {
                    val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)
                    if (nameIndex != -1) name = cursor.getString(nameIndex)
                    if (sizeIndex != -1 && !cursor.isNull(sizeIndex)) size = cursor.getLong(sizeIndex)
                }
            }
        } catch (e: Exception) {}
        return Pair(name, size)
    }
}
