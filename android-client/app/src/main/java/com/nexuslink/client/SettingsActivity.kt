package com.nexuslink.client

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.widget.Button
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.widget.Toolbar

class SettingsActivity : AppCompatActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)

        val toolbar = findViewById<Toolbar>(R.id.toolbar)
        toolbar.setNavigationOnClickListener {
            finish()
        }

        val tvCurrentDirectory = findViewById<TextView>(R.id.tvCurrentDirectory)
        val btnSelectDirectory = findViewById<Button>(R.id.btnSelectDirectory)

        val prefs = getSharedPreferences("NexusPrefs", Context.MODE_PRIVATE)
        val savedDir = prefs.getString("DOWNLOAD_DIR", "Default (Downloads)")
        tvCurrentDirectory.text = savedDir

        btnSelectDirectory.setOnClickListener {
            val intent = Intent(Intent.ACTION_OPEN_DOCUMENT_TREE)
            intent.addFlags(
                Intent.FLAG_GRANT_READ_URI_PERMISSION or
                Intent.FLAG_GRANT_WRITE_URI_PERMISSION or
                Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or
                Intent.FLAG_GRANT_PREFIX_URI_PERMISSION
            )
            startActivityForResult(intent, 201)
        }
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == 201 && resultCode == Activity.RESULT_OK) {
            val treeUri = data?.data
            if (treeUri != null) {
                val takeFlags = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                contentResolver.takePersistableUriPermission(treeUri, takeFlags)

                val prefs = getSharedPreferences("NexusPrefs", Context.MODE_PRIVATE)
                prefs.edit().putString("DOWNLOAD_DIR_URI", treeUri.toString()).apply()
                
                // Decode path for better readability
                val decodedPath = android.net.Uri.decode(treeUri.toString())
                val displayPath = decodedPath.substringAfterLast(":")
                
                prefs.edit().putString("DOWNLOAD_DIR", displayPath).apply()
                
                val tvCurrentDirectory = findViewById<TextView>(R.id.tvCurrentDirectory)
                tvCurrentDirectory.text = displayPath
            }
        }
    }
}
