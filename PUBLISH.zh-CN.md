# 发布与上架说明（MSSQL Tool / SSMS 22）

本文件记录扩展的**发布链路、图库上架信息与注意事项**。工程怎么编译见 [BUILD.zh-CN.md](BUILD.zh-CN.md)，
日常改动记录见 [local-audit/切换MSSQLTOOL操作手册.md](local-audit/切换MSSQLTOOL操作手册.md)。

当前状态（最后一次发布）：**版本 4.73** · 回归测试 **228 项全过** · `main` = `6ae9dd6`。

---

## 1. 发布链路总览

```
① 本地编译 + 跑回归测试          （必须装有 SSMS，见下）
        ↓
② 发布 GitHub Release（只挂 VSIX） tools\Publish-GitHubRelease.ps1
        ↓  release: published 触发
③ GitHub Actions 自动上架图库      .github/workflows/publish-gallery.yml
```

### ① 编译与测试（本地）

```powershell
$env:FrameworkPathOverride = "$env:USERPROFILE\.nuget\packages\microsoft.netframework.referenceassemblies.net472\1.0.3\build\.NETFramework\v4.7.2"
& 'D:\Microsoft SQL Server Management Studio 22\Release\MSBuild\Current\Bin\MSBuild.exe' `
  'MSSQLTool\MSSQLTool.sln' /restore /t:Rebuild /m /p:Configuration=Release '/p:Platform=Any CPU' `
  '/p:SsmsInstallRoot=D:\Microsoft SQL Server Management Studio 22\Release' /v:minimal

& 'MSSQLTool\RegressionTests\bin\Release\MSSQLTool.RegressionTests.exe'   # 期望 “All N regression tests passed.”
```

> 版本号要**同时**改两处：`MSSQLTool\source.extension.vsixmanifest` 的 `<Identity Version="…">`
> 与 `MSSQLTool\Properties\AssemblyInfo.cs` 的 `AssemblyVersion/AssemblyFileVersion`。

### ② 发布 Release

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Publish-GitHubRelease.ps1
# 或指定版本：  -Version 4.74
```

脚本行为：tag `v<版本>` → 已存在则**替换同名资产**（幂等）→ 上传后**把资产下载回来比对大小与 SHA-256**，不一致立即报错。

### ③ 图库上架（CI 或手动）

CI：推一个 Release 即自动上架（也可在 Actions 页手动 `Run workflow`，可指定 tag）。
手动：`.\tools\Publish-VsixGallery.ps1`（不传参数则取 `artifacts\` 里最新的 VSIX）。

---

## 2. 图库上架信息

| 项 | 值 |
|---|---|
| 图库 | **Open SSMS VSIX Gallery**（面向 SSMS 插件；VS 用的 open-vsix-gallery 是另一个站点） |
| 上传接口 | `POST https://ssmsgallery.azurewebsites.net/api/upload` |
| 查询参数 | `?repo=<仓库URL>`、`?readmeUrl=main/README.zh-CN.md`、`?issuetracker=<issues URL>` |
| 认证 | 请求头 `X-Manage-Token: <令牌>`；**首传可省略**（图库会生成一次性令牌放进返回的 manageUrl） |
| 详情页 | <https://ssmsgallery.azurewebsites.net/extension/MSSQLTool> |
| 订阅源 | `https://ssmsgallery.azurewebsites.net/feed/extension/MSSQLTool` |
| 徽章 | `https://ssmsgallery.azurewebsites.net/badge/MSSQLTool.svg` |
| 扩展 ID / 目标 | `MSSQLTool` / SSMS 22、23（`Microsoft.VisualStudio.Ssms`，amd64） |
| 管理页令牌 | **不入库**。保存在本机 `%USERPROFILE%\mssqltool-gallery-manage.txt`；CI 用仓库 secret `VSIX_GALLERY_TOKEN` |

**令牌语义**：带令牌发布 → 该值成为/替换管理密码；不带令牌发布 → 沿用已有密码。
所以 CI 里配上 `VSIX_GALLERY_TOKEN` 之后，管理密码就由该 secret 统一决定（本地文件里的旧令牌随之失效）。

**上架命令必须是 multipart**（该站点的原始包体形式会返回 HTTP 500）：

```bash
curl --fail-with-body -X POST "https://ssmsgallery.azurewebsites.net/api/upload?repo=https://github.com/PuLuShen/MSSQLTOOL" \
     -H "X-Manage-Token: <令牌>" -F "file=@MSSQLTool.vsix"
```

---

## 3. GitHub Actions

| 项 | 说明 |
|---|---|
| 工作流 | `.github/workflows/publish-gallery.yml` |
| 触发 | `release: published`（自动）· `workflow_dispatch`（手动，可填 tag，留空取最新 release） |
| 步骤 | 解析 tag → `gh release download` 取 VSIX → **校验首 4 字节 `504b0304`** + `unzip -l` → multipart 上传 → 摘要写入 run summary |
| 失败条件 | 下载件不是合法 ZIP、上传非 2xx、图库返回 `hasValidatorErrors != false` |
| 需要的 secret | `VSIX_GALLERY_TOKEN`（**可选**；不配置则沿用图库现有令牌） |
| 需要的权限 | 内建 `GITHUB_TOKEN` 只读 `contents`（够用） |

### 为什么 CI 不负责编译

工程引用约 20 个**只存在于 SSMS 安装目录**的程序集（`$(SsmsInstallRoot)\Common7\IDE\...`：
SMO、Dac、ScriptDom、Visual Studio Shell/Interop、SqlWorkbench.Interfaces、SqlPackageBase、Microsoft.Data.SqlClient 等）。
GitHub 托管 runner 上没有 SSMS，**编译必然失败**，因此流水线是"本地/任意装有 SSMS 的机器编译并发布 Release，CI 负责上架"。
若将来要让 CI 也能出包，需要把这些引用改为 NuGet 包（`Microsoft.SqlServer.SqlParser` 等），属于较大重构。

---

## 4. 日后需要注意的点

### 必须遵守

1. **令牌绝不进仓库**：管理令牌只放本机文件或仓库 secret。`git-governance/scripts/audit-repo.sh` 会扫描已跟踪/未跟踪/全历史补丁。
2. **提交用 noreply 身份**，提交前跑一次 `git-governance/scripts/audit-repo.sh`（五区要全绿）：
   `D:\Git\usr\bin\sh.exe git-governance/scripts/audit-repo.sh`
3. **版本号两处同步改**（manifest + AssemblyInfo），tag 必须与版本一致（`v4.74` ↔ `4.74`）。
4. **保持发布脚本的上传校验**：`Publish-GitHubRelease.ps1` 必须"原始字节上传 + 上传后下载回来比对大小/SHA"。
   历史上正是这里的 multipart 上传 bug 让**所有** Release 资产损坏，VSIXInstaller 一律报"此文件不是有效的 VSIX 包"。
5. **图库上传只能用 multipart**（见上），原始包体会 500。

### 容易踩的坑

6. **回归测试目录大多被 .gitignore 忽略**：`MSSQLTool\RegressionTests\` 只有少数文件入库，
   别以为 `git status` 干净就代表测试文件已备份；新增测试前先确认是否需要 `git add -f`。
7. **新增 UI 文案要同时加中文词条**：`LocalizationManager.cs` 里缺少词条时，
   "每个 UI 标签都要有中文词条"守卫测试会直接失败。
8. **注册了命令就必须有 UI 落点**：vsct 里没有 placement 的命令会被"每个命令都必须有 UI 落点"守卫测试拦下。
9. **README 变更要重新上架才生效**：图库详情页的说明来自 `?readmeUrl=main/README.zh-CN.md`，
   改完 README 后需要一次新的 Release/工作流运行才会刷新。
10. **更新安装是可见窗口 + 关闭后安装**：用户侧若"关掉 SSMS 没看到安装窗口"，先看
    `D:\SQLTOOLDATA\MSSQLToolLog\update-install.log`（助手日志）与 `MSSQLToolLog\log_*.log`（扩展日志）；
    再确认下载包大小/SHA 与 Release 资产一致。
11. **磁盘/临时目录**：暂存包在 `%TEMP%\MSSQLToolUpdate\`，过期由启动清理按 30 分钟保护期回收。
12. **`SsmsInstallRoot` 因机器而异**：本机是 `D:\Microsoft SQL Server Management Studio 22\Release`，
    换机器时改这个参数即可，工程里不要写死路径。

### 出问题时的排查顺序

1. 编译/测试报错 → 看 MSBuild 的 `error` 行与测试输出（测试失败会打印断言行号）。
2. Release 资产可疑 → 在浏览器/命令行下载资产，首 4 字节必须是 `50 4b 03 04`；否则重跑发布脚本。
3. 图库没更新 → Actions 页看 run 日志（步骤级结论 + summary 里的版本/详情页链接）。
4. 用户装不上 → 按第 10 条看两份日志；必要时用 `VSIXInstaller.exe /quiet <vsix> /instanceIds:<id>` 手工复现。
