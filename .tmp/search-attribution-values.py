import os
import sqlite3

db = os.path.join(os.environ["APPDATA"], "Cursor", "User", "globalStorage", "state.vscdb")
con = sqlite3.connect(db)
needles = ("attributeCommits", "attributePRs", "cursoragent@", "Commit Attribution", "Made-with")
for key, value in con.execute("SELECT key, value FROM ItemTable"):
    text = value.decode("utf-8", "ignore") if isinstance(value, (bytes, bytearray)) else str(value)
    low = text.lower()
    if any(n.lower() in low or n.lower() in key.lower() for n in needles):
        print("KEY:", key)
        print("VAL:", text[:500])
        print("---")
