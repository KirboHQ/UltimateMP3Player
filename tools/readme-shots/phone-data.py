# The demo data folder made for the phone (phone.ps1 install): <folder>/data copied to <folder>/phone/data with each song's path
# in the test app's music folder on Android, and no Windows path left.
#   python -I phone-data.py <folder> <package>
import json, ntpath, os, shutil, sys

folder, pkg = sys.argv[1], sys.argv[2]
music = f"/storage/emulated/0/Android/data/{pkg}/files/Music"
src, dst = os.path.join(folder, "data"), os.path.join(folder, "phone", "data")
shutil.rmtree(dst, ignore_errors=True)
shutil.copytree(src, dst, ignore=shutil.ignore_patterns("*.bak"))

def edit(name, change):
    path = os.path.join(dst, name)
    with open(path, encoding="utf-8-sig") as f: data = json.load(f)
    change(data)
    with open(path, "w", encoding="utf-8") as f: json.dump(data, f, ensure_ascii=False, indent=2)

def songs(lib):
    for t in lib["Tracks"]:
        if t.get("Path"): t["Path"] = music + "/" + ntpath.basename(t["Path"])
        for k in ("CachePath", "VideoPath"):
            if t.get(k): t[k] = None

edit("library.json", songs)
edit("settings.json", lambda s: s.update(MusicDir=music))

left = [n for n in ("library.json", "settings.json") if ":\\\\" in open(os.path.join(dst, n), encoding="utf-8").read()]
assert not left, f"Windows paths left in {left}"
print("phone data ready:", dst)
