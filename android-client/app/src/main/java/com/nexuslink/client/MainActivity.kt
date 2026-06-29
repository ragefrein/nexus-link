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
import android.widget.Button
import android.widget.TextView
import android.widget.Toast
import android.widget.ProgressBar
import androidx.appcompat.app.AppCompatActivity
import android.os.Build
import android.content.IntentFilter
import android.os.BatteryManager
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

        // Initialize Native Views
        val btnSendFile = findViewById<Button>(R.id.btnSendFile)
        val btnSendClipboard = findViewById<Button>(R.id.btnSendClipboard)

        btnSendFile.setOnClickListener {
            val intent = Intent(Intent.ACTION_GET_CONTENT)
            intent.type = "*/*"
            startActivityForResult(intent, 101)
        }

        btnSendClipboard.setOnClickListener {
            val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            if (clipboard.hasPrimaryClip() && clipboard.primaryClip != null) {
                val text = clipboard.primaryClip!!.getItemAt(0).text?.toString()
                if (text != null) {
                    sendCommandToPc("CLIP:$text")
                    updateActivityList("content_copy", "Mengirim Teks Clipboard", "Baru saja", "done")
                } else {
                    Toast.makeText(this, "Clipboard kosong", Toast.LENGTH_SHORT).show()
                }
            }
        }

        // Request SYSTEM_ALERT_WINDOW permission if not granted
        if (!Settings.canDrawOverlays(this)) {
            val intentOverlay = Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName"))
            startActivity(intentOverlay)
        }

        thread {
            discoverAndConnectPc()
        }
        
        thread {
            startSystemStatsMonitor()
        }

        // Load recent activity from SharedPreferences
        val prefs = getSharedPreferences("NexusPrefs", Context.MODE_PRIVATE)
        val lastTitle = prefs.getString("LAST_ACT_TITLE", null)
        val lastSub = prefs.getString("LAST_ACT_SUB", null)
        if (lastTitle != null && lastSub != null) {
            updateActivityList("folder_zip", lastTitle, lastSub, "done")
        }

        // Monitor local clipboard changes
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.addPrimaryClipChangedListener {
            val clip = clipboard.primaryClip
            if (clip != null && clip.itemCount > 0) {
                val text = clip.getItemAt(0).text?.toString()
                if (text != null && text != lastClipboardText) {
                    lastClipboardText = text 
                    sendCommandToPc("CLIP:$text")
                }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        // Reload recent activity in case it was updated while app was in background
        val prefs = getSharedPreferences("NexusPrefs", Context.MODE_PRIVATE)
        val lastTitle = prefs.getString("LAST_ACT_TITLE", null)
        val lastSub = prefs.getString("LAST_ACT_SUB", null)
        if (lastTitle != null && lastSub != null) {
            updateActivityList("folder_zip", lastTitle, lastSub, "done")
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
                        sendCommandToPc("CLIP:$text")
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

    private fun sendCommandToPc(packet: String) {
        thread {
            try {
                if (tcpSocket?.isConnected == true) {
                    val out = tcpSocket!!.getOutputStream()
                    out.write(packet.toByteArray())
                    out.flush()
                }
            } catch (e: Exception) {
                Log.e("NEXUS", "Failed to send: ${e.message}")
            }
        }
    }

    private fun updateConnectionUI(isConnected: Boolean, ip: String) {
        runOnUiThread {
            val tvConnectionStatus = findViewById<TextView>(R.id.tvConnectionStatus)
            if (isConnected) {
                tvConnectionStatus.text = "Terhubung ke $ip"
            } else {
                tvConnectionStatus.text = "Mencari PC..."
            }
        }
    }

    private fun updateActivityList(iconName: String, title: String, subtitle: String, statusIconName: String) {
        runOnUiThread {
            val tvEmptyActivity = findViewById<TextView>(R.id.tvEmptyActivity)
            tvEmptyActivity.text = "$title\n$subtitle"
            tvEmptyActivity.setTextColor(Color.parseColor("#191c1d"))
            
            // Map icon string to drawable resource
            val startRes = when(iconName) {
                "image" -> R.drawable.ic_image
                "content_paste" -> R.drawable.ic_copy
                "folder_zip" -> R.drawable.ic_file
                else -> R.drawable.ic_file
            }
            
            val endRes = when(statusIconName) {
                "done" -> R.drawable.ic_check_circle
                else -> 0
            }
            
            tvEmptyActivity.setCompoundDrawablesWithIntrinsicBounds(startRes, 0, endRes, 0)
            tvEmptyActivity.compoundDrawablePadding = 24 // 24px padding
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
                    updateConnectionUI(true, pcIp)
                    startInfoSender()
                    listenForPcMessages()
                }
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }
    
    private fun startInfoSender() {
        thread {
            try {
                while (tcpSocket?.isConnected == true) {
                    val batteryIntent = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
                    val level = batteryIntent?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: 100
                    val scale = batteryIntent?.getIntExtra(BatteryManager.EXTRA_SCALE, -1) ?: 100
                    val batteryPct = (level * 100) / scale.coerceAtLeast(1)
                    val status = batteryIntent?.getIntExtra(BatteryManager.EXTRA_STATUS, -1) ?: -1
                    val isCharging = if (status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL) "1" else "0"
                    
                    val deviceName = Build.MODEL
                    val infoPacket = "INFO:$deviceName:$batteryPct:$isCharging:Smartphone\n"
                    
                    try {
                        val out = tcpSocket!!.getOutputStream()
                        out.write(infoPacket.toByteArray())
                        out.flush()
                    } catch (e: Exception) {}
                    
                    Thread.sleep(5000)
                }
            } catch (e: Exception) {
                e.printStackTrace()
            }
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
                    updateActivityList("content_paste", "Teks Clipboard", "Diterima dari PC", "done")
                } 
                else if (incomingMsg.startsWith("INFO:")) {
                    val parts = incomingMsg.split(":")
                    if (parts.size >= 4) {
                        val hostname = parts[1]
                        val battery = parts[2]
                        val charging = parts[3]
                        val deviceType = if (parts.size >= 5) parts[4] else "Desktop"
                        val storageInfo = if (parts.size >= 6) parts[5] else "-- Free"
                        val syncSpeed = if (parts.size >= 7) parts[6] else "0 KB/s"
                        
                        runOnUiThread {
                            val tvConnectionStatus = findViewById<TextView>(R.id.tvConnectionStatus)
                            tvConnectionStatus.text = "Terhubung ke\n$hostname"
                            tvConnectionStatus.setTextColor(android.graphics.Color.parseColor("#191c1d"))
                            
                            val ivDeviceIcon = findViewById<android.widget.ImageView>(R.id.ivDeviceIcon)
                            ivDeviceIcon.visibility = android.view.View.VISIBLE
                            if (deviceType == "Laptop") {
                                ivDeviceIcon.setImageResource(R.drawable.ic_laptop)
                            } else {
                                ivDeviceIcon.setImageResource(R.drawable.ic_desktop)
                            }
                            
                            val tvStorageInfo = findViewById<TextView>(R.id.tvStorageInfo)
                            tvStorageInfo?.text = storageInfo
                            
                            val tvSyncSpeed = findViewById<TextView>(R.id.tvSyncSpeed)
                            tvSyncSpeed?.text = syncSpeed
                            
                            val tvBatteryLevel = findViewById<TextView>(R.id.tvBatteryLevel)
                            val tvBatteryState = findViewById<TextView>(R.id.tvBatteryState)
                            val vBatteryFill = findViewById<View>(R.id.vBatteryFill)
                            
                            tvBatteryLevel.text = "$battery%"
                            tvBatteryState.text = if (charging == "1") "Charging" else "On Battery"
                            
                            val bLevel = battery.toIntOrNull() ?: 0
                            val maxWidth = (60 * resources.displayMetrics.density).toInt()
                            val targetWidth = (maxWidth * bLevel) / 100
                            
                            val anim = android.animation.ValueAnimator.ofInt(vBatteryFill.layoutParams.width.coerceAtLeast(0), targetWidth)
                            anim.addUpdateListener { animation ->
                                val w = animation.animatedValue as Int
                                val lp = vBatteryFill.layoutParams
                                lp.width = w
                                vBatteryFill.layoutParams = lp
                            }
                            anim.duration = 500
                            anim.start()

                            // Set color dynamically
                            val batteryColor = when {
                                bLevel <= 20 -> android.graphics.Color.parseColor("#BA1A1A")
                                charging == "1" -> android.graphics.Color.parseColor("#386A20")
                                else -> android.graphics.Color.parseColor("#00459A")
                            }
                            vBatteryFill.background?.setTint(batteryColor)
                        }
                    }
                }
                else if (incomingMsg.startsWith("FILE_SEND:")) {
                    val parts = incomingMsg.split(":")
                    if (parts.size >= 3) {
                        val name = parts[1]
                        val size = parts[2].toLongOrNull() ?: 0L
                        
                        thread {
                            try {
                                val ip = tcpSocket?.inetAddress?.hostAddress
                                if (ip != null) {
                                    val downloadSocket = Socket(ip, 5053)
                                    val input = java.io.BufferedInputStream(downloadSocket.getInputStream(), 1024 * 1024)
                                    
                                    val downloadsDir = android.os.Environment.getExternalStoragePublicDirectory(android.os.Environment.DIRECTORY_DOWNLOADS)
                                    val file = java.io.File(downloadsDir, name)
                                    val out = java.io.BufferedOutputStream(java.io.FileOutputStream(file), 1024 * 1024)
                                    
                                    runOnUiThread { Toast.makeText(this@MainActivity, "📥 Mengunduh $name...", Toast.LENGTH_SHORT).show() }
                                    
                                    val buffer = ByteArray(65536)
                                    var bytesRead: Int
                                    var totalRead = 0L
                                    while (input.read(buffer).also { bytesRead = it } != -1) {
                                        out.write(buffer, 0, bytesRead)
                                        totalRead += bytesRead
                                        if (totalRead >= size) break
                                    }
                                    out.flush()
                                    out.close()
                                    input.close()
                                    downloadSocket.close()
                                    
                                    runOnUiThread { 
                                        Toast.makeText(this@MainActivity, "✅ File diterima: $name", Toast.LENGTH_SHORT).show()
                                        updateActivityList("image", name, "Diterima dari PC", "done")
                                    }
                                }
                            } catch (e: Exception) {
                                runOnUiThread { Toast.makeText(this@MainActivity, "❌ Gagal mengunduh file", Toast.LENGTH_SHORT).show() }
                            }
                        }
                    }
                }
            }
        } catch (e: Exception) {
            Log.e("NEXUS", "Disconnected: ${e.message}")
        }
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == 101 && resultCode == RESULT_OK) {
            val uri = data?.data
            if (uri != null) {
                // Forward the selected file to ShareReceiverActivity to handle the actual transfer
                val intent = Intent(this, ShareReceiverActivity::class.java)
                intent.action = Intent.ACTION_SEND
                intent.putExtra(Intent.EXTRA_STREAM, uri)
                startActivity(intent)
            }
        }
    }

    private fun startSystemStatsMonitor() {
        var lastTxBytes = android.net.TrafficStats.getTotalTxBytes()
        var lastRxBytes = android.net.TrafficStats.getTotalRxBytes()
        var firstRun = true
        
        while (true) {
            try {
                Thread.sleep(1000)
                
                // Network Speed
                val currentTx = android.net.TrafficStats.getTotalTxBytes()
                val currentRx = android.net.TrafficStats.getTotalRxBytes()
                val diffTx = currentTx - lastTxBytes
                val diffRx = currentRx - lastRxBytes
                lastTxBytes = currentTx
                lastRxBytes = currentRx
                
                var speedKbps = (diffTx + diffRx) / 1024.0
                if (firstRun) { speedKbps = 0.0; firstRun = false; }
                val speedText = if (speedKbps > 1024) String.format("%.1f MB/s", speedKbps / 1024.0) else String.format("%.0f KB/s", speedKbps)
                
                // Storage
                val path = android.os.Environment.getDataDirectory()
                val stat = android.os.StatFs(path.path)
                val blockSize = stat.blockSizeLong
                val totalBlocks = stat.blockCountLong
                val availableBlocks = stat.availableBlocksLong
                
                val totalSize = totalBlocks * blockSize
                val availableSize = availableBlocks * blockSize
                val freeGB = availableSize / (1024.0 * 1024.0 * 1024.0)
                val totalGB = totalSize / (1024.0 * 1024.0 * 1024.0)
                val percentFree = if (totalGB > 0) (freeGB / totalGB) * 100 else 0.0
                val percentUsed = 100.0 - percentFree
                
                val storageText = String.format("%.0f%% Free (%.1f GB)", percentFree, freeGB)
                val storagePercent = percentUsed.toInt()
                
                runOnUiThread {
                    val tvSyncSpeed = findViewById<TextView>(R.id.tvSyncSpeed)
                    tvSyncSpeed?.text = speedText
                    // DO NOT update tvStorageInfo here because user said Android UI uses PC's storage? Wait:
                    // "sedangkan di pc utuk storage nya itu ambil storage hp saya" -> Android UI storage uses PC's?
                    // Actually, let's keep Android UI storage as PC's storage. It was already parsed from INFO.
                }
                
                // Send Phone STATS to PC
                sendCommandToPc("STATS:$storageText:$storagePercent:$speedText")
                
            } catch (e: Exception) {
                // Ignore
            }
        }
    }
}
