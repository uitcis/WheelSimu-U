#!/usr/bin/env bash
# =============================================================================
# WheelSimu 客户端：构建并安装到【真机】，自动跳过模拟器
#
# 本机存在一个伪装成三星 SM-A5360 的模拟器（经 127.0.0.1 本地端口接入 adb），
# 直接 `adb install` 会默认装到它身上。本脚本的挑选规则：
#
#   排除（命中任一即跳过）：
#     1. 序列号形如 emulator-xxxx
#     2. 序列号为回环地址（127.0.0.1 / localhost）
#     3. getprop ro.kernel.qemu 返回 1
#     4. 型号命中已知模拟器特征（SM-A5360 / SM_A5360 / a53x）
#     5. 状态不是 device（未授权 / 离线）
#   优先：厂商为 Xiaomi/Redmi 的真机排在最前，其余真机次之。
#
# 找不到真机时直接报错退出，绝不退而求其次装到模拟器上。
#
# 注意：本环境的 adb 守护进程会在两次命令之间被回收，无线的 192.168.x.x:5555
# 连接因此会丢。所以连接必须与安装在同一次执行内完成 —— 用 --connect 即可，
# 连接成功后会记住地址（tools/.last-wireless），下次不传也会自动重连。
#
# 用法：
#   tools/deploy-android.sh                      # 构建 + 安装
#   tools/deploy-android.sh --connect IP:端口     # 先连无线调试再装（会记住地址）
#   tools/deploy-android.sh --no-build           # 只安装上次构建的 APK
#   tools/deploy-android.sh --launch             # 装完自动拉起 App
#   tools/deploy-android.sh --list               # 只列出设备判定结果，不安装
#   tools/deploy-android.sh --serial X           # 指定目标（仍会拒绝模拟器）
#   tools/deploy-android.sh --forget             # 清掉记住的无线地址
#   ADB=/path/to/adb tools/deploy-android.sh
# =============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$ROOT/WheelSimu/WheelSimu/WheelSimu.csproj"
APK_DIR="$ROOT/WheelSimu/WheelSimu/bin/Release/net10.0-android"
APP_ID="com.Howell.wheelsimu"
WL_FILE="$ROOT/tools/.last-wireless"

DO_BUILD=1
DO_LAUNCH=0
LIST_ONLY=0
WANT_SERIAL=""
WANT_CONNECT=""
DO_FORGET=0

while [ $# -gt 0 ]; do
    case "$1" in
        --no-build) DO_BUILD=0 ;;
        --launch)   DO_LAUNCH=1 ;;
        --list)     LIST_ONLY=1 ;;
        --connect)  WANT_CONNECT="${2:-}"; shift ;;
        --serial)   WANT_SERIAL="${2:-}"; shift ;;
        --forget)   DO_FORGET=1 ;;
        -h|--help)  sed -n '2,33p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "未知参数: $1（--help 查看用法）" >&2; exit 2 ;;
    esac
    shift
done

RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; CYAN=$'\033[36m'; GREY=$'\033[90m'; NC=$'\033[0m'
[ -t 1 ] || { RED=; GREEN=; YELLOW=; CYAN=; GREY=; NC=; }

# ---------------------------------------------------------------- adb 定位
resolve_adb() {
    local c
    for c in "${ADB:-}" \
             "$(cygpath -u "${LOCALAPPDATA:-}" 2>/dev/null)/Android/Sdk/platform-tools/adb.exe" \
             "/c/Users/${USERNAME:-$USER}/AppData/Local/Android/Sdk/platform-tools/adb.exe"; do
        [ -n "$c" ] && [ -x "$c" ] && { printf '%s' "$c"; return 0; }
    done
    if command -v adb >/dev/null 2>&1; then command -v adb; return 0; fi
    return 1
}

ADB_BIN="$(resolve_adb)" || {
    echo "${RED}找不到 adb.exe${NC}：请设置 ADB=/path/to/adb 或把 platform-tools 加入 PATH" >&2
    exit 1
}

if [ "$DO_FORGET" -eq 1 ]; then
    rm -f "$WL_FILE"
    echo "${GREY}已清除记住的无线地址${NC}"
fi

# ---------------------------------------------------------------- 设备快照
# 输出：每行 "序列号 状态"，已剔除空行与表头
snapshot() {
    "$ADB_BIN" devices 2>/dev/null | tail -n +2 | tr -d '\r' | awk 'NF>=2 {print $1, $2}'
}

# 统计"非模拟器"且已授权的设备数（回环 / emulator- 前缀都算模拟器）
real_device_count() {
    local n=0 s st
    while read -r s st; do
        [ -n "$s" ] || continue
        [ "$st" = "device" ] || continue
        [[ "$s" == emulator-* ]] && continue
        [[ "$s" == 127.0.0.1* || "$s" == localhost* ]] && continue
        n=$((n + 1))
    done < <(snapshot)
    printf '%s' "$n"
}

# 无线连接 + 等待手机授权
connect_wireless() {
    local tgt="$1" i st
    echo "  ${GREY}连接 $tgt ...${NC}"
    "$ADB_BIN" connect "$tgt" 2>&1 | tr -d '\r' | sed "s/^/    /"
    for i in $(seq 1 40); do
        st="$(snapshot | awk -v s="$tgt" '$1==s{print $2}')"
        case "$st" in
            device) echo "    ${GREEN}已连接并授权${NC}"; return 0 ;;
            unauthorized) printf "    ${YELLOW}等待手机点「允许」... %ss${NC}\r" "$((i * 3))" ;;
        esac
        [ -z "$st" ] && "$ADB_BIN" connect "$tgt" >/dev/null 2>&1
        sleep 3
    done
    printf '%*s\r' 40 ''
    echo "    ${RED}连接超时：手机上没授权，或端口已变（重开无线调试会换端口）${NC}" >&2
    return 1
}

# ---------------------------------------------------------------- 建立连接
echo "${CYAN}[1/4] 连接设备...${NC}"
if [ -n "$WANT_CONNECT" ]; then
    if connect_wireless "$WANT_CONNECT"; then
        printf '%s\n' "$WANT_CONNECT" > "$WL_FILE"
    fi
elif [ "$(real_device_count)" -eq 0 ] && [ -s "$WL_FILE" ]; then
    saved="$(tr -d '\r\n' < "$WL_FILE")"
    echo "  ${GREY}未发现真机，尝试重连上次的无线地址 $saved${NC}"
    connect_wireless "$saved" >/dev/null 2>&1 || true
else
    echo "  ${GREY}已有可用真机连接，跳过${NC}"
fi

# ---------------------------------------------------------------- 构建
if [ "$LIST_ONLY" -eq 0 ] && [ "$DO_BUILD" -eq 1 ]; then
    echo "${CYAN}[2/4] 构建 Release...${NC}"
    if ! command -v dotnet >/dev/null 2>&1; then
        echo "${YELLOW}找不到 dotnet，跳过构建（改用 --no-build 可显式跳过）${NC}" >&2
    else
        MSBUILDDISABLENODEREUSE=1 dotnet build "$CSPROJ" -c Release -nodereuse:false --nologo -v q || {
            echo "${RED}构建失败${NC}" >&2; exit 1; }
    fi
elif [ "$LIST_ONLY" -eq 0 ]; then
    echo "${GREY}[2/4] 跳过构建（--no-build）${NC}"
fi

# ---------------------------------------------------------------- APK 定位
APK=""
APK_ADB=""      # adb.exe 是 Windows 程序，必须喂 Windows 路径（G:\...），不能给 /g/...
if [ "$LIST_ONLY" -eq 0 ]; then
    APK="$(ls -t "$APK_DIR"/*-Signed.apk 2>/dev/null | head -1)"
    [ -n "$APK" ] || { echo "${RED}未找到已签名 APK${NC}：$APK_DIR" >&2; exit 1; }
    if command -v cygpath >/dev/null 2>&1; then
        APK_ADB="$(cygpath -w "$APK")"
    else
        APK_ADB="$APK"
    fi
fi

# ---------------------------------------------------------------- 设备判定
echo "${CYAN}[3/4] 挑选真机...${NC}"

FAKE_MODELS=" SM-A5360 SM_A5360 a53x "

CANDIDATES=""   # 每行: 优先级|序列号|型号|厂商
SKIPPED=""      # 每行: 序列号|原因

while read -r serial state; do
    [ -n "$serial" ] || continue
    reason=""

    if [ "$state" != "device" ]; then
        reason="状态为 $state（请在手机上确认调试授权）"
    elif [[ "$serial" == emulator-* ]]; then
        reason="emulator- 前缀"
    elif [[ "$serial" == 127.0.0.1* || "$serial" == localhost* ]]; then
        reason="回环地址接入（模拟器常用）"
    else
        qemu="$("$ADB_BIN" -s "$serial" shell getprop ro.kernel.qemu 2>/dev/null | tr -d '\r\n')"
        if [ "$qemu" = "1" ]; then
            reason="ro.kernel.qemu=1"
        else
            model="$("$ADB_BIN" -s "$serial" shell getprop ro.product.model 2>/dev/null | tr -d '\r\n')"
            manu="$("$ADB_BIN" -s "$serial" shell getprop ro.product.manufacturer 2>/dev/null | tr -d '\r\n')"
            if [ -n "$model" ] && [[ "$FAKE_MODELS" == *" $model "* ]]; then
                reason="型号 $model 命中已知模拟器特征"
            else
                if [[ "$manu" == *Xiaomi* || "$manu" == *Redmi* || "$model" == *Redmi* ]]; then
                    prio=0
                else
                    prio=1
                fi
                CANDIDATES="${CANDIDATES}${prio}|${serial}|${model:-未知型号}|${manu:-未知厂商}"$'\n'
            fi
        fi
    fi

    [ -n "$reason" ] && SKIPPED="${SKIPPED}${serial}|${reason}"$'\n'
done < <(snapshot)

if [ -n "$SKIPPED" ]; then
    echo "${YELLOW}  已跳过：${NC}"
    while IFS='|' read -r s r; do
        [ -n "$s" ] && echo "    ${YELLOW}-${NC} $s  ${GREY}($r)${NC}"
    done <<< "$SKIPPED"
fi

if [ -n "$WANT_SERIAL" ]; then
    TARGET="$(printf '%s' "$CANDIDATES" | grep -F -- "|${WANT_SERIAL}|" | head -1)"
    [ -n "$TARGET" ] || { echo "${RED}指定设备 $WANT_SERIAL 不是可用真机（未连接/未授权/是模拟器）${NC}" >&2; exit 1; }
else
    TARGET="$(printf '%s' "$CANDIDATES" | sort | head -1)"
fi

if [ -z "$TARGET" ]; then
    cat >&2 <<EOF

${RED}未找到可用真机，已中止（不会装到模拟器上）。${NC}
连接真机（Redmi）的两种方式：
  A. USB：手机开启「开发者选项 → USB 调试」，插线后在手机上允许本机调试
  B. 无线：手机「开发者选项 → 无线调试」开启，然后
       tools/deploy-android.sh --connect <IP:端口>       # 脚本会等你在手机上点「允许」
     若提示需要配对码：先 adb pair <IP:配对端口>，再输入手机上显示的 6 位配对码
EOF
    exit 1
fi

IFS='|' read -r _ SERIAL MODEL MANU <<< "$TARGET"
echo "  ${GREEN}目标真机: $MANU $MODEL [$SERIAL]${NC}"

if [ "$LIST_ONLY" -eq 1 ]; then
    echo "${GREY}--list：仅列出判定结果，未执行安装${NC}"
    exit 0
fi

# ---------------------------------------------------------------- 安装
echo "${CYAN}[4/4] 安装 ${GREY}$(basename "$APK")${NC}"
"$ADB_BIN" -s "$SERIAL" install -r "$APK_ADB" || { echo "${RED}安装失败${NC}" >&2; exit 1; }

if [ "$DO_LAUNCH" -eq 1 ]; then
    "$ADB_BIN" -s "$SERIAL" shell monkey -p "$APP_ID" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1 \
        && echo "  ${GREEN}已启动 App${NC}"
fi

echo "${GREEN}完成。${NC}"
