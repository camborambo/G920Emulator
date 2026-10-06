import os
import sqlite3

db = os.path.join(os.environ["APPDATA"], "Cursor", "User", "globalStorage", "state.vscdb")
con = sqlite3.connect(db)
keys = [
    r[0]
    for r in con.execute("SELECT key FROM ItemTable")
    if any(s in r[0].lower() for s in ("attribut", "coauthor", "cursoragent", "git", "agent"))
]
for k in sorted(keys)[:120]:
    print(k)
