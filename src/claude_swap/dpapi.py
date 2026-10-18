"""At-rest sealing for dormant credential files (``.enc``, ``.enc.prev``, stash).

On Windows the payload is encrypted with DPAPI (``CryptProtectData``, current
user scope) and stored as ``dpapi1:<base64 blob>``: only the same Windows user
on the same machine can open it. Elsewhere the historical plain base64 format is
kept (macOS backups normally live in the Keychain; Linux relies on 0600).

``unseal`` reads both formats, so stores written before this change keep
working and are re-sealed on their next write.
"""

from __future__ import annotations

import base64
import sys

PREFIX = "dpapi1:"
# Binds blobs to this tool: another DPAPI consumer running as the same user
# cannot decrypt them by accident without also knowing this value.
_ENTROPY = b"claude-swap/credential-backup/v1"
_CRYPTPROTECT_UI_FORBIDDEN = 0x1


def _crypt(data: bytes, protect: bool) -> bytes:
    import ctypes
    from ctypes import wintypes

    class Blob(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

    def blob(b: bytes) -> tuple[Blob, ctypes.Array]:
        buf = ctypes.create_string_buffer(b, len(b))
        return Blob(len(b), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char))), buf

    crypt32 = ctypes.WinDLL("crypt32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.LocalFree.argtypes = [ctypes.c_void_p]
    data_in, _keep1 = blob(data)
    entropy, _keep2 = blob(_ENTROPY)
    out = Blob()
    fn = crypt32.CryptProtectData if protect else crypt32.CryptUnprotectData
    fn.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.POINTER(Blob),
                   ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
    fn.restype = wintypes.BOOL
    if not fn(ctypes.byref(data_in), None, ctypes.byref(entropy), None, None,
              _CRYPTPROTECT_UI_FORBIDDEN, ctypes.byref(out)):
        raise OSError(ctypes.get_last_error(), "DPAPI call failed")
    try:
        return ctypes.string_at(out.pbData, out.cbData)
    finally:
        kernel32.LocalFree(out.pbData)


def seal(text: str) -> str:
    raw = text.encode("utf-8")
    if sys.platform == "win32":
        return PREFIX + base64.b64encode(_crypt(raw, protect=True)).decode("ascii")
    return base64.b64encode(raw).decode("ascii")


def unseal(encoded: str) -> str:
    """Inverse of ``seal``; raises on corrupt or foreign data."""
    encoded = encoded.strip()
    if encoded.startswith(PREFIX):
        if sys.platform != "win32":
            raise ValueError("DPAPI-sealed credential can only be opened on Windows")
        blob = base64.b64decode(encoded[len(PREFIX):], validate=True)
        return _crypt(blob, protect=False).decode("utf-8")
    return base64.b64decode(encoded, validate=True).decode("utf-8")
