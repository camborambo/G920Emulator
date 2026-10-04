import winreg

base = r"SYSTEM\CurrentControlSet\Enum\HID\VID_046D&PID_C262&REV_9601&Col01\2&1ba86156&3&0000\Filters"
try:
    k = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, base, 0, winreg.KEY_READ)
    i = 0
    while True:
        try:
            print("subkey:", repr(winreg.EnumKey(k, i)))
            i += 1
        except OSError:
            break
    winreg.CloseKey(k)
except Exception as e:
    print("Filters open fail:", e)

path = base + r"\*Upper"
try:
    k = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path, 0, winreg.KEY_ALL_ACCESS)
    i = 0
    while True:
        try:
            n, v, t = winreg.EnumValue(k, i)
            print("value:", repr(n), "type", t, "data", v)
            i += 1
        except OSError:
            break
    try:
        winreg.DeleteValue(k, "hidgamepad")
        print("DELETED hidgamepad")
    except Exception as e:
        print("delete fail:", e)
    winreg.CloseKey(k)
except Exception as e:
    print("*Upper open fail:", e)
