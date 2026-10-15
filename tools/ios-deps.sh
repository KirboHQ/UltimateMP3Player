#!/bin/bash
# The iPhone app's download engines, which aren't in git (too big) and can only be built on a Mac: Python for iOS
# (python.org's official XCframework), yt-dlp (the same zip the Android app runs with Python), certifi (the certificates
# Python needs for HTTPS) and LAME (the MP3 encoder, built here as a framework). Each pinned to a version and its SHA-256.
#
#   tools/ios-deps.sh              downloads and builds them into deps/ios (kept until a version here changes)
#   tools/ios-deps.sh install APP  puts them into a built UltimateMP3Player.app, where Platform/Python.cs looks:
#       APP/Frameworks/Python.framework       Python itself
#       APP/Frameworks/<module>.framework     Python's binary modules (_ssl, _ctypes...): iOS loads code only from
#                                             frameworks; each .so becomes one, a .fwork file in its place points to it
#       APP/python/lib/python3.x              the standard library
#       APP/python/app                        yt-dlp and certifi, next to ump_ios.py (put there by the build)
#       APP/Frameworks/mp3lame.framework      LAME
#
# Used by .github/workflows/ios.yml. On a Mac, by hand: build the app, then "tools/ios-deps.sh install <the .app>".
# The .app stays unsigned: SideStore signs it, frameworks included, when it installs it.
set -euo pipefail

PYTHON_VERSION=3.15.0
PYTHON_URL="https://www.python.org/ftp/python/$PYTHON_VERSION/python-$PYTHON_VERSION-iOS-XCframework.tar.gz"
PYTHON_SHA256=68e98ab35cc0643ba3a227a8c7768da45c2a5e67dbe1bc26598537e96680fd19

YTDLP_VERSION=2026.08.19
YTDLP_URL="https://github.com/yt-dlp/yt-dlp/releases/download/$YTDLP_VERSION/yt-dlp"
YTDLP_SHA256=1fa6733c37ea6fb51c99ad8fe785e7b7e5f3246c9b980230329d4fb72ed8d4d6

CERTIFI_VERSION=2026.7.22
CERTIFI_URL="https://files.pythonhosted.org/packages/0b/a7/71ac2cff56fec219ed242bb11b8efb69fcc4bec75db06fb7bfe35de520e6/certifi-$CERTIFI_VERSION-py3-none-any.whl"
CERTIFI_SHA256=62f22742b58a1a33014a2b6b706588a8d7e2a88ae7bd1a6ebe8c992928483775

LAME_VERSION=3.100
LAME_URL="https://downloads.sourceforge.net/project/lame/lame/$LAME_VERSION/lame-$LAME_VERSION.tar.gz"
LAME_MIRROR="https://deb.debian.org/debian/pool/main/l/lame/lame_$LAME_VERSION.orig.tar.gz"
LAME_SHA256=ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e

# As the app (SupportedOSPlatformVersion in the csproj).
MIN_IOS=15.0
BUNDLE_PREFIX=com.kirbohq.ultimatemp3player

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEPS="$ROOT/deps/ios"
STAMP="python-$PYTHON_VERSION yt-dlp-$YTDLP_VERSION certifi-$CERTIFI_VERSION lame-$LAME_VERSION"

sha256() { shasum -a 256 "$1" | cut -d' ' -f1; }

# url sha256 file [mirror]: downloaded unless already there, then checked.
fetch() {
    local url=$1 sha=$2 out=$3 mirror=${4:-}
    if [ -f "$out" ] && [ "$(sha256 "$out")" = "$sha" ]; then return 0; fi
    rm -f "$out"
    echo "Downloading $url"
    if ! curl -fsSL --retry 4 --retry-delay 5 -o "$out" "$url"; then
        if [ -z "$mirror" ]; then echo "Download failed: $url" >&2; exit 1; fi
        echo "Downloading $mirror"
        curl -fsSL --retry 4 --retry-delay 5 -o "$out" "$mirror"
    fi
    local got
    got=$(sha256 "$out")
    if [ "$got" != "$sha" ]; then
        echo "SHA-256 mismatch for $out: $got (expected $sha)" >&2
        rm -f "$out"
        exit 1
    fi
}

# file executable identifier version: a framework's Info.plist.
framework_plist() {
    cat > "$1" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleExecutable</key>
    <string>$2</string>
    <key>CFBundleIdentifier</key>
    <string>$3</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>$2</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleShortVersionString</key>
    <string>$4</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>CFBundleSupportedPlatforms</key>
    <array>
        <string>iPhoneOS</string>
    </array>
    <key>MinimumOSVersion</key>
    <string>$MIN_IOS</string>
</dict>
</plist>
EOF
}

# LAME's static library (its own configure, for the iPhone's processor) linked into a framework's dynamic library.
build_lame() {
    local src="$DEPS/build/lame"
    rm -rf "$src"
    mkdir -p "$src"
    tar -xzf "$1" -C "$src" --strip-components 1
    local sdk cc flags
    sdk=$(xcrun --sdk iphoneos --show-sdk-path)
    cc=$(xcrun --sdk iphoneos -f clang)
    flags="-arch arm64 -isysroot $sdk -miphoneos-version-min=$MIN_IOS -O2"
    echo "Building LAME $LAME_VERSION"
    (
        cd "$src"
        ./configure --host=arm-apple-darwin --disable-shared --enable-static --disable-frontend --disable-decoder \
            --disable-gtktest --disable-analyzer-hooks CC="$cc" CFLAGS="$flags -Wno-error=implicit-function-declaration" \
            LDFLAGS="$flags" > configure.log
        make -j"$(sysctl -n hw.ncpu)" > make.log
    )
    local fw="$DEPS/mp3lame.framework"
    rm -rf "$fw"
    mkdir -p "$fw"
    # shellcheck disable=SC2086
    "$cc" $flags -dynamiclib -install_name @rpath/mp3lame.framework/mp3lame -Wl,-all_load "$src/libmp3lame/.libs/libmp3lame.a" -o "$fw/mp3lame"
    framework_plist "$fw/Info.plist" mp3lame "$BUNDLE_PREFIX.mp3lame" "$LAME_VERSION"
    cp "$src/COPYING" "$fw/COPYING"
}

prepare() {
    if [ -f "$DEPS/.stamp" ] && [ "$(cat "$DEPS/.stamp")" = "$STAMP" ]; then
        echo "deps/ios is ready ($STAMP)"
        return 0
    fi
    local dl="$DEPS/downloads"
    mkdir -p "$dl"
    find "$DEPS" -mindepth 1 -maxdepth 1 ! -name downloads -exec rm -rf {} +
    fetch "$PYTHON_URL" "$PYTHON_SHA256" "$dl/python-$PYTHON_VERSION.tar.gz"
    fetch "$YTDLP_URL" "$YTDLP_SHA256" "$dl/yt-dlp-$YTDLP_VERSION"
    fetch "$CERTIFI_URL" "$CERTIFI_SHA256" "$dl/certifi-$CERTIFI_VERSION.whl"
    fetch "$LAME_URL" "$LAME_SHA256" "$dl/lame-$LAME_VERSION.tar.gz" "$LAME_MIRROR"

    mkdir -p "$DEPS/python" "$DEPS/app"
    tar -xzf "$dl/python-$PYTHON_VERSION.tar.gz" -C "$DEPS/python"
    test -f "$DEPS/python/Python.xcframework/ios-arm64/Python.framework/Python"
    # (a wheel is a zip)
    unzip -q "$dl/certifi-$CERTIFI_VERSION.whl" 'certifi/*' -d "$DEPS/app"
    cp "$dl/yt-dlp-$YTDLP_VERSION" "$DEPS/app/yt-dlp"
    build_lame "$dl/lame-$LAME_VERSION.tar.gz"
    echo "$STAMP" > "$DEPS/.stamp"
}

# app so base: one of Python's binary modules made into a framework, as CPython's own Xcode build step does
# (Python.xcframework/build/utils.sh, install_dylib).
module_framework() {
    local app=$1 so=$2 base=$3
    local rel=${so#"$app/"}
    local name
    name=$(echo "${rel#"$base"}" | cut -d. -f1 | tr '/' '.')
    local fw="Frameworks/$name.framework"
    mkdir -p "$app/$fw"
    framework_plist "$app/$fw/Info.plist" "$name" "$BUNDLE_PREFIX.$(echo "$name" | tr '_' '-')" "$PYTHON_VERSION"
    mv "$so" "$app/$fw/$name"
    echo "$fw/$name" > "${so%.so}.fwork"
    echo "${rel%.so}.fwork" > "$app/$fw/$name.origin"
    local privacy
    privacy="$(dirname "$so")/$(basename "$so" | cut -d. -f1).xcprivacy"
    if [ -e "$privacy" ]; then mv "$privacy" "$app/$fw/PrivacyInfo.xcprivacy"; fi
}

install_into() {
    local app=${1%/}
    if [ ! -f "$app/Info.plist" ]; then echo "Not an app: $app" >&2; exit 1; fi
    prepare
    local xc="$DEPS/python/Python.xcframework"
    local slice="$xc/ios-arm64"
    local version
    version=$(ls "$xc/lib" | grep -E '^python3\.[0-9]+$' | head -1)
    echo "Installing Python ($version), yt-dlp, certifi and LAME into $app"

    mkdir -p "$app/Frameworks"
    rm -rf "$app/Frameworks/Python.framework" "$app/Frameworks/mp3lame.framework"
    cp -R "$slice/Python.framework" "$app/Frameworks/"
    # (only the library is needed to run)
    rm -rf "$app/Frameworks/Python.framework/Headers"
    cp -R "$DEPS/mp3lame.framework" "$app/Frameworks/"

    # The standard library: the shared part, then the device's (its binary modules), without the compiled files (Python
    # compiles into the app's caches: Platform/Python.cs) and without what no app uses.
    # (plain copies, then deletions: macOS's own rsync, openrsync, matches exclude patterns its own way)
    local lib="$app/python/lib"
    rm -rf "$lib"
    mkdir -p "$lib"
    cp -R "$xc/lib/." "$lib/"
    cp -R "$slice/lib-arm64/." "$lib/"
    find "$lib" -name '__pycache__' -type d -prune -exec rm -rf {} +
    find "$lib" -name 'libpython*.dylib' -delete
    local unused
    for unused in test idlelib tkinter turtledemo ensurepip __phello__; do rm -rf "${lib:?}/$version/$unused"; done
    # Python's own tests and examples among the binary modules.
    find "$lib/$version/lib-dynload" \( -name '_test*' -o -name '_ctypes_test*' -o -name '_xxtestfuzz*' -o -name 'xx*' \) -delete
    find "$lib/$version/lib-dynload" -name '*.so' | while read -r so; do
        module_framework "$app" "$so" "python/lib/$version/lib-dynload/"
    done

    mkdir -p "$app/python/app"
    cp "$DEPS/app/yt-dlp" "$app/python/app/yt-dlp"
    rm -rf "$app/python/app/certifi"
    cp -R "$DEPS/app/certifi" "$app/python/app/"
    if [ ! -f "$app/python/app/ump_ios.py" ]; then echo "ump_ios.py is missing from the app (UltimateMP3Player.iOS.csproj)" >&2; exit 1; fi
    du -sh "$app/Frameworks" "$app/python"
}

case "${1:-}" in
    install) install_into "${2:?the .app to install into}" ;;
    "") prepare ;;
    *) echo "usage: tools/ios-deps.sh [install <UltimateMP3Player.app>]" >&2; exit 2 ;;
esac
