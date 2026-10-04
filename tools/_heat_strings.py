from pathlib import Path

path = Path(r"D:\SteamLibrary\steamapps\common\Need for Speed Heat\NeedForSpeedHeat.exe")
data = path.read_bytes()

def dump_ascii(pat: bytes, limit: int = 25):
    idx = 0
    n = 0
    while n < limit:
        i = data.find(pat, idx)
        if i < 0:
            break
        start = max(0, i - 48)
        end = min(len(data), i + 96)
        chunk = data[start:end]
        s = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
        print(f"A@{i}: {s}")
        idx = i + 1
        n += 1

def dump_u16(text: str, limit: int = 20):
    pat = text.encode("utf-16le")
    idx = 0
    n = 0
    while n < limit:
        i = data.find(pat, idx)
        if i < 0:
            break
        start = max(0, i - 80)
        end = min(len(data), i + 160)
        raw = data[start:end]
        s = raw.decode("utf-16le", errors="ignore")
        s = "".join(ch if ch.isprintable() else "." for ch in s)
        print(f"U@{i}: {s}")
        idx = i + 2
        n += 1

for pat in [
    b"G920",
    b"G29",
    b"Driving Force",
    b"WheelLayout",
    b"ThrustmasterTX",
    b"LogitechG920",
    b"techG29",
]:
    print(f"\n=== ASCII {pat!r} ===")
    dump_ascii(pat)

for text in [
    "G920",
    "G29",
    "Driving Force",
    "WheelLayout",
    "Logitech G920",
    "Wireless Controller",
    "HID-compliant game controller",
]:
    print(f"\n=== UTF16 {text!r} ===")
    dump_u16(text)
