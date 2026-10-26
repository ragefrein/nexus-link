package com.nexuslink.client

import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.graphics.Color
import android.widget.Button
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import android.os.Build
import android.content.IntentFilter

class MainActivity : AppCompatActivity() {

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
                    updateActivityList("content_copy", "Sent Clipboard Text", "Just now", "done")
                } else {
                    Toast.makeText(this, "Clipboard is empty", Toast.LENGTH_SHORT).show()
                }
            }
        }

        if (Build.VERSION.SDK_INT >= 33 && androidx.core.content.ContextCompat.checkSelfPermission(this, android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 202)
        }
        androidx.core.content.ContextCompat.startForegroundService(this, Intent(this, SyncService::class.java))

        // Load recent activity from SharedPreferences
        val prefs = getSharedPreferences("NexusPrefs", Context.MODE_PRIVATE)
        val lastTitle = prefs.getString("LAST_ACT_TITLE", null)
        val lastSub = prefs.getString("LAST_ACT_SUB", null)
        if (lastTitle != null && lastSub != null) {
            updateActivityList("folder_zip", lastTitle, lastSub, "done")
        }

        val bottomNav = findViewById<com.google.android.material.bottomnavigation.BottomNavigationView>(R.id.bottomNav)
        bottomNav.setOnItemSelectedListener { item ->
            when (item.itemId) {
                R.id.nav_settings -> {
                    startActivity(Intent(this, SettingsActivity::class.java))
                    true
                }
                else -> true
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
        if (hasFocus) SyncService.active?.applyClipboardOrSync()
    }

    private fun sendCommandToPc(packet: String) { SyncService.active?.send(packet) }

    private val events = object : android.content.BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            val message = intent.getStringExtra("message") ?: return
            when {
                message.startsWith("CONNECTED:") -> updateConnectionUI(true, message.substring(10))
                message == "DISCONNECTED" -> updateConnectionUI(false, "")
                message.startsWith("ACTIVITY:") -> updateActivityList("folder_zip", message.substring(9), "Just now", "done")
                message.startsWith("ERROR:") -> Toast.makeText(this@MainActivity, message.substring(6), Toast.LENGTH_LONG).show()
                else -> handlePcMessage(message)
            }
        }
    }
    override fun onStart() {
        super.onStart()
        androidx.core.content.ContextCompat.registerReceiver(this, events, IntentFilter(SyncService.EVENT), androidx.core.content.ContextCompat.RECEIVER_NOT_EXPORTED)
        updateConnectionUI(SyncService.connectedIp != null, SyncService.connectedIp.orEmpty())
    }
    override fun onStop() { unregisterReceiver(events); super.onStop() }

    private fun updateConnectionUI(isConnected: Boolean, ip: String) {
        runOnUiThread {
            val tvConnectionStatus = findViewById<TextView>(R.id.tvConnectionStatus)
            if (isConnected) {
                tvConnectionStatus.text = "Connected to $ip"
            } else {
                tvConnectionStatus.text = "Searching for PC..."
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

    private fun handlePcMessage(incomingMsg: String) {
                if (incomingMsg.startsWith("CLIP:")) {
                    val clipboardContent = incomingMsg.substring(5)
                    lastClipboardText = clipboardContent
                    
                    if (hasWindowFocus()) SyncService.active?.applyClipboardOrSync()
                    updateActivityList("content_paste", "Clipboard Text", "Received from PC", "done")
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
                            tvConnectionStatus.text = "Connected to\n$hostname"
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

                            // Set color (always blue)
                            vBatteryFill.background?.setTint(android.graphics.Color.parseColor("#00459A"))
                        }
                    }
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

}
