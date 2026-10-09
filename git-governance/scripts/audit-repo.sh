#!/bin/sh
# git-governance/scripts/audit-repo.sh —— 仓库敏感信息全量审计（只读，可重复执行）
# 审计范围：
#   [1] 当前提交身份        [2] 历史全部作者/提交者身份
#   [3] 已跟踪文件内容      [4] 未跟踪文件内容      [5] 全历史补丁（含已删除内容）
# 报告写入 local-audit/（该目录永不入库）。

REPO_ROOT=$(git rev-parse --show-toplevel) || exit 1
cd "$REPO_ROOT" || exit 1
GOV="$REPO_ROOT/git-governance"
AUDIT_DIR="$REPO_ROOT/local-audit"
mkdir -p "$AUDIT_DIR"

# ---- 加载双层模式 ----
PATFILE=$(mktemp) || exit 1
trap 'rm -f "$PATFILE" "$S3" "$S4" "$S5"' EXIT
: > "$PATFILE"
for f in "$GOV/sensitive-patterns.txt" "$AUDIT_DIR/patterns.local.txt"; do
  if [ -f "$f" ]; then
    grep -vE '^[[:space:]]*(#|$)' "$f" >> "$PATFILE"
  fi
done
if [ ! -s "$PATFILE" ]; then
  echo "[audit][错误] 未找到任何模式定义，中止"
  exit 1
fi

S3=$(mktemp); S4=$(mktemp); S5=$(mktemp)

REPORT="$AUDIT_DIR/audit-$(date +%Y%m%d-%H%M%S).txt"

# [1] 当前身份
EMAIL=$(git config --get user.email 2>/dev/null)
case "$EMAIL" in
  *@users.noreply.github.com) ID_STATUS="通过（noreply 匿名身份）" ;;
  *) ID_STATUS="不通过！当前身份会公开真实邮箱" ;;
esac

# [3] 已跟踪文件（-I 跳过二进制）
git grep -nI -E -f "$PATFILE" -- . > "$S3" 2>&1

# [4] 未跟踪且未被忽略的文件（含本次即将 git add 的新文件）
# 跳过 local-audit/（审计报告与隐私清单自身，避免报告递归引用产生噪音）
: > "$S4"
git ls-files --others --exclude-standard -z |
while IFS= read -r -d '' f; do
  case "$f" in
    local-audit/*|local/*) continue ;;
  esac
  grep -nI -E -f "$PATFILE" -- "$f" 2>/dev/null | sed "s|^|$f:|" >> "$S4"
done

# [5] 全历史补丁
git log --all -p -U0 --no-color 2>/dev/null | grep -nI -E -f "$PATFILE" | head -300 > "$S5"

{
  echo "==== 敏感信息审计报告  $(date '+%Y-%m-%d %H:%M:%S') ===="
  echo "仓库: $REPO_ROOT"
  echo
  echo "---- [1] 当前提交身份 ----"
  echo "user.name : $(git config --get user.name 2>/dev/null)"
  echo "user.email: $EMAIL"
  echo "状态: $ID_STATUS"
  echo
  echo "---- [2] 历史中全部作者/提交者身份（真实邮箱=泄露） ----"
  git log --all --format='%an <%ae>  /  committer: %ce' | sort | uniq -c | sort -rn
  echo
  echo "---- [3] 已跟踪文件内容扫描 ----"
  if [ -s "$S3" ]; then cat "$S3"; else echo "(无命中)"; fi
  echo
  echo "---- [4] 未跟踪文件内容扫描 ----"
  if [ -s "$S4" ]; then cat "$S4"; else echo "(无命中)"; fi
  echo
  echo "---- [5] 全历史补丁扫描（最多 300 行） ----"
  if [ -s "$S5" ]; then cat "$S5"; else echo "(无命中)"; fi
} > "$REPORT"

echo "审计报告: $REPORT"
echo
echo "摘要:"
echo "  [1] 当前身份      : $ID_STATUS"
[ -s "$S3" ] && echo "  [3] 已跟踪文件    : 命中 $(wc -l < "$S3") 行 —— 详见报告" || echo "  [3] 已跟踪文件    : 干净"
[ -s "$S4" ] && echo "  [4] 未跟踪文件    : 命中 $(wc -l < "$S4") 行 —— 详见报告（这些文件 git add 前必须处理）" || echo "  [4] 未跟踪文件    : 干净"
[ -s "$S5" ] && echo "  [5] 全历史补丁    : 命中 $(wc -l < "$S5") 行 —— 详见报告" || echo "  [5] 全历史补丁    : 干净"
