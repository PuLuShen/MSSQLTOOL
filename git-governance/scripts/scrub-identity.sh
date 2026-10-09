#!/bin/sh
# git-governance/scripts/scrub-identity.sh —— 改写历史提交身份
# 用途：把历史中某个真实邮箱（含作者名）的提交，改写为 GitHub noreply 匿名身份。
# 默认 dry-run（只列出会被改写的提交，不改任何东西）：
#   sh git-governance/scripts/scrub-identity.sh
# 确认后执行（必须显式指定要清洗的旧邮箱，脚本绝不自动猜测）：
#   sh git-governance/scripts/scrub-identity.sh --apply --old-email "旧邮箱"
# 说明：
#   - 优先使用 git-filter-repo（若已安装），否则退回内置 git filter-branch
#   - 只改写 main 分支；改写会变更提交哈希（本仓库本就准备推送到全新远程，无影响）
#   - 执行前自动创建备份分支 backup/pre-scrub-<时间>（仅留在本机，禁止推送到远程）

REPO_ROOT=$(git rev-parse --show-toplevel) || exit 1
cd "$REPO_ROOT" || exit 1

APPLY=0
OLD_EMAIL=""
while [ $# -gt 0 ]; do
  case "$1" in
    --apply)     APPLY=1 ;;
    --old-email) OLD_EMAIL="$2"; shift ;;
    *) echo "[scrub] 未知参数: $1"; echo "用法: sh git-governance/scripts/scrub-identity.sh [--apply --old-email <旧邮箱>]"; exit 1 ;;
  esac
  shift
done

NEW_NAME=$(git config --get user.name)
NEW_EMAIL=$(git config --get user.email)
case "$NEW_EMAIL" in
  *@users.noreply.github.com) ;;
  *) echo "[scrub][中止] 仓库级新身份未配置或不是 noreply 地址。"
     echo "  先运行: sh git-governance/scripts/setup-governance.sh"
     exit 1 ;;
esac

echo "== 历史中全部身份（作者） =="
git log --format='%an <%ae>' | sort | uniq -c
echo "== 历史中全部身份（提交者） =="
git log --format='%ce' | sort | uniq -c
echo
CANDIDATES=$(git log --format='%ae %ce' | tr ' ' '\n' | grep -v 'users.noreply.github.com' | sort -u)
echo "候选旧邮箱（非 noreply，请自行甄别哪些是自己要清洗的，上游作者邮箱不要动）:"
printf '%s\n' "$CANDIDATES"

if [ "$APPLY" -ne 1 ]; then
  echo
  echo "（dry-run 结束，未做任何修改。确认后执行："
  echo "  sh git-governance/scripts/scrub-identity.sh --apply --old-email \"<上面候选中的旧邮箱>\" ）"
  exit 0
fi

# ---- apply 模式 ----
if [ -z "$OLD_EMAIL" ]; then
  echo "[scrub][中止] --apply 必须配合 --old-email 指定旧邮箱"
  exit 1
fi
case "$OLD_EMAIL" in
  *users.noreply.github.com) echo "[scrub][中止] 旧邮箱不应是 noreply 地址"; exit 1 ;;
esac

BACKUP="backup/pre-scrub-$(date +%Y%m%d-%H%M%S)"
git branch "$BACKUP" main
echo
echo "已创建本机备份分支: $BACKUP （仅留本机，禁止推送）"

echo "目标: $OLD_EMAIL  →  $NEW_NAME <$NEW_EMAIL>"

if command -v git-filter-repo >/dev/null 2>&1; then
  echo "== 使用 git filter-repo =="
  CB="if commit.author_email == b'$OLD_EMAIL':
    commit.author_name = b'$NEW_NAME'
    commit.author_email = b'$NEW_EMAIL'
if commit.committer_email == b'$OLD_EMAIL':
    commit.committer_name = b'$NEW_NAME'
    commit.committer_email = b'$NEW_EMAIL'"
  # --refs main：只改写 main，备份分支保留旧历史
  git filter-repo --force --refs main --commit-callback "$CB"
  echo "完成。若 origin 远程被 filter-repo 移除，属预期行为，"
  echo "后续按手册重新设置: git remote add origin https://github.com/PuLuShen/MSSQLTOOL.git"
else
  echo "== 未安装 git-filter-repo，使用内置 git filter-branch =="
  FILTER_BRANCH_SQUELCH_WARNING=1 git filter-branch -f --env-filter "
if [ \"\$GIT_AUTHOR_EMAIL\" = '$OLD_EMAIL' ]; then
  export GIT_AUTHOR_NAME='$NEW_NAME'
  export GIT_AUTHOR_EMAIL='$NEW_EMAIL'
fi
if [ \"\$GIT_COMMITTER_EMAIL\" = '$OLD_EMAIL' ]; then
  export GIT_COMMITTER_NAME='$NEW_NAME'
  export GIT_COMMITTER_EMAIL='$NEW_EMAIL'
fi
" -- main
  echo "完成。清理本机残留旧对象（可选但建议）："
  echo "  git for-each-ref --format='%(refname)' refs/original/ | xargs -n1 git update-ref -d"
  echo "  git reflog expire --expire=now --all && git gc --prune=now"
fi

echo
echo "复核身份（应只看到 noreply）:"
git log --format='%an <%ae> / %ce' | sort -u
