#!/usr/bin/env bash
#
# 一键发布：更新版本号 -> 编译双平台 -> 产物入库 -> 打标签 -> 推送 -> 创建 GitHub Release
#
# 用法：
#   ./release.sh 1.2.0                    完整发布
#   ./release.sh 1.2.0 --notes-file n.md  使用自定义发布说明
#   ./release.sh 1.2.0 --dry-run          只演练，不提交/不推送/不发布
#
# 依赖：git、dotnet SDK、gh（GitHub CLI 且已 gh auth login）
#
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

log()  { printf '[release] %s\n' "$*"; }
warn() { printf '[release] WARN: %s\n' "$*"; }
die()  { printf '[release] ERROR: %s\n' "$*" >&2; exit 1; }

DRY_RUN=0
VERSION=""
NOTES_FILE=""

while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run)
      DRY_RUN=1
      ;;
    --notes-file)
      [ $# -ge 2 ] || die "--notes-file 需要一个文件路径"
      NOTES_FILE="$2"
      shift
      ;;
    -h|--help)
      sed -n '2,12p' "$0"
      exit 0
      ;;
    -*)
      die "未知参数: $1"
      ;;
    *)
      VERSION="$1"
      ;;
  esac
  shift
done

run() {
  if [ "$DRY_RUN" = "1" ]; then
    printf '          (dry-run) %s\n' "$*"
  else
    eval "$@"
  fi
}

# ------------------------------------------------ 前置检查
[ -n "$VERSION" ] || die "请指定版本号，例如: ./release.sh 1.2.0"
case "$VERSION" in
  [0-9]*.[0-9]*.[0-9]*) ;;
  *) die "版本号格式应为 x.y.z（当前: $VERSION）" ;;
esac

command -v git >/dev/null || die "未找到 git"
command -v dotnet >/dev/null || die "未找到 dotnet"
[ -f "CadLockPlugin.sln" ] || die "请把脚本放在仓库根目录"

TAG="v$VERSION"
BRANCH="$(git rev-parse --abbrev-ref HEAD)"
[ "$BRANCH" = "master" ] || warn "当前分支是 $BRANCH（不是 master），将按当前分支推送"

DIRTY="$(git status --porcelain -uno)"
[ -z "$DIRTY" ] || die "工作区有未提交的改动，请先提交：
$DIRTY"

if git rev-parse -q --verify "refs/tags/$TAG" >/dev/null; then
  die "标签 $TAG 已存在。请换版本号，或先执行：git tag -d $TAG && git push origin :refs/tags/$TAG"
fi

if git ls-remote --exit-code --tags origin "$TAG" >/dev/null 2>&1; then
  die "远端已存在标签 $TAG，请换版本号"
fi

command -v gh >/dev/null || die "未找到 GitHub CLI (gh)，请安装后执行 gh auth login"
if [ "$DRY_RUN" = "0" ]; then
  gh auth status >/dev/null 2>&1 || die "gh 未登录，请先执行 gh auth login"
fi

# 记录上一个发布标签，用于生成发布说明（必须在打新标签之前取）
PREV_TAG="$(git describe --tags --abbrev=0 HEAD 2>/dev/null || true)"
[ -n "$PREV_TAG" ] && log "上一个发布标签：$PREV_TAG"

log "版本 $VERSION | 分支 $BRANCH | 标签 $TAG | dry-run=$DRY_RUN"

# ------------------------------------------------ 1. 版本号
log "1/6 写入版本号 $VERSION.0（AssemblyVersion / FileVersion）"
for f in src/CadLockPlugin.csproj src/CadLockPlugin.ZWCAD.csproj; do
  [ -f "$f" ] || die "找不到 $f"
  if [ "$DRY_RUN" = "1" ]; then
    printf '          (dry-run) 更新 %s\n' "$f"
  else
    sed -i -E "s#<AssemblyVersion>[^<]+</AssemblyVersion>#<AssemblyVersion>$VERSION.0</AssemblyVersion>#" "$f"
    sed -i -E "s#<FileVersion>[^<]+</FileVersion>#<FileVersion>$VERSION.0</FileVersion>#" "$f"
  fi
done

# ------------------------------------------------ 2. 编译
log "2/6 编译双平台 (Release)"
run "dotnet build CadLockPlugin.sln -c Release -v q"
run "dotnet build tests/CadLockSmoke/CadLockSmoke.csproj -c Release -v q"

[ -f "bin/Release/CadLockPlugin.dll" ] || die "缺少 bin/Release/CadLockPlugin.dll"
[ -f "bin/ZWCAD/Release/CadLockPlugin.ZWCAD.dll" ] || die "缺少中望CAD 版 DLL"

# ------------------------------------------------ 3. 产物
log "3/6 复制产物到根目录并计算校验值"
run "cp bin/Release/CadLockPlugin.dll ."
run "cp bin/ZWCAD/Release/CadLockPlugin.ZWCAD.dll ."

SHA_AUTO="$(sha256sum CadLockPlugin.dll | cut -d' ' -f1)"
SHA_ZW="$(sha256sum CadLockPlugin.ZWCAD.dll | cut -d' ' -f1)"
SIZE_AUTO="$(wc -c < CadLockPlugin.dll | tr -d ' ')"
SIZE_ZW="$(wc -c < CadLockPlugin.ZWCAD.dll | tr -d ' ')"
log "          CadLockPlugin.dll $SIZE_AUTO bytes $SHA_AUTO"
log "          CadLockPlugin.ZWCAD.dll $SIZE_ZW bytes $SHA_ZW"

# ------------------------------------------------ 4. 提交
log "4/6 提交版本号与产物"
run "git add src/CadLockPlugin.csproj src/CadLockPlugin.ZWCAD.csproj CadLockPlugin.dll CadLockPlugin.ZWCAD.dll"
run "git commit -m 'chore(release): v$VERSION 编译产物'"

# ------------------------------------------------ 5. 标签与推送
log "5/6 打标签并推送"
run "git tag -a '$TAG' -m 'v$VERSION'"
run "git push origin '$BRANCH'"
run "git push origin '$TAG'"

# ------------------------------------------------ 6. GitHub Release
log "6/6 创建 GitHub Release 并上传 DLL"

TMP_NOTES="$(mktemp 2>/dev/null || echo "./.release_notes_$$.md")"
trap 'rm -f "$TMP_NOTES"' EXIT

if [ -n "$NOTES_FILE" ]; then
  [ -f "$NOTES_FILE" ] || die "找不到发布说明文件: $NOTES_FILE"
  cp "$NOTES_FILE" "$TMP_NOTES"
else
  if [ -n "$PREV_TAG" ]; then
    RANGE="$PREV_TAG..HEAD"
  else
    RANGE="HEAD"
  fi
  log "          按提交记录生成发布说明（$RANGE）"

  {
    echo "## v$VERSION"
    echo ""
    echo "### 变更"
    echo ""
    git log --no-merges --pretty=format:'- %s' $RANGE || true
    echo ""
    echo ""
    echo "### 文件校验"
    echo ""
    echo "| 文件 | 大小 | SHA256 |"
    echo "| :--- | ---: | :--- |"
    echo "| CadLockPlugin.dll | $SIZE_AUTO | $SHA_AUTO |"
    echo "| CadLockPlugin.ZWCAD.dll | $SIZE_ZW | $SHA_ZW |"
    echo ""
    echo "### 安装"
    echo ""
    echo "1. CAD 中执行 NETLOAD：AutoCAD 选 CadLockPlugin.dll，中望CAD 选 CadLockPlugin.ZWCAD.dll；"
    echo "2. 命令：LOCK 面板 / BATLOCK 批量加密 / UNLOCK 解锁当前图纸 / BATUNLOCK 批量解锁 / STOPEX 卸载防护钩子。"
    echo ""
    echo "> 加密前请备份原图；Excel 公式转值不可逆。"
  } > "$TMP_NOTES"
fi

run "gh release create '$TAG' --title 'v$VERSION' --notes-file '$TMP_NOTES' CadLockPlugin.dll CadLockPlugin.ZWCAD.dll"

if [ "$DRY_RUN" = "1" ]; then
  warn "演练结束：未提交、未打标签、未推送、未创建 Release"
else
  URL="$(gh release view "$TAG" --json url --jq .url 2>/dev/null || true)"
  log "发布完成: $URL"
fi
