# Security scope

Use the WPF desktop server and matching Android client. Control and file-transfer TCP connections require TLS 1.2 or newer, an explicitly paired certificate fingerprint, and a random 256-bit shared secret. Pairing credentials must be exchanged over a trusted channel. Never commit pairing codes, certificates, private keys, screenshots, received files, or Android signing keys.

The UDP discovery beacon is unauthenticated and exposes server presence. TLS validates the paired server after discovery; a different server cannot receive application data merely by answering a beacon. Limit firewall access to private networks. Control handshakes have a deadline, control connections are capped, transfer concurrency is bounded, and packet lengths are checked before allocating payload buffers. Authenticated peers can still send large files; monitor available disk space.

Clipboard and notification forwarding expose the contents to the authorized paired computer by design. Each feature should be enabled only on devices you own and trust. Notification access is opt-in. Clipboard access to other Android apps uses separately authorized ADB; revoke ADB permissions in Developer options when no longer needed. Do not expose classic unencrypted ADB TCP ports to untrusted networks. Wireless debugging uses Android's own pairing and transport.

The desktop pairing secret is encrypted using the current Windows user's certificate key. Android excludes the pairing preferences from cloud and device-transfer backup. Clearing the desktop encrypted secret while the desktop is stopped revokes all LAN pairings after restart. All phones sharing one code have the same trust; individual phone revocation is not implemented. Revoke ADB trust separately.

The C++ `pc-server` is an archived, unauthenticated prototype. Its CMake build is disabled by default. Its explicit unsafe-build switch is only for isolated legacy development and is unsupported for normal use.

Personal transfer media and logs that were previously committed are excluded from the current tree. Removing paths in a new commit does **not** erase earlier Git history, forks, caches, or existing public copies. History removal requires a separate coordinated operation; no history rewrite or force push is part of this change.

Validation includes transport tests with TLS, rejected secrets/certificate pins, framing limits, interrupted transfers, receipt acknowledgements, and file isolation. This is not a full penetration test or a guarantee that every dependency or device is vulnerability-free. The bundled scrcpy client/server use protocol version 2.4; upgrading must update and retest the control-only bridge as well. Keep the Android OS, Windows, SDKs, and dependencies maintained.

To report an issue, use the repository's private vulnerability reporting feature if available. Do not publish credentials or private user data in an issue.
