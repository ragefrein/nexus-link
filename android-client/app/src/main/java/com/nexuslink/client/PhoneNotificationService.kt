package com.nexuslink.client

import android.app.Notification
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import org.json.JSONObject

/** The user grants access in Android Notification Access settings. */
class PhoneNotificationService : NotificationListenerService() {
    private val recent = object : LinkedHashMap<String, String>(128, .75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, String>?) = size > 128
    }
    override fun onNotificationPosted(sbn: StatusBarNotification) {
        if (sbn.packageName == packageName || SyncService.connectedIp == null) return
        if (!getSharedPreferences("NexusPrefs", MODE_PRIVATE).getBoolean("NOTIFICATION_SYNC", false)) return
        val notification = sbn.notification
        if (notification.flags and (Notification.FLAG_ONGOING_EVENT or Notification.FLAG_GROUP_SUMMARY) != 0) return
        val title = notification.extras.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty().take(256)
        val text = (notification.extras.getCharSequence(Notification.EXTRA_BIG_TEXT)
            ?: notification.extras.getCharSequence(Notification.EXTRA_TEXT))?.toString().orEmpty().take(2048)
        if (title.isBlank() && text.isBlank()) return
        val signature = "$title\n$text"
        if (recent[sbn.key] == signature) return
        recent[sbn.key] = signature
        val app = try { packageManager.getApplicationLabel(packageManager.getApplicationInfo(sbn.packageName, 0)).toString() }
            catch (_: Exception) { sbn.packageName }
        SyncService.active?.send("NOTIFY:" + JSONObject().put("app", app).put("title", title).put("text", text).toString())
    }
    override fun onNotificationRemoved(sbn: StatusBarNotification) { recent.remove(sbn.key) }
}
