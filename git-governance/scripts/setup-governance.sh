#!/bin/sh
# git-governance/scripts/setup-governance.sh —— 一键在本仓库应用治理配置
# 幂等，可重复执行。只改本仓库的 git 配置与文件，不动全局配置，不做任何网络操作。
# 用法：
#   sh git-governance/scripts/setup-governance.sh
#   GIT_EMAIL="<你的GitHub noreply地址>" sh git-governance/scripts/setup-governance.sh

REPO_ROOT=$(git rev-parse --show-toplevel) || exit 1
cd "$REPO_ROOT" || exit 1

NEW_NAME="${GIT_NAME:-PuLuShen}"
NEW_EMAIL="${GIT_EMAIL:-PuLuShen@users.noreply.github.com}"

echo "== [1/4] 仓库级提交身份（覆盖全局身份，仅本仓库生效） =="
echo "  当前: $(git config --get user.name 2>/dev/null) <$(git config --get user.email 2>/dev/null)>"
git config user.name  "$NEW_NAME"
git config user.email "$NEW_EMAIL"
echo "  已设为: $(git config --get user.name) <$(git config --get user.email)>"
echo "  提醒：GitHub 的 noreply 地址可能是 <数字ID>+PuLuShen@users.noreply.github.com，"
echo "        请到 GitHub → Settings → Emails 复制精确地址后执行："
echo "        git config user.email '<复制的地址>'"

echo
echo "== [2/4] 启用 hooks =="
git config core.hooksPath git-governance/hooks
echo "  core.hooksPath = $(git config --get core.hooksPath)"
echo "  已启用：pre-commit（身份+内容双重防线）、commit-msg（提交说明防线）"

echo
echo "== [3/4] 合并 .gitignore 增量 =="
ADD="git-governance/.gitignore.additions"
if [ -f "$ADD" ]; then
  while IFS= read -r line; do
    [ -z "$line" ] && continue
    case "$line" in \#*) continue ;; esac
    if grep -qxF "$line" .gitignore 2>/dev/null; then
      echo "  已存在，跳过: $line"
    else
      printf '%s\n' "$line" >> .gitignore
      echo "  已追加: $line"
    fi
  done < "$ADD"
else
  echo "  未找到 $ADD，跳过"
fi

echo
echo "== [4/4] 运行一次仓库自检 =="
echo "  （结果同时写入 local-audit/ 下报告文件，请打开核对）"
sh git-governance/scripts/audit-repo.sh
