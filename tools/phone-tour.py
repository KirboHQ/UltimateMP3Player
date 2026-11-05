#!/usr/bin/env python3
# The computer's half of the phone's tour (src/UltimateMP3Player.Android/PhoneTour.cs, built with -p:PhoneTour=true): reads what the
# app wants done from logcat and does it with adb, real touches and pictures of the screen. Usage: phone-tour.py <package> <folder> [flings]
import os, re, subprocess, sys, threading, time

ADB = os.environ.get("ADB", r"C:\Program Files\Netease\MuMuPlayer\nx_main\adb.exe")
DEVICE = os.environ.get("ADB_DEVICE", "127.0.0.1:16384")
package, folder = sys.argv[1], sys.argv[2]
os.makedirs(os.path.join(folder, "shots"), exist_ok=True)


def adb(*args, **kw):
    return subprocess.run([ADB, "-s", DEVICE, *args], **kw)


adb("shell", "am", "force-stop", package)
adb("logcat", "-c")
adb("shell", "am", "start", "-n", package + "/com.kirbohq.ultimatemp3player.MainActivity", *(["--es", "tour", sys.argv[3]] if len(sys.argv) > 3 else []), stdout=subprocess.DEVNULL)
log = open(os.path.join(folder, "tour.txt"), "w", encoding="utf-8")
reader = subprocess.Popen([ADB, "-s", DEVICE, "logcat", "-v", "raw", "-s", "UMPTOUR:I"], stdout=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")

# An app that doesn't start or dies says nothing more: without this the script would wait for it forever.
last_news = time.time()
SILENCE = 120


def watch():
    while time.time() - last_news < SILENCE:
        time.sleep(5)
    print(f"tour: nothing from the app for {SILENCE} s, stopped", flush=True)
    reader.kill()


threading.Thread(target=watch, daemon=True).start()
for line in reader.stdout:
    last_news = time.time()
    line = line.strip()
    if line.startswith("TAP "):
        adb("shell", "input", "tap", *line.split()[1:])
    elif line.startswith("PRESS "):
        x, y = line.split()[1:]
        adb("shell", "input", "swipe", x, y, x, y, "750")
    elif line.startswith("SWIPE "):
        adb("shell", "input", "swipe", *line.split()[1:])
    elif line == "BACK":
        adb("shell", "input", "keyevent", "KEYCODE_BACK")
    elif line.startswith("SHOT "):
        with open(os.path.join(folder, "shots", re.sub(r"[^a-z0-9-]", "", line[5:]) + ".png"), "wb") as f:
            adb("exec-out", "screencap", "-p", stdout=f)
    elif line and not line.startswith("-----"):
        print(line, flush=True)
        log.write(line + "\n")
        log.flush()
        if line == "tour: done":
            break
reader.kill()
adb("shell", "am", "force-stop", package)
