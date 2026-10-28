# MSSQL Tool 发布说明 / Release Notes

> 发布脚本 `tools\Publish-Release.cmd` 只读取当前版本对应的 `## <版本号>` 段落作为 Release 正文。
> 每次调整后直接改这一节即可；重新上传同一版本会同步更新已有 Release 的说明。
>
> The manual release script uses the `## <version>` section below as the GitHub release body.
> Re-running it for the same version patches the existing release, including this text.

## 5.375

### 中文

- **恢复完全交给 SSMS**：插件不再弹恢复框、不扫描 `BackupFiles`、不额外打开查询窗口；异常退出后的标签恢复由 SSMS 自带 AutoRecover 负责。
- **面板输入框 Delete 可用**：定位列/查找值输入框获得焦点时，Delete/Backspace（含 Ctrl 按词删除）由面板自身处理，不再删到 SQL 编辑器。
- **模板语句可编辑**：模板浏览器和停靠版模板窗口的预览支持 Delete/Backspace/Enter，停靠版提供 Save / Ctrl+S。

### English

- **Recovery owned by SSMS**: no plugin recovery prompt, `BackupFiles` scan or extra query windows; SSMS AutoRecover restores tabs after an abnormal exit.
- **Delete works in the panel inputs**: Delete/Backspace (including Ctrl-word variants) edit the focused locate/find box instead of the SQL editor.
- **Template statements are editable**: both template previews handle Delete/Backspace/Enter; the docked window also has Save / Ctrl+S.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.375.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.375.vsix` asset below, then restart SSMS.

## 5.374

### 中文

- **不再由插件做恢复弹框**：异常退出后的查询恢复完全交给 SSMS 自带的 AutoRecover；本插件不扫描 `BackupFiles`、不弹恢复框，也不额外打开查询窗口。
- **面板输入框的 Delete 真正可用**：SSMS 会在 WPF 之前把 Delete 翻译成编辑器命令；现在面板使用低层键盘钩子，在定位列/查找值输入框获得焦点时自行删除字符（支持 Delete、Backspace、Ctrl+Delete/Backspace），不再删到 SQL 编辑器。
- **模板语句可直接编辑**：模板浏览器的预览和停靠版模板窗口都改为可编辑，Delete/Backspace/Enter 由专门的键盘守卫写回编辑器，不再落到 SQL 编辑器；停靠版另提供 Save / Ctrl+S。

### English

- **No plugin-made recovery dialog**: query recovery after an abnormal exit is left entirely to SSMS AutoRecover. The extension does not scan `BackupFiles`, does not show a recovery prompt, and does not open extra query windows.
- **Delete works in the panel inputs**: SSMS translates Delete into an editor command before WPF sees it. A low-level hook now performs Delete/Backspace (including Ctrl-word variants) inside the focused locate/find box instead of letting the key reach the SQL editor.
- **Template statements are editable**: the template picker preview and the docked template window are editable. A keyboard guard writes Delete/Backspace/Enter back into the preview instead of letting them reach the SQL editor; the docked window also has Save / Ctrl+S.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.374.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.374.vsix` asset below, then restart SSMS.

## 5.373

### 中文

- **修复「新建查询」报 `已经存在 (STG_E_FILEREADYEXISTS)`**：SSMS 自动恢复历史标签后，扩展在窗口创建/激活时同步读取 `Window.Object` / `DocData`，会在 SSMS 还在创建新查询时再次初始化文档，导致临时查询文件 `SQLQuery*.sql` 被重复创建。现在窗口创建和激活的处理都延后到 WPF Dispatcher 的 Background 优先级，等 SSMS 完成命令后再挂接统计钩子和编辑器补全。

### English

- **Fix "already exists (STG_E_FILEREADYEXISTS)" when clicking New Query**: after SSMS restored auto-recovered tabs, the extension read `Window.Object` / `DocData` synchronously during window creation/activation, re-initialising the document while SSMS was still creating it and causing the temporary `SQLQuery*.sql` file to be created twice. Window-created and window-activated work now runs at Dispatcher Background priority, after the shell has finished the command.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.373.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.373.vsix` asset below, then restart SSMS.

## 5.372

### 中文

- **定位列下拉框只显示真实列名**：之前每个字段名后面还会多出 `Column_0`、`Column_1`……（结果集内部 DataTable 的占位列名），现在读取真实列名，不再重复。
- **Delete 键不再删到 SQL 编辑器**：点击「定位列」或「查找值」输入框时，面板会显式激活浮窗并把键盘焦点交给输入框；删除键只作用于输入内容。
- **自动恢复后新建查询报错**：修复查询历史「在新查询窗口中打开」在当前连接为空时的空引用；同时把 SSMS 恢复多个历史标签时的窗口创建/激活挂接延后到窗口创建完成之后，避免重入导致新建查询失败。

### English

- **The locate dropdown shows real column names only**: the internal `Column_0`, `Column_1`, ... placeholders no longer appear after the actual result columns.
- **Delete no longer reaches the SQL editor**: clicking the locate or find box explicitly activates the floating panel and focuses the input, so Delete only edits the text there.
- **New-query error after auto-recovery**: opening a history record in a new query no longer dereferences a null connection, and the window-created/activated hooks now run after SSMS has finished creating each restored tab.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.372.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.372.vsix` asset below, then restart SSMS.

## 5.371

### 中文

- **定位列不再记忆搜索内容**：下拉列表只显示当前结果集的列名，不再把以前搜过的词放在最上面；定位成功后输入框自动清空，换结果集时也会清空。
- **统计面板按钮改为图标**：固定列（图钉）、取消固定（带斜线的图钉）、定位列（十字准星）、查找值（放大镜）。图标带中英文工具提示，鼠标悬停即可看到名称。
- **版本号改为三位小数**：从本版起使用 `5.371` 这样的格式，后续为 `5.372`、`5.373`……，每一次小改动只增加最后一位；「关于」页也显示 `5.371`，不再是 `5.371.0.0`。
- **发布改为手动**：日常构建不再自动上传 GitHub Release；需要发布时双击 `tools\Publish-Release.cmd`，由你自行决定是否上传。

### English

- **Locate column no longer remembers searches**: the dropdown lists only the current result columns, previously searched terms are gone, and the box is cleared after a successful jump and whenever the result set changes.
- **Icon buttons in the statistics panel**: pin (freeze), pin-off (unfreeze), crosshair (locate column) and magnifier (find value), each with a localized tooltip.
- **Three-decimal version numbers**: this release is `5.371`, followed by `5.372`, `5.373`, ... so routine changes only bump the last digit; the About page now shows `5.371` instead of `5.371.0.0`.
- **Manual releases**: regular builds no longer upload to GitHub Releases. Double-click `tools\Publish-Release.cmd` when you decide to publish.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.371.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.371.vsix` asset below, then restart SSMS.
