#!/bin/bash
# SiteWatch для macOS: сборка без Xcode-проекта — только swiftc из Xcode или Command Line Tools.
# Результат: SiteWatch.app рядом с этим файлом, универсальный (Apple Silicon + Intel), macOS 13+.
set -euo pipefail
cd "$(dirname "$0")"

APP="SiteWatch.app"
MIN_OS="13.0"

rm -rf build "$APP"
mkdir -p build

# Две архитектуры по отдельности, потом склеиваем в один файл.
for ARCH in arm64 x86_64; do
  echo "swiftc: $ARCH"
  xcrun --sdk macosx swiftc -O -swift-version 5 -target "$ARCH-apple-macos$MIN_OS" \
    -o "build/SiteWatch-$ARCH" main.swift
done
lipo -create -output build/SiteWatch build/SiteWatch-arm64 build/SiteWatch-x86_64

# Обёртка .app: исполняемый файл + Info.plist.
mkdir -p "$APP/Contents/MacOS"
cp build/SiteWatch "$APP/Contents/MacOS/SiteWatch"
cp Info.plist "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

# Подпись «ad-hoc», без сертификата разработчика: без неё программа не запустится на Apple Silicon.
codesign --force --sign - "$APP"
codesign --verify --verbose "$APP"

echo "OK: $APP"
