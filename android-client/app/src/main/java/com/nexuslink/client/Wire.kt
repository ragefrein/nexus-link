package com.nexuslink.client

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import android.content.Context
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

/** Protocol v2: big-endian byte length followed by UTF-8; TCP reads are not messages. */
object Wire {
    const val MAX_PACKET = 1024 * 1024
    @Volatile private var appContext: Context? = null
    fun initialize(context: Context) { appContext = context.applicationContext }
    fun connect(ip: String, port: Int): SSLSocket {
        val prefs = requireNotNull(appContext).getSharedPreferences("NexusPrefs", Context.MODE_PRIVATE)
        val pairing = prefs.getString("PAIRING_CODE", "").orEmpty().trim()
        require(pairing.matches(Regex("[A-Fa-f0-9]{64}:[A-Fa-f0-9]{64}"))) { "Pairing diperlukan: tempel kode dari desktop di Settings HP" }
        val pin = pairing.substringBefore(':').uppercase(java.util.Locale.ROOT)
        val trust = object : X509TrustManager {
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) { throw CertificateException("Not a TLS server") }
            override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
                if (chain.isEmpty()) throw CertificateException("Missing server certificate")
                chain[0].checkValidity()
                val actual = MessageDigest.getInstance("SHA-256").digest(chain[0].encoded)
                    .joinToString("") { "%02X".format(it.toInt() and 255) }
                if (!MessageDigest.isEqual(actual.toByteArray(Charsets.US_ASCII), pin.toByteArray(Charsets.US_ASCII)))
                    throw CertificateException("Identitas laptop berbeda; pairing ditolak")
            }
        }
        val ssl = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), SecureRandom()) }
        val socket = ssl.socketFactory.createSocket() as SSLSocket
        try {
            socket.enabledProtocols = socket.supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
            socket.tcpNoDelay = true
            socket.keepAlive = true
            socket.connect(InetSocketAddress(ip, port), 2500)
            socket.soTimeout = 8000
            socket.startHandshake()
            write(socket.outputStream, "AUTH:" + pairing.substringAfter(':'))
            check(read(socket.inputStream) == "AUTH_OK") { "Pairing ditolak oleh laptop" }
            socket.soTimeout = 20000
            return socket
        } catch (e: Exception) { socket.close(); throw e }
    }
    fun write(output: OutputStream, message: String) {
        val bytes = message.toByteArray(Charsets.UTF_8)
        require(bytes.size in 1..MAX_PACKET)
        synchronized(output) {
            val data = DataOutputStream(output)
            data.writeInt(bytes.size)
            data.write(bytes)
            data.flush()
        }
    }
    fun read(input: InputStream): String {
        val data = DataInputStream(input)
        val length = data.readInt()
        require(length in 1..MAX_PACKET) { "Incompatible NexusLink protocol; update both devices" }
        val bytes = ByteArray(length)
        data.readFully(bytes)
        return String(bytes, Charsets.UTF_8)
    }
}
