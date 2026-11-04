# MSSQL Tool 发布说明 / Release Notes

> 发布脚本 `tools\Publish-Release.cmd` 只读取当前版本对应的 `## <版本号>` 段落作为 Release 正文。
> 每次调整后直接改这一节即可；重新上传同一版本会同步更新已有 Release 的说明。
>
> The manual release script uses the `## <version>` section below as the GitHub release body.
> Re-running it for the same version patches the existing release, including this text.

## 5.404

### 中文

- **增强 DJLSH 高亮可见性**：5.403 日志确认 `DJLSH` 已经生成 3 个矩形（A 两次、B 一次），但非当前行的灰色背景透明度太低，看起来像没高亮。现在把灰色背景加深，A/B 三处都能明显看到。
- `INSERT ... SELECT` 字面量和 `VALUES` 的位置配对保持。

### English

- **Make DJLSH highlighting visible**: the 5.403 logs confirmed three rectangles (A twice, B once), but the gray was too faint outside the current line. The background is now darker so all three occurrences are visible.
- Positional pairing for `INSERT ... SELECT` literals and `VALUES` is unchanged.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.404.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.404.vsix` asset below, then restart SSMS.

## 5.403

### 中文

- **INSERT 列对应高亮扩展到字面量和 VALUES**：`INSERT INTO t(CS,CS2) SELECT '123','321'` 中，光标在目标列或对应值上会同时高亮目标列和该位置的字面量；`INSERT ... VALUES (...)` 同样按位置配对。
- **高亮矩形诊断**：新增 `SQL-HIGHLIGHT rect` 日志，记录每个 span 的行、列、客户区坐标和 DIP 坐标，用于确认 `A.DJLSH` 与 `B.DJLSH` 是否都实际绘制。

### English

- **Column-pair highlighting now covers literals and VALUES**: for `INSERT INTO t(CS,CS2) SELECT '123','321'`, the target column and its positional literal are highlighted together; `INSERT ... VALUES (...)` is paired the same way.
- **Rectangle diagnostics**: `SQL-HIGHLIGHT rect` logs each span's line, columns, client coordinates and DIP coordinates to verify both `A.DJLSH` and `B.DJLSH` rectangles are drawn.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.403.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.403.vsix` asset below, then restart SSMS.

## 5.402

### 中文

- **高亮测试诊断**：高亮器改为按前台进程判断编辑器是否激活（不再要求焦点 HWND 是编辑器子窗口）；新增 `SQL-HIGHLIGHT scan` / `render` 日志，记录光标、span 数量、绘制矩形数和窗口 bounds。
- 5.400 的关联高亮逻辑不变：同词、同列、表/别名引用、`INSERT ... SELECT` 成对列。

### English

- **Highlight diagnostics**: activation is now based on the foreground process instead of requiring the focus HWND to be a child of the editor. Added `SQL-HIGHLIGHT scan` / `render` logs for caret, span count, rectangle count and window bounds.
- The 5.400 related-highlighting rules are unchanged.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.402.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.402.vsix` asset below, then restart SSMS.

## 5.401

### 中文

- **修复 `WHERE` 补全只显示临时表列**：会话临时表会在语义模型之前加入别名表，旧代码只在别名为空时解析当前语句，导致 `FROM dbo.YGDAH` 的别名被跳过，`WHERE Y` 只拿到 `#CSTEMP` 的列。现在无条件合并当前语句的 FROM/JOIN 别名。
- **Predicate 不再提供数据类型**：`WHERE/ON/HAVING` 的候选不再混入 `BINARY/GEOGRAPHY/...` 等类型；操作符仍保留但会被已输入前缀过滤。
- 保留 `COMPLETION-CONTEXT` 和 `SQL-HIGHLIGHT` 测试日志。

### English

- **Fix `WHERE` completion only showing temp-table columns**: session temp tables were added before the semantic model, and the old code only parsed current aliases when the map was empty, so `FROM dbo.YGDAH` was skipped and `WHERE Y` only offered `#CSTEMP` columns. Current-statement FROM/JOIN aliases are now merged unconditionally.
- **No data types in predicate lists**: `WHERE/ON/HAVING` no longer mixes in `BINARY/GEOGRAPHY/...`; operators remain but are filtered by the typed prefix.
- The `COMPLETION-CONTEXT` and `SQL-HIGHLIGHT` test logs remain.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.401.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.401.vsix` asset below, then restart SSMS.

## 5.400

### 中文

- **测试版：SQL Prompt 风格关联高亮**：光标在标识符中时，用灰色背景高亮同一语句内的相关词；支持同名列、表/别名及其列引用、`INSERT ... SELECT` 目标列与来源列的成对高亮。
- **补全上下文诊断日志**：新增 `COMPLETION-CONTEXT`，记录 `Kind`、`Prefix`、光标行列、别名、元数据对象数和候选数，用于分析 `WHERE y` 这类补全内容异常。
- **高亮分析日志**：新增 `SQL-HIGHLIGHT`，记录当前词、语句范围、表/列/配对数量及配对名称。

### English

- **Test build: SQL Prompt-style related highlighting**: a gray background marks related words in the current statement; supports same-named columns, table/alias references, and paired `INSERT ... SELECT` target/source columns.
- **Completion context diagnostics**: `COMPLETION-CONTEXT` logs kind, prefix, caret line/column, aliases, metadata object count and candidate count for the `WHERE y` issue.
- **Highlight analyzer diagnostics**: `SQL-HIGHLIGHT` logs the current token, statement range, table/column/pair counts and pair names.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.400.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.400.vsix` asset below, then restart SSMS.

## 5.399

### 中文

- **修复关闭 Object Explorer 后补全弹窗再次遮挡光标**：5.398 仍用「原始点是否在编辑器屏幕矩形内」判断坐标，编辑器左移后该判断失效。现在按 SSMS 22 的实际行为，将 `GetPointOfLineColumn` 返回的点统一从客户区转换到屏幕坐标，不再依赖窗口位置或行首位置。
- 关闭/打开 Object Explorer、移动 SSMS 窗口都不再影响弹窗定位。

### English

- **Fix the popup covering the caret again after closing Object Explorer**: 5.398 still guessed the coordinate convention from the editor's screen rectangle, which became ambiguous when the editor moved left. SSMS 22 returns client coordinates, so the point is now always converted to screen coordinates.
- Opening/closing Object Explorer or moving the SSMS window no longer changes the placement result.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.399.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.399.vsix` asset below, then restart SSMS.

## 5.398

### 中文

- **修复补全弹窗位置偏低/遮挡光标**：诊断日志确认 SSMS 22 的 `GetPointOfLineColumn` 返回的是**客户区坐标**，但旧逻辑因该点也落在编辑器屏幕矩形内而误判为屏幕坐标，少加了编辑器左上角偏移。现在用同一行第 0 列坐标作为判别依据，确定是客户区坐标时统一转换到屏幕坐标。
- 诊断日志 `COMPLETION-PLACEMENT` 已移除。

### English

- **Fix the completion popup being offset upward/covering the caret**: the diagnostics showed that SSMS 22's `GetPointOfLineColumn` returns **client coordinates**, but the old check also matched the editor's screen rectangle and skipped the conversion. The line's column-0 point now disambiguates the convention and the caret is converted consistently.
- Removed the `COMPLETION-PLACEMENT` diagnostics.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.398.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.398.vsix` asset below, then restart SSMS.

## 5.397

### 中文

- **诊断版本，不改变定位行为**：为补全弹窗增加了 `COMPLETION-PLACEMENT` 日志，记录光标行列、编辑器 API 返回的原始坐标、第 0 列坐标、编辑器窗口/客户区/客户区原点、Win32 光标坐标、坐标转换判断、最终布局和窗口实际 Bounds。
- 请在 18 行末尾按 Ctrl+Space 复现后把日志中所有 `COMPLETION-PLACEMENT` 行发回。

### English

- **Diagnostic build; placement behaviour is unchanged.** The completion popup now logs `COMPLETION-PLACEMENT` entries for the caret line/column, raw editor point, column-0 point, editor/client rectangles and origin, Win32 caret, coordinate conversion decision, layout and final window bounds.
- Reproduce at the end of line 18 with Ctrl+Space, then send every `COMPLETION-PLACEMENT` line.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.397.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.397.vsix` asset below, then restart SSMS.

## 5.396

### 中文

- **自动补全弹窗改为左边缘对齐光标**：不再锚定逻辑行第 0 列；横向优先保持光标 X 坐标，右侧空间不足时整体左移，不超出显示器工作区。
- **兼容 SSMS 自动换行**：使用光标的实际视觉行列位置计算弹窗位置，换行后的续行不会再被弹窗压住；上下仍选择空间更大的一侧并限制高度。
- 新增 3 项定位回归测试（光标对齐、视口右边界左移、换行续行）。

### English

- **The completion popup now starts at the caret** instead of logical column 0. It keeps the caret X when possible, shifts left at the monitor edge, and never leaves the working area.
- **Word-wrap compatible**: the popup uses the caret's visual line/column, so a wrapped continuation row is not covered. Vertical placement still picks the side with more room and caps the height.
- Added three placement regression tests (caret alignment, right-edge shift, wrapped row).

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.396.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.396.vsix` asset below, then restart SSMS.

## 5.395

### 中文

- **快速搜索页面全部元素接入多语言**：左侧服务器/数据库、右上搜索条件、结果表格、定义预览、按钮、复选框、Tooltip、最近搜索、对象类型、状态栏和错误提示均按当前语言显示；新增日语、韩语、德语、法语、西班牙语词条，繁體中文沿用简体转换。
- **移除服务器排障日志**：删除 `hierarchy fallback`、`TreeView fallback`、`Probed`、`Selected Object Explorer` 等临时诊断输出，只保留正常状态日志。

### English

- **The whole Quick Search page is localized**: server/database sidebar, conditions, result grid, definition preview, buttons, checkboxes, tooltips, recent searches, object types, status bar and error prompts all follow the selected language. Added Japanese, Korean, German, French and Spanish entries; Traditional Chinese uses the Simplified conversion.
- **Removed server troubleshooting logs**: the temporary `hierarchy fallback`, `TreeView fallback`, `Probed` and `Selected Object Explorer` diagnostics are gone; normal status logging remains.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.395.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.395.vsix` asset below, then restart SSMS.

## 5.394

### 中文

- **修复没有连接服务器时下拉框仍显示 3 个「未连接」**：移除了「已知服务器」历史兜底；快速搜索现在只显示 Object Explorer 当前实际存在的服务器根节点。没有连接时列表为空。
- 之前记录的 `(not connected)` 历史服务器不会再出现在下拉框里。

### English

- **Fix three "not connected" entries appearing when no server is connected**: removed the known-server history fallback; Quick Search now only lists Object Explorer's current roots. With no connections the list is empty.
- Previously recorded `(not connected)` hosts no longer appear in the picker.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.394.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.394.vsix` asset below, then restart SSMS.

## 5.393

### 中文

- **修复服务器下拉显示 5 个（实际连接 2 个）**：5.392 同时加入了 Object Explorer 的根节点和「已知服务器」列表，导致重复。现在只要 Object Explorer 提供了根节点，就不再追加已知服务器；当前主机仍显示在可编辑文本框中。
- 下拉列表只展示 Object Explorer 的根节点（2 个根节点就是 2 项），不再出现重复的 IP/主机名条目。

### English

- **Fix the picker showing five servers for two connections**: 5.392 added both Object Explorer roots and the known-server list. Known servers are now only used when Object Explorer provides no roots; the active host stays in the editable text box.
- The dropdown now contains only the Object Explorer roots (two roots = two items), without duplicate IP/host entries.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.393.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.393.vsix` asset below, then restart SSMS.

## 5.392

### 中文

- **修复 5.390 点击快速搜索闪退**：5.390 在刷新服务器列表时逐个选中 Object Explorer 根节点并调用了 `Application.DoEvents()`，造成重入。5.392 在列表加载阶段完全不做节点探测/选中/消息泵。
- **服务器下拉列出 Object Explorer 的全部根节点**（例如「宝庆」「珠宝厂」「老凤祥银楼」3 个），不再受已知服务器列表只有 2 条的限制；当前主机名通过可编辑下拉框文本显示，不会额外插入重复项。
- 只有用户真正选择某个服务器时才选中对应根节点并复用其连接。

### English

- **Fix the Quick Search crash introduced by 5.390**: 5.390 selected roots and pumped `Application.DoEvents()` while loading the list, causing re-entrancy. 5.392 performs no probing, selecting or message pumping during list load.
- **The picker lists every Object Explorer root** (three entries in the reported case) instead of being limited to the two known servers; the current host is shown as editable text without adding a duplicate entry.
- Roots are only selected after the user actually chooses a server.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.392.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.392.vsix` asset below, then restart SSMS.

## 5.391

### 中文

- **修复点击快速搜索闪退**：5.390 在刷新服务器列表时会逐个选中 Object Explorer 根节点并调用 `Application.DoEvents()`，造成重入/交互卡死。5.391 取消了列表加载阶段的节点探测、焦点切换和消息泵。
- **服务器下拉直接显示 Object Explorer 的全部根节点**（好友好名称，如「宝庆」「珠宝厂」「老凤祥银楼」），不再只显示已知服务器列表的 2 条。
- 切换服务器时仍会按节点名称选中对应根节点并复用其实时连接。

### English

- **Fix the Quick Search crash**: 5.390 selected Object Explorer roots while refreshing the list and pumped `Application.DoEvents()`, causing re-entrancy. 5.391 removes root probing, focus changes and message pumping during list load.
- **The picker now lists every Object Explorer root caption** (for example 宝庆 / 珠宝厂 / 老凤祥银楼) instead of only the two known servers.
- Switching still selects the matching root and reuses its live connection.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.391.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.391.vsix` asset below, then restart SSMS.

## 5.390

### 中文

- **修复只显示 2 个服务器（实际连接了 3 个）**：Object Explorer 根节点是友好名称，不能直接作为服务器名。现在刷新列表时逐个选中根节点，读取实际连接服务器名，全部加入下拉框，不再受已知服务器列表数量限制。
- 新增日志 `Resolved Object Explorer root '...' -> server '...'`，可直接核对 3 个根节点与 3 个服务器。

### English

- **Fix only two servers showing when three are connected**: Object Explorer roots are friendly captions, not host names. The picker now selects every root, reads its live server name and adds all of them, instead of relying on the known-server list.
- New log line `Resolved Object Explorer root '...' -> server '...'` shows the full mapping.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.390.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.390.vsix` asset below, then restart SSMS.

## 5.389

### 中文

- **继续修复服务器切换报错**：日志显示 Object Explorer 根节点是友好名称（如「宝庆 (SQL Server 15.0 - sa)」），不是主机名；现在切换服务器时会逐个探测这些根节点，选中后比较实际连接服务器名，找到目标服务器并复用其连接（含凭据）。
- 新增探测日志：`Probed Object Explorer item '...' -> server '...'`，可直接看到友好名称与实际服务器名的对应关系。

### English

- **Further fix for the server-switch error**: Object Explorer roots use friendly captions (for example "宝庆 (SQL Server 15.0 - sa)") rather than host names. Switching now probes each root, compares the selected node's actual server name, and reuses the matching connection (including credentials).
- New log line `Probed Object Explorer item '...' -> server '...'` shows the caption-to-server mapping.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.389.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.389.vsix` asset below, then restart SSMS.

## 5.388

### 中文

- **修复服务器切换仍报「无法使用连接」**：在 `FindNode` 失败后，先检查 Object Explorer 当前选中的节点；如果它正是目标服务器，直接复用该节点的实时连接（日志显示这正是目标服务器的常见路径）。
- 树节点选中兜底继续保留；新增日志会打印树根名称和实际选中的树节点文本，便于定位名称不一致。

### English

- **Fix the remaining "could not use the connection" error when switching servers**: after `FindNode` fails, check the currently selected Object Explorer node first; when it is the requested server, reuse that node's live connection.
- The tree-selection fallback remains; new logs print the tree root names and the selected item text for diagnosing name mismatches.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.388.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.388.vsix` asset below, then restart SSMS.

## 5.387

### 中文

- **修复切换快速搜索服务器时报「无法使用连接」**：第二个服务器能从 Object Explorer 树中列出，但 `FindNode` 解析不到它的连接；现在切换时会在 Object Explorer 中选中对应服务器节点，再用该节点的实时连接（含凭据）建立查询连接。
- 保留原有行为：如果当前已选中的就是目标服务器，直接使用现有连接，不改变对象资源管理器选择。

### English

- **Fix "could not use the connection" when switching the Quick Search server**: the second server could be listed from the Object Explorer tree, but `FindNode` could not resolve its connection. Switching now selects the matching Object Explorer server node and uses that node's live connection (including credentials).
- The existing behaviour is kept when the target server is already the selected node.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.387.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.387.vsix` asset below, then restart SSMS.

## 5.386

### 中文

- **继续修复服务器下拉只显示一个**：5.385 的托管树兜底在部分 SSMS 22 上仍无法取得根节点；新增原生 `SysTreeView32` 读取兜底，直接从 Object Explorer 根节点读取所有服务器，再逐个用 `FindNode` 校验。
- 诊断日志新增：`Object Explorer hierarchy fallback` 分支、根节点子项、`Object Explorer TreeView fallback found N root item(s)`，便于确认实际读取结果。

### English

- **Further fix for the server picker showing one entry**: the managed hierarchy fallback can still lack a root on some SSMS 22 builds; added a native `SysTreeView32` reader that reads the Object Explorer roots directly and validates each name with `FindNode`.
- New diagnostic log lines cover the hierarchy branches, root children and the TreeView root count.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.386.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.386.vsix` asset below, then restart SSMS.

## 5.385

### 中文

- **快速搜索布局重排**：左侧固定为服务器与数据库选择；右侧上方是搜索条件，下方左侧为结果表格、右侧为联动的定义预览脚本，中间可拖动调整宽度。
- **修复服务器下拉只有一个选项**：当 SSMS 不提供 `IObjectExplorerNavigationService` 时，改为遍历 Object Explorer 的树根节点，列出所有已连接的服务器；仍保留当前服务器和本插件已知服务器作为兜底。

### English

- **Quick Search layout redesign**: server/database selection is now a left sidebar; the right side has the search conditions on top and a results/preview split below, with a draggable divider.
- **Fix the server picker showing only one option**: when SSMS does not expose `IObjectExplorerNavigationService`, walk the Object Explorer hierarchy root to list every connected server; the active server and known servers remain as fallbacks.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.385.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.385.vsix` asset below, then restart SSMS.

## 5.384

### 中文

- **语言设置现在会同步菜单和工具栏**：VSCT 里的命令文字是构建时固定的中文，新增 `ShellLocalization` 在启动后和切换语言后重写 MSSQL Tool 的菜单/工具栏标题与提示。
- **新增语言**：繁體中文、日本語、한국어、Deutsch、Français、Español（连同简体中文、English 共 8 种）。
- 繁體中文复用简体词典并自动转换为繁体；其他新语言覆盖菜单、工具栏、设置主界面、常用按钮和提示，未覆盖的细节回退英文。
- 切换语言会立即刷新所有已打开的本插件窗口，并同时刷新工具栏/菜单文字。

### English

- **The language setting now updates menus and toolbars**: VSCT captions are fixed at build time, so `ShellLocalization` rewrites MSSQL Tool's menu/toolbar captions and tooltips after startup and after a language change.
- **New languages**: Traditional Chinese, Japanese, Korean, German, French and Spanish (8 languages including Simplified Chinese and English).
- Traditional Chinese reuses the Simplified dictionary and converts it; the other new languages cover menus, toolbars, the main settings pages, common buttons and prompts, with English fallback for not-yet-translated details.
- Switching language immediately refreshes every open extension window and the shell menus/toolbars.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.384.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.384.vsix` asset below, then restart SSMS.

## 5.383

### 中文

- **模板「在新查询中打开」改为直接打开模板文件**：使用 SSMS 的 `ItemOperations.OpenFile`，因此在查询窗口里修改后保存会直接写回模板文件，不再创建临时新文件。
- **模板树默认折叠**：打开模板页面时，左侧真实目录全部关闭；只有顶部的「最近 10 条」和「常用」默认展开。
- **新增「常用」目录**：位于「最近 10 条」下方，可把选中语句固定/取消固定；工具栏新增「添加到常用 / 从常用移除」按钮，状态随选中项变化。
- 模板相关新增按钮、提示均已中英文本地化。

### English

- **Open template in new query now opens the template file itself** through SSMS `ItemOperations.OpenFile`, so editing and saving the query window writes back to the template instead of creating a temporary copy.
- **The template tree starts collapsed**: real folders are closed by default; only the top "Recent (10)" and "Frequently used" folders are expanded.
- **New "Frequently used" folder** below Recent; pin/unpin the selected statement with the toolbar button (label changes to Add/Remove from frequently used).
- All new template buttons, status text and prompts are localized in Chinese and English.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.383.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.383.vsix` asset below, then restart SSMS.

## 5.382

### 中文

- 工具栏按钮改名为 **结果集面板**。
- 设置页说明改为：**关闭后仍可通过工具栏按钮【结果集面板】打开面板。**
- 保存成功提示「修改已保存 / 设置已保存」已加入中文本地化（之前只显示英文）。

### English

- Renamed the toolbar button to **Result panel**.
- The settings note now reads: **When this is off, you can still open the panel with the [Result panel] toolbar button.**
- The save-success messages "The change has been saved" / "Setting saved" are now localized too (they previously stayed in English).

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.382.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.382.vsix` asset below, then restart SSMS.

## 5.381

### 中文

- **修复工具栏「结果集统计」按钮不打开面板**：该按钮的命令处理器之前没有注册；现在重新注册，无论「点击结果集自动显示」开关是否开启，工具栏按钮都能打开面板。
- **移除结果集右键菜单扩展**：不再接管/重建结果网格的右键菜单，保留 SSMS 原生菜单；自动显示只由左键点击和工具栏按钮触发。
- 新增设置页的所有提示与说明均使用中英文本地化，包括保存失败提示。

### English

- **Fix the toolbar Result statistics button**: its command handler was not registered; it is now registered, so the toolbar button opens the panel whether or not automatic click-to-show is enabled.
- **Remove the result-grid context-menu extension**: the native SSMS grid menu is left untouched; the panel is opened only by the left-click popup and the toolbar button.
- All new settings labels and prompts are localized in Chinese and English, including the save-failure message.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.381.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.381.vsix` asset below, then restart SSMS.

## 5.380

### 中文

- **新增设置开关「点击结果集时自动显示统计面板」**：设置 → **结果集面板**，默认开启以保持原有行为；关闭后左键点击结果网格不再自动弹出面板，工具栏按钮和结果集右键菜单仍可打开。

### English

- **New setting "Show the result statistics panel when I click the result grid"**: Settings → **Result panel**. It is on by default to keep the existing behaviour; when off, a left click on the result grid no longer opens the panel, while the toolbar button and the grid context menu still do.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.380.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.380.vsix` asset below, then restart SSMS.

## 5.379

### 中文

- **修正 A 方案的保存方式**：部分 SSMS 版本的 `Document.Save(newPath)` 只写副本、不改变文档路径，导致 `SQLQuery1.sql` 又被重建。现在检测到路径未变化时，会关闭旧恢复标签并重新打开唯一路径的 `.sql` 副本，再删除旧临时文件，真正释放 `SQLQueryN` 编号。标签内容保留，连接可能需要重新选择。

### English

- **Fix the Plan A save path**: on some SSMS builds `Document.Save(newPath)` writes a copy but leaves the document path unchanged, so `SQLQuery1.sql` is recreated. When that happens the old recovered tab is now closed, the unique `.sql` copy is reopened, and the old temp file is deleted, truly freeing the `SQLQueryN` names. Content is kept; the connection may need to be reselected.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.379.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.379.vsix` asset below, then restart SSMS.

## 5.378

### 中文

- **绕过 SSMS 恢复标签后新建查询的 `STG_E_FILEREADYEXISTS`**：插件启动时把 `%TEMP%\SQLQuery*.sql*` 临时查询文件移到 `D:\SQLTOOLDATA\RecoveredQueries`，并给打开中的恢复标签执行 `Document.Save` 到唯一文件名；随后删除旧的 `SQLQueryN.sql`，把编号让给 SSMS 的「新建查询」。真实路径的 .sql 文件不会被动。
- 被移动的恢复标签会改成 `SQLQueryN_<时间戳>.sql` 这样的文件名，内容保留，仍可继续编辑/保存。

### English

- **Work around `STG_E_FILEREADYEXISTS` after SSMS restores auto-recovered tabs**: on startup the extension moves `%TEMP%\SQLQuery*.sql*` files into `D:\SQLTOOLDATA\RecoveredQueries`, uses `Document.Save` to give open recovered tabs unique names, then removes the old `SQLQueryN.sql` so native New Query can allocate that number again. Real .sql files are never touched.
- Recovered tabs are renamed to `SQLQueryN_<timestamp>.sql`; their content is kept and they stay editable/saveable.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.378.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.378.vsix` asset below, then restart SSMS.

## 5.377

### 中文

- **继续修复「新建查询」报 `已经存在 (STG_E_FILEREADYEXISTS)`**：5.376 已移除 `Window.Object`，但窗口创建/激活时仍会做连接颜色和统计钩子，会通过 `ServiceCache` 访问活动文档。现在这些操作不再在窗口创建/激活时执行；连接颜色改到执行查询前，统计钩子也在执行前挂接。窗口激活只保留 `SVsTextManager.GetActiveView` 的补全过滤器。
- 首次机会异常会记录 `STG_E_FILEREADYEXISTS` 的完整堆栈到日志，若仍复现可直接定位到具体调用方。

### English

- **Continue fixing "already exists (STG_E_FILEREADYEXISTS)" on New Query**: 5.376 removed `Window.Object`, but window creation/activation still applied connection colour and the statistics hook, which reach the active document through `ServiceCache`. Those no longer run during window creation/activation; colour and statistics are applied before query execution, and activation only attaches the completion filter through `SVsTextManager.GetActiveView`.
- First-chance `STG_E_FILEREADYEXISTS` exceptions now log their full stack, so a remaining occurrence can be traced to its caller.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.377.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.377.vsix` asset below, then restart SSMS.

## 5.376

### 中文

- **修复 SSMS 自动恢复标签后「新建查询」报 `已经存在 (STG_E_FILEREADYEXISTS)`**：根因是窗口创建/激活/关闭事件里读取了 `Window.Object` / `DocData`，SSMS 还在创建临时 `SQLQuery*.sql` 时会再次初始化文档，第二次创建同名文件就失败。现在这些事件完全不碰 `Window.Object`：补全过滤器改用 `SVsTextManager.GetActiveView`，统计钩子改在窗口激活/执行前挂接，窗口关闭也只用活动文本视图。

### English

- **Fix "already exists (STG_E_FILEREADYEXISTS)" when clicking New Query after SSMS restores auto-recovered tabs**: the window created/activated/closing handlers read `Window.Object` / `DocData`; that re-initialised the document while SSMS was still creating the temporary `SQLQuery*.sql` file, so the second create failed. Those handlers no longer touch `Window.Object`: the editor filter uses `SVsTextManager.GetActiveView`, the statistics hook attaches on activation/before execution, and window closing only uses the active text view.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.376.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.376.vsix` asset below, then restart SSMS.

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
