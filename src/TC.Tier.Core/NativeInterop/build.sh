#!/bin/bash
# ============================================================
# atomic128 跨平台编译脚本
# 用法: ./build.sh [win-x64|win-arm64|linux-x64|linux-arm64|all]
#
# 前置依赖:
#   - clang + lld（所有平台）
#   - mingw-w64（win-x64 本地编译）
#   - 交叉编译 win-arm64：clang + lld-link（无需 mingw ARM64 sysroot）
#   - macOS 无法交叉编译（需 macOS 原生工具链）
# ============================================================
set -euo pipefail
cd "$(dirname "$0")"

TARGET="${1:-all}"

compile_win_x64() {
    echo "=== win-x64 ==="
    if command -v x86_64-w64-mingw32-gcc &>/dev/null; then
        x86_64-w64-mingw32-gcc -shared -O2 -o lib/win-x64/atomic128.dll atomic128.c
    else
        clang --target=x86_64-w64-windows-gnu -fuse-ld=lld -shared -O2 \
            -o lib/win-x64/atomic128.dll atomic128.c
    fi
    echo "  -> lib/win-x64/atomic128.dll"
}

compile_win_arm64() {
    echo "=== win-arm64 (cross via clang + lld-link) ==="
    mkdir -p lib/win-arm64
    clang --target=aarch64-w64-windows-gnu -c -O2 \
        -o lib/win-arm64/atomic128.o atomic128.c -D__aarch64__
    lld-link /DLL /OUT:lib/win-arm64/atomic128.dll \
        lib/win-arm64/atomic128.o /NOENTRY
    rm -f lib/win-arm64/atomic128.o lib/win-arm64/atomic128.lib
    echo "  -> lib/win-arm64/atomic128.dll"
}

compile_linux_x64() {
    echo "=== linux-x64 ==="
    mkdir -p lib/linux-x64
    clang -shared -O2 -o lib/linux-x64/libatomic128.so atomic128.c -fPIC
    echo "  -> lib/linux-x64/libatomic128.so"
}

compile_linux_arm64() {
    echo "=== linux-arm64 (cross via clang) ==="
    mkdir -p lib/linux-arm64
    clang --target=aarch64-linux-gnu -shared -O2 \
        -o lib/linux-arm64/libatomic128.so atomic128.c -fPIC -D__aarch64__
    echo "  -> lib/linux-arm64/libatomic128.so"
}

case "$TARGET" in
    win-x64)      compile_win_x64 ;;
    win-arm64)    compile_win_arm64 ;;
    linux-x64)    compile_linux_x64 ;;
    linux-arm64)  compile_linux_arm64 ;;
    all)
        compile_win_x64
        compile_win_arm64
        compile_linux_x64
        compile_linux_arm64
        ;;
    *)
        echo "Usage: $0 [win-x64|win-arm64|linux-x64|linux-arm64|all]"
        exit 1
        ;;
esac

echo "=== Done ==="
