package com.nexuslink.client

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Bundle
import android.widget.Toast

class TransparentActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        
        val text = intent.getStringExtra("CLIP_TEXT")
        if (text != null) {
            // Write to clipboard immediately upon activity focus
            val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            val clip = ClipData.newPlainText("Nexus", text)
            clipboard.setPrimaryClip(clip)
            
            Toast.makeText(this, "📋 Pasted from PC!", Toast.LENGTH_SHORT).show()
        }
        
        // Destroy activity instantly to maintain seamless UX
        finish()
    }
}
