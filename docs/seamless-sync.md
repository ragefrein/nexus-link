# Seamless sync: pengaturan dan pengujian

Perubahan protokol v2 memerlukan **desktop dan APK Android diperbarui bersamaan**. Versi lama tidak kompatibel dengan framing dan TLS/pairing wajib.

## Menjalankan versi baru

Tutup NexusLink desktop lama sebelum menjalankan `dotnet run --project pc-server-wpf/NexusLink.csproj` dari root proyek. SDK yang dipakai adalah .NET 10.

Build Android: jalankan `./gradlew.bat assembleDebug` dari `android-client`. APK berada di `android-client/app/build/outputs/apk/debug/app-debug.apk`. Pasang sebagai pembaruan lalu buka NexusLink sekali. Koneksi sekarang dimiliki foreground service, sehingga keluar dari halaman utama tidak memutus koneksi. Tombol **Putuskan** pada notifikasi layanan menghentikannya; buka ulang aplikasi untuk menyambung. Force stop atau penghentian oleh sistem juga memerlukan membuka ulang aplikasi. Beberapa vendor membatasi layanan ketika penghemat baterai aktif.

Jika executable desktop lama sedang terkunci, `dotnet build pc-server-wpf/NexusLink.csproj -p:OutputPath=bin/SecureVerification/` membuat build terpisah. Jangan jalankan dua server bersamaan karena keduanya memakai port yang sama.

## Pairing koneksi LAN (wajib sekali)

1. Di desktop baru, klik **Salin kode pairing laptop**.
2. Pindahkan kode itu ke HP melalui jalur tepercaya, misalnya paste melalui mirror pada ADB yang sudah diotorisasi. Jangan kirim ke chat publik atau masukkan ke repository.
3. Di Settings NexusLink Android, tempel kode lengkap pada **Kode pairing dari desktop**, lalu klik **Simpan pairing laptop**. Koneksi akan mencoba kembali otomatis.

Kode memuat pin SHA-256 sertifikat laptop dan rahasia acak 256 bit. Setiap koneksi TCP kontrol/file memakai TLS 1.2/1.3, memverifikasi sertifikat yang dipasangkan, lalu memverifikasi rahasia di dalam TLS. Klien tanpa pairing, rahasia salah, atau sertifikat berbeda ditolak sebelum data aplikasi diproses. UDP discovery hanya mengumumkan keberadaan server; identitas tetap diverifikasi pada TLS. Tidak ada fallback plaintext.

Kunci privat sertifikat desktop disimpan oleh Windows Certificate Store per pengguna. Rahasia pairing di `%LOCALAPPDATA%/NexusLink/pairing-secret.enc` dienkripsi memakai kunci sertifikat tersebut. Pengaturan HP disimpan di penyimpanan privat aplikasi dan dikecualikan dari backup/transfer data Android. Untuk mencabut semua pairing LAN, tutup desktop, hapus hanya file `pairing-secret.enc` tersebut, lalu buka ulang dan pasangkan ulang HP. Otorisasi ADB harus dicabut terpisah melalui Developer options HP. Semua HP yang memakai kode yang sama memiliki akses yang sama; belum ada daftar/cabut perangkat individual.

## Clipboard otomatis HP → laptop

Android 10+ membatasi akses clipboard aplikasi latar belakang. Foreground service saja tidak menghapus pembatasan tersebut. NexusLink menggunakan **ADB yang diotorisasi pengguna** dan server scrcpy 2.4 bawaan sebagai kanal kontrol clipboard saja: tidak merekam layar, mengalirkan audio, membuka Activity transparan, atau memunculkan jendela mirror.

1. Di HP, aktifkan Developer options dan USB debugging. Hubungkan USB dan setujui dialog otorisasi laptop.
2. Di desktop NexusLink, centang **Clipboard HP otomatis**. Jika hanya satu perangkat ADB tersedia, kolom perangkat boleh kosong. Jika ada beberapa koneksi/perangkat, isi serial yang tepat dari `adb devices -l`.
3. Tunggu status **Clipboard otomatis aktif melalui ADB**. Salin teks dari aplikasi lain di HP, kemudian paste di laptop. Clipboard teks dari laptop juga diteruskan melalui kanal kontrol ini tanpa menekan paste di HP.

Untuk Wireless debugging Android 11+, lakukan pairing sekali memakai ADB bawaan, dari PowerShell di root proyek:

```powershell
$adbPath = '.\pc-server-wpf\scrcpy\scrcpy-win64-v2.4\adb.exe'
& $adbPath pair IP_HP:PORT_PAIRING
# Masukkan kode pairing dari halaman Wireless debugging HP.
& $adbPath connect IP_HP:PORT_KONEKSI
& $adbPath devices -l
```

Ganti placeholder dengan alamat yang terlihat di HP. Port pairing dan port koneksi berbeda. Isi **IP:port koneksi** atau serial ADB di kolom perangkat NexusLink. Port dapat berubah setelah Wireless debugging atau HP dimulai ulang. NexusLink tidak mengasumsikan port 5555 dan tidak mengotorisasi debugging secara diam-diam.

Tanpa ADB, clipboard tetap tersinkron saat NexusLink Android memiliki fokus. Clipboard dari PC yang diterima di latar belakang disimpan sementara dan diterapkan saat NexusLink dibuka kembali. Auto-sync tidak aktif secara default; pilihan desktop disimpan per pengguna Windows. Clipboard melalui scrcpy memiliki batas sekitar 256 KiB teks UTF-8; gambar dan file dalam clipboard belum didukung. Kompatibilitas ADB dengan perangkat/vendor tetap mengikuti scrcpy 2.4.

## Notifikasi HP → Windows

Di NexusLink Android → Settings, aktifkan **Kirim notifikasi HP ke laptop**, lalu berikan **Notification access** di pengaturan Android. Koneksi layanan NexusLink harus aktif. Di desktop, aktifkan **Tampilkan notifikasi HP di Windows**.

Notifikasi baru/berubah diteruskan lewat koneksi aktif dan ditampilkan menggunakan notifikasi native tray Windows, serta dicatat di aktivitas NexusLink. Pengaturan Do not disturb/Focus Assist Windows dapat menahan popup. Ini bukan inbox yang menyinkronkan histori, penghapusan, balasan, atau aksi notifikasi. Notifikasi permanen layanan, ringkasan grup, duplikat, dan notifikasi NexusLink sendiri disaring. Notifikasi saat laptop tidak terhubung tidak ditimbun untuk dikirim belakangan. Android/vendor dapat menyembunyikan isi sensitif.

Fitur ini mati secara default di HP. Matikan switch untuk berhenti meneruskan atau cabut Notification access. Transport LAN sekarang mewajibkan TLS dan pairing; izin ADB terpisah dari koneksi LAN ini. Gunakan jaringan pribadi dan bagikan kode hanya ke perangkat milik sendiri.

## Mirroring

Profil **Wi-Fi cepat** memakai H.264, maksimum sisi 1280 px, bitrate 4 Mbps, target 60 FPS, dan display buffer 0 ms. Profil **USB kualitas tinggi** memakai maksimum sisi 1920 px dan bitrate 12 Mbps. Pemilihan profil tidak memindahkan transport: pilih serial USB di kolom perangkat untuk benar-benar memakai USB.

Audio dimatikan pada kedua profil untuk mengurangi pekerjaan dan bandwidth. Layar HP tidak dipaksa mati. Satu jendela mirror dibuka per aplikasi desktop. Perintah diluncurkan langsung dengan argumen terpisah tanpa shell. Kanal clipboard terpisah tidak memerlukan video encoder tambahan. Tidak ada jaminan FPS/delay tertentu: hasil bergantung encoder HP, temperatur, jaringan, dan decoder laptop. Bandingkan Wi-Fi cepat dengan USB, terutama saat mengirim file besar pada Wi-Fi yang sama.

## Transfer file

Upload HP → PC langsung membuka kanal data 5052 dan mengirim metadata berframing di kanal tersebut; handshake kontrol dan koneksi probe tambahan dihilangkan. Setiap upload punya metadata sendiri sehingga transfer bersamaan tidak berbagi nama/ukuran global. Download PC → HP memakai ID acak sekali pakai yang terikat IP penerima dan kedaluwarsa setelah dua menit. Desktop meminta tepat satu HP terhubung saat memilih tujuan file.

Kedua arah memakai streaming buffer 256 KiB, memeriksa jumlah byte, mendukung file kosong, serta menunggu ACK penerima sebelum melaporkan selesai. Desktop menulis file sementara lalu menggantinya menjadi file final hanya setelah semua byte diterima. File dengan nama sama tidak menimpa file lama. File yang terputus tidak ditandai sukses. Ini validasi panjang/ACK, belum checksum, resume, atau transfer paralel per bagian file.

Folder desktop: `received_files` di samping executable. Android 10+ memakai `Download/NexusLink` melalui MediaStore jika belum memilih folder khusus. Android 7–9 memakai folder Downloads khusus aplikasi tanpa izin penyimpanan luas; pilih folder sendiri di Settings jika ingin lokasi lain. Penyedia dokumen yang tidak melaporkan ukuran perlu staging ke cache sebelum upload. Kecepatan file besar dibatasi throughput jaringan dan penyimpanan; tidak ada klaim transfer instan atau peningkatan persentase sebelum benchmark.

## Validasi

```powershell
dotnet build pc-server-wpf/NexusLink.csproj -p:OutputPath=bin/SecureVerification/
dotnet run --project tests/TransportChecks/TransportChecks.csproj
cd android-client
.\gradlew.bat assembleDebug
```

Harness transport menguji Unicode/multiline, pesan TCP terfragmentasi/tergabung, panjang invalid, putus di tengah frame, upload bersamaan, integritas byte, file kosong, nama duplikat, cleanup transfer terputus, download dengan ACK, dan penolakan ID unduhan yang digunakan ulang, rahasia pairing salah, dan pin sertifikat berbeda.

Opsional, untuk perangkat ADB yang sudah diotorisasi:

```powershell
dotnet run --project tests/TransportChecks/TransportChecks.csproj -- --adb-smoke SERIAL_ADB
```

Smoke test memeriksa handshake dan kelangsungan kanal kontrol tanpa video/audio selama tiga detik, tanpa mencatat atau mengganti isi clipboard. Ini belum menguji copy/paste manual, notifikasi, atau FPS.

Uji perangkat setelah memasang APK baru dan membuka desktop baru: salin teks panjang/multiline dari aplikasi HP lain; kirim notifikasi saat NexusLink Android di latar belakang; transfer file kosong/kecil/besar dua arah dan bandingkan hash sumber/hasil; putuskan Wi-Fi di tengah transfer lalu periksa file parsial; bandingkan frame rate mirror selama 30 detik pada tiap profil. Jalankan ulang hanya jika hasil gagal atau kode terkait berubah.

Referensi: [batasan clipboard Android](https://developer.android.com/about/versions/10/privacy/changes), [NotificationListenerService](https://developer.android.com/reference/android/service/notification/NotificationListenerService), [protokol server scrcpy 2.4](https://github.com/Genymobile/scrcpy/tree/v2.4/server/src/main/java/com/genymobile/scrcpy), [pengaturan video scrcpy 2.4](https://github.com/Genymobile/scrcpy/blob/v2.4/doc/video.md).
