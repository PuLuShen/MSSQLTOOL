# Git 治理规则与开源防泄露方案

适用仓库：本机工作目录（MSSQLTool fork）→ 目标开源仓库 `https://github.com/PuLuShen/MSSQLTOOL.git`

> 本目录 `git-governance/` **可以随仓库开源**（内容不含任何隐私，还能体现项目管理规范）。
> 姊妹目录 `local-audit/` **永不提交**（含本机审计结论与隐私细节），由 `.gitignore` 增量规则兜底。

---

## 0. 现状结论（2026-09-14 审计）

- 本仓库是 [Axial-SQL/AxialSqlTools](https://github.com/PuLuShen/MSSQLTOOL) 的 fork，Apache-2.0 + NOTICE，历史 400+ 提交。
- **唯一确定的泄露点：提交身份。** 本机 git 全局配置是真实身份（非匿名用户名 + 个人常用邮箱），且历史中已有 2 个这样的提交（HEAD 附近）。直接推送 = 邮箱永久公开。
- 源码内容审计干净：无硬编码密钥/连接串/真实 IP/手机号/本机路径（密钥均走 Windows 凭据管理器与 DPAPI）。
- 本机有 3 处含隐私的本地文件，目前靠"未跟踪/被忽略"挡着，需要规则防止将来误提交（见 R3）。

## 1. 总原则

1. **真实身份永远不进 git 对象**（提交元数据比文件内容更容易被忽视）。
2. **hook 是最后防线，不是安全审计**——它拦截的是"手滑"，复杂的泄露要靠 `audit-repo.sh` 全量审计 + 人工 review。
3. 误报必须**显式放行**（行尾 `audit-ok` 标记），禁止图省事长期 `--no-verify`。
4. 所有治理配置只作用于本仓库（`--local`），不影响本机其它项目。

---

## 2. 规则清单

### R1 提交身份（最重要）

- 本仓库必须设置仓库级身份，覆盖全局配置：

  ```sh
  git config user.name  "PuLuShen"
  git config user.email "<GitHub noreply地址>"
  ```

- noreply 地址的获取：GitHub → Settings → Emails → 勾选 **Keep my email addresses private**，页面会显示形如 `12345678+PuLuShen@users.noreply.github.com` 的地址，复制它（`setup-governance.sh` 默认写入 `PuLuShen@users.noreply.github.com`，若你的带数字 ID，复制后用上面第二条命令覆盖）。
- `pre-commit` hook 会强制校验：非 noreply 邮箱直接拦截提交。
- 历史中已存在的真实邮箱提交：用 `scripts/scrub-identity.sh` 改写（见 R7）。

### R2 提交内容防线（pre-commit hook 行为）

- 只检查**暂存区新增行**；命中 `sensitive-patterns.txt`（通用）+ `local-audit/patterns.local.txt`(本机) 中任一模式即拦截。
- 模式覆盖：个人邮箱、本机用户目录路径、私钥块、常见 token 形态（ghp_ / AIza / sk- 等）、连接串明文口令、手机号、以及本机专属模式（用户名/目录名）。
- 误报处理：在该行**结尾**追加 `audit-ok` 标记后重新 `git add`（标记本身会随代码提交，表示"人工确认过"）。
- 漏报处理：发现新泄露形态就把模式补进对应清单（通用 → `git-governance/sensitive-patterns.txt`；本机 → `local-audit/patterns.local.txt`）。

### R3 本地隐私文件隔离

- 约定：一切不想开源的本地笔记、脚本、构建产物，放仓库根的 `local/` 目录（`.gitignore` 增量已包含 `/local/`）。
- 现状处置（由仓库所有者执行，治理文件不代改）：
  - `BUILD.zh-CN.md`：含本机用户目录与仓库绝对路径，已被现有 `.gitignore` 忽略 ✓ —— 保持忽略；若将来要开源构建文档，先脱敏（把绝对路径改成相对/占位写法）。
  - `tools/loc_audit*.py`：硬编码本机仓库绝对路径，目前未跟踪 ⚠ —— 建议移入 `local/`，或把 `ROOT` 改成相对路径后再考虑提交。
  - `artifacts/`（VSIX 成品）、`.vs/`、`bin/obj`：已被忽略 ✓。
- 禁止用 `git add -A` / `git add .` 提交来历不明的目录；优先 `git add <文件>`，或先 `git status` 确认 untracked 列表。hook 是兜底，不是许可。

### R4 敏感数据不入库

- 密钥/凭据一律走系统的凭据存储。本项目现状已做到：GitHub token / Google OAuth refresh token 走 Windows 凭据管理器（`WindowsCredentialHelper.cs`），SMTP 密码走 DPAPI 加密注册表（`SettingsManager.cs`）。新增功能沿用同一模式。
- 代码/文档/SQL 脚本里不允许出现真实服务器 IP、真实数据库名+账号组合的示例；示例一律用 `your_server` / `YourPassword` 占位。

### R5 提交信息规范

- 沿用仓库现有风格（conventional commits）：`feat: ...` / `fix: ...` / `docs: ...` 等，中英文皆可。
- 提交说明里禁止粘贴：报错日志中的 IP/主机名/邮箱、本机绝对路径、任何口令。`commit-msg` hook 会对提交说明做同样的模式扫描。
- 一次提交只做一件事；不确定的中间产物先 `git stash`，不要"顺手"提交。

### R6 分支与推送纪律

- 个人项目允许直接提交 `main`；多人协作阶段再引入 feature 分支。
- **禁止对已推送分支 `push --force`**（确需改历史时，先 `git push --force-with-lease` 且仅限自己独有的分支）。
- **禁止 `git push --mirror` / `--all`**（会把所有本地 refs，包括备份分支，一次性推上去）。
- 每次推送前跑一次 `sh git-governance/scripts/audit-repo.sh`，报告全绿再 `git push`。
- 危险操作（改历史、批量删除）前先打本机备份分支：`git branch backup/手头工作-$(date +%m%d)`。

### R7 历史与远程

- **fork 合规**：开源到 MSSQLTOOL 时必须保留根目录 `LICENSE`（Apache-2.0）与 `NOTICE`；README 建议保留一句 fork 声明（Based on Axial-SQL/AxialSqlTools，附链接）。`AssemblyInfo.cs` 里的 `Axial Solutions LLC` 公司名可改，但许可文件不可删。
- **README 里的原作者赞赏码**（`pics/赞赏码.png`，已跟踪）：推送自己的开源库前决定去留（建议删除图片与 README 对应段落，属于原作者个人收款码，保留在改名后的仓库里既奇怪也不合适）。
- **历史身份清洗**：`scripts/scrub-identity.sh`（默认 dry-run；`--apply --old-email <旧邮箱>` 执行；自动备份分支；优先 filter-repo，退回 filter-branch）。**只改写自己邮箱的提交，上游作者提交不动。**
- **切换远程**：完整分步见 `local-audit/切换MSSQLTOOL操作手册.md`（先清身份 → 再提交本地改动 → 最后 set-url + push，顺序不能反）。
- 新增敏感文件类型时同步补 `.gitignore`；`.gitignore` 变更本身也要提交，让规则跟着仓库走。

### R8 应急预案（误提交隐私后）

1. **凭据类（token/密码/密钥）**：第一动作永远是吊销/改密，然后才轮到清历史——历史清得再干净，也挡不住已经爬取的副本。
2. **个人隐私（邮箱/路径/身份证号等）**：改写历史（`scrub-identity.sh` 或 filter-repo）→ `push --force-with-lease` → 联系 GitHub Support 清除缓存视图（GitHub 有 deny/缓存的提交数据处理通道）。
3. 判断影响面：`git log --all -S"<泄露内容>" --oneline` 找出所有涉及提交；若已被 fork/clone，视为无法完全收回，按第 1 条处理。
4. 事后复盘：把泄露形态补进 pattern 清单，让 hook 下次能拦住。

---

## 3. 工具箱

| 文件 | 用途 | 典型用法 |
|---|---|---|
| `hooks/pre-commit` | 身份 + 暂存内容双重拦截 | 由 `core.hooksPath` 自动生效 |
| `hooks/commit-msg` | 提交说明敏感信息拦截 | 同上 |
| `sensitive-patterns.txt` | 通用敏感模式（可开源） | 手工维护 |
| `.gitignore.additions` | .gitignore 增量片段 | setup 脚本自动合并 |
| `scripts/setup-governance.sh` | 一键应用：本地身份 + hooks + .gitignore + 自检 | `sh git-governance/scripts/setup-governance.sh` |
| `scripts/audit-repo.sh` | 五区全量审计，报告写 `local-audit/` | `sh git-governance/scripts/audit-repo.sh` |
| `scripts/scrub-identity.sh` | 历史身份改写（dry-run 默认） | `sh git-governance/scripts/scrub-identity.sh [--apply --old-email <旧邮箱>]` |
| `local-audit/patterns.local.txt` | 本机专属模式（永不入库） | 手工维护 |

启用一次即可（幂等，可重复执行）：

```sh
sh git-governance/scripts/setup-governance.sh
```

临时绕过 hook（应急用，事后必须补审计）：

```sh
git commit --no-verify
```

---

## 4. 开源前检查清单

- [ ] 已运行 `setup-governance.sh`，`git config --local user.email` 是 GitHub noreply 地址
- [ ] GitHub 已开启 "Keep my email addresses private"（否则 noreply 不生效）
- [ ] `audit-repo.sh` 报告五区全部干净（或命中项均已确认/标记）
- [ ] 历史身份清洗完成：`git log --format='%ae' | sort -u` 只剩 noreply 与上游作者邮箱
- [ ] 本地隐私文件确认不在跟踪列表：`git ls-files | grep -iE "BUILD|loc_audit|赞赏码"`
- [ ] README 的赞赏码段落与图片已按决定处理
- [ ] LICENSE（Apache-2.0）与 NOTICE 仍在，README 有 fork 声明
- [ ] `git tag` 为空（本仓库现状）或已确认 tags 可公开
- [ ] 推送后到 GitHub 网页核对提交列表：头像/邮箱均匿名，无真实姓名与真实邮箱字样

---

## 5. 切换到 MSSQLTOOL 的操作总览

详细分步与命令见 `../local-audit/切换MSSQLTOOL操作手册.md`。顺序概要（**先清身份，后推远程**）：

1. GitHub 上创建空仓库 `PuLuShen/MSSQLTOOL`（不初始化 README/LICENSE）；
2. `setup-governance.sh` 应用身份与 hooks；
3. 处置 `tools/` 等本地隐私文件（R3）；
4. `audit-repo.sh` 确认干净；
5. `scrub-identity.sh` 清洗历史身份；
6. 提交本地未提交的开发改动（新身份）；
7. `git remote set-url origin https://github.com/PuLuShen/MSSQLTOOL.git` → `git push -u origin main`；
8. 按第 4 节清单到 GitHub 网页复核。
