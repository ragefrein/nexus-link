package com.nexuslink.client

import android.app.*
import android.content.*
import android.net.Uri
import android.os.*
import android.provider.MediaStore
import androidx.core.app.NotificationCompat
import androidx.documentfile.provider.DocumentFile
import org.json.JSONObject
import java.io.*
import java.net.*
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

/** One connection owned by the service, surviving Activity recreation/backgrounding. */
class SyncService : Service() {
    private val sender = java.util.concurrent.ThreadPoolExecutor(1, 1, 0L, TimeUnit.MILLISECONDS,
        java.util.concurrent.ArrayBlockingQueue<Runnable>(32), java.util.concurrent.ThreadPoolExecutor.DiscardOldestPolicy())
    private val transfers = Executors.newFixedThreadPool(2)
    private val telemetry = Executors.newSingleThreadScheduledExecutor()
    @Volatile private var running = true
    @Volatile private var socket: Socket? = null
    @Volatile private var output: OutputStream? = null
    private var worker: Thread? = null
    private val clipboardListener = ClipboardManager.OnPrimaryClipChangedListener { syncClipboard() }
    private var lastClip = ""
    @Volatile private var pendingClipboard: String? = null
    private val clipboard get() = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
    private val prefs get() = getSharedPreferences("NexusPrefs", MODE_PRIVATE)

    override fun onBind(intent: Intent?) = null
    override fun onCreate() {
        super.onCreate()
        Wire.initialize(this)
        active = this
        val manager = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= 26) manager.createNotificationChannel(
            NotificationChannel("sync", "NexusLink connection", NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, SyncService::class.java).setAction("STOP"), PendingIntent.FLAG_IMMUTABLE)
        startForeground(1, NotificationCompat.Builder(this, "sync")
            .setSmallIcon(R.drawable.ic_sync_alt).setContentTitle("NexusLink aktif")
            .setContentText("Clipboard dan transfer terhubung ke laptop")
            .setContentIntent(open).setOngoing(true).addAction(0, "Putuskan", stop).build())
        clipboard.addPrimaryClipChangedListener(clipboardListener)
        worker = thread(name = "NexusLink connection") {
            while (running) {
                try {
                    val ip = prefs.getString("PC_IP", null)
                    val connected = try { ip?.let { Wire.connect(it, 5050) } } catch (_: Exception) { null }
                        ?: Wire.connect(discover(), 5050)
                    socket = connected
                    connected.use { peer ->
                        output = peer.getOutputStream()
                        connectedIp = peer.inetAddress.hostAddress
                        prefs.edit().putString("PC_IP", connectedIp).apply()
                        publish("CONNECTED:$connectedIp")
                        sendTelemetry()
                        while (running) {
                            val message = Wire.read(peer.getInputStream())
                            when {
                                message.startsWith("CLIP:") -> {
                                    lastClip = message.substring(5)
                                    pendingClipboard = lastClip
                                    publish(message) // Apply only while an Activity actually has focus.
                                }
                                message.startsWith("FILE_SEND:") -> {
                                    val meta = JSONObject(message.substring(10))
                                    transfers.execute {
                                        try { receiveFile(peer.inetAddress.hostAddress!!, meta) }
                                        catch (e: Exception) { publish("ERROR:Transfer gagal: ${e.message}") }
                                    }
                                }
                                else -> publish(message)
                            }
                        }
                    }
                } catch (e: Exception) {
                    if (running) android.util.Log.d("NEXUS", "Reconnecting: ${e.message}")
                    if (running && e is IllegalArgumentException) publish("ERROR:Pairing diperlukan di Settings HP")
                } finally {
                    output = null
                    socket?.close()
                    socket = null
                    connectedIp = null
                    publish("DISCONNECTED")
                }
                try { Thread.sleep(1500) } catch (_: InterruptedException) { break }
            }
        }
        telemetry.scheduleWithFixedDelay({ sendTelemetry() }, 3, 3, TimeUnit.SECONDS)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == "STOP") stopSelf()
        return START_NOT_STICKY
    }

    fun send(message: String) {
        if (!running || message.toByteArray(Charsets.UTF_8).size > Wire.MAX_PACKET) return
        sender.execute {
            try { output?.let { Wire.write(it, message) } }
            catch (_: Exception) { socket?.close() }
        }
    }

    /** Call only from a focused Activity, as required by Android clipboard access rules. */
    fun applyClipboardOrSync() {
        val incoming = pendingClipboard
        if (incoming != null) {
            lastClip = incoming
            clipboard.setPrimaryClip(ClipData.newPlainText("NexusLink", incoming))
            pendingClipboard = null
        } else syncClipboard()
    }

    fun syncClipboard() {
        try {
            val clip = clipboard.primaryClip ?: return
            if (clip.itemCount == 0) return
            if (Build.VERSION.SDK_INT >= 33 && clip.description.extras?.getBoolean("android.content.extra.IS_SENSITIVE") == true) return
            val text = clip.getItemAt(0).text?.toString() ?: return
            if (text != lastClip && text.isNotEmpty() && output != null) {
                lastClip = text
                send("CLIP:$text")
            }
        } catch (_: Exception) { /* Android 10+ denies background clipboard access. */ }
    }

    private fun discover(): String = DatagramSocket().use { udp ->
        udp.broadcast = true
        udp.soTimeout = 2500
        val bytes = "NEXUS_DISCOVER".toByteArray()
        udp.send(DatagramPacket(bytes, bytes.size, InetAddress.getByName("255.255.255.255"), 5051))
        val reply = DatagramPacket(ByteArray(1024), 1024)
        udp.receive(reply)
        check(String(reply.data, 0, reply.length) == "NEXUS_SERVER_HERE")
        reply.address.hostAddress!!
    }

    private fun sendTelemetry() {
        if (output == null) return
        val battery = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val level = battery?.getIntExtra(BatteryManager.EXTRA_LEVEL, 100) ?: 100
        val scale = battery?.getIntExtra(BatteryManager.EXTRA_SCALE, 100) ?: 100
        val plugged = battery?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0
        send("INFO:${Build.MODEL}:${level * 100 / scale.coerceAtLeast(1)}:${if (plugged != 0) 1 else 0}:Smartphone")
        val stat = StatFs(Environment.getDataDirectory().path)
        val percent = stat.availableBytes * 100 / stat.totalBytes.coerceAtLeast(1)
        send("STATS:${percent}% Free (${String.format(java.util.Locale.US, "%.1f", stat.availableBytes / 1073741824.0)} GB):${100 - percent}:LAN")
    }

    private fun receiveFile(ip: String, meta: JSONObject) {
        val name = meta.getString("name").substringAfterLast('/').substringAfterLast('\\')
        require(name.isNotBlank() && name != "." && name != "..")
        val size = meta.getLong("size")
        require(size >= 0)
        var pendingUri: Uri? = null
        var document: DocumentFile? = null
        var legacy: File? = null
        try {
            val directory = prefs.getString("DOWNLOAD_DIR_URI", null)
            val dest: OutputStream = if (directory != null) {
                document = DocumentFile.fromTreeUri(this, Uri.parse(directory))?.createFile("application/octet-stream", name)
                    ?: error("Folder tidak dapat ditulis; pilih ulang di Settings")
                contentResolver.openOutputStream(document!!.uri) ?: error("Cannot open destination")
            } else if (Build.VERSION.SDK_INT >= 29) {
                pendingUri = contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, ContentValues().apply {
                    put(MediaStore.MediaColumns.DISPLAY_NAME, name)
                    put(MediaStore.MediaColumns.MIME_TYPE, "application/octet-stream")
                    put(MediaStore.MediaColumns.RELATIVE_PATH, "Download/NexusLink")
                    put(MediaStore.MediaColumns.IS_PENDING, 1)
                }) ?: error("Cannot create download")
                contentResolver.openOutputStream(pendingUri!!) ?: error("Cannot open download")
            } else {
                // App-specific storage needs no broad storage permission on Android 7-9.
                legacy = File(getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), "${System.currentTimeMillis()}_$name")
                FileOutputStream(legacy)
            }
            dest.buffered(256 * 1024).use { file ->
                Wire.connect(ip, 5053).use { peer ->
                    Wire.write(peer.getOutputStream(), meta.getString("id"))
                    val input = peer.getInputStream().buffered(256 * 1024)
                    val buffer = ByteArray(256 * 1024)
                    var remaining = size
                    while (remaining > 0) {
                        val count = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
                        if (count < 0) throw EOFException("File terputus")
                        file.write(buffer, 0, count)
                        remaining -= count
                    }
                    file.flush()
                    if (Build.VERSION.SDK_INT >= 29 && pendingUri != null) contentResolver.update(pendingUri!!,
                        ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
                    Wire.write(peer.getOutputStream(), "OK")
                }
            }
            prefs.edit().putString("LAST_ACT_TITLE", name).putString("LAST_ACT_SUB", "Received from PC").apply()
            publish("ACTIVITY:Received $name")
        } catch (e: Exception) {
            pendingUri?.let { contentResolver.delete(it, null, null) }
            document?.delete()
            legacy?.delete()
            throw e
        }
    }

    private fun publish(message: String) = sendBroadcast(Intent(EVENT).setPackage(packageName).putExtra("message", message))
    override fun onDestroy() {
        running = false
        active = null
        connectedIp = null
        clipboard.removePrimaryClipChangedListener(clipboardListener)
        socket?.close()
        worker?.interrupt()
        telemetry.shutdownNow()
        sender.shutdownNow()
        transfers.shutdownNow()
        super.onDestroy()
    }
    companion object {
        const val EVENT = "com.nexuslink.client.SYNC_EVENT"
        @Volatile var active: SyncService? = null
            private set
        @Volatile var connectedIp: String? = null
            private set
    }
}
