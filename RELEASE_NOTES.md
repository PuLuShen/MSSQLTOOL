# MSSQL Tool 发布说明 / Release Notes

> 发布脚本 `tools\Publish-Release.cmd` 只读取当前版本对应的 `## <版本号>` 段落作为 Release 正文。
> 每次调整后直接改这一节即可；重新上传同一版本会同步更新已有 Release 的说明。
>
> The manual release script uses the `## <version>` section below as the GitHub release body.
> Re-running it for the same version patches the existing release, including this text.

## 5.547（稳定版）

### 中文

- **移除临时诊断代码**
  - 删除 `PerfCounters`（每 30 秒的 `PERF` 日志行）；日志恢复为只记录真实事件。
  - 删除环境变量开关 `MSSQLTOOL_NO_HIGHLIGHT`。
  - 代码分析、关联高亮、补全的单次执行/去重/取消逻辑（5.542）完整保留，只是不再上报计数。
- 本版包含 5.544 的 CPU 修复与 5.545/5.546 的设置调整，已确认长时间使用 CPU 不再上升。

### English

- **Temporary diagnostics removed**
  - `PerfCounters` (the 30-second `PERF` line) is gone; the log only records real events again.
  - The `MSSQLTOOL_NO_HIGHLIGHT` environment switch is gone.
  - The single-flight/dedupe/cancel behaviour added in 5.542 stays; only the counters were dropped.
- Includes the 5.544 CPU fix and the 5.545/5.546 settings changes; long sessions no longer climb in CPU.

## 5.546

### 中文

- **设置精简：去掉重复的“回车也采用建议”复选框**
  - “回车是否接受建议”统一由“插入方式”下拉框决定：Tab / Enter / Tab 或 Enter。
  - 默认值改为 **Tab 或 Enter**（与 5.545 里“复选框默认打开”的效果一致）。
  - 旧配置自动迁移一次：原来是 Tab 且未手动关过复选框的，升级后变成“Tab 或 Enter”；曾手动取消的保持 Tab。迁移只做一次，之后可以自由选回 Tab。
- 保留 5.545 的两项：结果集面板自动显示默认关闭；5.544 的 CPU 修复。

### English

- **Settings cleanup: dropped the duplicate “Enter also accepts the suggestion” checkbox**
  - “Insert with” (Tab / Enter / Tab or Enter) is now the only control for whether Enter accepts a suggestion.
  - The default is **Tab or Enter**, matching what the 5.545 checkbox-on default did.
  - Legacy profiles migrate once: Tab with the switch untouched becomes “Tab or Enter”, an explicitly unchecked switch stays Tab. The migration runs once, so Tab can be chosen again afterwards.
- Keeps the 5.545 changes (result panel auto-show off by default) and the 5.544 CPU fix.

## 5.545

### 中文

- **新增开关：回车采用建议（默认打开）**
  - 位置：设置 → SQL Completion → “Insert with”右边的 **Enter also accepts the suggestion**。
  - 打开时，补全弹窗打开时按回车接受高亮项；关闭后回车恢复为换行。Tab 仍然按“Insert with”的设置生效。
- **结果集面板开关默认关闭**
  - “点击结果集时自动显示统计面板”现在是默认关闭，仍可在设置 → Result panel 里打开，或用工具栏按钮手动打开。
- 同时保留 5.544 的 CPU 修复与诊断计数器。

### English

- **New switch: Enter accepts the suggestion (on by default)**
  - Settings → SQL Completion, next to “Insert with”: **Enter also accepts the suggestion**.
  - When on, Enter accepts the highlighted row while the popup is open; when off, Enter inserts a newline again.  Tab keeps following the “Insert with” setting.
- **Result panel auto-show now defaults to off**
  - “Show the result statistics panel when I click the result grid” starts unchecked; it can still be turned on under Result panel or opened with the toolbar button.
- Keeps the 5.544 CPU fix and the diagnostic counters.

## 5.544

### 中文

- **修复：长时间使用后 CPU 占用升到数核（补全参数匹配是平方复杂度）**
  - 原因：生成 EXEC 参数候选时，`AddValues` 把 `RoutineTarget(...)` 写在了 `Where` 谓词里，而 `RoutineTarget` 自己又要全表扫描一次参数表。参数表有几万个参数时，每次按键的代价是“参数数 × 参数数”，一次补全要跑几十秒到几分钟；多个编辑器视图并发时就顶满好几个核心，直到重启 SSMS 才恢复。
  - 现在每个请求只解析一次目标过程，并按“名字最后一段”为参数建立快照级索引，只对命中的小桶做完整比对；匹配结果与原来的全表扫描完全一致（已加等价性测试）。
  - 实测路径：4 万参数的快照上，一次候选生成从分钟级降到毫秒级，并有回归测试守住（`Completion: a huge parameter table stays responsive`）。
  - 诊断计数器保留：`PERF` 行新增 `cand=` / `candms=`，用来观察候选生成耗时；确认稳定后可移除。

### English

- **Fix: CPU climbing to several cores after a while (quadratic parameter matching in completion)**
  - Cause: while building EXEC argument candidates, `AddValues` called `RoutineTarget(...)` inside the `Where` predicate, and `RoutineTarget` itself scanned the whole parameter table. With tens of thousands of parameters every keystroke cost parameters × parameters, so one completion ran for tens of seconds to minutes; several editor views in parallel pinned several cores until SSMS restarted.
  - Each request now resolves the target routine once and uses a snapshot-level index keyed by the last name part, running the full comparison only on the matching bucket. Results are identical to the previous full scan (equivalence test added).
  - Measured: on a 40k-parameter snapshot one candidate build drops from minutes to milliseconds, guarded by `Completion: a huge parameter table stays responsive`.
  - The diagnostic counters stay: the `PERF` line gains `cand=` / `candms=`; they can be removed once this is confirmed.

## 5.543

### 中文

- **诊断版：用计数器定位“用一会儿 CPU 升高”**
  - 每 30 秒输出一行 `PERF` 日志：代码分析/关联高亮/补全的启动数、完成数、丢弃数、跳过数、累计耗时，加上设置轮询/视图探测/去抖的触发次数、存活的 tagger 与 classifier 数量、线程池可用线程、GC 占用和线程数。
  - 新增临时开关：环境变量 `MSSQLTOOL_NO_HIGHLIGHT=1` 可完全不创建关联高亮分类器，用于 A/B 定位（此前没有任何开关）。
  - 本版不改变正常使用行为，仅供定位；结论出来后会把计数器移降。

### English

- **Diagnostic build: counters for the "CPU creeps up" investigation**
  - One `PERF` line every 30 seconds: analysis/highlight/completion starts, completions, discards and skips with cumulative time, plus settings/attach/debounce ticks, live tagger and classifier counts, thread pool availability, GC size and thread count.
  - Temporary switch: the environment variable `MSSQLTOOL_NO_HIGHLIGHT=1` skips the related-highlight classifier entirely, so it can be A/B tested (there was no user-facing switch before).
  - No behaviour change for normal use; the counters will be removed once the cause is known.

## 5.542

### 中文

- **修复长时间使用后 CPU 持续升高**
  - 代码分析改为“单飞 + 最新优先”：每个文档同一时刻只跑一次分析，整个 shell 最多同时分析两个文档。过期请求只标记一次重跑，不再是每次编辑都新起一个线程池任务。
  - 分析支持取消：被新编辑取代的分析在语句/规则边界退出，不再占着线程跑完 300ms 预算再丢弃结果。
  - 同一文本与设置组合只分析一次；被丢弃的过期结果改为 Debug 日志，便于后续排查“CPU 高但日志安静”的情况。
- **定时器不再随标签页累积**
  - 每个缓冲区一个 1 秒设置轮询，改为全 shell 共享一个监视器；监听者用弱引用，最后一个注销后定时器停止。
  - 关联高亮的视图探测由无限轮询改为 10 秒上限 + 5 秒退避；标签页关闭即停止去抖与轮询，编辑或重新渲染时按需恢复。
- **测试**：新增“分析可取消”回归用例，全部 376 项通过。

### English

- **Fixed the CPU creep after a long session**
  - Code analysis is now single-flight with latest-wins: one pass per document and at most two documents at a time in the shell. A superseded request only marks the buffer dirty instead of starting another pool task on every edit.
  - Analysis honours cancellation and exits at statement/rule boundaries instead of burning the full 300 ms document budget and throwing the result away.
  - Unchanged text plus settings is not analysed twice; discarded stale results are logged at Debug level so "high CPU with a quiet log" can be diagnosed later.
- **Timers no longer accumulate with tabs**
  - The per-buffer 1 second settings poll is now one shell-wide watcher with weak listeners, and it stops once the last listener unregisters.
  - Related-highlight view discovery is bounded (10 s of polling, then a 5 s backoff) and stops when the view closes; edits and classification requests resume it on demand.
- **Tests**: a new "analysis honours cancellation" case; all 376 tests pass.

## 5.541

### 中文

- **结果集浮动面板改为「字段列表 + 结果网格」**
  - 左侧新增**字段**列表：可按字段名或类型过滤，勾选框控制该列显示/隐藏，点击字段跳到对应列；列多时分批构建，宽结果集打开不再卡顿。
  - 右侧直接显示结果集，保留按列筛选、排序、导出 CSV / JSON / INSERT 与复制统计；原来的「显示列…」按钮由字段列表取代。
  - 新增**固定列**选择器：不固定，或固定最左侧 1～10 个可见列，向右滚动时保持可见。按 `Ctrl+Shift+F` 输入列名后，`F3` 跳到下一个匹配列（按名称、字段名或 SQL 类型匹配，隐藏列会先自动显示）。
- **统计改为跟随选区**
  - 选中一个单元格时统计该列，选中多个单元格时统计整个选区：非空、空值、去重、最长、合计、平均、最小、最大；单列扫描最多 10 万行，避免百万行结果集卡住面板。
  - 打开面板不再预先扫描所有列；原先整表的「列统计」表格已移除，统计只按当前选区计算。
- **输入与高亮更轻**
  - 补全列表改为批量更新，减少每次按键的控件通知；分类页签一次扫描得出；弹窗尺寸不变时不再重复设置。
  - 相关列高亮在光标不落在标识符上时直接返回，只让真正变化的范围重新分类，大脚本里移动光标不再整篇重绘。
  - 快速修复灯泡改为输入暂停 400 毫秒后刷新，规则引擎在后台线程执行，输入不再等待分析。
- **测试**：全量 375 项通过，1 项 SQL Server 元数据集成测试因未设置 `MSSQLTOOL_TEST_CONNECTION` 跳过。

### English

- **The floating result panel is now a field list plus the result grid**
  - A new **Fields** list on the left filters by name or type, toggles each column's visibility, and jumps to a column when clicked; wide result sets build it in batches, so opening the panel stays responsive.
  - The grid keeps the per-column row filters, sorting, CSV / JSON / INSERT export and copy-statistics actions; the old "Columns..." button is replaced by the field list.
  - A new **Freeze columns** selector pins the first 1-10 visible columns while scrolling right. After `Ctrl+Shift+F`, type a column name and press `F3` to cycle matches by caption, field name or SQL type; a hidden match is revealed first.
- **Statistics follow the selection**
  - One selected cell describes its column, several cells describe the selection: non-null, empty, distinct, longest, sum, average, minimum and maximum. A single-column scan stops after 100,000 rows so a million-row grid cannot block the panel.
  - Opening the panel no longer scans every column up front, and the old whole-table "Column statistics" table is gone; statistics are computed for the current selection only.
- **Lighter typing and highlighting**
  - The completion list is updated in batches instead of one notification per row, the category tabs come from a single pass, and the popup size is only assigned when it changes.
  - Related-column highlighting returns immediately when the caret is not on an identifier and only invalidates the span that actually changed, so moving the caret in a large script no longer repaints the document.
  - The quick-fix light bulb refreshes 400 ms after typing stops and runs the rule engine on a background thread, so typing no longer waits for the analysis.
- **Tests**: all 375 tests pass; one SQL Server metadata integration test is skipped because `MSSQLTOOL_TEST_CONNECTION` is not set.

## 5.512

### 中文

- **逗号连接的 `FROM` 现在识别整张来源列表**
  - `FROM dbo.A AS A, dbo.B AS B` 只把紧跟 `FROM` 的第一张表登记为来源，`AND ` 后补全未限定列时只按 A 解析，B 的字段不出现（必须先输入 `B.` 才看得到）。
  - 现在作用域树会读完整个 `FROM` 列表（包括逗号后的表、`WITH (NOLOCK)` 之类提示后的表），`AND |` 会同时给出两张表的列；`SELECT *` 的展开也包括第二张表。
- **测试**：新增逗号来源的作用域与补全用例；全量 375 项通过，1 项 SQL Server 元数据集成测试因未设置 `MSSQLTOOL_TEST_CONNECTION` 跳过。

### English

- **A comma-separated `FROM` now contributes every source**
  - `FROM dbo.A AS A, dbo.B AS B` registered only the table directly after `FROM`. Unqualified column completion after `AND ` resolved against A alone, so B's fields were missing until `B.` was typed.
  - The scope tree now reads the whole FROM list, including tables after commas and after `WITH (NOLOCK)` style hints. `AND |` offers both tables' columns, and `SELECT *` expansion includes the second table too.
- **Tests**: new scope-tree and completion cases for comma sources; all 375 tests pass, and one SQL Server metadata integration test is skipped because `MSSQLTOOL_TEST_CONNECTION` is not set.

## 5.511

### 中文

- **来源尾部补齐 APPLY 与 OUTER JOIN**
  - `FROM dbo.XSMXZH A ` 之后的补全列表原来缺少 `OUTER APPLY`、`CROSS APPLY` 与 `LEFT/RIGHT/FULL OUTER JOIN`。现在来源尾部给出完整的连接写法；`AS 别名` 排在连接写法之后，不再压住它们。
- **派生表、APPLY 里的第二个 `FROM` 能补全表名**
  - `FROM (`、`JOIN (`、`APPLY (` 这类括号原来被当成函数调用，光标被解析成函数参数，所以派生表里第二个 `FROM` 后面没有表建议。现在 `FROM/JOIN/APPLY/USING/INTO/MERGE/PIVOT/UNPIVOT/TABLESAMPLE/WITH/OPTION` 后面的括号不再按函数处理，派生表与 APPLY 子查询内部可以正常补全表。
- **新增表提示与查询提示**
  - `FROM dbo.T WITH (` 提供 `NOLOCK`、`ROWLOCK`、`UPDLOCK`、`HOLDLOCK` 等常用表提示；`OPTION (` 提供 `RECOMPILE`、`MAXDOP`、`OPTIMIZE FOR` 等查询提示，并带用法说明（含中文）。
- **按完整前缀输入片段时，自动列表直接显示片段**
  - 在 `OUTER APPLY (` 后逐字输入 `SSF`，列表原来不显示该片段，必须按 Ctrl+Space。逐字输入走的是上一轮缓存的候选列表，而片段只有完整分析读到整个前缀后才会加入。现在检测到“刚补全一个片段前缀、缓存里还没有它”就重新分析，片段立即出现；Ctrl+Space 行为不变。
- **测试**：新增连接写法、派生表 `FROM`、表/查询提示与片段前缀的回归用例；全量 372 项通过，1 项 SQL Server 元数据集成测试因未设置 `MSSQLTOOL_TEST_CONNECTION` 跳过。

### English

- **APPLY and OUTER JOIN forms are offered after a source**
  - The list after `FROM dbo.XSMXZH A ` was missing `OUTER APPLY`, `CROSS APPLY` and `LEFT/RIGHT/FULL OUTER JOIN`. Every join/APPLY form is now offered at the source tail, and the `AS alias` suggestion ranks below them instead of burying them.
- **A second `FROM` inside a derived table or APPLY now completes table names**
  - Parentheses after `FROM (`, `JOIN (` and `APPLY (` were treated as a function call, so the caret resolved to function arguments and the second `FROM` offered no tables. Parentheses following `FROM/JOIN/APPLY/USING/INTO/MERGE/PIVOT/UNPIVOT/TABLESAMPLE/WITH/OPTION` are no longer function calls, so derived tables and APPLY subqueries complete normally.
- **Table hints and query hints**
  - `FROM dbo.T WITH (` offers `NOLOCK`, `ROWLOCK`, `UPDLOCK`, `HOLDLOCK` and the other common hints; `OPTION (` offers `RECOMPILE`, `MAXDOP`, `OPTIMIZE FOR` and the rest, each with usage text (localized).
- **A snippet appears in the automatic list once its whole prefix is typed**
  - Typing `SSF` after `OUTER APPLY (` did not show the snippet until Ctrl+Space. A word-only keystroke is served from the candidate list built before that letter existed, and a snippet is only added when the analyzer reads the whole prefix. The editor now re-analyzes when a keystroke completes a snippet prefix that the cached list lacks, so the snippet appears at once; Ctrl+Space is unchanged.
- **Tests**: new regression cases for the join forms, the derived-table `FROM`, table/query hints and the snippet prefix; all 372 tests pass, and one SQL Server metadata integration test is skipped because `MSSQLTOOL_TEST_CONNECTION` is not set.

## 5.510

### 中文

- **`SELECT *` 的 Tab 展开支持嵌套查询**
  - 上一版修复了补全弹窗里的 `(all SELECT columns)`，但专门的星号展开服务（关闭弹窗后直接按 Tab）只认语句最外层查询与 CTE，遇到 APPLY、派生表、子查询、UNION 分支里的 `*` 会直接不处理。
  - 现在用语法树访问器定位**包含光标的那个查询块**，`SELECT *` 按该查询块自身的 `FROM` 展开。例如 `OUTER APPLY (SELECT * FROM dbo.DJKCZH B WHERE A.sptm = B.sptm)` 中，内层 `*` 只展开 `B` 的列，不再漏到外层 `A`；顶层 `*` 仍包含 APPLY 派生表的列。
  - 嵌套位置不再用整条语句的 `SET FMTONLY` 元数据回退（否则会把外层列带回来），改为按表名从元数据缓存/服务器目录解析列。
- **测试**：回归用例同时断言补全项与 Tab 展开路径（内层来源为 `B`、顶层为 `A,CC`）；全量 371 项通过。

### English

- **Tab expansion of `SELECT *` now works inside nested queries**
  - The previous fix covered the `(all SELECT columns)` completion item, but the dedicated star-expansion service (Tab with the popup closed) only recognised the outermost query and CTEs, so a `*` inside an APPLY, derived table, subquery or UNION branch was left untouched.
  - A syntax-tree visitor now locates the query block that contains the caret, and the star expands from that block's own `FROM`. Inside `OUTER APPLY (SELECT * FROM dbo.DJKCZH B WHERE A.sptm = B.sptm)` only `B`'s columns are emitted; a top-level `*` still includes the APPLY derived table's columns.
  - Nested offsets no longer fall back to `SET FMTONLY` over the whole statement (which would reintroduce the outer columns); columns resolve per table from the metadata cache or the server catalog instead.
- **Tests**: the regression case now covers both the completion item and the Tab path (inner sources `B`, top-level `A,CC`); all 371 tests pass.

## 5.509

### 中文

- **修复 APPLY 子查询内 `SELECT *` 把外层表字段一起展开的问题**
  - 对 `OUTER APPLY (SELECT * FROM dbo.DJKCZH B WHERE A.sptm = B.sptm)` 这类相关子查询，补全弹窗里的 `(all SELECT columns)` 原来会把外层 `A` 与内层 `B` 的列全部插入。
  - 根因是列来源取的是“光标处所有可见别名”，而相关子查询确实能看到外层别名。现在 `SELECT *` 只使用光标所在查询块自身 `FROM` 的列（`SqlScopeTree.OwnSourcesAt`），外层别名仍保留用于 `A.列` 这类相关引用补全。
  - 顶层 `SELECT * ... APPLY (...)` 仍会包含 APPLY 派生表的列，与 SQL Server 的 `SELECT *` 语义一致。
- **测试**：新增 APPLY 子查询的回归用例；全量 371 项回归测试通过，1 个 SQL Server 元数据集成测试因未设置 `MSSQLTOOL_TEST_CONNECTION` 跳过。

### English

- **Fixed `SELECT *` inside an APPLY subquery expanding the outer table's columns**
  - For a correlated subquery such as `OUTER APPLY (SELECT * FROM dbo.DJKCZH B WHERE A.sptm = B.sptm)`, the `(all SELECT columns)` completion item inserted both the outer `A` and inner `B` columns.
  - The list was built from every alias visible at the caret, and a correlated subquery can see the outer alias. `SELECT *` now uses only the sources declared by the caret query block (`SqlScopeTree.OwnSourcesAt`); outer aliases remain available for `A.column` member completion.
  - A top-level `SELECT * ... APPLY (...)` still includes the APPLY derived table's columns, matching SQL Server semantics.
- **Tests**: new regression case for APPLY subqueries; all 371 regression tests pass, and one SQL Server metadata integration test is skipped because `MSSQLTOOL_TEST_CONNECTION` is not set.

## 5.508

### 中文

- **修复深色模式下相关标识符高亮块颜色不适合的问题**（issue #6）
  - 高亮背景原来一律使用 `#C0C0C0` 浅灰实心块，在深色主题下过亮：与深色编辑器背景对比强烈，同时把浅色语法文字压成约 1.2:1 的对比度，几乎不可读。
  - 现在根据当前 Shell 主题自适应：浅色主题保留 `#C0C0C0`；深色主题从主题背景色提亮约 16% 得到标记色，既跟自定义深色主题的色相，又不会盖住文字。
  - 订阅 `VSColorTheme.ThemeChanged`，切换主题时通过视图的 `IClassificationFormatMap` 立即刷新已打开窗口的高亮色，无需重启 SSMS。
  - 窗口回退高亮层 `SqlPromptHighlightOverlay` 的固定灰也改为同一套主题色（保留半透明）。
- **测试**：全量回归测试通过；1 个 SQL Server 元数据集成测试因未设置 `MSSQLTOOL_TEST_CONNECTION` 跳过。

### English

- **Fixed the related-token highlight colour being unsuitable in dark themes** (issue #6)
  - The marker used a fixed `#C0C0C0` solid block. On a dark editor that block is far brighter than the background and drops light syntax colours to roughly 1.2:1 contrast, which makes the highlighted text unreadable.
  - The colour is now theme-aware: light themes keep `#C0C0C0`; dark themes lift the themed background by about 16%, so the marker follows custom dark themes instead of pasting a light block over them.
  - The classifier subscribes to `VSColorTheme.ThemeChanged` and pushes the new brush into the view's `IClassificationFormatMap`, so an open window updates immediately without restarting SSMS.
  - The window fallback overlay (`SqlPromptHighlightOverlay`) now uses the same palette (still translucent) instead of a hard-coded grey.
- **Tests**: the full regression suite passes; one SQL Server metadata integration test is skipped because `MSSQLTOOL_TEST_CONNECTION` is not set.

## 5.507

### 中文

- **修复 Ctrl+Space 补全面板被 SSMS 主窗口遮挡**
  - 5.506 为避免补全窗口浮到其他应用之上，把显示时的 `SetWindowPos` 改成了 `SWP_NOZORDER`，并且只在尺寸或位置变化时重新定位。补全窗口的 owner 实际是隐藏的 `HwndlessEditorFakeHwndWrapper`，所以窗口虽然 `IsWindowVisible=True`，却排在 SSMS 主窗口下面，Ctrl+Space 看起来没有反应。
  - 现在改用真正的 SSMS 主窗口作为 owner，而不是编辑器的隐藏 fake HWND；只有当主窗口在前台时才把面板抬到前面，SSMS 失去前台后自动收起，因此不会压在其他应用上。
  - 分析器、命令路由和输入法均未改动。
- **测试**：全量回归测试通过；1 个 SQL Server 元数据集成测试因未设置 `MSSQLTOOL_TEST_CONNECTION` 跳过。

### English

- **Fixed the Ctrl+Space completion popup being hidden behind the SSMS main window**
  - 5.506 changed the show-time `SetWindowPos` call to `SWP_NOZORDER` and only repositioned when the geometry changed. The popup's owner is actually the hidden `HwndlessEditorFakeHwndWrapper`, so the window reported `IsWindowVisible=True` but sat below the visible SSMS frame; Ctrl+Space looked like it did nothing.
  - The popup now uses the real SSMS main window as its owner instead of the editor's hidden fake HWND. It is raised only while the main window is foreground and is dismissed when SSMS loses foreground, so it never floats above other applications.
  - The analyzer, command routing and IME handling are unchanged.
- **Tests**: the full regression suite passes; one SQL Server metadata integration test is skipped because `MSSQLTOOL_TEST_CONNECTION` is not set.

## 5.506

### 中文

- **修复存储过程体内 `SELECT *` 被误判为 UPDATE SET**
  - 根因：`CREATE PROC ... AS` 的模块体让语句窗口跨过前面的 `UPDATE ... WHERE` 与后面的 `SELECT *`；UPDATE 末尾没有分号时，`UpdateSetPattern` 的 `[^;]*$` 一直匹配到 SELECT，补全因此提供 `(all column assignments)`，按 Tab 会插入整段 `列 = NULL`。
  - 语句窗口现在只把 `CREATE/ALTER VIEW ... AS` 视为单一查询体；存储过程、函数、触发器体按语句列表处理，行首 `SELECT` 会成为新语句。
  - 解析器已识别为 `SelectList`/`DataSource` 时不再被旧的 `UpdateSet` 上下文覆盖；从 UPDATE 继承的目标表也会清空。
  - 星号展开支持遍历 `CREATE PROCEDURE`、函数、触发器以及 `BEGIN/END`、`IF`、`WHILE`、`TRY/CATCH` 内的 SELECT，Tab 可以正确执行 `*` 展开。
- **测试**：新增“存储过程体内 UPDATE 无分号 + SELECT *”回归用例；全量 370 项通过，1 个 SQL Server 元数据集成测试因未设置 `MSSQLTOOL_TEST_CONNECTION` 跳过。

### English

- **Fixed `SELECT *` inside a procedure body being treated as UPDATE SET**
  - Cause: the `CREATE PROC ... AS` module body kept the preceding `UPDATE ... WHERE` and the following `SELECT *` in one statement window. With no semicolon after UPDATE, `UpdateSetPattern`'s `[^;]*$` matched through the SELECT, so completion offered `(all column assignments)` and Tab inserted the whole `column = NULL` list.
  - The statement window now treats only `CREATE/ALTER VIEW ... AS` as a single query body; procedure, function and trigger bodies are statement lists, so a line-leading `SELECT` starts a new statement.
  - A parser-resolved `SelectList`/`DataSource` is no longer overridden by a stale `UpdateSet` context, and a target inherited from UPDATE is cleared.
  - Asterisk expansion now walks `CREATE PROCEDURE`, function and trigger bodies plus `BEGIN/END`, `IF`, `WHILE` and `TRY/CATCH` blocks, so Tab expands the star correctly.
- **Tests**: added a regression for an UPDATE without a semicolon followed by `SELECT *` inside a procedure body; all 370 regression tests pass, with one SQL Server metadata integration test skipped because `MSSQLTOOL_TEST_CONNECTION` is not set.

## 5.505

### 中文

- **SQL 补全的 P0/P1/P2 优化一次落地**
  - 只分析光标所在语句：新增语句窗口扫描，遇到顶层 `;`、`GO` 或行首语句起始词即停止；600 KB 会话脚本不再在每次按键时重新解析全文。
  - 修正语句窗口的续句判断：`UPDATE ... SET`、`INSERT ... SELECT`、`UNION SELECT`、`WITH ... SELECT`、`CREATE/ALTER ... AS` 模块体不会被误切成两条语句。
  - 候选过滤改为有界 Top-N 堆和字母 bitmask 预过滤，不再为每个候选创建临时对象或全量排序；模糊匹配只在小候选集或长前缀时兜底。
  - 增加按语句文本、光标列、afterCaret、兼容级别和会话局部对象键控的分析缓存；`#temp`/表变量声明按语句缓存。
  - 解析器按数据库兼容级别选择 `TSql100Parser`～`TSql170Parser`；补全作用域优先使用解析树，修复同一张表在嵌套子查询中使用不同别名时的列泄漏。
  - 元数据快照增加数据源和存储过程索引；候选文本、描述和插入文本改为惰性生成，降低大库候选集构建成本。
- **补全交互**
  - 弹窗下方空间不足时自动翻到光标上方；只在位置或尺寸变化时重定位，并使用 `SWP_NOZORDER` 避免打乱窗口层级。
  - 新增“插入键”设置：`Tab`、`Enter`、`Tab or Enter`，设置页、持久化和本地化同步完成。
  - 名称段边界匹配优先于普通中间子串，例如 `CustomerName` 中的 `Name` 排名更高。
- **测试**：全量 369 项回归通过；1 个 SQL Server 元数据集成测试因未设置 `MSSQLTOOL_TEST_CONNECTION` 跳过。新增语句窗口、同表嵌套别名、提交键、段边界排序和弹窗翻转用例。

### English

- **SQL completion P0/P1/P2 improvements**
  - Analyze only the statement at the caret: a new statement-window scanner stops at top-level `;`, `GO`, or a line-leading statement starter, so a 600 KB session no longer re-parses the whole document on every keystroke.
  - Statement continuation is handled for `UPDATE ... SET`, `INSERT ... SELECT`, `UNION SELECT`, `WITH ... SELECT`, and `CREATE/ALTER ... AS` module bodies.
  - Candidate filtering now uses a bounded Top-N heap plus an ASCII bitmask prefilter instead of per-candidate objects and a full sort; fuzzy matching is reserved for small lists or long prefixes.
  - Added an analysis cache keyed by statement text, caret column, after-caret text, compatibility level, and session-local objects; temp-table and table-variable declarations are cached per statement.
  - Parser selection follows the database compatibility level (`TSql100Parser` through `TSql170Parser`); completion scope prefers the parse tree and no longer leaks a same-table alias from a nested subquery.
  - Metadata snapshots now index data sources and procedures; candidate text, descriptions, and insert text are built lazily to keep large schemas responsive.
- **Completion interaction**
  - The popup flips above the caret when the space below is too short, repositions only when its geometry changes, and uses `SWP_NOZORDER` to preserve window layering.
  - Added an Insert key setting (`Tab`, `Enter`, `Tab or Enter`) with matching settings UI, persistence, and localization.
  - Segment-boundary matches now outrank buried substrings, for example `Name` in `CustomerName`.
- **Tests**: all 369 regression tests pass; one SQL Server metadata integration test is skipped because `MSSQLTOOL_TEST_CONNECTION` is not set. Added coverage for statement windows, same-table nested aliases, commit keys, segment-boundary ranking, and popup flipping.

## 5.504

### 中文

- **查询历史分组重做：新建查询的 `SQLQuery1.sql` 不再被当成同一个“文件”**
  - 根因：SSMS 每个未保存的查询标签背后都是 `%TEMP%\SQLQueryN.sql`（自动恢复后是 `~vsXXXX.sql`）这样的临时文档，插件把这些临时路径当成“已保存文件”的身份。临时名会被 SSMS 回收复用，于是一批互不相关的查询被并进同一组；实测原库 4,165 组里有 139 组混着多个不同标题，`SQLQuery1.sql` 这个名字出现在 389 个组里。
  - 另外两个叠加问题：`ContentHash` 全局唯一让同一段 SQL 在另一个标签出现时整行被“搬”到后一个分组；列表取 `MAX(Id)` 当“最新版本”，重用旧文本后仍显示旧内容。
  - 修法：临时文档不再拥有文件身份；每个打开的标签在本进程内分配一个稳定 token，分组键为 `tab:<token>`；保存的文件仍按路径分组；唯一索引从 `ContentHash` 改为 `(GroupKey, ContentHash)`，同一段 SQL 可以同时属于两个标签的历史；代表版本按 `LastSeenAt` 取（同秒再按 Id）；“另存为”时把该标签的版本迁到目标文件（文本一致才迁移，避免“全部保存”搬错）。
  - 历史窗口新增“类型”列（未保存标签 / 文件），同名 `SQLQueryN` 可以靠连接、时间和首行区分。
  - 旧库自动迁移到 schema v2，一次性重算全部分组键。在真实库副本上验证：17,206 行 → 4,307 组，混组从 139 降为 **0**，`.sql` 文件与临时标签分开统计（49 / 4,258）。
- **修复 SQLite 语句复用不重置**
  - `StepToCompletion` 结束后没有 `sqlite3_reset`，同一 prepared statement 第二次绑定时新值被忽略，循环更新只有第一行生效。备份分组键等批量写入都可能受影响；现在统一在完成时重置。
- **测试**：全量 361 项回归通过；新增 `--history-probe` 数据库探针（建 v1 库、迁移、按查询去重、代表版本、另存为迁移）与 `--history-inspect` 副本检查命令。

### English

- **Query history grouping rebuilt: `SQLQuery1.sql` is no longer treated as one file**
  - Cause: every unsaved SSMS tab is backed by a temporary document in `%TEMP%` (`SQLQueryN.sql`, or `~vsXXXX.sql` after an auto-recovery), and the extension used that path as the identity of a saved file. SSMS recycles those names, so unrelated queries were merged into one group: 139 of 4,165 groups mixed different titles, and the name `SQLQuery1.sql` appeared on 389 groups.
  - Two smaller defects added to it: `ContentHash` was globally unique, so the same SQL in a second tab moved the existing row to the newer group; and the list picked `MAX(Id)` as the current version, so reusing an older text kept showing the stale one.
  - Fix: temporary documents never get a file identity; each open tab receives a stable token for the session and groups by `tab:<token>`; saved files still group by path; the unique index is now `(GroupKey, ContentHash)`, so one text can belong to two tabs' histories; the representative version is chosen by `LastSeenAt` (then Id); Save As moves the tab's versions onto the saved file, and only when the saved text matches that tab.
  - The history window gained a Type column (unsaved tab / file), so repeated `SQLQueryN` titles are distinguishable by connection, time and first line.
  - Existing databases migrate to schema v2 and re-key every row once. Verified on a copy of a real database: 17,206 rows became 4,307 groups and mixed-title groups went from 139 to **0**, with `.sql` files and temporary tabs reported separately (49 / 4,258).
- **Reused SQLite statements are now reset**
  - `StepToCompletion` did not call `sqlite3_reset`, so the second bind of a reused prepared statement was ignored and a loop updated only its first row (the group-key backfill was exposed to this). Statements are reset when they finish.
- **Tests**: all 361 regression tests pass; added the `--history-probe` database probe (v1 database, migration, per-query de-duplication, representative version, Save As move) and the `--history-inspect` copy inspector.

## 5.503

### 中文

- **执行语句后整片变紫：底色误取了执行指示器的深色背景**
  - 日志：执行后那次重绘为 `opacity=0.225`（而平时是 `0.3`）、`chrome=Border,Indicator`。`0.225 = 0.3 × 0.75` 说明底色被判定为深色；多出来的 `Indicator` 就是 SSMS 执行后出现的指示器，它的背景色被当成了“主题底色”铺到图片下面，于是整块编辑器砦上紫色。
  - 原因：上一版解析底色时会把“刚被透明化的所有背景”都当作候选，而列表末尾恰好是`chrome` 扫描途中清掉的控件。
  - 修法：底色只从**视图自身的祖先链**（`ClearSurfaceChain`）里取最外层的不透明背景，然后才看面板/祖先，都没有就用白色；被清掉的边缘控件（滚动条、指示器、margin）不再参与底色选择。
- **测试**：全量 360 项回归通过。

### English

- **The editor turned purple after executing a statement: the base colour was taken from the dark execution indicator**
  - Log: the repaint after execution reported `opacity=0.225` (normally `0.3`) and `chrome=Border,Indicator`. The `0.225 = 0.3 x 0.75` factor means the base was classified as dark; the extra `Indicator` is what SSMS shows while a query runs, and its background was used as the theme colour under the picture, tinting the whole editor.
  - Cause: the previous base resolution treated every brush that had just been made transparent as a candidate, and the tail of that list was whatever the chrome pass had swept.
  - Fix: the base is taken only from the view's own ancestor chain (outermost opaque brush), then the panel and its ancestors, then white; chrome visuals (scrollbars, the indicator, margins) no longer influence it.
- **Tests**: all 360 regression tests pass.

## 5.502

### 中文

- **移除背景功能排查期新增的日志**
  - 删除：每次绘制都输出的 `apply:` 详情（`mode/backdrop/chrome/panel/base/host/geometry` 等）、每个格式项一条的 `key=` 日志、`chrome:` 可视树、`ancestors:` 祖先链。5.501 的日志已确认白带来源于编辑器边缘控件（滚动条轨道 `RepeatButton`、`ToggleButton` 及各 margin），这些信息不再需要。
  - 保留四条低频日志：`listener:`（监听器触发）、`attach:`（视图挂载，含 formatMap/enabled）、`map:`（格式映射来源）、`refresh:`（保存设置后的重绘）；一行一个视图，不再随布局刷屏。
  - 同时清理了只为日志存在的辅助方法，未改变背景绘制逻辑。
- **测试**：全量 360 项回归通过。

### English

- **Removed the diagnostics added while chasing the background bugs**
  - Dropped: the per-paint `apply:` dump (`mode/backdrop/chrome/panel/base/host/geometry`), the per-format-item `key=` lines, the `chrome:` visual tree dump and the `ancestors:` chain. The 5.501 log already proved the strip came from the editor's edge controls (scrollbar `RepeatButton`, `ToggleButton`, the margins).
  - Kept four low-frequency lines: `listener:` (view created), `attach:` (view attached, with formatMap/enabled), `map:` (format map source) and `refresh:` (repaint after saving the settings) - one line per view instead of one per layout pass.
  - Helper methods that only served those logs were removed; painting behaviour is unchanged.
- **Tests**: all 360 regression tests pass.

## 5.501

### 中文

- **图片下方那条白带仍在：上一版只扫了视图那条链，没扫到面板里的兄弟元素**
  - 5.500 日志：`apply: ... mode=backdrop backdrop=Grid 1110x732 picture=1110x732 chrome=Canvas ...`——整次只找到一个可透明化的画布；同时 `ancestors: ^Grid 1110x732 panel bg=DrawingBrush` 说明视图上方还有一层带画笔的面板，而滚动条/空白条是面板的其它子元素，不在上一版的扫描范围内。
  - 修法：透明化改为从**背景网格所在面板本身**开始递归（包含面板自己及其全部子元素），跳过背景网格本身、滚动条滑块、弹出层/提示框/装饰层与图片；被清掉的控件类型仍记在 `chrome=`。
  - 新增 `panel=` 字段，打印该面板前 6 个子元素的类型、尺寸和背景色；若白带仍未消失，这一行会直接指出是哪个控件。
  - `ancestors` 日志现在也能正确打印 `Border` 的背景色（之前统一显示 `-`）。
- **测试**：全量 360 项回归通过。

### English

- **The white band under the picture survived: the previous pass only scanned the view chain, not the panel's other children**
  - 5.500 log: `apply: ... mode=backdrop backdrop=Grid 1110x732 picture=1110x732 chrome=Canvas ...` - exactly one element (a canvas) was made transparent, while `ancestors: ^Grid 1110x732 panel bg=DrawingBrush` shows the panel above the view still carries a brush, and the scrollbar/empty strip are its other children, outside the scanned subtree.
  - Fix: the transparency pass now starts at the panel that holds the backdrop grid (the panel itself plus every child), skipping the backdrop grid, scrollbar thumbs, popups, tooltips, adorners and images. Cleared types are still reported as `chrome=`.
  - New `panel=` field prints the type, size and background of the panel's first six children, so a surviving strip names its own control.
  - The `ancestors` line now also prints the background of `Border` elements (it used to show `-`).
- **Tests**: all 360 regression tests pass.

## 5.500

### 中文

- **设置页去掉一行说明文字**：删除编辑器背景图下方“内置图片来自 Reasonix 主题包（MIT 许可），来源见 Resources/backgrounds/NOTICE.txt。”及其语言对照条目；图片包里的 `Resources/backgrounds/NOTICE.txt` 保留不动，许可署名仍随包分发。
- **测试**：全量 360 项回归通过。

### English

- **Removed one caption from the settings page**: the “Bundled pictures come from the Reasonix theme packs (MIT)…” line under the editor background options is gone, together with its localization entry. `Resources/backgrounds/NOTICE.txt` stays in the package, so the attribution still ships.
- **Tests**: all 360 regression tests pass.

## 5.499

### 中文

- **图片下方多出一条白带：滚动条区域没有透明化**
  - 图片现在铺满宿主面板，但横向滚动条和正文下方那条空白属于其他可视层，底色仍是主题白，于是叠在图片上方形成一条白带。
  - 修法：透明化范围从“视图到背景面板的那串祖先”扩展到编辑器宿主的整棵可视子树，递归清掉仍是不透明背景的控件（滚动条轨道、边距、空白条）；按类型名保留滚动条滑块（`Thumb`）、弹出层、提示框、装饰层和图片本身，保证滑块还能看到、能拖。
  - `apply` 日志新增 `chrome=`，列出这次被透明化的控件类型（最多 12 个），便于核对是否命中滚动条。
- **测试**：全量 360 项回归通过。

### English

- **A white band under the picture: the scrollbar area was never made transparent**
  - The picture now fills the host panel, but the horizontal scrollbar and the empty strip below the text belong to other visuals that still painted the theme colour on top of it.
  - Fix: the transparency pass now covers the whole visual subtree of the editor host (scrollbar tracks, margins, the empty strip), while scrollbar thumbs, popups, tooltips, adorners and images are kept by type name so the thumb stays visible and draggable.
  - The `apply` log line gains `chrome=`, listing the control types that were made transparent (up to 12).
- **Tests**: all 360 regression tests pass.

## 5.498

### 中文

- **编辑器变回纯白：图片根本没有被应用（不是渲染错）**
  - 日志证据：重启后的会话（5.497）里只有 `SQL-BACKGROUND refresh: views=0 enabled=True`，**没有任何 `listener:` 和 `attach:` 行**；同时同一会话里 `EDITOR host=... wpfView=yes` 证明插件确实拿到了一个编辑器视图。也就是说：SSMS 启动时恢复查询窗口，比扩展的 MEF 文本视图监听器就绪更早，监听器没触发，于是整个背景功能没挂上去，看到的就是原始主题白底。
  - 修法：新增 `EditorBackground.EnsureAttached`，在插件通过 `EditorHostFactory.Create` 拿到编辑器视图时再挂一次；`Attach` 同时加了幂等保护，监听器后到也不会重复订阅事件。
  - 影响：从这条路径挂上时日志会先出现 `attach:`（来源是编辑器宿主），不再是只有 `listener:`。
- **测试**：全量 360 项回归通过。

### English

- **The editor went back to plain white: the picture was never applied (not a rendering fault)**
  - Log evidence: after the restart, the 5.497 session only contains `SQL-BACKGROUND refresh: views=0 enabled=True` and **no `listener:` or `attach:` line at all**, while `EDITOR host=... wpfView=yes` in the same session proves the package did get a text view. SSMS restores query windows before the extension's MEF text-view listener is composed, so the listener never fired and the whole feature stayed off - what was left is the plain theme background.
  - Fix: `EditorBackground.EnsureAttached` attaches from the editor host path (`EditorHostFactory.Create`, which the package always goes through when it touches a view), and `Attach` is now idempotent so a late listener cannot subscribe twice.
  - When this path attaches, the log shows `attach:` without a preceding `listener:` line.
- **Tests**: all 360 regression tests pass.

## 5.497

### 中文

- **编辑器整片发黑、图片发焖：背后层下面没有底色**
  - 5.496 日志：`apply: ... mode=backdrop backdrop=Grid 1110x732 picture=1110x732 base=#00FFFFFF/Captured viewBackground=#00FFFFFF controlBackground=#00FFFFFF`。背后层确实插进去了，但它的背景色 `root.Background` 拿到的 `baseBrush` 是 `#00FFFFFF`（全透明）。
  - 原因：这个 shell 里 `view.Background` 本身就是透明的，真正的白底由视图上方的 `Border`/`ContentPresenter` 画；5.496 为让图透出来把这些背景全置透明后，图片实际就叠在了窗口底层的黑色上。按“透明度 70%”（即不透明度 30%）一算，就是截屏里那种又暗又焖的效果——不是图没画，而是图下面没底。
  - 修法：新增 `ResolveBackdropBase`，按顺序找一个**不透明**的底色：当前视图画刷 → 首次记录的画刷 → 刚被置透明的那串元素原本的背景（从外到内，最外层才是主题底色）→ 面板及其祖先的背景；都没有时用白色。底色先铺满整块面板，再叠图片，并改用这个底色重新计算暗色主题压暗系数。
  - 日志 `SQL-BACKGROUND ancestors:` 现在带 `bg=`，能直接看出每一层祖先原本的背景色。
- **设置里增加开关**
  - 「编辑器」标签页新增勾选项「整张图作为一层放在编辑器背后（推荐）」（对应 `useBackdrop`，默认开启），与其它背景设置一起保存，保存后立即重绘所有已打开的 SQL 编辑器；如果某个 SSMS 版本下仍然不正常，取消勾选即可回到“画进文本层”的旧方案。
- **测试**：全量 360 项回归通过。

### English

- **The editor went black and the picture muddy: the backdrop had no colour underneath**
  - The 5.496 log: `apply: ... mode=backdrop backdrop=Grid 1110x732 picture=1110x732 base=#00FFFFFF/Captured`. The layer was inserted, but its `Background` was `#00FFFFFF` (fully transparent).
  - Cause: in this shell `view.Background` is transparent and the real white is painted by the `Border`/`ContentPresenter` chain above the view. 5.496 made that chain transparent so the picture could show through, which left the picture composited on the bare (black) window. With the configured 30% opacity that is exactly the dark, muddy result in the screenshot - the picture was there, the base was missing.
  - Fix: `ResolveBackdropBase` now looks for a genuinely opaque colour (current view brush, the first remembered brush, the original brushes of the chain that was just cleared - outermost first, then the panel and its ancestors) and falls back to white. That colour fills the whole panel under the picture, and the dark-theme opacity factor is recomputed from it.
  - The `SQL-BACKGROUND ancestors:` log line now carries `bg=` for every ancestor.
- **New switch in the settings**
  - The Editor tab has a new checkbox, "Draw the picture as one layer behind the editor" (`useBackdrop`, on by default), saved with the rest and applied to every open SQL editor immediately. Clearing it returns to the older "paint into the text layer" path.
- **Tests**: all 360 regression tests pass.

## 5.496

### 中文

- **换架构：整张图放到编辑器“背后”，不再分成两个面各自画**
  - 参考 ClaudiaIDE（`github.com/buchizo/ClaudiaIDE`）的做法：它的图不进文本渲染管线，而是作为一个普通 WPF 层插在编辑器宿主父容器的第 0 个子元素位置，再把中间所有层（文本视图、格式映射、面板）改成透明。因此它天生没有接缝、没有白块，也完全不受编辑器缩放影响（源码里没有任何 `Zoom` 处理）。
  - 5.496 采用同样思路：从文本视图向上找第一个可用 `Panel` 祖先，插入拉伸铺满的 `Grid`（`MSSQLToolEditorBackdrop`，`IsHitTestVisible=false`）作为背景层，里面是主题底色加一张 `Image`（按设置透明度）；随后把视图到该面板之间每个 `Control/Panel/Border` 的背景置透明，格式映射的四项也全部透明，文字直接透到这张图上。
  - 结果：左边距、正文、正文下方那条边看到的是**同一张图、同一个矩形**，接缝与白块从结构上消失；119% 缩放也不再需要额外几何换算（这张图不在缩放空间里）。
  - 回退：找不到可插入的面板（或填充方式为 `Tile`，`Image` 不能平铺）时，自动回到 5.495 的“格式映射 + 宿主层”方案；两种模式都会在日志写明 `mode=backdrop` / `mode=format`。
  - 新增日志 `SQL-BACKGROUND ancestors:`，打印视图向上的 6 层祖先（类型、尺寸、是否 Panel），用于核对背景层插到了哪一层。
  - 设置里新增 `useBackdrop`（默认 `true`）。若某版本 SSMS 把文本层画成不透明、出现整片黑，把设置存储中 `EditorBackgroundSettings` 的 JSON 里该字段改成 `false` 即可回到旧方案，无需重新编译。
- **测试**：全量 360 项回归通过。

### English

- **New architecture: one picture behind the editor instead of two separately painted surfaces**
  - Follows ClaudiaIDE (`github.com/buchizo/ClaudiaIDE`): its picture never enters the text rendering pipeline. It is a plain WPF layer inserted as child 0 of the panel that holds the editor host, and every surface in between (text view, format map, panels) is made transparent. That is why it has no seam, no uncovered strip, and no zoom coupling at all (the source has no `Zoom` handling).
  - 5.496 does the same: the first usable `Panel` ancestor of the text view receives a stretched `Grid` (`MSSQLToolEditorBackdrop`, `IsHitTestVisible=false`) holding the theme colour and one `Image` at the configured opacity; then every `Control/Panel/Border` between the view and that panel is made transparent, as are the four format-map entries, so the text shows the picture directly.
  - Result: left margin, code area and the strip below the text show the same picture rectangle by construction, and the 119% zoom no longer needs any geometry: the picture is not in the zoomed space.
  - Fallback: when no suitable panel exists (or the fit mode is `Tile`, which an `Image` cannot do), the 5.495 format-map plus host-layer path is used; the log states `mode=backdrop` or `mode=format`.
  - New `SQL-BACKGROUND ancestors:` log line prints the six ancestors above the view (type, size, whether a panel), so the chosen layer can be verified.
  - New `useBackdrop` setting (default `true`). If a shell paints the text surface opaque and the editor turns black, set it to `false` in the stored `EditorBackgroundSettings` JSON to return to the old path without a rebuild.
- **Tests**: all 360 regression tests pass.

## 5.495

### 中文

- **左下白块仍在：宿主层没有乘编辑器缩放**
  - 5.494 的 `geometry` 日志：`view=850x585 canvas=2,0 848x585 picture=-14,0 1040x585 offset=79,0`。截图里正文区画面画到 y≈696 DIP，而宿主那份只到 585 DIP——差的 19% 正是状态栏上的缩放 **119%**。
  - 原因：格式层是在**缩放后的坐标**里绘制的，583 DIP 的视口在 119% 下被画成 696 DIP；宿主层用的是未缩放的 DIP，于是左边距（透明、露出宿主层）那份比正文小 19%，且比正文矮一截；585 DIP 以下只有底色，就是那块白。
  - 修法：宿主层按 `ZoomedArea(canvas × zoom)` 计算图片矩形（`view.ZoomLevel / 100`，异常/越界时回退 100%），并在 `ZoomLevelChanged` 时重算——否则用户改缩放后宿主层会停在旧尺寸。
  - `geometry` 日志新增 `zoom=…`，可直接核对：119% 时 585 DIP 的画布应得到 696 DIP 高的图片矩形。
- **测试**：新增 `ZoomedArea` 断言（119% 与非法缩放回退），全量回归通过。

### English

- **The bottom-left white block survived: the host layer did not apply the editor zoom**
  - The `geometry` line from 5.494: `view=850x585 canvas=2,0 848x585 picture=-14,0 1040x585 offset=79,0`. In the screenshot the text picture reaches y≈696 DIP while the host copy stops at 585 DIP - the missing 19% is exactly the **119%** zoom shown in the status bar.
  - Cause: the format layer is painted in the **zoomed coordinate space**, so a 585 DIP viewport is painted 696 DIP tall; the host layer used unzoomed DIP. The left margin (transparent, reveals the host layer) therefore showed a copy 19% smaller and shorter than the text area, and below 585 DIP only the theme colour was left - that is the white block.
  - Fix: the host rectangle is now computed from `ZoomedArea(canvas × zoom)` (`view.ZoomLevel / 100`, falling back to 100% on a bogus reading), and it is recomputed on `ZoomLevelChanged` - otherwise the host layer would keep the old size after the user changes the zoom.
  - The `geometry` log line now carries `zoom=…`: at 119% a 585 DIP canvas must yield a 696 DIP tall picture rectangle.
- **Tests**: new `ZoomedArea` assertions (119% and the invalid-zoom fallback); the full suite passes.

## 5.494

### 中文

- **左下白块与左侧“割裂”：宿主层没有对齐格式层真正绘制的区域**
  - 日志与截图对照：`host=Grid 1110x732`，而编辑器视图只有 850x598 左右；截图里竖条边界正好在**左边距**结束处，条带里的画面比正文区更大、位置也更靠上，条带底部还露出主题底色。
  - 原因：格式层（`TextView Background`）画的是**文字画布**——从左边距之后开始、高度是整个视图；而宿主层用的是 `view.ViewportWidth/Height`（不含边距、更小）。同一个 `UniformToFill` 放进两个不同大小的矩形，就得到两个不同缩放/偏移的副本：左边距是透明的，露出宿主层的那份，于是边界处“割裂”；宿主那份按视口高度算，比视图矮一截，左下就露出底色白块。
  - 修法：宿主层改用与格式层**同一个矩形**。先从可视树里取 `TextContentLayer`（取不到时用首行首字符的左边界）得到文字画布的起点与尺寸，再按当前“填充方式”算出图片矩形（`PlacementForFit`：Fill / Uniform / UniformToFill），最后换算到宿主坐标以 1:1 绝对坐标画一次。合并后两层在左边距处是同一张图，条带不再断层。
  - `apply` 日志新增 `geometry=view=… canvas=… picture=… offset=…`，下一轮可直接核对画布与图片矩形是否一致。
- **填充方式也会影响宿主层**：之前宿主层固定按“覆盖并居中”画；选 `Fill` / `Uniform` 时同样会出现第二缩放比。现在宿主层跟随设置里的 `fit`；`Tile` 无法用一次 `ImageDrawing` 表达，宿主层保持主题底色，不再画错位的副本。
- **测试**：新增 `Editor background: placement follows the fit mode`（Fill/Uniform/UniformToFill/Tile 与未测量视图），全量回归通过。

### English

- **White block at the bottom left and the split on the left: the host layer was not aligned with the rectangle the format layer really paints**
  - Log and screenshot together: `host=Grid 1110x732` while the editor view is only about 850x598; the vertical edge in the screenshot sits exactly at the end of the **left margin**, the strip shows the picture larger and higher than the text area, and its bottom shows the theme colour.
  - Cause: the format layer (`TextView Background`) paints the **text canvas** - it starts after the left margin and is as tall as the view - while the host layer used `view.ViewportWidth/Height`, which excludes the margins and is smaller. The same `UniformToFill` put into two different rectangles produces two copies with different scale and offset: the left margin is transparent and shows the host copy, so the boundary breaks; the host copy is shorter than the view, so the bottom-left corner shows the plain theme colour.
  - Fix: the host layer now uses **the same rectangle** as the format layer. The text canvas origin and size are read from the visual tree (`TextContentLayer`, falling back to the left edge of the first character of the first line), the picture rectangle is computed for the selected fit (`PlacementForFit`: Fill / Uniform / UniformToFill) and painted once in 1:1 absolute host coordinates. Both layers now carry the same image, so the margin continues it instead of breaking it.
  - The `apply` log line now ends with `geometry=view=… canvas=… picture=… offset=…`, so the next round can verify canvas and picture rectangles directly.
- **The fit mode now applies to the host layer as well**: it used to always cover-and-centre, so `Fill` and `Uniform` produced a second scale there too. It now follows the configured `fit`; `Tile` cannot be expressed with a single `ImageDrawing`, so the host keeps the theme colour instead of painting a misplaced copy.
- **Tests**: new `Editor background: placement follows the fit mode` (Fill / Uniform / UniformToFill / Tile and an unmeasured view); the full regression suite passes.

## 5.493

### 中文

- **黑色方块：宿主画刷只在图片矩形内绘制**
  - 日志已确认宿主是 `host=Grid 1110x732`。5.492 把宿主画刷做成“绝对 Viewport = 图片矩形”，而 `TileMode=None` 的笔刷在矩形之外**什么都不画**，于是矩形没盖到的左下角、右侧滚动条旁边就露出了黑窗口。
  - 改成**宿主坐标 1:1 映射**：`Viewbox == Viewport == 整个宿主矩形`、`Stretch=Fill`，底色矩形铺满整个宿主，图片按 `PicturePlacement` 算出的位置画在里面。这样：① 任何角落都是底色，不会再黑；② 图片位置仍然和格式层一致，条带处接着画。
  - 宿主单独改变尺寸也会重算（`host.SizeChanged`），关闭时恢复宿主原来的 `Background`。
- **chrome 日志之前都是 `0x0`**：日志在布局前被记了下来并烧掉了一次性标记。现在只有控件真的有尺寸时才记录，且每次布局都尝试，直到记成功。
- **测试**：仍是 **359 项全过**（`PicturePlacement` 的 9 条断言覆盖“图片始终覆盖整个区域”）。

### English

- **Black blocks: the host brush only painted inside the picture rectangle**
  - The log confirmed the host: `host=Grid 1110x732`. 5.492 gave the host brush an absolute viewport equal to the picture rectangle, and a brush with `TileMode=None` paints **nothing** outside that rectangle - so the bottom-left corner and the area beside the scrollbar showed the black window behind.
  - The brush now maps **host coordinates 1:1**: `Viewbox == Viewport == the whole host rectangle`, `Stretch=Fill`, the base rectangle fills the entire host and the picture is drawn at the position `PicturePlacement` computes. All corners get the theme colour again, and the picture still lines up with the format layer, continuing into the strip.
  - A host-only resize also recomputes the brush (`host.SizeChanged`), and the host's original `Background` is restored when the feature is switched off.
- **The chrome log was always `0x0`**: it was recorded before layout and burned its one-shot marker. It now records only once the control really has a size, and keeps retrying on each layout pass until it succeeds.
- **Tests**: still **359 passing** (the nine `PicturePlacement` assertions pin "the picture always covers the area").

## 5.492

### 中文

- **底部那条白线：由宿主控件托管补上**
  - 日志里 `SQL-BACKGROUND chrome: WpfTextView 0x0` 说明首次 apply 时编辑器还没布局（尺寸 0），所以看不到子节点。现在改为**首次布局之后再记录**可视树，带有尺寸和背景。
  - 白线的成因：格式映射只负责文本视图自己的区域；文本下方那一条是**宿主控件**（文字的父节点）的底色，所以无论怎么改格式映射，它都不会被图片覆盖。
  - 现在同时把图片画到宿主控件上，并且用**同一套几何**：`EditorBackgroundRules.PicturePlacement` 按“图片拉伸覆盖整个视口并居中”算出图片在视口内的矩形，再换算到宿主坐标，以绝对 `Viewport` + `Stretch=Fill` 画一次。两层的映射完全一致，所以图片在白条处是**接着画下去**的，不会出现第二个缩放比的副本（不会再把割裂带回来），文本区仍由不透明的格式层盖住。
  - 窗口尺寸变化时重新计算（`SizeChanged`），否则白条会停在上一次的裁剪位置。关闭开关时把宿主原来的 `Background` 还回去。
  - 若某种宿主没有这种节点（`FindHost` 找不到），最多重试 4 次就放弃，不会在每次布局事件里重复 apply。
- **测试**：`PicturePlacement` 新增 9 条断言（覆盖视口、保持比例、居中、未测量时返回空），全量回归 **359 项全过**。

### English

- **The white line at the bottom is painted by the host control**
  - The log said `SQL-BACKGROUND chrome: WpfTextView 0x0`: the first apply runs before layout, so no children were listed. The tree is now logged **after the first layout pass**, with sizes and backgrounds.
  - Why the line exists: the format map only covers the text view's own area; the strip underneath belongs to the **host control** (the text view's parent), so no format-map change can ever cover it.
  - The picture is now painted on the host as well, with **the same geometry**: `EditorBackgroundRules.PicturePlacement` computes where the picture sits inside the viewport (cover and centre), that rectangle is translated into host coordinates, and the brush is drawn once with an absolute `Viewport` and `Stretch=Fill`. Both layers share one mapping, so the picture simply **continues** into the strip instead of showing a second copy at another scale (the seam cannot come back), and the text area is still covered by the opaque format layer.
  - The geometry is recomputed when the editor is resized (`SizeChanged`), and the host's original `Background` is restored when the feature is switched off.
  - If a shell has no such host node (`FindHost` finds nothing), the retry stops after four attempts instead of re-applying on every layout event.
- **Tests**: nine new `PicturePlacement` assertions (cover, aspect, centring, unmeasured viewport). All **359 regression tests pass**.

## 5.491

### 中文

- **修复 5.490 的黑屏：图必须画在格式映射上**
  - 5.490 把图片从格式映射改到编辑器控件后整片黑。黑屏本身就是证据：**格式映射那层才是真正画东西的地方**，把它改全透明后就露出了编辑器后面的黑窗口。视图（控件）的 `Background` 根本不参与显示，只是被那层盖住。
  - 现在只在 `TextView Background` 一项里放图片（`PictureSurfaces = { "format-map" }`），其余分类项（`Plain Text` / `SQL` / `TSQL`）保持透明；视图/控件背景只放**不透明的主题底色**，不再放图片。
  - 这样仍然只有一个绘画面、一个缩放比，5.490 想去掉的接缝/割裂不会回来（割裂本来就是两处同时画同一张图造成的）。
- 保留 `SQL-BACKGROUND chrome:` 可视树日志，下一轮定位底部留白可直接看节点。
- **测试**：断言“图片只在一个面：format-map”，全量回归 **359 项全过**。

### English

- **Fixes the black editor of 5.490: the picture must live in the format map**
  - 5.490 moved the picture from the format map to the editor control and the whole editor turned black.  The black screen is the evidence: **the format-map entry is the surface that is actually painted**; making it transparent reveals the black window behind the editor, and the view's own `Background` is simply covered by that layer.
  - The picture is back in `TextView Background` only (`PictureSurfaces = { "format-map" }`); the other classification entries (`Plain Text` / `SQL` / `TSQL`) stay transparent, and the view/control background now holds the opaque theme colour instead of a second copy of the picture.
  - There is still exactly one painted surface at one scale, so the seam 5.490 was fixing cannot come back (that seam was two surfaces painting the same picture).
- The `SQL-BACKGROUND chrome:` visual-tree log is kept, so the remaining bottom strip can be traced to a node next round.
- **Tests**: assert the picture has exactly one surface, `format-map`. All **359 regression tests pass**.

## 5.490

### 中文

- **背景图只画一遍：去掉中间的割裂线**
  - 之前的照片同时画在两个面上：编辑器控件的 `Background`（整块区域）和格式映射的 `TextView Background`（文本画布）。同一个 `DrawingBrush` 在两个不同大小的矩形里被 `UniformToFill` 拉伸 → 得到两个不同缩放比的副本；重叠处看起来更清楚，另一个面的边界就是那条“割裂”线，重叠区以外又会变成只有一层的淡色。
  - 现在图片**只画在编辑器控件上**（`EditorBackgroundRules.PictureSurfaces` 固定为一个面），格式映射的四个项全部改成全透明，让唯一的这一层透上来。一个面、一个比例，就不会有接缝。
  - `TextView Background` 不再是画图的面，所以关掉后也不会再因为它残留一张图。
- **新增诊断日志**：每个视图首次 apply 时会打一行 `SQL-BACKGROUND chrome: …`，列出编辑器可视树（类型/尺寸/背景，深度 6、最多 20 项）。如果底部那条素色带还在，日志里就能直接看出是哪个节点在盖色，不用再猜。
- **测试**：新增 `Editor background: the picture is painted once`，全量回归 **359 项全过**。

### English

- **The picture is painted once: the seam in the middle is gone**
  - The picture used to live on two surfaces at the same time: the editor control's `Background` (the whole area) and the format map's `TextView Background` (the text canvas). The same `DrawingBrush` was `UniformToFill`-stretched into two rectangles of different size, so two copies at slightly different scales existed — the overlap looked stronger, the boundary of the smaller surface was the visible seam, and the area covered by only one copy looked washed out.
  - The picture now lives **only on the editor control** (`EditorBackgroundRules.PictureSurfaces` has exactly one entry) and all four format items are made fully transparent, so the single painting shows through. One surface, one scale, no seam.
  - `TextView Background` no longer carries a picture, so switching the feature off cannot leave one behind there either.
- **New diagnostic log**: the first apply of a view writes `SQL-BACKGROUND chrome: …` with the editor visual tree (type/size/background, depth 6, at most 20 nodes). If the plain strip at the bottom survives, the log names the node that paints it.
- **Tests**: `Editor background: the picture is painted once`. All **359 regression tests pass**.

## 5.489

### 中文

- **背景图越开越亮：图片被画了两遍**
  - 日志里每开一个脚本就是一个 `listener: view created` + `apply`，而且**新视图的格式映射里已经是我们的 `DrawingBrush`**：`key=TextView Background originalBrush=DrawingBrush`。
  - 原因：`Attach` 把 `view.Background` 当作“主题底色”存进 `BaseBrush`，而新视图的 `view.Background` 已经是上一个视图留下的、里面**已经含了那张图**。于是新的画刷 = （图 @ 25%）上再画一层（图 @ 25%），实际看着约 44%，每开一个文档就更清、更亮。存储里的透明度一直是 0.25，所以“设置没变、画面变了”。
  - 修法一：自己画过的画刷一律记住（`OwnBrushes`），永远不允许它当底色；选底色改成显式规则 `EditorBackgroundRules.ChooseBase(当前是不是我们画的, 创建时是不是我们画的, 有没有记住主题色)`，四级回退到“首次见到的主题底色”。
  - 修法二：格式映射的“原始值”改为**按映射存一份**（`MapOriginals`），第二个视图复用同一个映射时不再把已经上色的值当原始值。这样“关掉背景图”也真能回到主题色，而不是把图留在那里（`Restore` 同步修正）。
  - 日志 `apply` 行新增 `base=…/Captured|Creation|Remembered|None`，下轮能直接看出底色来自哪里。
- **测试**：`TestEditorBackgroundOpacityClampsAndDims` 新增 5 条断言，固定 `ChooseBase` 的四种输入组合。全量回归 **358 项全过**。

### English

- **The background got brighter with every document: the picture was painted twice**
  - The log shows one `listener: view created` + `apply` per opened script, and the new view's format map **already contains our `DrawingBrush`**: `key=TextView Background originalBrush=DrawingBrush`.
  - Cause: `Attach` stored `view.Background` as the “theme colour” in `BaseBrush`, but on a later view that brush is the previous view's, which **already contains the picture**. The new brush was therefore (picture @ 25%) drawn on top of (picture @ 25%) — about 44% on screen, getting clearer with every document. The stored transparency never changed, which is why the setting looked innocent.
  - Fix 1: every brush we paint is remembered (`OwnBrushes`) and can never be the base again; the base is chosen by an explicit rule, `EditorBackgroundRules.ChooseBase(capturedIsOurs, creationIsOurs, hasRemembered)`, falling back to the first theme brush ever seen.
  - Fix 2: format-map originals are stored **per map** (`MapOriginals`), so a second view reusing a painted map no longer records painted values as original. Switching the feature off now really returns the theme colour (the `Restore` path was corrected too).
  - The `apply` log line now carries `base=…/Captured|Creation|Remembered|None`.
- **Tests**: five new assertions in `TestEditorBackgroundOpacityClampsAndDims` pin all four input combinations of `ChooseBase`. All **358 regression tests pass**.

## 5.488

### 中文

- **FROM/JOIN 的补全里新增“表值构造函数”建议**
  - 列表项 `VALUES (...)`（类型 Snippet，分数 38，紧跟表/视图/同义词之后），选中后直接粘出一整段派生表，不再只是提示关键字：
    ```
    (
        VALUES (1, 2)
    ) AS t (c1, c2)
    ```
  - 光标停在第一个 `1` 上，顺序 Tab 可依次走到 `2`、别名 `t`、列名 `c1`、`c2`。
  - 详情面板（中英双语）：用 VALUES 直接写出几行固定数据，不必建临时表；**SQL Server 要求 AS 后面跟列名清单**，且每一行 VALUES 的值的个数必须一致。
  - 只在 `FROM `/`JOIN ` 的源位置出现（`CompletionContextKind.DataSource`），`SELECT ` 列表里不会多出这一项。
- **测试**：新增 1 项——源列表必须含这项且 InsertText 是整段结构（以 `(` 开头、含 `AS `、含 `c1`/`c2`、含 `${1:1}`）；输入 `VAL` 时它排在第一；`SELECT ` 列表中没有它；中文下详情被真的翻译（含“临时表”）。全量回归 **358 项全过**。

### English

- **The FROM/JOIN completion now offers a table value constructor**
  - The row is `VALUES (...)` (kind Snippet, score 38, right after tables/views/synonyms) and pastes a complete derived table instead of just the keyword:
    ```
    (
        VALUES (1, 2)
    ) AS t (c1, c2)
    ```
  - The caret starts on the first `1`; Tab moves through `2`, the alias `t` and the column names `c1`, `c2`.
  - Details pane (both languages): a few fixed rows written inline without a temporary table; **SQL Server needs the column list after AS**, and every VALUES row must carry the same number of values.
  - It only appears in a source slot (`FROM `/`JOIN `, `CompletionContextKind.DataSource`), never in a `SELECT ` list.
- **Tests**: one new case — the source list must contain the row and its InsertText must be the whole structure (starts with `(`, contains `AS `, `c1`/`c2` and `${1:1}`); typing `VAL` puts it first; `SELECT ` does not list it; the Chinese details are really translated (contain “临时表”). All **358 regression tests pass**.

## 5.487

### 中文

- **表值函数也有说明了**
  - 之前 `OPENJSON` / `OPENXML` / `STRING_SPLIT` / `GENERATE_SERIES` 的详情只显示一行 `SQL Server table-valued function`，没有任何用法。现在它们和其他内置函数一样，详情面板给出**签名 + 中文用法 + 示例**：
    - `OPENJSON(json [, path]) [WITH (column type 'path' [,...])]` —— 表值函数：JSON 数组每个元素一行，`WITH` 把指定路径变成列；示例带 `WITH (Id int '$.id', ...)`。
    - `OPENXML(document_handle, rowpattern [, flags]) [WITH (...)]` —— 通过 `sp_xml_preparedocument` 的句柄读 XML，用完要 `sp_xml_removedocument`。
    - `STRING_SPLIT(string, separator [, enable_ordinal])` —— 每一段一行，列名 `value`（开启 `enable_ordinal` 时还有 `ordinal`）。
    - `GENERATE_SERIES(start, stop [, step])` —— 数字序列（SQL Server 2022，兼容级别 160）。
  - **用户自定义表值函数**：详情最前面先给一行用法（“在 FROM/JOIN 里带参数调用……”，中英文均已翻译），再跟原来的列结构与定义。
  - `AddBuiltInTableFunctions` 改为统一走 `TableFunctionItem(name)`，与标量函数用同一个 `FunctionDocumentation`，不另开一套文案。
- **测试**：新增 1 项——内置表值函数必须有签名/用法/示例，`OPENJSON` 的详情包含 `WITH (`，自定义表值函数详情包含用法行与全名，中文下两者都真的被翻译（不回落英文）。全量回归 **357 项全过**。

### English

- **Table-valued functions now explain themselves**
  - `OPENJSON` / `OPENXML` / `STRING_SPLIT` / `GENERATE_SERIES` used to show the single line `SQL Server table-valued function` in the details pane. They now carry **signature + usage note + example**, the same way scalar functions do:
    - `OPENJSON(json [, path]) [WITH (column type 'path' [,...])]` — one row per JSON array element; `WITH` turns named paths into columns (example includes `WITH (Id int '$.id', ...)`).
    - `OPENXML(document_handle, rowpattern [, flags]) [WITH (...)]` — reads XML through a handle from `sp_xml_preparedocument`; release it with `sp_xml_removedocument`.
    - `STRING_SPLIT(string, separator [, enable_ordinal])` — one row per part, column `value` (plus `ordinal` when `enable_ordinal` is on).
    - `GENERATE_SERIES(start, stop [, step])` — numeric sequence (SQL Server 2022, compatibility level 160).
  - **User-defined table-valued functions** now open their detail with a usage line ("call it in FROM/JOIN with its arguments…", translated both ways) before the column list and definition.
  - `AddBuiltInTableFunctions` now goes through one `TableFunctionItem(name)` helper and the same `FunctionDocumentation` as scalar functions, so there is no second set of texts to keep in sync.
- **Tests**: one new case — built-in table-valued functions must have a signature, a usage note and an example, `OPENJSON` details must contain `WITH (`, a user table-valued function's details must carry the usage line and the full name, and both must really be translated in Chinese (no English fallback). All **357 regression tests pass**.

## 5.486

### 中文

- **彻底移除内联补全预览（ghost text）**
  - 删除 `Editor\Vs2022\GhostTextAdornment.cs`（装饰层 + `GhostTextBridge` + 层定义）与 `Completion\GhostTextSuffix.cs`（`GhostTextSuffix` / `GhostTextLifetime`）。
  - `CompletionController`：删除 `UpdateInlinePreview` / `ClearInlinePreview` 及其 6 处调用、会话监视里的 `navigated` 分支、`CommitCurrent` 的预清理。
  - 设置：删除 `showInlinePreview` 字段、设置界面里的 `CompletionInlinePreview` 复选框及其三处读写、默认值恢复，以及 `LocalizationManager` 里对应两条中文文案。
  - 回归测试：移除两条 ghost 用例，当前 **356 项全过**。
- **保留未动**：弹窗侧两处修正——预览高亮行取 `presenter.SelectedItem`、`KeepsManualSelection`（前缀变化丢弃手动选择）。在列表内部它们仍然是正确行为。
- **经验记录**：本次八轮迭代的根因、VS 装饰层的 API 事实、诊断方法的得失与重做指南，整理为 `docs\内联补全-复盘.md`；该文件已加入 `.gitignore`，不进版本管理。

### English

- **Inline completion preview (ghost text) removed entirely**
  - Deleted `Editor\Vs2022\GhostTextAdornment.cs` (adornment layer + `GhostTextBridge` + layer definition) and `Completion\GhostTextSuffix.cs` (`GhostTextSuffix` / `GhostTextLifetime`).
  - `CompletionController`: removed `UpdateInlinePreview` / `ClearInlinePreview`, their six call sites, the `navigated` branch in the session monitor, and the pre-commit cleanup.
  - Settings: removed the `showInlinePreview` field, the `CompletionInlinePreview` checkbox plus its three read/write sites and default restore, and the two matching Chinese strings in `LocalizationManager`.
  - Regression tests: the two ghost cases are gone; **356 tests pass**.
- **Left in place**: the two popup-side corrections (preview row from `presenter.SelectedItem`, `KeepsManualSelection` resetting a manual choice when the prefix changes) are still correct behaviour inside the list.
- **Lessons**: root causes from the eight iterations, the API facts about VS adornment layers, what the diagnostics got right and wrong, and a re-implementation guide are recorded in `docs\内联补全-复盘.md`; that file is listed in `.gitignore` and is not version controlled.

## 5.485

### 中文

- **带限定符的候选永远算不出后缀**
  - 5.484 的日志（输入 `DJL`）：`edit @106:0->'L'` 之后 40 ms 就是 `state reason=caret-moved prefix='DJ' typed='JL'` → `clear`，而且整个 `DJL` 阶段里一条 `place` 都没有。
  - 原因在 `GhostTextSuffix.Compute`：它要求 `insertText.StartsWith(prefix)`。`DJL` 匹配到的是 `A.DjLsh`，整串以 `A.` 开头，永远不成立 → 后缀是 null → 没有新预览，旧的又被收掉。`DjGuid` 能显示，只是因为它恰好不带限定符。这就是“只显示 DJGUID、不显示 DJLSH”。
  - 现在改为：整串不匹配时，拿**最后一段名字**（`A.DjLsh` → `DjLsh`，`[DjLsh]` → `DjLsh`）去比；对上了就预览剩下的部分（`DJL` → `sh`）。带空白的文本（片段/SQL 串）不参与这个回退，避免把 `INSERT INTO dbo.WHERE_CLAUSE` 这种片段当成名字。
- **测试**：`GhostTextSuffix` 新增 6 条断言（含大小写无关、方括号、不匹配、片段排除）。全量回归 **358 项全过**。

### English

- **Qualified candidates could never produce a suffix**
  - The 5.484 log (typing `DJL`) shows `edit @106:0->'L'` followed 40 ms later by `state reason=caret-moved prefix='DJ' typed='JL'` → `clear`, with no `place` at all during the whole `DJL` phase.
  - The cause is `GhostTextSuffix.Compute`: it required `insertText.StartsWith(prefix)`. `DJL` matches `A.DjLsh`, whose text starts with `A.`, so the test never held → the suffix was null → no new preview, while the old one was removed. `DjGuid` only appeared because its name happens to carry no qualifier. That is exactly "DJGUID shows, DJLSH never does".
  - The rule now falls back to the **last name segment** (`A.DjLsh` → `DjLsh`, `[DjLsh]` → `DjLsh`) and previews the remainder of that (`DJL` → `sh`). Text containing whitespace (snippets, SQL strings) is excluded from the fallback so `INSERT INTO dbo.WHERE_CLAUSE` is never treated as a name.
- **Tests**: six new `GhostTextSuffix` assertions (case folding, brackets, non-match, snippet exclusion). All **358 regression tests pass**.

## 5.484

### 中文

- **`navigated` 变成“一旦置位、整个会话生效”**
  - 5.483 的日志（输入 `D`、`DJ`）：`place text=jGuid` 之后 22–50 ms 就出现 `state reason=navigated` → `clear`。`navigated` 的意思是“用户手动移动过选中行”，但这个标记只在 `sessionContinues` 为假时清零；打字时会话一直连续，所以**只要曾经按过一次方向键，之后每一条预览都会被强制收掉**。这就是“款式编码不闪、`DjLsh` 不正常”的直接原因。
  - 规则改为：**前缀变了就不再保留手动选择**（`CompletionPresenter.KeepsManualSelection(sessionContinues, prefixChanged)`）。手动选择只在“同一段输入内的重刷新”里保留，一打字就交回给过滤器自己算出的最佳匹配。
- **预览与高亮行不是同一行**
  - `UpdateInlinePreview` 一直取 `items[0]`，而弹窗可能高亮别的行：你的截图里高亮是 `A.DjLsh`，预览却是 `jGuid`（`DjGuid`）。现在预览取 **`presenter.SelectedItem`**，没有选中行时才退回第一行，屏上看到的就是按 Tab 会插入的那一行。
- **测试**：新增 4 条断言固定 `KeepsManualSelection` 的四围。全量回归 **358 项全过**。

### English

- **`navigated` latched for the whole session**
  - The 5.483 log (typing `D`, `DJ`) shows `place text=jGuid` followed 22–50 ms later by `state reason=navigated` → `clear`. "Navigated" means the user moved the selection by hand, but the flag was only reset when `sessionContinues` was false; typing keeps the session alive, so **one arrow press earlier made every later preview get force-cleared**. That is exactly why the style code behaved while `DjLsh` did not.
  - The rule is now: **a new prefix drops the manual choice** (`CompletionPresenter.KeepsManualSelection(sessionContinues, prefixChanged)`). A manual row only survives a refresh within the same typed input; the moment the prefix changes, the filter's own best match wins again.
- **The preview and the highlighted row were different rows**
  - `UpdateInlinePreview` always used `items[0]` while the popup could highlight another row: the screenshot showed `A.DjLsh` selected with `jGuid` (`DjGuid`) previewed. The preview now uses **`presenter.SelectedItem`** and only falls back to the first row when nothing is selected, so what is drawn is what Tab will insert.
- **Tests**: four new assertions pin the corners of `KeepsManualSelection`. All **358 regression tests pass**.

## 5.483

### 中文

- **元素被视图“摆下”了：不再重加就永远画不出来**
  - 5.481（只改 `Text`）之后变成“敲一下闪一下”，5.482（不撤不重加）之后变成“敲一下闪一下、其它不出现”——两次反馈合起来只有一个解释：**元素一旦不再重新加进装饰层，视图就不再把它挂在可视树里，改 `Text` 不会有任何视觉效果**。
  - 现在每次放置前探测元素是否还在可视树上（`VisualTreeHelper.GetParent`）；被摆下就**同一帧补一个新的**。旧的此时本来就不可见，所以补新的不会产生空帧——这正是 5.480 的闪烁与 5.481 的“不出现”之间那个被遗漏的中间态。
  - 新加的块有 50 ms 宽限期再探测，避免“视图还没掛上就被判定为掉线”导致每帧重建。
  - 日志新增 `SQL-GHOST reattach offset=… text=…`，以及 `place` 行里的 `attached=True/False`，下轮能直接看清是“掉线重加”还是别的。
- **验证**：全量回归 **358 项全过**。

### English

- **The view detaches the visual: a block that is not re-added is never drawn again**
  - With 5.481 (only `Text` changes) the report became "flashes once per keystroke"; with 5.482 (no removal, no re-add) it became "flashes once on K and then nothing appears". Together they only support one explanation: **once the element stops being added into the adornment layer again, the view no longer keeps it in the visual tree, and changing its `Text` has no visible effect at all.**
  - Every placement now probes whether the block is still in the visual tree (`VisualTreeHelper.GetParent`). If it was detached, a replacement is added **in the same frame**; the old block is already invisible at that point, so the swap costs no blank frame — that is the middle state 5.480's blink and 5.481's "nothing appears" were both missing.
  - A freshly added block gets a 50 ms grace period before the probe, so "not attached yet" cannot be mistaken for "dropped" and rebuilt every frame.
  - New log line `SQL-GHOST reattach offset=… text=…`, plus `attached=True/False` on every `place` line, so the next run shows which of the two it is.
- **Verification**: all **358 regression tests pass**.

## 5.482

### 中文

- **第二个频闪源：行未排版时把元素移除再放回来**
  - 5.481 的日志里，`edit` 到 `place` 的间隔有时是同步的（同一毫秒），有时却是 9–40 ms——后者说明走的是 `HideAndRetry()`：先 `RemoveElement()`，再用 `DispatcherPriority.Background` 下一帧把元素加回去。编辑之后那一瞬间行通常还没重新排版，于是**每敲一下就会真的移除并重画一次**。
  - 现在行拿不到就不动它：元素留在原处不撤，只把文本换成新的（`block.Text = suffix`），位置等排版好后由 `LayoutChanged` 修正。只有连续 40 帧都拿不到行才真正结束预览（`retry-exhausted`）。
  - 新增日志 `SQL-GHOST defer reason=no-line offset=… retry=… retained=…`，下次再闪就能直接看到是不是这条路径。
- **验证**：全量回归 **358 项全过**，`--completion-perf` 预算内。

### English

- **Second strobe source: removing the element while the line has no layout**
  - In the 5.481 log the gap from `edit` to `place` is sometimes the same millisecond and sometimes 9–40 ms. The latter means `HideAndRetry()` ran: `RemoveElement()` first, then the element is added back on the next `DispatcherPriority.Background` frame. Right after an edit the line is usually not reformatted yet, so **every keystroke did remove and redraw the block**.
  - Now a missing line leaves the element alone: it stays where it is and only its text is refreshed (`block.Text = suffix`); `LayoutChanged` moves it once the layout is back. The preview is only ended after 40 fruitless frames (`retry-exhausted`).
  - New log line `SQL-GHOST defer reason=no-line offset=… retry=… retained=…` makes that path visible if the blinking ever comes back.
- **Verification**: all **358 regression tests pass**; `--completion-perf` stays within budget.

## 5.481

### 中文

- **根因：预览每敲一下就被“删掉重建”**
  - 5.480 的日志把每一帧都记了下来：`edit changes=@98:0->'S'` 后立刻 `place offset=99 text=BM`，`edit changes=@98:1->''` 后又立刻 `place offset=98 text=SBM`。文字始终是对的，但实现是 `RemoveElement()` + `AddAdornment()`——每敲一个字符，旧的 `TextBlock` 被移除、新的被创建，WPF 就会在每一下按键上闪一帧。这就是“敲一下出现一下、立马消失”的来源，和生命期规则无关。
  - 改为**复用同一个元素**：元素只在第一次放到装饰层，之后只改 `Text` 和 `Canvas.Left/Top`。文字从 `SBM` 变 `BM` 再变 `M` 时，那就是同一个方块在缩短，而不是三个方块轮流上台。
  - 真正结束预览（提交、取消、光标离开、文本不再以已输前缀收尾、设置关闭）仍然走 `Clear`，元素才被移除。
- **验证**：全量回归 **358 项全过**，`--completion-perf` 预算内。

### English

- **Root cause: the preview was deleted and recreated on every keystroke**
  - The 5.480 log records each frame: `edit changes=@98:0->'S'` followed at once by `place offset=99 text=BM`, then `edit changes=@98:1->''` followed by `place offset=98 text=SBM`. The text was always correct, but the implementation was `RemoveElement()` + `AddAdornment()` — every typed character removed one `TextBlock` and created another, which flashes one frame per keystroke. That is the "appears once, then vanishes" strobe, and it has nothing to do with the lifetime rules.
  - The element is now **reused**: it enters the layer once, after which only `Text` and `Canvas.Left/Top` change. Going from `SBM` to `BM` to `M` is one block getting shorter instead of three blocks taking turns.
  - Actually ending the preview (commit, cancel, caret leaving, text no longer ending with the typed prefix, setting switched off) still goes through `Clear`, which is when the element is removed.
- **Verification**: all **358 regression tests pass**; `--completion-perf` stays within budget.

## 5.480

### 中文

- **先把“谁改了文本”变成可测的事实**
  - 5.479 的日志（带 `ctx` 与 `version`）已经把消失的时机锁到了编辑上：`place version=36 ctx='…WHERE K|'` → `state version=37 ctx='…WHERE |'` → 下一个版本又回到 `WHERE K`。也就是说，预览不是在后台刷新时被收掉的，而是**光标前的字符被删掉**后被收掉的。
  - 这一版在预览可见期间把**每一次缓冲区变更**写进日志：`SQL-GHOST edit version=… changes=@偏移:旧长度->'新文本' caret=… retained=… ctx='…|…'`。这样就能直接分辨是你自己按了退格，还是别的东西在改文本。
  - 没有改任何行为：文本变了就该收起，这条规矩不能因为想让它“待着”而发水。
- **验证**：全量回归 **358 项全过**。

### English

- **First make "who changed the text" a measurable fact**
  - The 5.479 log (with `ctx` and `version`) already pinned the disappearance to an edit: `place version=36 ctx='…WHERE K|'` → `state version=37 ctx='…WHERE |'`, with the next version back at `WHERE K`. The preview is not being dropped by a background refresh; it is dropped because the character in front of the caret was deleted.
  - This version logs **every buffer change while a preview is visible**: `SQL-GHOST edit version=… changes=@position:oldLength->'newText' caret=… retained=… ctx='…|…'`, which tells a typed Backspace apart from something else editing the text.
  - No behaviour changed: when the text it covers changes, the preview must go. That rule is not going to be loosened to make it look stable.
- **Verification**: all **358 regression tests pass**.

## 5.479

### 中文

- **预览的生命期只看文字，并且由光标/文本事件驱动即时校验**
  - 5.478 的日志里还有一种情况：`state caret=97 point=97 prefix='K' typed=' '`——光标与位置一致，但位置前是空格、记录的前缀是 `K`。这是快照平移把位置移到了前缀之外，旧规则用“光标位置”作为判据，于是把它当成失效。
  - 修法：`StillCurrent()` 只校验**文字**——位置前文的末尾是否仍等于前缀（大小写不敏感），不再拿光标位置做判据；光标移开的情形已经由控制器（弹窗失焦后 200 ms 内 `Dismiss`）和“跟着建议字符前移”处理。
  - 校验时机从 250 ms 定时器改成**编辑器事件驱动**（`Caret.PositionChanged` 与 `TextBuffer.Changed`），键入后立即跟随或收起，不再等一个定时周期，也不依赖下一轮分析。
  - 锚点规那么改为“谁与前缀自洽就用谁”：先试控制器给的位置，不匹配再试**本视图**的光标。
  - 日志加料：`place` 与 `state` 两行现在带 `version`、`pointVersion`、`viewCaret` 与前后 12 字的 `ctx='…|…'`，能直接重建当时的键入过程。
- **验证**：全量回归 **358 项全过**。

### English

- **The preview's lifetime is now decided by the text alone, verified from editor events**
  - The 5.478 log still had `state caret=97 point=97 prefix='K' typed=' '`: caret and position agreed, but the character in front of that position is a space while the recorded prefix is `K`. Snapshot translation had moved the position outside the prefix, and the old rule used the caret position as part of the verdict, so it treated the preview as stale.
  - Fix: `StillCurrent()` now checks **only the text** — does the text in front of the position still end with the prefix (case insensitive). Caret movement is already handled by the controller (dismiss within 200 ms once the caret is elsewhere) and by "follow the suggested character".
  - Verification moved from a 250 ms timer to **editor events** (`Caret.PositionChanged`, `TextBuffer.Changed`), so typing either advances or removes the ghost immediately instead of waiting for a tick or the next analysis pass.
  - Anchoring is now "whichever position the prefix agrees with": the controller's offset first, then this view's caret.
  - Logging: `place` and `state` lines now carry `version`, `pointVersion`, `viewCaret` and a 12-character `ctx='…|…'` window, enough to reconstruct the exact typing.
- **Verification**: all **358 regression tests pass**.

## 5.478

### 中文

- **预览锚点改成实时光标，前缀不匹配就宁可不画**
  - 5.477 的状态日志把最后一个缺口暴露出来：`state reason=caret-moved caret=97 point=97 prefix='K' typed=' '`——光标与位置完全一致（97=97），但那位置前面的字符是**空格**，记下的前缀却是 `K`。说明某次 `Show` 的“位置”和“前缀”来自不同时刻的上下文，预览被放到错的格子上，随后的 250 ms 校验自然判定它“不再描述光标”并移除。
  - 修法一：`UpdateInlinePreview` 不再用上下文里捕获的行列，而是取**实时光标**（`GetCaretPos` + `OffsetOf`）作为预览锚点，保证“放下去的那一刻”位置就是光标。
  - 修法二：`GhostTextAdornment.Show` 增加前缀一致性检查——如果位置前文的末尾不是那个前缀，就**忽略这次陈旧请求**（Debug 日志记一笔），不动已经正确的预览；不再“画上去→下一帧发现不对→拿掉”。
  - 日志继续保留 `SQL-GHOST place / state / clear reason=…`，下一轮若有异常仍可量。
- **验证**：全量回归 **358 项全过**；十个位置的完成性能断言仍在 3.00 ms 内。

### English

- **The preview is anchored at the live caret, and a mismatched prefix is never painted**
  - The 5.477 state log exposed the last gap: `state reason=caret-moved caret=97 point=97 prefix='K' typed=' '`. The caret and the placed position agreed (97/97), but the character in front of that position is a **space** while the recorded prefix is `K` — one `Show` mixed a position and a prefix captured at different moments, so the ghost sat on the wrong cell and the 250 ms verification removed it.
  - Fix 1: `UpdateInlinePreview` no longer uses the context's captured line/column. It anchors at the **live caret** (`GetCaretPos` plus `OffsetOf`), so the position is the caret at the moment of placement.
  - Fix 2: `GhostTextAdornment.Show` checks that the text in front of the position really ends with that prefix; if not, the stale request is ignored (Debug log) and an already correct preview is left alone — no more "paint, notice it is wrong, remove".
  - The `SQL-GHOST place / state / clear reason=…` lines stay available for the next report.
- **Verification**: all **358 regression tests pass**; the ten completion-performance positions remain inside 3.00 ms.

## 5.477

### 中文

- **内联预览真正改成“跟着输入走”，刷新再也收不掉它**
  - 5.476 的日志已经把原因钉死：`SQL-GHOST clear reason=no-candidate force=False`——后台分析拿不到候选就发了一次清理，且 `StillCurrent` 当时为假（光标已因下一次按键前移），于是元素被移除，等下一轮分析再放上去，表现出来就是“闪一下就没”。
  - 现在职责归位：
    - **非强制清理一律不结束预览**。只有 Tab 提交、Esc/点击取消、上下选行、弹窗隐藏、关闭预览设置这些用户动作才强制结束。
    - **生命期由装饰层自己看编辑器**：250 ms 校验一次，光标离开预览位置或文字不再匹配才移除。
    - **打字时预览跟着缩短前移**：你输入的下一个字符正好是它建议的字符（`W` + 预览 `HERE`，输入 `H`），预览就地变成 `ERE` 并贴到新光标处，不再 “拿掉→等分析→再放上” 地闪。
  - 日志：`SQL-GHOST clear reason=…` 外加一行 `SQL-GHOST state caret=… point=… prefix='…' typed='…' sameSnapshot=…`，再有异常能直接量出当时的真实状态。
- **验证**：全量回归 **358 项全过**；十个位置的完成性能断言仍在 3.00 ms 内。

### English

- **The inline preview now follows typing; analysis refreshes can no longer end it**
  - The 5.476 log pinned the cause: `SQL-GHOST clear reason=no-candidate force=False` — a background analysis pass with no candidates asked to clear, `StillCurrent` was false because the caret had already moved with the next keystroke, so the element was removed and only came back with the next analysis pass. That is the blink.
  - Ownership is fixed:
    - **A non-forced clear never ends the preview.** Only user actions end it: Tab commit, Esc/click dismiss, list navigation, hidden popup, preview setting off.
    - **The adornment watches the editor itself**: a 250 ms check removes the ghost only when the caret left its cell or the typed word changed.
    - **Typing advances it**: when the next typed character is the one the preview suggests (`W` plus preview `HERE`, typing `H`), the ghost becomes `ERE` and moves to the new caret instead of disappearing and waiting for the next analysis.
  - Logging: `SQL-GHOST clear reason=…` plus a `SQL-GHOST state caret=… point=… prefix='…' typed='…' sameSnapshot=…` line, so any future report can be measured.
- **Verification**: all **358 regression tests pass**; the ten completion-performance positions remain inside 3.00 ms.

## 5.476

### 中文

- **内联预览不再被后台刷新收掉（“闪一下就没”）**
  - 日志证据：`SQL-GHOST place offset=98 line=4` 之后 0.7–2 杬会出现一次 `clear`，而那段时间**光标没动、文字没变**。说明不是你在改字，是后台分析每刷新一次就调一次 `ClearInlinePreview`。
  - 修法：把“请求清理”和“真的清理”分开。新的纯函数 `GhostTextLifetime.StillDescribesCaret(当前位置, 预览位置, 已输入文本, 前缀)` 定义了预览的生命期：**光标还在原位 + 光标前仍是那个前缀**（大小写不敏感）就保留。后台刷新只能“请求”，不能“结束”。
  - 只有真正的用户动作才强制结束：Tab 提交、Esc/点击取消、上下选行、弹窗隐藏、设置关闭预览。
  - 日志改为带原因：`SQL-GHOST clear reason=dismiss force=True`、`reason=no-suffix force=False`，`keep` 记 Debug。下一次若还有问题，能直接看出是哪条路径。
- **验证**：全量回归 **358 项全过**（新增“预览能活过一次后台刷新”测试）；性能断言仍在 3.00 ms 内。

### English

- **A background refresh can no longer remove the inline preview (the "blinks once and is gone" report)**
  - Log evidence: a `SQL-GHOST place offset=98 line=4` was followed 0.7–2 s later by a `clear` while the caret and the text had not moved — the completion analysis clears the preview on every refresh.
  - Fix: "asked to clear" and "cleared" are now separate. The pure rule `GhostTextLifetime.StillDescribesCaret(caret, previewPosition, typed, prefix)` keeps the preview while the caret still sits where the ghost was placed and the word in front of it is still the typed prefix (case insensitive). A refresh may request, it may not end.
  - Only real user actions force it: Tab commit, Esc/click dismiss, list navigation, popup hidden, preview setting off.
  - Logging now carries the reason: `SQL-GHOST clear reason=dismiss force=True` versus `reason=no-suffix force=False`, with `keep` at Debug, so a future report pinpoints the caller.
- **Verification**: all **358 regression tests pass** (new: the preview survives a background refresh); performance stays inside 3.00 ms.

## 5.475

### 中文

- **修掉“内联预览闪一下就消失”**
  - 根因：5.474 改成自己定位后，只要**某一帧拿不到光标行的布局**（添加装饰元素本身会触发一次重排、弹窗展开、刚新建的行还没排版），就会把元素移除；而下一帧不一定会再有 `LayoutChanged`，预览就永久消失了。旧实现依赖 `TextRelative`，虽然位置错但元素一直在，所以看上去“只闪不掉”。
  - 修法：把“暂时隐藏”和“结束预览”分开——拿不到行时只移除元素并**下一帧重试**（`DispatcherPriority.Background`，最多重试 40 帧后放弃）；快照前进时用 `SnapshotPoint.TranslateTo` 跟随，不再因为一次快照错位就结束；只有明确的 `Clear()`（提交/取消/无候选）才真正结束预览。
  - 观测：`SQL-GHOST place offset=… line=… left=… top=…` 与 `SQL-GHOST clear` 改为 Info 级写入日志（只在文本或状态变化时记一行），下次若仍然消失，日志能直接分出是“装饰层自己收的”还是“补全控制器收的”。
- **验证**：全量回归 **357 项全过**；十个位置的完成性能断言仍在 3.00 ms 内。

### English

- **Fixed the inline preview vanishing after one blink**
  - Cause: after 5.474 started placing the adornment itself, a single frame without a layout for the caret line (adding the element triggers a re-layout, the popup opens, a freshly typed line is not formatted yet) removed the element, and the next frame often brought no `LayoutChanged` at all — so the preview was gone for good. The old `TextRelative` version looked merely misplaced because the element stayed alive.
  - Fix: "hide for a moment" is now separate from "end the preview". A missing line only removes the element and retries on the next background dispatcher frame (up to 40 frames). A snapshot that moved forward is followed with `SnapshotPoint.TranslateTo` instead of ending the preview, and only an explicit `Clear()` (commit, dismiss, no candidates) really ends it.
  - Observability: `SQL-GHOST place offset=… line=… left=… top=…` and `SQL-GHOST clear` now log at Info, one line per change, so a future report can tell whether the adornment or the completion controller ended the preview.
- **Verification**: all **357 regression tests pass**; the ten completion-performance positions remain inside 3.00 ms.

## 5.474

### 中文

- **修掉“输入时预览文本跑到第一行”（如输入 `WH` 时第一行出现灰色 `ERE`）**
  - 根因：那段灰字是**内联补全预览**（`GhostTextAdornment`，`WHERE` 去掉已输入的 `WH` 剩下的 `ERE`），它用 `AdornmentPositioningBehavior.TextRelative` 交给编辑器定位。当目标行**还没有完成布局**（刚回车新建的第 5 行、或滚动/换行刚发生）时，VS 会把这个装饰元素停在视图顶部——于是预览出现在第一行，而光标在第五行。
  - 修法：改成 `OwnerControlled`，用 `IWpfTextViewLine.GetCharacterBounds` + `line.TextTop` 自己算像素坐标：行没布局不画（不再退化成顶部），布局完成立即补画；监听 `LayoutChanged` 在滚动/换行/缩放后重新定位，并用“坐标未变则不重建元素”避免自己触发自己。
  - 附带：`SQL-GHOST place offset=… line=… left=… top=…` 写入 Debug 日志，以后类似问题能直接量出偏移与行号。
- **验证**：全量回归 **357 项全过**；十个位置的完成性能断言仍全部在 3.00 ms 内。

### English

- **Fixed the inline preview jumping to the first line (typing `WH` used to paint a grey `ERE` at the top)**
  - Cause: the grey text is the **inline completion preview** (`GhostTextAdornment`, i.e. `WHERE` minus the typed `WH`), positioned with `AdornmentPositioningBehavior.TextRelative`. When the target line has no layout yet (the line just created with Enter, or right after a scroll/wrap), the editor parks that adornment at the top of the view — first line on screen, caret on the fifth.
  - Fix: `OwnerControlled` placement computed from `IWpfTextViewLine.GetCharacterBounds` and `line.TextTop`. A line without layout is no longer drawn instead of being drawn at the top, and it is drawn as soon as the layout arrives. `LayoutChanged` repositions it after scroll/wrap/zoom, and an unchanged position no longer rebuilds the element (no self-triggered layout loop).
  - Also: `SQL-GHOST place offset=… line=… left=… top=…` goes to the debug log so a future report can be measured instead of guessed.
- **Verification**: all **357 regression tests pass**; the ten completion-performance positions remain inside 3.00 ms.

## 5.473

### 中文

- **修掉“输入时编辑器第一行闪刷灰色字块”**
  - 根因：相关词高亮有两套渲染。编辑器原生路径是分类器（`RelatedHighlightClassifier`，背景色由分类格式给），兜底路径是窗口叠加层（`SqlPromptHighlightOverlay`，自画矩形）；叠加层只在 `RelatedHighlightActivation.IsAdornmentActive` 为真时隐藏，而 `Register()` **从来没有调用方**，所以两套同时在画。
  - 叠加层用 `IVsTextView.GetPointOfLineColumn` 取坐标：**视图没在画的行（滚出屏幕、或超出最后可见行）会返回钳制后的点**，矩形于是全部落在视图顶端——就是第一行上那几个灰色块；90 ms 定时器每轮重算，所以表现为“闪刷”。
  - 修法：① 分类器创建时 `Register()`、释放时 `Unregister()`，原生渲染在画时叠加层直接隐藏（同一段高亮不再画两遍）；② 叠加层增加可见性护栏，映射点不在客户区内直接跳过——即使真的退回兜底路径，也不会再把离屏的 span 画到第一行。
- **验证**：全量回归 **357 项全过**；语料 54 条与性能断言（十个位置 ≤ 3.00 ms）均通过；日志中 `related highlight spans=N` 仍由分类器单路输出。

### English

- **Fixed the grey blocks flashing on the editor's first line while typing**
  - Cause: related-token highlighting had two renderers. The editor-native path is the classifier (`RelatedHighlightClassifier`, painting through a classification format); the fallback is a window overlay (`SqlPromptHighlightOverlay`) that draws its own rectangles. The overlay hides only when `RelatedHighlightActivation.IsAdornmentActive` is true, and `Register()` had **no caller at all**, so both painted the same spans.
  - The overlay places rectangles with `IVsTextView.GetPointOfLineColumn`, which returns a **clamped point for a line the view is not painting** (scrolled away, or past the last visible row). Every such span therefore landed at the top of the view — the grey blocks on the first line — and the 90 ms timer recomputed them, which is the flicker.
  - Fix: (1) the classifier calls `Register()` when it is created and `Unregister()` when it is disposed, so the overlay hides whenever the native renderer is drawing; (2) the overlay now skips any span whose mapped point falls outside the client area, so even the fallback can no longer paint an off-screen span on the first line.
- **Verification**: all **357 regression tests pass**; the 54-row corpus and the performance assertions (ten positions ≤ 3.00 ms) pass; the log still shows a single classifier-driven `related highlight spans=N` stream.

## 5.472

### 中文

- **把“判定”变成一条可验证的链，而不是一次口头结论**
  - ① 位置层把下游需要的事实全部发布成属性：`ResolvedKind`（原始判定）、`EnclosingClause`（SELECT / FROM / JOIN / ON / WHERE / GROUP BY / ORDER BY / INTO / UPDATE …）、`StatementKind`（SELECT / INSERT / UPDATE / DELETE / MERGE / DDL / EXEC，取自解析树）；谓词归属 `PredicateOwner` 沿用。成员访问也带上了所属子句。
  - ③ 候选层只许读、不许改：`ApplyParserPosition` 改为单一映射 `MapCaretKind`；语料逐条断言“`PositionResolved` 为真时，最终 `Kind` 必须等于 `ResolvedKind` 的映射”——上一版“精确片段前缀直接改成 General”这类覆盖再也过不去。
- **给条目合法性立规则，并让语料逐条强制执行**
  - 源槽位（`DataSource` / `Join` / `MergeSource` / `TableSourceTail` / `JoinSourceTail`）不得出现 `Column` 条目；
  - `Predicate` 只在 `PredicateOwner == ON` 时才允许 JOIN 条目（WHERE / HAVING 后面接 JOIN 是语法错误）；
  - 54 条语料每跑一遍都验证这两条，不再依赖个别人对某个形状的手写断言。
- **注释也是规格**：`TestFromSourceTailRanksContinuations` 的注释写的是“hint / joins / alias / WHERE 子句”，断言却要求“列存在且排在 join 之后”——已改成断言“不得出现列”。这类“断言把 bug 锁成期望”的问题今后由上面的规则兜住。
- **验证**：全量回归 **356 项全过**；语料 54 条 46/54 由判定入口给出（27 树 / 19 token），新增两条全局不变量与三条事实断言均通过；`--completion-perf` 十个位置仍在 3.00 ms 预算内。

### English

- **The decision is now a verifiable chain instead of a claim**
  - Layer ① publishes every fact the item lists need: `ResolvedKind` (the raw decision), `EnclosingClause` (SELECT / FROM / JOIN / ON / WHERE / GROUP BY / ORDER BY / INTO / UPDATE …) and `StatementKind` (SELECT / INSERT / UPDATE / DELETE / MERGE / DDL / EXEC, taken from the tree); `PredicateOwner` stays. Member access carries its enclosing clause too.
  - Layer ③ may read those facts but not replace them: `ApplyParserPosition` now goes through a single `MapCaretKind`, and every corpus row asserts that when `PositionResolved` is true the final `Kind` equals the mapping of `ResolvedKind`. The previous "exact snippet prefix switches to General" style of override cannot pass any more.
- **Grammar rules for items, enforced row by row**
  - No `Column` item in a source slot (`DataSource`, `Join`, `MergeSource`, `TableSourceTail`, `JoinSourceTail`).
  - No JOIN item in a `Predicate` unless `PredicateOwner == ON` (a JOIN after WHERE / HAVING is a syntax error).
  - All 54 corpus rows check both rules on every run, instead of relying on hand-written expectations per shape.
- **Comments are specification too**: the comment on `TestFromSourceTailRanksContinuations` described "hint / joins / alias / WHERE clause" while the assertion required a column after the joins; the assertion now forbids columns. The rules above keep a wrong expectation from passing again.
- **Verification**: all **356 regression tests pass**; 54 corpus rows, 46 decided by the resolver (27 tree, 19 token scan); two global invariants and three fact assertions pass; all ten `--completion-perf` positions stay inside the 3.00 ms budget.

## 5.471

### 中文

- **ON 条件写完后再给后续 JOIN**
  - 现象：`ON JS = A.DjLsh` 下一行 `AND KHBM = A.DjState ` 处只有 AND / OR 和列，没有 LEFT JOIN 等。
  - 原因：谓词列表只按“条件是否完整”分级，从未区分这个谓词属于 WHERE、HAVING 还是 ON。而语法上只有 **ON 谓词后面还能再接一个 JOIN**。
  - 修法：位置判定新增 `PredicateOwner`（在同一括号层回退到最近的 WHERE / HAVING / ON，AND / OR 不改变归属；解析树决定谓词跨度时也同步沿用 token 的归属）。谓词完整时：`ON` → AND / OR / `)` → 五种 JOIN（INNER 104 → CROSS 96）→ WHERE，并用 JOIN 列表**取代** GROUP BY / ORDER BY / HAVING（它们在 JOIN 条件后不合法）；`WHERE` / `HAVING` → 维持原来的 GROUP BY / ORDER BY / HAVING。
  - 列表顺序：AND(126) / OR(124) → LEFT JOIN 等 → 列(85)，所以打完条件先看到连接词，再往下就是下一张表。
  - JOIN 关键字与说明抽成 `AddJoinContinuationItems`，FROM 尾部（220起）与 ON 谓词（104起）共用一份文案。
- **验证**：全量回归 **356 项全过**；新增两条测试锁住“ON 谓词给 JOIN 且排 AND 之后、不给 GROUP BY”与“WHERE 谓词不给 JOIN”。

### English

- **A finished ON predicate now offers the next JOIN**
  - Symptom: after `ON JS = A.DjLsh`, typing `AND KHBM = A.DjState ` offered only AND / OR and columns, never LEFT JOIN.
  - Cause: the predicate list was ranked by "is the condition complete" only, and never by which clause owns the predicate; grammatically only an **ON** predicate can be followed by another JOIN.
  - Fix: the position resolver now reports `PredicateOwner` (walks back to the nearest WHERE / HAVING / ON on the same level; AND / OR do not change the owner, and a tree-decided predicate keeps the token-derived owner). For a complete predicate: `ON` → AND / OR / `)` → the five join forms (INNER 104 → CROSS 96) → WHERE, with the join list **replacing** GROUP BY / ORDER BY / HAVING (illegal after a join condition); `WHERE` / `HAVING` keep the previous GROUP BY / ORDER BY / HAVING list.
  - Ranking: AND (126) / OR (124) → LEFT JOIN and friends → columns (85).
  - The join keywords and their descriptions moved into `AddJoinContinuationItems`, shared by the FROM tail (from 220) and the ON predicate (from 104).
- **Verification**: all **356 regression tests pass**; two new tests lock "an ON predicate offers joins after AND and no GROUP BY" and "a WHERE predicate offers no joins".

## 5.470

### 中文

- **修掉“FROM 表 别名 后再打词也弹列名”**
  - 现象：`FROM dbo.ZZJGH A LJ` 弹出 `LXTJGID`（列）以及 LEFT JOIN / FULL JOIN。
  - 两个根因：① 别名后面再打一个词时位置判定返回 Unknown，整个列表退回 General（全部候选 450 条，包含列/函数/片段）；② `LJ` 恰好是内置片段前缀，旧逻辑为了让片段在任意上下文都能插入，直接把 `Kind` 改成 General，丢掉了已经判定为 `TableSourceTail` 的位置。
  - 修法：位置判定在“已写完的数据源 + 后续词”时保持 `TableSourceTail` / `JoinSourceTail`（该词作为过滤前缀）；`TableSourceTail` / `JoinSourceTail` 不再提供作用域内的列（列的合法位置是 ON / WHERE 谓词）；精确匹配的片段改为在位置允许的列表**顶部**追加（Score 300），不再把整张列表换成 General。
  - 结果：`FROM dbo.ZZJGH A LJ` 只剩 `Snippet LJ`、`LEFT JOIN`、`FULL JOIN` 三项，没有列名；`A L` 时 LEFT JOIN 排第一。
- **验证**：全量回归 **354 项全过**；新增两条测试锁住“源尾部保持源位置”与“源尾部不给列、但片段仍在”；`--completion-perf` 十个位置全部在 3.00 ms 预算内。

### English

- **Fixed: a column popped up after `FROM table alias <word>`**
  - Symptom: `FROM dbo.ZZJGH A LJ` offered `LXTJGID` (a column) next to LEFT JOIN / FULL JOIN.
  - Two causes: (1) a word typed after a finished source made the position resolver answer Unknown, so the list fell back to General (all 450 candidates, columns and functions included); (2) `LJ` is exactly a snippet prefix, and the old rule switched the context to General so the snippet could be inserted anywhere — discarding the resolved `TableSourceTail`.
  - Fix: the resolver keeps `TableSourceTail` / `JoinSourceTail` when a word follows a finished source (that word becomes the filter prefix); those positions no longer offer scoped columns (a column belongs in an ON / WHERE predicate); and an exact snippet prefix is now appended at the top of whatever the position allows (score 300) instead of replacing the list with General.
  - Result: `FROM dbo.ZZJGH A LJ` shows only `Snippet LJ`, `LEFT JOIN` and `FULL JOIN`; `A L` ranks LEFT JOIN first.
- **Verification**: all **354 regression tests pass**; two new tests lock "a word after a source stays a source tail" and "a source tail offers the snippet but no columns"; all ten `--completion-perf` positions stay inside the 3.00 ms budget.

## 5.469

### 中文

- **每键少解析一遍（实测快一倍）**
  - `SqlTextContext.FindStatementStart` 原本为求“语句起点”自己再跑一次 `TSql170Parser.Parse`，而语义模型几行后又把同一段文本解析一遍。现在起点由语义模型那棵树给出（`SqlSemanticModel.StatementStart`），必要时用 `SqlTextContext.AdvanceStatement` 把语句窗口前移；占位符插入过的偏移会映射回原文。
  - 112 字符语句 analyze 0.21 ms；60 列宽 SELECT 从 **3.01 → 1.58 ms**（合计 2.11 ms）；30 个 JOIN 从 **3.01 → 1.65 ms**（合计 1.86 ms）；6 KB、120 条语句的长会话 0.79 ms。
  - 行为不变：文本扫描（分号 + 行首起始词 + EXEC 尾巴）仍是基线，解析结果只能把起点**向后**推，所以不完整 SQL（解析器整批丢弃）与以前一致。
- **`--completion-perf` 覆盖真实语句形状**
  - 以前只测一条 122 字符语句，60 列宽查询其实已经 3.0 ms 超预算却仍“绿”。新增 `wide SELECT`、`JOIN chain`（30 个 JOIN）、`long session`（120 条语句）三个位置，同样 3.00 ms 预算、逐位置断言。
- **别名只留一个来源**
  - `FROM/JOIN` 别名直接来自解析树（已按查询作用域），`AliasPattern` 对该语句的全文匹配只在树没给出任何别名时作为兜底跑一次；删除了“临时表已存在所以不再合并”的注释与两次无条件全文扫描。
- **`ContainsIdentifier` 不再现场拼正则**
  - 改为 `IndexOf` + 标识符边界走查；原写法每次生成 pattern 进入 .NET 静态正则缓存（15 条上限），不同标识符互相挤出、反复重编译。
- **小分配**
  - `BuildCandidates` 不再每次 `Objects.Concat(LocalObjects).ToList()`：没有临时表/CTE 时直接用元数据列表。
  - `CompletionItem.Key` 缓存刷新差异比较用的字符串，`DisplayText`/`Kind` 变更时失效；不再每行每键拼一次字符串。
- **未改并说明**：设置页缩略图 `DecodePixelWidth = 320`，对应 158 px 瓦片约 2 倍 DPI，属于有意保留的清晰度，不是浪费。
- **验证**：全量回归 **352 项全过**；语料 54 条 46/54 由判定入口给出（27 树 / 19 token）；`--completion-perf` 十个位置全部在 3.00 ms 预算内（退出码 0）。

### English

- **One parse per keystroke instead of two (measured ~2x faster)**
  - `SqlTextContext.FindStatementStart` ran its own `TSql170Parser.Parse` just to find the statement start, and the semantic model parsed the same text again a few lines later. The start now comes from that tree (`SqlSemanticModel.StatementStart`) and `SqlTextContext.AdvanceStatement` moves the statement window when needed; placeholder insertions are mapped back to the caller's offsets.
  - 122-character statement: analyze 0.21 ms; a 60-column SELECT went **3.01 → 1.58 ms** (2.11 ms total); 30 joins **3.01 → 1.65 ms** (1.86 ms total); a 6 KB session with 120 statements: 0.79 ms.
  - Behaviour is unchanged: the text scan (semicolons, line-leading starters, EXEC tail) stays the baseline and the tree can only move the start forward, so incomplete SQL (where ScriptDOM drops the batch) resolves exactly as before.
- **`--completion-perf` now covers real statement shapes**: it measured a single 122-character query, while a 60-column query was already over the 3.00 ms budget and still passed. Added `wide SELECT`, `JOIN chain` (30 joins) and `long session` (120 statements), each asserted against the same budget.
- **One source for aliases**: `FROM`/`JOIN` aliases come from the parse tree (already scoped to the query); `AliasPattern` runs once as a fallback only when the tree produced no aliases at all.
- **`ContainsIdentifier` no longer builds a regex per call**: an `IndexOf` plus identifier-boundary walk replaces a pattern that entered the static regex cache (15 entries), pushed other identifiers out and recompiled repeatedly.
- **Smaller allocations**: `BuildCandidates` only concatenates the local tables when there are any, and `CompletionItem.Key` caches the refresh-diff string, invalidated when `DisplayText`/`Kind` change.
- **Checked and left alone**: the settings thumbnails decode at 320 px for a 158 px tile (~2x DPI), which is deliberate sharpness rather than waste.
- **Verification**: all **352 regression tests pass**; 54 corpus rows, 46 decided by the resolver (27 tree, 19 token scan); all ten `--completion-perf` positions are inside the 3.00 ms budget (exit code 0).

## 5.468

### 中文

- **位置错位根因修正：补位不再让光标漂移**
  - `SqlSemanticModel` 为让“未写完的成员”可解析，会在光标前后插入占位符 `__mssqltool_symbol`；插入点在光标之前时，后面所有偏移都会变，但传给位置判定的仍是原文偏移，于是 `SELECT h. FROM dbo.Header h WHERE h.Amount > 1 ` 被读成 `General`、`SELECT c. FROM dbo.Customer c ` 被读成 `SelectList`。
  - `MakeParseable` 现在返回“原文光标 → 解析文本光标”的映射，位置判定、作用域查找、局部对象访客全用同一个偏移；占位符永远不会盖住光标。
  - 同时修了占位规则把 `dbo.Orders` 误认成“未写完成员”（`ORDER` 缺词边界），表名不再被改成 `__mssqltool_symbolOrders`。
- **成员访问并入位置判定（不再有第二套“当前单词”）**
  - `MemberPattern` / `OmittedSchemaMemberPattern` / `JoinSourcePattern` / `PrefixPattern` 全部删除：限定符、前缀、“是不是数据源 / 是不是 JOIN 源”都由 `SqlCaretPositionResolver` 的 token 归档给出（`SqlCaretKind.Member`）。
  - “当前单词”只剩一个定义（字符回溯，能处理未闭合的 `[Ord`）；`context.Prefix` 与替换起点同源。
  - 删掉了“把行内列号当绝对偏移”的 `caretLineStart + caretColumn` 切片分支；`SqlTextContext` 新增 `StatementStart`，`Analyze` 把列号一次性换算成语句内偏移，语义模型按真实光标求值（光标在行中间时不再按句尾算）。
- **谓词列改由 token 读取**
  - `PredicateColumnPattern` 删除；`WHERE h.Amount > 100 AND |` 的 `ActiveColumn` 改由 token 归档在层内回退到最近的比较运算符再取名字链，`[h].[Amount]` 这类引号名也能还原。
- **注释/字符串判定只留一套状态机**
  - `MaskCommentsAndStrings` 与 `ScanState` 原本是两份同形实现，现在合成一个 `Scan`：同一遍同时产出屏蔽文本与“光标是否在注释/字符串/引号标识符内”，`Suppress` 行为不变但不再可能漂移。
- **命名跟上语义**：`PositionDecidedByParser` → `PositionResolved`（表示“由位置判定入口给出”，含树与 token 归档两种来源）。
- **验证**：语料 54 条（新增“未写完成员在前 + 光标在句尾”的两条真 bug 形状），46/54 由判定入口给出（27 树 / 19 token 归档）；全量回归 348 项全过；`--completion-perf` 全程在 3.00 ms 预算内。

### English

- **The offset bug behind the wrong positions is fixed: placeholders no longer move the caret**
  - To keep an unfinished member parseable, `SqlSemanticModel` inserts `__mssqltool_symbol`. When that insertion lands before the caret every later offset moves, while the position resolver still received the original offset: `SELECT h. FROM dbo.Header h WHERE h.Amount > 1 ` resolved to `General` and `SELECT c. FROM dbo.Customer c ` to `SelectList`.
  - `MakeParseable` now returns an original-to-parsed caret map, and the position resolver, the scope finder and the local-object visitor all use that one offset; a placeholder can never sit under the caret.
  - The patch rule also stopped mistaking `dbo.Orders` for an unfinished member (the `ORDER` alternative had no word boundary), which used to rewrite the table name to `__mssqltool_symbolOrders`.
- **Member access is part of the position resolver (no second "current word")**
  - `MemberPattern`, `OmittedSchemaMemberPattern`, `JoinSourcePattern` and `PrefixPattern` are gone: the qualifier, the prefix, "is this member a data source" and "is it a join source" all come from the resolver's token walk (`SqlCaretKind.Member`).
  - One definition of the word being typed (a character walk, so an unterminated `[Ord` still replaces the right range); `context.Prefix` and the replacement start share it.
  - The branch that used the line-relative column as an absolute offset is deleted; `SqlTextContext` exposes `StatementStart` and `Analyze` converts the column to a statement offset once, so the semantic model is evaluated at the real caret instead of the end of the line.
- **The predicate column is read from tokens**: `PredicateColumnPattern` is deleted; `ActiveColumn` walks back to the last comparison operator on the caret's level and takes the dotted name before it, so `[h].[Amount]` comes back unquoted and whole.
- **One state machine for comments and strings**: `MaskCommentsAndStrings` and `ScanState` were two copies of the same loop; both are now `Scan`, which produces the masked text and the caret state in one pass. `Suppress` behaves the same and can no longer drift.
- **The name now matches the meaning**: `PositionDecidedByParser` → `PositionResolved` (the position entry point decided, whether from the tree or the token scan).
- **Verification**: 54 corpus rows (two new ones are the exact bug shapes), 46/54 decided by the resolver (27 tree, 19 token scan); all 348 regression tests pass; `--completion-perf` stays inside the 3.00 ms budget.

## 5.467

### 中文

- **补丁和备用梯子都收掉了：光标位置只剩一个入口**
  - 删掉“补假 SQL 再重解析”整条补丁（`Repairs`：补 `)`、补 `1 = 1`、补 `ON 1 = 1`），也删掉三条正则尾部判定（`TryReadFromSourceTail` / `TryReadJoinSourceTail` / `IsAwaitingJoinPredicate`）和 `DetectClauseContext` 的子句猜测。
  - 树给不出跨度时（空的子句头、没闭合的括号、JOIN 还没写 ON），改由 token 归档回答：先看光标所在的最内层括号，再找该层最后一个子句关键字，最后看它后面写到哪。不猜、不补、不动用户的 SQL。
  - 词内位置（`... WHERE Na`）由同一套归档承接：结尾没写完的词先摘掉，再看它落在哪个子句；`FROM Cus`、`JOIN Or` 因此仍是“在选源”，不会跳成尾部提示。
  - 新增 `SqlCaretKind.Join` / `SqlCaretKind.MergeSource`，把原来只能靠正则区分的“正在选 JOIN 目标”和“MERGE … USING 的目标源”交回统一判定；解析树的关键字兜底也一并删掉，避免同一位置有两处结论。
  - 成员访问的“这个成员是不是数据源”（`IsDataSourceMemberContext`）改用同一个入口，`ClauseScan`、`DetectClauseContext`、`ClauseTokens` 全部删除。
- **验证（不放水）**
  - 语料 52 条逐条断言性质与必备候选：25 条由解析树直接定位，19 条只能由 token 归档定位（逐条列在 `ScanOnly` 清单里，清单每一条都必须仍由归档判定，数量多一条少一条都会失败），8 条属于专用上下文、成员访问或注释/字符串抑制；判定入口覆盖 44/52 = 84%。
  - 全量回归 345 项全过；`--completion-perf` 七个真实位置 0.59–1.28 ms/键（预算 3.00 ms）。

### English

- **The patches and the spare ladder are gone: one entry point decides the caret position**
  - Removed the whole "complete the SQL, then parse again" patch (`Repairs`: closing `)`, inserting `1 = 1`, inserting `ON 1 = 1`) and the three regex tail readers (`TryReadFromSourceTail`, `TryReadJoinSourceTail`, `IsAwaitingJoinPredicate`) together with the `DetectClauseContext` clause guess.
  - When the tree has no span to give (an empty clause header, an unclosed parenthesis, a join target without its `ON`), the token scan answers instead: the innermost open level, the last clause keyword on that level, and what has been typed after it. Nothing is guessed, nothing is appended, the user's SQL is not touched.
  - Positions inside a word (`... WHERE Na`) are handled by the same scan: the unfinished word is dropped first, then the clause it sits in decides. `FROM Cus` and `JOIN Or` therefore stay "still picking a source" instead of jumping to a tail.
  - Added `SqlCaretKind.Join` and `SqlCaretKind.MergeSource` so "picking a join target" and "MERGE ... USING source" no longer need a regex; the keyword fallback inside the tree decision was deleted too, so one position never has two conclusions.
  - The member-access question "is this member a data source" (`IsDataSourceMemberContext`) now asks the same entry point; `ClauseScan`, `DetectClauseContext` and `ClauseTokens` are gone.
- **Verification**
  - 52 corpus rows assert both the kind and the required items: 25 are placed by the parse tree, 19 only by the token scan (each listed in `ScanOnly`, and every listed row must still be scan-decided — gaining or losing one row fails), 8 are specialised contexts, member access, comments or string literals; the resolver covers 44/52 = 84%.
  - All 345 regression tests pass; `--completion-perf` reports 0.59–1.28 ms per keystroke across seven real positions (budget 3.00 ms).

## 5.466

### 中文

- **选择列表每次按键 11.7 ms → 1.1 ms**
  - 热点不在建项（实测 0.2 ms），而在 `FilterAndSort`：每个候选项都调一次 `CompletionUsageStore.Boost()`，而它要拿锁并线性扫全部历史条目，200 条候选就扫 200 遍。现在每次过滤只取一次历史快照（`BoostMap`），逐项变成字典命中；排序语义不变，并有“快照 vs 逐项查询”的等价性测试盯着。
  - 关键字表按“附加关键字”设置文本缓存（`KeywordsFor`），不再每次按键重新 trim、去重约 150 个词。
  - 关键字与内置函数的 `Description` 也改成首次读取才构造（与 `DetailText` 同一套惰性机制），`Clone()` 复制的是 provider 而不是已构造的字符串。
- **防回退**：`--completion-perf` 现在带 3.00 ms/键的预算断言，超预算或选择列表候选少于 150 条都会以退出码 1 失败；回归套件新增 3 项结构性测试（描述惰性、关键字缓存、历史快照与逐项查询等价），共 345 项。

### English

- **Select list: 11.7 ms → 1.1 ms per keystroke**
  - The hotspot was not item building (0.2 ms measured) but `FilterAndSort`: every candidate called `CompletionUsageStore.Boost()`, which takes a lock and walks the whole history — 200 candidates meant 200 walks. The filter pass now takes one history snapshot (`BoostMap`) and each row becomes a dictionary hit; ordering semantics are unchanged and an equivalence test compares the snapshot against the per-item lookup.
  - The keyword table is cached by the additional-keywords setting (`KeywordsFor`) instead of re-trimming and de-duplicating ~150 names on every keystroke.
  - Keyword and built-in function `Description` values are built on first read (same lazy mechanism as `DetailText`), and `Clone()` copies the provider instead of the materialized string.
- **Regression guard**: `--completion-perf` now asserts a 3.00 ms per-keystroke budget and fails with exit code 1 when a position exceeds it or the select list drops below 150 candidates. Three structural tests were added (deferred descriptions, keyword cache, usage snapshot equivalence), 345 in total.

## 5.465

### 中文

- **光标位置改由解析树决定，正则只做兜底**
  - 新增 `SqlCaretPositionResolver`：取光标前最后一个有效 token，在 ScriptDOM 语法树里找“包含它的最小节点”。表引用落在它上面就是 FROM / JOIN 尾部（并区分是否还需要 `ON`），WHERE / HAVING / ON 就是谓词，ORDER BY / GROUP BY / 选择列表各自归位。别名、列、位置现在来自同一棵树，不会再互相打架。
  - “最小节点优先”修掉一类错判：`WHERE EXISTS (SELECT 1 FROM dbo.T ` 以前被外层 WHERE 吞掉、报成谓词，现在归内层表引用。
  - 树给不出结论时先“补齐”再解析（只追加在光标之后，token 偏移不动）：未闭合括号补 `)`、子句只有关键字时补条件、`JOIN 表 别名 ` 补 `ON 1 = 1`、`IN (` 给一个占位值。补齐仍无结论才回落到原有正则路径。
- **修掉多行语句的位置误判**
  - `SELECT h.HeaderName, h.Amount` 换行后写 `FROM dbo.Header h ` 时，光标列号被当成语句内绝对偏移，截出 `h.HeaderNam`，整个位置被判成 `Member`。现在先换算成“行首 + 列号”的真实偏移。
  - `WHERE (a = 1 AND ` 不再被当成函数调用，不会再给函数参数。
  - `UPDATE t SET a = 'x' WHERE ` 现在给谓词（AND / OR / 列），同时保留目标表列。
- 新增位置语料测试 52 条（选择列表、FROM / JOIN / ON / WHERE / HAVING / GROUP BY / ORDER BY、CROSS JOIN / APPLY、子查询、EXISTS、CTE、多语句、GO 分批、注释、字符串内、方括号、UPDATE / INSERT / MERGE、`t.` 成员），逐条断言上下文与必备候选项，并统计“解析器决定”的比例。
- 新增 `--completion-perf` 探针：真实语句每次按键 Analyze 0.6–1.3 ms，候选项构建 0.4–11 ms（选择列表约 200 条时最重）。
- 回归测试 342 项。

### English

- **The caret position now comes from the parse tree; the regexes are only a fallback**
  - New `SqlCaretPositionResolver`: the last significant token before the caret is looked up in the ScriptDOM tree, and the smallest node that contains it decides. A table reference means a FROM or JOIN tail (and whether `ON` is still expected), WHERE / HAVING / ON means a predicate, and ORDER BY, GROUP BY and the select list each keep their own slot. Aliases, columns and the position now come from one tree, so they cannot disagree.
  - Smallest-node-wins fixes a class of misreads: `WHERE EXISTS (SELECT 1 FROM dbo.T ` used to be swallowed by the outer WHERE and reported as a predicate; it belongs to the inner table reference.
  - When the tree cannot decide, the text is completed first and parsed again (only after the caret, so token offsets never move): unclosed parentheses get their `)`, a clause that has only its keyword gets a condition, `JOIN table alias ` gets `ON 1 = 1`, and `IN (` gets a placeholder value. Only when that still fails does the old regex path answer.
- **Fixed a position misread on multi-line statements**
  - After a select list like `SELECT h.HeaderName, h.Amount`, typing `FROM dbo.Header h ` treated the caret column as an absolute offset and sliced `h.HeaderNam` out of the first line, so the position became `Member`. The offset is now line start plus column.
  - `WHERE (a = 1 AND ` is no longer read as a function call, so function arguments are not offered there.
  - `UPDATE t SET a = 'x' WHERE ` now offers the predicate (AND / OR / columns) and still includes the target table columns.
- 52 new caret-position corpus cases (select list, FROM / JOIN / ON / WHERE / HAVING / GROUP BY / ORDER BY, CROSS JOIN / APPLY, subqueries, EXISTS, CTE, multiple statements, GO batches, comments, inside strings, bracketed identifiers, UPDATE / INSERT / MERGE, `t.` members). Each asserts the context and the entries that must be there, and the test also reports how often the parse tree decided.
- New `--completion-perf` probe: 0.6–1.3 ms for Analyze per keystroke on a realistic statement, 0.4–11 ms for building the entries (a select list with about 200 entries is the heaviest).
- 342 regression tests.

## 5.464

### 中文

- **`INNER JOIN 表 别名 ` 之后不再给 AND/OR，改给 ON + 表提示**
  - 根因：这个位置被谓词分类器当成了“条件已写完”（它看到上一个 ON 里的 `=`），于是弹出 AND/OR，而表提示只挂在 FROM 尾部那条路径上。
  - 现在 JOIN 尾部是独立上下文：`ON` 排第一，接着 10 个 `WITH (...)` 提示，然后才是下一个 JOIN、别名、WHERE/GROUP BY/ORDER BY；AND/OR 不再出现。
  - `CROSS JOIN 表 ` / `CROSS APPLY ... ` 不给 ON（本来就不需要），但依然给表提示。
  - `ON A.DJLSH = B.DJLSH ` 这种真正的 ON 条件尾部不受影响，仍给 AND/OR。
- 回归测试增至 341 项（新增：JOIN 尾部有 ON 与表提示、无 AND/OR、顺序、CROSS JOIN 不推 ON）。

### English

- **`INNER JOIN table alias ` now offers ON plus table hints, not AND/OR**
  - Root cause: the predicate classifier called this position a finished condition (it saw the `=` of the previous ON), so the popup offered AND/OR — and hints only hung off the FROM tail.
  - A join target tail is its own context now: `ON` first, then the ten `WITH (...)` hints, then the next JOIN, the alias and WHERE / GROUP BY / ORDER BY. AND/OR are gone.
  - `CROSS JOIN table ` and `CROSS APPLY ...` get the hints but no ON, since they take none.
  - `ON A.DJLSH = B.DJLSH ` (a finished ON condition) is unchanged and still offers AND/OR.
- 341 regression tests (new: ON plus hints at a join tail, no AND/OR, ordering, CROSS JOIN without ON).

## 5.463

### 中文

- **内置函数不再出现“这个函数暂时还没有用法说明”**
  - 补全列里的 91 个内置函数（含 `SYSUTCDATETIME`、`SUSER_SNAME`、`MAX/MIN`、`STRING_SPLIT`、`JSON_*`、`DATEFROMPARTS` 系列、`OBJECT_ID`、`SERVERPROPERTY`、`XACT_STATE` 等）现在都有用法条目：签名 + 一句中文说明 + 示例。
  - 以前只有 40 个函数有说明，其余显示“SQL Server 内置函数 / 暂无用法说明”；后者看起来像功能坏了。
  - 新增测试会逐个检查“候选里能出现的每个内置函数都有说明”，以后新增函数忘了写说明会直接失败。
- 说明继续走逐行本地化：简体中文显示中文，繁体自动转繁，英文环境显示英文。

### English

- **No more “no usage notes are available” for built-in functions**
  - All 91 built-in functions the popup offers now carry a note — signature, one-line summary and an example. That includes `SYSUTCDATETIME`, `SUSER_SNAME`, `MAX/MIN`, `STRING_SPLIT`, the `JSON_*` family, the `*FROMPARTS` builders, `OBJECT_ID`, `SERVERPROPERTY` and `XACT_STATE`.
  - Only 40 names used to have a note; the rest answered “SQL Server built-in function / no usage notes yet”, which read like a bug.
  - A new test walks every built-in name in the candidate list, so adding a function without a note now fails the build.
- The notes stay localized line by line (Chinese, converted Traditional Chinese, English).

## 5.462

### 中文

- **`SELECT SUM` 不再出现三个 SUM**
  - 以前同一个名字会出现三条：片段 `SUM(`、内置函数 `SUM`、关键字 `SUM`，而只有其中一条带用法说明。
  - 现在关键字、函数、片段按裸名字合并成一条：保留分最高的（插入 `SUM(` 并带占位符），说明从原来带文档的那条继承过来。
  - 列名、表名不受影响——叫 `SUM` 的列仍与 `SUM` 函数分开；`WITH (NOLOCK)` 这类自身带括号的提示也不会互相合并。
- **用法说明改成中文**
  - 函数说明（40 个内置函数）、表格提示（10 个）、JOIN / WHERE / GROUP BY / ORDER BY / ON / AS 别名，全部逐行本地化；切繁体中文时同一张表自动转繁。
  - 签名行（如 `SUM([ALL|DISTINCT] expression)`）和示例 SQL 保持原文，这是要照写的语法。
  - 切回英文仍显示英文；未收录的函数仍显示“暂无用法说明”。
- 回归测试增至 340 项：新增“SELECT 列表只有一个 SUM 且有说明”和“所有用法说明都有中文版本”（逐条比对中英不同）。

### English

- **`SELECT SUM` no longer lists SUM three times**
  - The same name used to appear as the snippet `SUM(`, the built-in function and the keyword — with the usage note on only one of them.
  - Keywords, functions and snippets now merge on the bare name: the highest score wins (the snippet that inserts `SUM(` with a placeholder) and the documentation is inherited from the duplicate that had it.
  - Columns and tables are untouched (a column named `SUM` is not the `SUM` function), and hints that carry their own parentheses (`WITH (NOLOCK)`) still list separately.
- **Usage notes are localized**
  - Function notes (40 built-ins), the ten table hints and the JOIN / WHERE / GROUP BY / ORDER BY / ON / AS-alias notes are translated line by line; zh-TW converts from the same table.
  - Signature lines (`SUM([ALL|DISTINCT] expression)`) and example SQL stay verbatim — that is the syntax to copy.
  - English is still English, and a function without notes still says so.
- 340 regression tests (new: one SUM row with its note; every usage note has a Chinese version).

## 5.461

### 中文

- **表别名之后也能提示 `WITH (...)`，而且不再只有 NOLOCK**
  - `FROM dbo.WDDDB B ` 之后与 `FROM dbo.WDDDB ` 之后给同一套提示（表提示写在别名后面：`FROM dbo.WDDDB B WITH (NOLOCK)`）。
  - 共 10 个常用表提示（比原来多 9 个），按常用度排序：`WITH (NOLOCK)`、`READPAST`、`UPDLOCK`、`HOLDLOCK`、`ROWLOCK`、`TABLOCK`、`SNAPSHOT`、`NOWAIT`、`FORCESEEK`、`INDEX(...)`。
  - 插入 `WITH (NOLOCK) ` 等带尾随空格，写完后继续接 JOIN / WHERE；`WITH (INDEX(` 停在左括号，直接敲索引名。
- **详情面板改成真正的用法说明**（以前只有 `table hint` / `join` 两个词）
  - 每个表提示 4–7 行：做什么 / 什么场景用 / 代价与风险 / 组合写法（如 `READPAST, UPDLOCK, ROWLOCK`）。
  - `INNER/LEFT/RIGHT/FULL/CROSS JOIN`、`WHERE`、`GROUP BY`、`ORDER BY`、`ON`、`AS 别名` 也补了说明。
- 回归测试扩展到 338 项（新增断言：别名后仍给全部提示、每个提示带多行说明、`WITH (NOLOCK) ` 尾随空格、`WITH (INDEX(` 停在括号）。

### English

- **Table hints are offered after an alias too, and no longer just NOLOCK**
  - `FROM dbo.WDDDB B ` gets the same list as `FROM dbo.WDDDB ` — a hint belongs after the alias (`FROM dbo.WDDDB B WITH (NOLOCK)`).
  - Ten hints, ordered by how often they are typed: `NOLOCK`, `READPAST`, `UPDLOCK`, `HOLDLOCK`, `ROWLOCK`, `TABLOCK`, `SNAPSHOT`, `NOWAIT`, `FORCESEEK`, `INDEX(...)`.
  - Hints are inserted with a trailing space so JOIN / WHERE follows directly; `WITH (INDEX(` stops at the parenthesis so the index name can be typed.
- **The details pane now explains the usage** (it only said `table hint` / `join` before)
  - Each hint carries 4–7 lines: what it does, when it is the right choice, what it costs, and the usual combination (e.g. `READPAST, UPDLOCK, ROWLOCK`).
  - `INNER/LEFT/RIGHT/FULL/CROSS JOIN`, `WHERE`, `GROUP BY`, `ORDER BY`, `ON` and `AS alias` are described too.
- 338 regression tests pass (new assertions cover hints after an alias, the multi-line descriptions, the trailing space and the index-hint parenthesis).

## 5.460

### 中文

- **默认界面语言改为英文**
  - 安装后未手动选择语言的用户，现在看到英文界面；开关位置不变：设置 > 语言，可随时切回简体中文等 8 种语言。
  - 已保存过语言选择的用户不受影响（注册表/配置文件里的 `UiLanguage` 仍然优先）。
  - 无意义或被写坏的语言值也回退到英文，不再默默回到中文。
  - VSIX 包元数据语言同步改为 `en-US`（扩展管理器里的名称与描述本来就是英文）。
- 新增三条语言回退测试；回归测试全过。

### English

- **English is now the default UI language**
  - A fresh install with no language chosen shows English; the switch is unchanged (Settings > Language) and still offers all eight languages.
  - Users who already picked a language are unaffected — the stored `UiLanguage` keeps priority.
  - An empty, unsupported or corrupted value falls back to English instead of silently returning to Chinese.
  - The VSIX package metadata language is now `en-US`, matching the English display name and description.
- Three fallback tests added; all regression tests pass.

## 5.459

### 中文

- **修正：唯一字段也被加上了表名前缀（如 `WDDDB.KSBM`）**
  - 原因：只要语句里不止一个数据源，`AddScopedColumns` 就给所有字段加限定符，而且限定符退化成用表名（没写别名时）。`KSBM` 只属于一张表，根本不需限定，却变成了 `WDDDB.KSBM`。
  - 现在只在“该字段名在多张表里都存在”时才加限定符（`DjLsh` 这种重名列仍会得到 `A.DjLsh` / `B.DjLsh`），唯一字段直接插入 `[KSBM]`。
  - 同时修了源去重：同一张表因别名键、裸表名、带/不带架构名而重复出现时，不再被当成“两个数据源”（这也是限定符误加的一个来源）。
- 新增回归测试（`Completion: unique columns stay unqualified`）：两表共享 `DjLsh`、`KSBM` 只有一张表有，断言 `KSBM` 裸插、`DjLsh` 带别名。
- 回归测试全过。

### English

- **Fixed unique columns getting a table prefix (`WDDDB.KSBM`)**
  - Cause: whenever a statement had more than one source, `AddScopedColumns` qualified every column, and with no explicit alias the qualifier fell back to the table name. `KSBM` belongs to one table, needs no qualifier, and still came out as `WDDDB.KSBM`.
  - Now a qualifier is added only when the same column name exists in more than one table (shared names still become `A.DjLsh` / `B.DjLsh`); a unique column inserts as `[KSBM]`.
  - Source de-duplication was fixed too: the same table listed under an alias, its bare name and with or without the schema is no longer counted as two sources, which was another way to force the qualifier.
- New regression test (`Completion: unique columns stay unqualified`).
- All regression tests pass.

## 5.458

### 中文

- **`FROM 表 ` 之后先给表提示、JOIN 与别名，字段往后排**
  - 以前 `FROM WDDDH ` 只把该表的字段列在第一位，而这里真正能接的是 `WITH (NOLOCK)`、`INNER JOIN` / `LEFT JOIN` / `RIGHT JOIN` / `FULL JOIN` / `CROSS JOIN` 和别名。
  - 新增“表来源尾部”上下文（`TableSourceTail`）：序号为 `WITH (NOLOCK)`、`INNER JOIN`、`LEFT JOIN`、`RIGHT JOIN`、`FULL JOIN`、`CROSS JOIN`、`AS <别名>`，后面才是 `WHERE`/`GROUP BY`/`ORDER BY`，字段降到最下面作参考。
  - 已经写了别名（`FROM WDDDH A `）时不再给 `WITH (NOLOCK)` 与 `AS`：表提示必须写在别名之前，否则插出来是非法语句；JOIN 仍然保留。
  - 换行后不触发（`FROM 表` 换行再写下一子句），`WHERE`/`GROUP BY` 等已开始的子句不会被误认为别名。
- 新增回归测试（`Completion: FROM source tail ranks hint, joins and alias`）。
- 回归测试全过。

### English

- **After `FROM table ` the popup now leads with the hint, joins and the alias**
  - `FROM WDDDH ` used to put that table's columns first, but what can actually follow a written source is `WITH (NOLOCK)`, `INNER JOIN` / `LEFT JOIN` / `RIGHT JOIN` / `FULL JOIN` / `CROSS JOIN` or an alias.
  - New `TableSourceTail` context: `WITH (NOLOCK)`, the five joins and `AS <alias>` first, then `WHERE` / `GROUP BY` / `ORDER BY`, with the columns dropped to the bottom as reference.
  - Once an alias is typed (`FROM WDDDH A `) the hint and `AS` are withheld — a table hint must precede the alias, so inserting it there would produce invalid SQL — while the joins stay.
  - Does not fire across a line break, and a clause that already started (`WHERE`, `GROUP BY`) is never mistaken for an alias.
- New regression test (`Completion: FROM source tail ranks hint, joins and alias`).
- All regression tests pass.

## 5.457

### 中文

- **相关字段高亮加深，背景图下也看得清**
  - 光标停在字段/表名上时，同名字词的高亮底色从 `#E9E9E9` 改为 `#C0C0C0`。原来的颜色和背景图（尤其是偏亮的图）几乎融在一起，看不出哪里被高亮。
  - 因为编辑器不认画刷的 alpha（半透明会被画成实心），所以只能直接加深 RGB；新颜色对黑色/深色代码文字仍有足够对比，不至于抢戏。
- 回归测试全过。

### English

- **Darker related-token highlight so it survives a background image**
  - The related-word swatch behind matching column/table names went from `#E9E9E9` to `#C0C0C0`; the old tone blended into the picture (especially a pale one) and the highlight was effectively invisible.
  - The editor ignores brush alpha (a translucent brush paints solid), so the darkening has to live in the RGB values; the new tone still leaves enough contrast for black or dark syntax text.
- All regression tests pass.

## 5.456

### 中文

- **同名列在补全列表里带上表别名**
  - 以前只在插入文本里加限定符，列表里仍是两个一模一样的 `DjLsh`，看不出哪个来自 `A`、哪个来自 `B`。在 `ON A.DjLsh = ` 这类位置只能靠逐条看右侧详情猜。
  - 现在当列名在多个数据源里同名时，列表直接显示 `A.DjLsh` / `B.DjLsh`（用别名，没有别名时用表名），插入仍是带括号的限定形式。
  - 只在“真的重名”时加前缀：唯一列名保持 `YHID` 这种干净写法，不把整个列表刷成一堆 `A.xxx`。
- 新增回归测试（`Completion: ambiguous columns show their alias`）。
- 回归测试全过。

### English

- **Duplicate column names now carry their table alias in the list**
  - Only the insert text used to be qualified, so the popup showed two identical `DjLsh` rows and the source had to be read off the detail pane.
  - When a name exists in more than one source, the list now shows `A.DjLsh` / `B.DjLsh` (alias when present, table name otherwise); insertion stays fully qualified.
  - Only genuinely duplicated names get the prefix — a unique `YHID` keeps its short form instead of turning the whole list into `A.xxx` noise.
- New regression test (`Completion: ambiguous columns show their alias`).
- All regression tests pass.

## 5.455

### 中文

- **修正 5.454 的 `ON` 排序在多行语句下不生效**
  - 5.454 把 `Analyze` 的第二个参数当成了“语句内偏移”，实际它是“行内列号”（控制器直接传 `GetCaretPos` 的列）。语句第一行是 `SELECT *` 时，识别位置偏了一整行的长度，判断永远不成立，所以弹窗仍只给列。
  - 现在改用语句本身的位置：语句总是以光标结尾，直接用掩码语句的长度作偏移，与行号无关。
  - 回归测试改为按控制器的方式传“行内列号”，并用多行语句（第一行 `SELECT *`）验证，避免同类偏差再溜过。
- 回归测试全过。

### English

- **Fixed `ON` ranking not applying to multi-line statements in 5.454**
  - 5.454 treated the second argument of `Analyze` as a statement offset, but the controller passes the caret's column inside the line (`GetCaretPos`). With `SELECT *` on the first line the probe landed a whole line short and never matched, so the popup kept listing columns only.
  - The probe now uses the statement's own end: the masked statement always stops at the caret, so its length is the offset, independent of line breaks.
  - The regression test now passes a line column like the controller does, on a multi-line statement, so the same mistake cannot pass again.
- All regression tests pass.

## 5.454

### 中文

- **JOIN 之后把 `ON` 排到第一位**
  - 在 `FROM dbo.WDDDH A INNER JOIN dbo.ZZJGB B ` 这种位置（表名/别名之后只剩一个空格、还没有写 `ON`）时，补全列表第一项就是 `ON`，而不是原来的列清单。之前这个位置分析器不认为是 JOIN 上下文，`ON` 根本不在候选里。
  - 选中 `ON` 会插入 `ON ` 并立即重新弹出补全，直接接着选连接键，一个手势完成。
  - 只在“确实缺 `ON`”的位置生效：已经写了 `ON`、正在输入表名、`CROSS JOIN`、`CROSS/OUTER APPLY`（这些没有 `ON` 子句）都不会出现该项。
- 新增回归测试（`Completion: JOIN target ranks ON first`），覆盖有/无别名、已写 `ON`、CROSS JOIN 与部分表名五种输入。
- 回归测试全过。

### English

- **`ON` now ranks first right after a JOIN target**
  - At `FROM dbo.WDDDH A INNER JOIN dbo.ZZJGB B ` — target typed, nothing but a space after it — the first suggestion is `ON` instead of the column list. The analyzer did not classify this position as a JOIN context, so `ON` was not even a candidate.
  - Committing `ON` inserts `ON ` and immediately reopens the popup, so the join key follows in one gesture.
  - Only fires where `ON` is genuinely missing: past `ON`, while a table name is still being typed, and for `CROSS JOIN` / `CROSS` or `OUTER APPLY` it stays out of the way.
- New regression test (`Completion: JOIN target ranks ON first`) covering with/without alias, past `ON`, CROSS JOIN and a partial table name.
- All regression tests pass.

## 5.453

### 中文

- **重排“SQL 自动补全”设置页**
  - 选项从一长串参差不齐的勾选框改为描述单元两列对齐（`UniformGrid`），不再出现一行两个、一行一个的跳动。新增三个分组标题：行为、别名与 JOIN 规则、关键字与响应。
  - 改为两栏：左栏放行为选项、前缀/别名/JOIN 规则、额外关键字与延迟参数；右栏放“最近使用的建议”列表（高度从 96 提到 260）及其操作按钮，长建议文本有足够宽度可读。
  - “Remove selected / Clear history / Clear and refresh completion cache”三个按钮合成一行，刷新按钮靠右对齐，不再各占一行。
  - 整页包进 `ScrollViewer`，窗口变矮时可滚动；“保存”按钮与说明文字移到跨越两栏的页脚，位置固定可预期。
  - 两个过长的勾选项文案稍作缩写（“补全元数据连接信任 SQL Server 证书”对应项、行内预览项），并在本地化表里同步了新的键和三个分组标题。
- 回归测试全过。

### English

- **Reworked the SQL Completion settings page**
  - The ragged list of check boxes is now an evenly aligned two-column `UniformGrid`, and the page gained three section headings: Behaviour, Alias and JOIN rules, Keywords and timing.
  - Two columns: behaviour options, alias/JOIN rules, extra keywords and timing on the left; recently used suggestions (list height 96 → 260) with its actions on the right, wide enough to read long suggestion summaries.
  - “Remove selected”, “Clear history” and “Clear and refresh completion cache” share one row, with the refresh button right-aligned instead of each taking its own line.
  - The page scrolls when the window is short, and Save plus the hint moved to a footer spanning both columns.
  - Two overlong option labels were shortened, and the new keys plus the three section headings were added to the localization table.
- All regression tests pass.

## 5.452

### 中文

- **设置页“编辑器”选项卡：超出高度时可滚动，右侧空白改为预览区**
  - 整页内容包进 `ScrollViewer`（纵向自动、横向禁用），窗口变矮时出现滚动条，不再把保存按钮和说明文字裁掉。
  - 改为两栏：左栏固定 540 宽放缩略图、透明度、填充方式等控件，右栏是随窗口宽度伸展的实时预览（高 320，带边框与“预览”标题），不再空着半个窗口。
  - 文案同步修正：提示从“不透明度建议保持较低”改为“透明度建议保持较高”；内置图片说明从 CC0 改为 Reasonix 主题包（MIT）。
- 回归测试全过。

### English

- **Settings → Editor: the page scrolls when content is taller than the window, and the empty right half is now the preview**
  - The whole page is wrapped in a `ScrollViewer` (vertical auto, horizontal disabled), so shrinking the window shows a scroll bar instead of cutting off the Save row and the notes.
  - Two columns now: a fixed 540 px column with thumbnails, transparency and fill mode, and a live preview on the right that grows with the window (320 px tall, framed, with a "Preview" caption).
  - Wording fixed along the way: the hint now says to keep the transparency high (it used to say keep opacity low), and the bundled-picture note now credits the Reasonix theme packs (MIT) instead of CC0.
- All regression tests pass.

## 5.451

### 中文

- **设置页选图改为缩略图网格**
  - 不再用下拉框选文字，改为每张内置图一个可点击的缩略图片块（158×89，下方带名称），当前选中项用主题强调色描边高亮；鼠标悬停有名称提示。
  - 缩略图按 320 px 解码并缓存（设置页的大预览也降到 800 px），避免为了几个小图块各解码一张 2560 px 位图。
  - 块表按 `EditorBackgroundRules.BuiltInImages` 动态生成，切语言时会重建，与内置图列表不会脱节；“自定义图片”由旁边的“Browse...”按钮承担，旧的下拉项已移除。
- 回归测试全过。

### English

- **The settings page now picks pictures from a thumbnail grid**
  - The text-only combo box is gone: every bundled picture is a clickable thumbnail tile (158×89 with its name underneath), the current one outlined in the theme accent colour, and the name shown as a tooltip.
  - Thumbnails decode at 320 px and are cached (the large preview drops to 800 px) instead of decoding a 2560 px bitmap per tile.
  - Tiles are generated from `EditorBackgroundRules.BuiltInImages` and rebuilt on load, so they cannot drift from the bundled set and follow a language switch; the old "custom picture" entry is replaced by the "Browse..." button next to the grid.
- All regression tests pass.

## 5.450

### 中文

- **修 5.449 的“一片全黑”：图片现在放进格式项真正绘制的那个画刷**
  - 5.449 日志：`nowBrush=#00FFFFFF`、`viewBackground=#00FFFFFF`、`controlBackground=ImageBrush`，结果全黑。原因是 SSMS 实际绘制的是 `TextView Background` 里的 `Background` 画刷，而透明画刷会被当成不透明的黑色画上去；控件层上的 `ImageBrush` 根本不参与绘制。
  - 5.449 的做法是把该画刷置透明，等于“什么都不画” ⇒ 黑底。
  - 5.450 改为把图片本身放进这个画刷：用 `DrawingBrush` 合成“主题底色 + 按透明度叠上的图片”，两者都在图片像素坐标系里绘制，所以半透明时是与主题色混合，而不是露出编辑器背后的黑底；100% 时完全盖住。`BackgroundColor` 仍置透明，防止纯色填充盖掉图片。
  - 原来的 `ImageBrush` 换成合成刷子后，关闭功能仍按快照逐项恢复原值。
- 回归测试全过。

### English

- **Fixes the all-black editor from 5.449: the picture now goes into the brush the shell actually paints**
  - 5.449 log: `nowBrush=#00FFFFFF`, `viewBackground=#00FFFFFF`, `controlBackground=ImageBrush`, screen all black. The shell paints the `Background` brush of the `TextView Background` item, and a transparent brush there is drawn as opaque black; the `ImageBrush` on the WPF control never participates.
  - 5.450 puts the picture itself into that brush: a `DrawingBrush` composes "theme colour + picture at the configured transparency" in the picture's own pixel space, so partial transparency blends with the theme colour instead of revealing the black host, and 100% covers it. `BackgroundColor` stays transparent so no solid fill can cover the picture.
  - Disabling the feature still restores every saved property bag verbatim.
- All regression tests pass.

## 5.449

### 中文

- **5.447 的日志定位到最后一层遮盖：`TextView Background` 里的 `Background` 画刷**
  - 5.447 日志已经证明：监听器跑了、格式映射拿到了（`source=editor-format-map`）、四个格式项都改成了 `#00FFFFFF`。但紧接着 `viewBackground=#FFFFFFFF`——这个格式项除了 `BackgroundColor` 还有一个现成的 `Background` 画刷，SSMS 把它推回视图盖在图片上面。
  - 修法：该画刷也一并置为透明（仍在同一个快照里备份，关闭时原样恢复）；日志新增 `originalBrush=/nowBrush=`，下次可以直接看出它是不是又变回白色。
  - 侧带修了一个副作用：主题推回的透明画刷会把“主题原色”记成透明，导致关闭功能后底色丢失；现在全透明画刷不再被当作主题底色。
- **设置里的“不透明度”改为“透明度”**
  - 滑块改为透明度 0–95%：0% 图片最清晰，95% 最淡；内部仍按不透明度存储，已有的 `settings.json` 无需迁移（旧的 16% 不透明度会显示为 84% 透明度）。
- 回归测试全过。

### English

- **The 5.447 log found the last thing covering the picture: the `Background` brush inside `TextView Background`**
  - The 5.447 log proved the listener ran, the format map was resolved (`source=editor-format-map`) and all four items were set to `#00FFFFFF`. But `viewBackground=#FFFFFFFF` right after: besides `BackgroundColor` that item carries a ready-made `Background` brush, and the shell pushes it onto the view on top of the picture.
  - That brush is now made transparent too (backed up in the same snapshot and restored verbatim when the feature is off); the log gained `originalBrush=/nowBrush=` so the next report shows immediately whether it went white again.
  - Fixed a side effect on the way: the transparent brush pushed by the theme was recorded as “the theme colour”, which would lose the background on disable; a fully transparent brush is no longer treated as the theme colour.
- **The settings slider now reads “transparency” instead of “opacity”**
  - 0–95% transparency: 0% shows the picture fully, 95% is the faintest. The stored setting stays in opacity, so existing `settings.json` files need no migration (an old 16% opacity shows as 84% transparency).
- All regression tests pass.

## 5.448

### 中文

- **换掉整套内置背景图，并把不透明度上限提到 100%**
  - 新图取自 `https://github.com/esengine/DeepSeek-Reasonix` 的内置主题包（`internal/ext/theme/builtin`，MIT 许可），共 6 张：青绿晨风（默认）、绯红天际、暗夜镏金、紫夜星海、灵感手记、玫瑰晨曦。
  - 上游素材是 WebP，SSMS 使用的 WPF 解码器读不了，已统一转为 2560 px 宽的渐进式 JPEG（质量 88），未做裁切或修图。
  - 旧的四张（雾中森林/夜空星野/云雾山脊/沙漠沙丘）已删除；保存了旧名字的设置会自动映射到最接近的新图（`mist-forest → sage-breeze`、`night-sky → violet-starlight`、`mountain-ridge → crimson-horizon`、`desert-dunes → noir-gold`），升级后编辑区不会变成无背景。
  - 不透明度滑块上限从 60% 改为 100%，`EditorBackgroundSettings.Normalize` 与服务端夹取同步放宽到 5–100%；100% 为完全不透明，暗色主题下的自动压暗不再把它降成 75%。
  - `Resources/backgrounds/NOTICE.txt` 已更新出处与许可说明（MIT，Copyright (c) 2026 Reasonix Contributors）。
- 回归测试全过。

### English

- **Replaced the bundled background set and raised the opacity ceiling to 100%**
  - The new pictures come from the built-in theme packs of `https://github.com/esengine/DeepSeek-Reasonix` (`internal/ext/theme/builtin`, MIT): sage breeze (default), crimson horizon, noir gold, violet starlight, spark notebook and rose dawn.
  - Upstream ships WebP, which the WPF decoder inside SSMS cannot read, so each was re-encoded as a 2560 px progressive JPEG (quality 88) with no cropping or retouching.
  - The four old pictures were removed; a saved setting naming one of them is mapped to the closest new picture (`mist-forest → sage-breeze`, `night-sky → violet-starlight`, `mountain-ridge → crimson-horizon`, `desert-dunes → noir-gold`), so an upgrade never leaves the editor without a background.
  - The opacity slider now goes to 100% (was 60%), and `EditorBackgroundSettings.Normalize` clamps to the same 5–100 range. 100% is fully opaque: the dark-theme softening no longer turns it into 75%.
  - `Resources/backgrounds/NOTICE.txt` records the new source and licence (MIT, Copyright (c) 2026 Reasonix Contributors).
- All regression tests pass.

## 5.447

### 中文

- **找到背景图真正没生效的一环：监听器声明了 SSMS 并不存在的 content type，所以从未被调用**
  - 5.446 的日志里 `SQL-BACKGROUND attach` 只出现在点“保存”的那一刻（`00:47:40.5145`），而查询窗口是在 `00:47:37` 打开的——说明 `TextViewCreated` 根本没有触发，`IEditorFormatMapService` 也从未被注入（`formatMap=no`）。
  - 原因：`EditorBackgroundViewListener` 只声明了 `[ContentType("SQL Server Tools")]` + `TextViewRole(Document)`，而 `SQLEditors.dll` 里没有这个 content type。项目里确实会执行的 part（右键菜单、诊断、幽灵文本、`SqlEditorServices`）都同时声明了 `"SQL Server Tools"` 和 `"SQL"`，并且用 `Editable` 角色。现已改成与之一致（两个 content type、`Document` + `Editable` 两个角色）。
  - 格式映射改为三条独立路径，并记录命中来源：`IEditorFormatMapService`、`IClassificationFormatMapService`、视图/缓冲区属性包里已发布的 `IEditorFormatMap`。日志 `SQL-BACKGROUND map: source=... type=... services=...` 能直接区分“服务没导入”和“导入成功但返回 null”。
  - 透明化修复自反馈循环：`FormatMappingChanged` 由我们自己的写入触发时会无限重入，现在只在某个键真的又变回不透明时才重刷。
  - 日志补充可直接判读的字段：`listener: view created contentType=... roles=...`、`attach ... contentType=... roles=...`、`key=... original=#AARRGGBB now=#AARRGGBB entries=...`、`apply ... viewBackground=... controlBackground=...`。
- 回归测试全过。

### English

- **Found the real missing link: the listener declared a content type SSMS never publishes, so it was never invoked**
  - In the 5.446 log `SQL-BACKGROUND attach` appeared only at the moment Save was clicked (`00:47:40.5145`), while the query window opened at `00:47:37` — `TextViewCreated` never ran and `IEditorFormatMapService` was never injected (`formatMap=no`).
  - Cause: `EditorBackgroundViewListener` declared only `[ContentType("SQL Server Tools")]` + `TextViewRole(Document)`, and `SQLEditors.dll` publishes no such content type. Every part that actually runs in this build (context menu, diagnostics, ghost text, `SqlEditorServices`) declares both `"SQL Server Tools"` and `"SQL"` and uses the `Editable` role; the listener now matches that.
  - The format map is resolved through three independent routes and the winner is logged: `IEditorFormatMapService`, `IClassificationFormatMapService`, and an `IEditorFormatMap` already published in the view or buffer property bag. `SQL-BACKGROUND map: source=... type=... services=...` separates “service not imported” from “imported but returned null”.
  - Fixed a self-feeding loop: our own writes raise `FormatMappingChanged`, so a re-apply now happens only when a key really is opaque again.
  - New log fields make the next report conclusive: `listener: view created contentType=... roles=...`, `attach ... contentType=... roles=...`, `key=... original=#AARRGGBB now=#AARRGGBB entries=...`, `apply ... viewBackground=... controlBackground=...`.
- All regression tests pass.

## 5.446

### 中文

- **背景图仍不显示：改为可诊断、且不依赖单一钩子**
  - 应用路径不再只靠新建视图的监听器：在设置页点“保存”时会遍历编辑器现有的视图登记表（与补全/高亮共用，已确认有效）逐个重新上色；之前没被跟踪的视图会先补上事件再应用。
  - 把 `IEditorFormatMapService` 改为可选导入（`AllowDefault`）：之前它一旦不可满足，MEF 会静默丢弃整个监听器，导致背景功能完全不执行且没有任何报错。
  - 透明背景覆盖到 `TextView Background`、`Plain Text`、`SQL`、`TSQL` 四个格式项，原值逐项备份、关闭时原样恢复。
  - 关键路径日志升为 Info：`SQL-BACKGROUND attach/refresh/apply/key=`，能直接看出“监听器没跑 / 视图数为 0 / 拿不到格式映射 / 某个键的原值是什么”。
- 回归测试 331 项全过。

### English

- **Background still not visible: made diagnosable and no longer dependent on a single hook.**
  - Saving the setting now re-colours every editor from the editor's existing view registry (the one completion and highlighting already use) instead of relying only on the new-view listener; views that were never tracked get their handlers attached first.
  - `IEditorFormatMapService` is now an optional import (`AllowDefault`): when it cannot be satisfied MEF silently discards the whole listener, which is exactly why the feature could do nothing without an error.
  - Transparency covers `TextView Background`, `Plain Text`, `SQL` and `TSQL`; every original value is backed up and restored when the feature is turned off.
  - The key logs are now Info level (`SQL-BACKGROUND attach/refresh/apply/key=`), which distinguishes “listener never ran / zero views / no format map / what the original colours were”.
- All 331 regression tests pass.

## 5.445

### 中文

- **修复 5.444 的“编辑区看不到背景图”**（设置里的预览正常，但编辑器一片纯白）。
  - 原因：只把图片设成了编辑器视图的背景刷，而 SSMS 的文本渲染器会用格式映射（Editor Format Map）里的**不透明背景色**把每一行盖掉——`TextView Background` 与 `Plain Text` 两个格式项都是不透明的，所以刷子再对也看不到。
  - 修法：开启时把这两个格式项的背景临时改为透明（原值完整备份），并把图片刷同时设到视图与它的 WPF 控件上；关闭时按备份原样恢复，主题颜色不会残。
  - 另外监听主题变更与布局变化：主题重设了透明值或当前背景刷被覆写时自动重新应用；新增 `SQL-BACKGROUND ...` 日志，方便定位（日志目录 `D:\SQLTOOLDATA\MSSQLToolLog`）。
- 回归测试 331 项全过。

### English

- **Fixed “editor shows no background picture” in 5.444** (the settings preview worked, the editor stayed plain white).
  - Cause: the picture was only set as the text view's background brush, but the SSMS text renderer repaints every line with the **opaque** background colour from the editor format map (`TextView Background` and `Plain Text`), hiding it.
  - Fix: while enabled, both format entries get a transparent background (originals are backed up verbatim) and the picture brush is set on both the view and its WPF control; disabling restores the backed-up theme values exactly.
  - Theme changes and layout passes are watched: if the theme resets the transparency or the brush is overwritten, the picture is applied again. New `SQL-BACKGROUND ...` log lines help diagnose (`D:\SQLTOOLDATA\MSSQLToolLog`).
- All 331 regression tests pass.

## 5.444

### 中文

- **新增“编辑器背景图”**（设置 → 编辑器）：可给 SQL 编辑区铺一张图片，支持 **4 张内置 CC0 图片**（雾中森林 / 夜空星野 / 云雾山脊 / 沙漠沙丘）和自定义本地图片。
  - 可调项：不透明度 5%–60%（默认 16%）、填充方式（等比铺满 / 拉伸铺满 / 等比适应 / 平铺）、暗色主题额外压暗。设置页带实时预览，保存后立即应用到所有已打开的 SQL 编辑器。
  - **默认关闭**：勾选“在 SQL 编辑器窗口中显示背景图”后生效；点“恢复主题背景”一处回到原生外观，不会残留半透明底色。
  - 实现方式是把图片设为编辑器视图的背景刷，**不插入任何装饰层**：滚动、缩放、拖窗口都不重绘，不参与排版，对输入延迟无影响；图片只解码一份（最大 2560 px）并缓存。
  - 主题切换、重开标签页、其他扩展改写背景时，会重新记住真实主题底色并重新上色，所以开关能随时干净地撤销；图片文件不存在或解不开时只记日志并退回主题背景，不会弄坏编辑器。
  - 图片只画在代码后面，查询文本、语法颜色、选中、光标均不受影响，也不会进入复制/导出/打印内容。
- **内置图片版权**：均来自 Wikimedia Commons 的 CC0（公有领域）照片（作者分别为 Paul Gilmore、Paul Volkmer、Cosmic Timetraveler、Joe Mania），已在安装包内附 `Resources/backgrounds/NOTICE.txt` 标注来源与许可。
- 回归测试 331 项全过（新增 6 项：内置图/自定义路径解析、图片确实被打包、不透明度夹取与暗色压暗、设置归一化、填充模式映射）。

### English

- **New "Editor background picture"** (Settings → Editor): paint a photo behind the SQL code, with **four bundled CC0 pictures** (mist forest, night sky, mountain ridge, desert dunes) or your own file.
  - Options: opacity 5%–60% (default 16%), fill mode (crop / stretch / fit / tile), and extra dimming on dark themes. The settings page previews the result and applies it to every open SQL editor immediately.
  - **Off by default**; tick “Show a background image in SQL editor windows” to turn it on. “Use the theme background” restores the stock appearance exactly.
  - Implemented by replacing the text view's background brush rather than adding an adornment: scrolling, zooming and resizing never repaint the document and typing latency is untouched. Each picture is decoded once (capped at 2560 px) and cached.
  - Theme switches, reopened tabs and other extensions overwriting the background are detected: the real theme colour is remembered and the picture reapplied, so the switch stays reversible. A missing or unreadable file only logs and falls back to the theme colour.
  - The picture sits behind the code only: text, syntax colours, selection and the caret are unchanged, and it never reaches copied, exported or printed content.
- **Bundled picture licences**: all four are CC0 (public domain) photographs from Wikimedia Commons (Paul Gilmore, Paul Volkmer, Cosmic Timetraveler, Joe Mania); sources are recorded in `Resources/backgrounds/NOTICE.txt` inside the package.
- All 331 regression tests pass (6 new).

## 5.443

### 中文

- **修复列统计整张表几乎全空（只有“空值%”有数字）**：
  - 根本原因：`GridColumnInsight` 里列名、类型、非空、空值、去重、最大长度、最小/最大/平均/合计/最常见全是**字段**，而 WPF 绑定只认**属性**。唯一写成属性的 `NullPercent` 正常显示，其余静默空白——不报错，所以一直没被发现。全部改为属性，并新增回归测试逐个校验统计列绑定路径确实是可读属性。
  - 顺带修正统计口径：文本列不再显示“合计/平均”（`DjGuid`、编号这类列求和无意义）；最小/最大按列的真实类型取值（数字按数值、日期按时间、文本按字符串）；“空值%”带 `%`，“最常见”直接显示“值 (次数)”。
- **打开不再卡在“正在分析”**：旧实现先把统计算完才把行绑定到表格，所以宽结果集打开时很长时间是一片空白。现在先立刻显示数据行，统计在后台算完再填进统计表。
- **统计更快、切换不再重算**：按列类型走快路径（数字/日期不做字符串解析，值直接用对象做去重键）；扫描上限调整为 20,000 行并明确标注是否抽样；切换结果集或刷新会取消上一次扫描；同一个结果集再次查看直接复用缓存。
- **列多的时候更好用**：
  - 统计表上方新增“查找列”输入框，按列名、类型或序号过滤；列头可点击排序。
  - 点统计表的某一行，上方数据表自动滚动到对应列；在数据表里选中某个单元格，统计表会自动定位到那一行。双击统计行查看该列前 10 个值及占比。
  - 统计行高压缩为 22px，一屏能看到更多列；表格数字列右对齐。
  - 行筛选输入加了 250ms 防抖（连着打字不再每敲一个字符就重建一次行集）；筛选生效时会明确提示“统计针对全部行，不受筛选影响”。
- 空结果集也会列出列名与非空/空值 0 的统计行，而不是空白面板。
- 回归测试 325 项全过（新增 5 项：绑定属性校验、文本列不求和、数字/日期范围、显示文本、取消支持）。

### English

- **Fixed the column statistics table rendering almost entirely blank** (only the null-percentage column had numbers).
  - Root cause: every statistic on `GridColumnInsight` was a **field**, and WPF bindings only see **properties**. `NullPercent` was the single property, so it was the single column that worked — silently, with no error.
  - All values are now properties, and a regression test walks the statistics column definitions and fails if any binding path is not a readable property.
  - Semantics were tightened too: text columns no longer report Sum/Average (summing ids like `DjGuid` is meaningless), Min/Max follow the real column type (numeric, temporal or text), the null column shows a `%` suffix, and “most common” reads `value (count)`.
- **Opening the panel no longer waits for the statistics**: the grid used to be bound only after the scan finished, so a wide result set stared at a blank panel. Rows now appear immediately while the numbers are computed in the background.
- **Faster, and no recomputation**: type-aware fast paths (numeric/date columns skip string parsing; values are keyed by object for distinct counts), a documented 20,000-row sample cap, cancellation of an in-flight scan when the result set changes, and a small cache so revisiting the same result set is instant.
- **Usable with many columns**: a “filter columns” box (name, type or ordinal), sortable statistics, click a statistics row to scroll the grid to that column, selecting a cell highlights its statistics row, double-click for the top 10 values with their share, compact 22px rows and right-aligned numbers.
- Row filtering is debounced (250 ms) and the status line states when statistics still describe all rows rather than the filtered view.
- An empty result set now lists its columns with zero counts instead of an empty panel.
- All 325 regression tests pass (5 new).

## 5.442

### 中文

- **修复「关于」页赞赏码不显示**：只有「赞赏支持」标题，下面一片空白。
  - 根本原因不是多语言：`MSSQLTool.csproj` 里赞赏码的引用路径是乱码 `..\pics\璧炶祻鐮?png`（GBK 字节被当成 UTF-8），文件对不上，`Resources/appreciation.png` 从未被嵌进程序集，`Image` 控件运行时找不到资源只会静默空白，不会报错。
  - 修法：图片改为项目内的 ASCII 路径 `Resources\appreciation.png`，不再引用带中文名的 `..\pics` 外部文件；同时清理了过期的 `obj` 资源缓存（否则增量构建会继续用旧的 `.g.resources`）。
  - 新增回归测试：直接检查程序集内 `resources/appreciation.png` 是否真实存在，以后再出现路径写错或图片没打包会在测试里报错，而不是靠用户发现。
- 回归测试 320 项全过。

### English

- **Fixed the missing appreciation QR code on the About page** (the heading was there, the image area was blank).
  - The cause was not localization: the `MSSQLTool.csproj` entry pointed at `..\pics\<mojibake>.png`, so the file never matched, `Resources/appreciation.png` was never embedded, and a WPF `Image` with an unresolvable pack URI simply renders nothing.
  - The image now lives inside the project at an ASCII path (`Resources\appreciation.png`); the stale `obj` resource cache was cleared so the incremental build actually regenerates `.g.resources`.
  - New regression test asserts that `resources/appreciation.png` really exists in the assembly, so a broken path fails the build's test run instead of silently blanking the window.
- All 320 regression tests pass.

## 5.441

### 中文

- **查询历史改为“每个查询一行”（SQL Prompt 的列表方式）**：
  - 同一文件/同一个标签页的多次保存不再各自占一行，而是一个查询一行；右侧新增**版本列表**，点哪一条就预览哪一版，插入/复制/删除都作用于选中的那个版本。
  - 分组键：已保存的文件按完整路径（所以另存前后不会混在一起）；未保存的标签页按“标题 + 服务器 + 数据库”。
  - 删除：工具栏「删除版本」只删当前版本，「删除该查询」清掉这个查询的全部版本。
- **修“导入的数据只到 6/7/8 月”**：旧实现列表只加载最新 1000 条文本，17,000 多条历史里较早的部分（包括 2024 年导入的记录）根本进不了列表。现在按查询分组后全量展示，2024-08 起的记录都能看到。
- **过滤器从“只有搜索框”扩到多维**：时间（全部 / 今天 / 近 7 天 / 近 30 天 / 近 90 天 / 今年）、服务器、数据库、来源（全部 / 编辑器快照 / SQL Prompt 导入），与关键词搜索叠加生效；服务器与数据库下拉项来自库里实际存在的值。
- 升级时会自动新增 `GroupKey` 列并回填已有记录，旧数据直接生效，不需要重新导入（重新导入也会顺带修正分组的元数据）。
- 编辑器快照现在会记录服务器/数据库，连接列与连接过滤不再为空。
- 回归测试 319 项全过（新增：分组键生成、过滤条件拼接）。

### English

- **The query history now lists one row per query (SQL Prompt's model)**:
  - Saving the same file/tab repeatedly no longer produces one row per save. A versions list appears on the right: pick a version to preview it, and Insert/Copy/Delete act on that version.
  - Grouping key: saved files use the full path (so a save-as does not merge two files); unsaved tabs use title + server + database.
  - Delete version removes one snapshot; Delete query removes every version of that query.
- **Fixed “the import only goes back to June/July/August”**: the list used to load only the newest 1000 text snapshots, so older entries (including the 2024 imports) never reached the list. Grouping made the whole history visible again.
- **Filters went from a single search box to real criteria**: time range (all / today / 7 / 30 / 90 days / this year), server, database and source (all / editor snapshots / SQL Prompt imports), combined with the keyword search. The server and database pickers list the values that exist in the history.
- The upgrade adds a `GroupKey` column and backfills it, so existing rows group immediately; no re-import is required (a re-import also refreshes the grouping metadata).
- Editor snapshots now record server/database, so the connection column and filter are populated.
- All 319 regression tests pass (new: group key derivation, filter WHERE construction).

## 5.440

### 中文

- **修关联高亮只亮一处**（光标放在 `DemoQueue` 上只亮 1 个）：
  - 根本原因：分析器只把 `FROM` 后面的表识别为对象。而 `CREATE QUEUE dbo.DemoQueue`、`ALTER QUEUE dbo.DemoQueue`、`CREATE SERVICE … ON QUEUE dbo.DemoQueue`、`RECEIVE … FROM dbo.DemoQueue` 里的队列名都不是表引用，语义阶段解析不到，只能回退到“同语句同名匹配”；每条 DDL 又被 `GO` 分开成单独语句，所以只亮了光标所在的那一个。
  - 现在收集脚本里所有**对象名位置**（`SchemaObjectName` 全家族：表/视图/存储过程/队列/类型等，外加 Service Broker 里是普通标识符的服务名、契约名、消息类型名、`SEND`/`BEGIN DIALOG` 的引用），光标在任一对象名上时，**全脚本**同名对象一起高亮。
  - 跨同语句的表名同样生效：`SELECT * FROM dbo.WDDDH` 出现多次时全部高亮。
  - 限定名防误亮：`dbo.T` 不会亮 `sales.T`；未限定的同名引用视为同一对象。
  - 列名与别名仍按语句/查询块限定：不同查询里的同名列不是同一个列，不会跨语句乱亮。
- 回归测试 317 项全过（新增：队列名 4 处全亮、跨语句表名、同名单模式隔离、带方括号对象名、消息类型/契约引用）。

### English

- **Fixed related highlighting lighting up a single occurrence** (caret on `DemoQueue` highlighted one token):
  - Root cause: only tables after `FROM` were treated as objects. A queue name in `CREATE QUEUE dbo.DemoQueue`, `ALTER QUEUE dbo.DemoQueue`, `CREATE SERVICE … ON QUEUE dbo.DemoQueue` or `RECEIVE … FROM dbo.DemoQueue` is never a table reference, so the semantic pass could not resolve it and the fallback matched names **inside the caret statement** only — and every DDL batch is its own statement because of `GO`.
  - The analyzer now records every **object-name position** in the script (`SchemaObjectName` family: tables, views, procedures, queues, types, plus the plain-identifier service, contract and message-type names, and `SEND` / `BEGIN DIALOG` references). A caret on any of them highlights that object everywhere in the script.
  - Table names now behave the same way across statements.
  - Qualified names stay separated (`dbo.T` never lights `sales.T`); unqualified references are treated as the same object.
  - Columns and aliases stay scoped to their statement/query block, so unrelated same-named columns never light up together.
- All 317 regression tests pass (new: four queue references, tables across statements, schema isolation, bracketed names, message type/contract references).

## 5.439

### 中文

- **修导入 SQL Prompt 历史报错**：`导入失败：Could not prepare a query history statement: no such column: v.QueryVersionPropertiesIdentity`。
  - 不再把外键列写死：先 `PRAGMA table_info` 读取实际列名。SQL Prompt 10 的真实列是 `QueryVersions.QueryPropertiesIdentity`，旧版本才是 `QueryVersionPropertiesIdentity`，两种都能识别。
  - 文本列、时间列、`QueryVersionProperties` 的 `Name/Path/Server/Database` 也改为按存在性选取；缺表或缺列时仍能导入正文，只是少了标题与连接信息，而不是整个导入失败。
  - `DateSaved` 修正：SQL Prompt 10 存的是 Windows FILETIME（1601 起的 100ns），之前被当成 unix 毫秒而溢出，导致导入条目的时间全部变成“导入时刻”；现在能还原真实保存时间。
- 已用本机真实库（20,617 条版本、毫秒级验证）核对：语句可执行、标题与服务器/库名均能取到。
- 回归测试 312 项全过（新增：真实列名生成语句、旧列名兼容、无属性表降级、FILETIME 时间解析）。

### English

- **Fixed the SQL Prompt history import failure** (`no such column: v.QueryVersionPropertiesIdentity`).
  - The join column is no longer hard-coded: the importer reads the actual schema with `PRAGMA table_info` first. SQL Prompt 10 names it `QueryVersions.QueryPropertiesIdentity`, older builds use `QueryVersionPropertiesIdentity`; both are recognised.
  - The text column, the timestamp column and `QueryVersionProperties.Name/Path/Server/Database` are resolved the same way, so a missing table or column costs the tab name and connection info instead of failing the whole import.
  - `DateSaved` is parsed correctly now: SQL Prompt 10 stores a Windows FILETIME (100ns since 1601), which used to be read as unix milliseconds and overflowed, stamping every imported row with the import time.
- Verified against the real local database (20,617 versions): the statement runs and returns titles plus server/database for every row.
- All 312 regression tests pass (new: statement built from the real column names, older names still work, graceful fallback without the properties table, FILETIME parsing).

## 5.438

### 中文

- **JOIN 补全改成两段式**：
  - 打表名时（`JOIN ZZJG` 这类未完成的名字）列表里**只有表名**，Tab 只把名字补成 `dbo.ZZJGB`。
  - 表名补完、光标停在名字后面时，列表自动弹出 **ON 选项**，Tab/回车接受就把条件补上，Esc 跳过。
- **每个关联键单独一项**：复合外键不再合成一个用 `AND` 连起来的长条件，而是 `ON A.DjGuid = ZZJGB.DjGuid`、`ON A.DjLsh = ZZJGB.DjLsh` 这样一倪一项，DjGuid 只是其中之一；同名列推断也按列拆开。
- 第二段插入的只有 ` ON …` 本身，不会重复插入表名。
- 回归测试 310 项全部通过（新增：部分名称不出 ON、完整名称才出 ON、复合外键按键拆分、第二段只附加 ON 子句）。

### English

- **JOIN completion is two-stage now**:
  - While the table name is still incomplete (`JOIN ZZJG`) the list contains the name only, so Tab just expands it to `dbo.ZZJGB`.
  - Once the name is complete and the caret sits right after it, the popup reopens with the **ON options**; accepting one appends the condition, Esc skips it.
- **One entry per join key**: a composite foreign key no longer collapses into a single `AND`-ed condition — `ON A.DjGuid = ZZJGB.DjGuid` and `ON A.DjLsh = ZZJGB.DjLsh` are separate choices, and same-name column matches are split the same way.
- The second stage inserts only ` ON …`, never the table name again.
- All 310 regression tests pass (new: no ON while the name is partial, ON once complete, composite key split per key, second stage appends only the clause).

## 5.437

### 中文

- **JOIN 补全默认只插表名**：之前表名后面的智能补全会把 `ON A.DjGuid = ZZJGB.DjGuid` 一并插进去，因为「同名列关联 / 外键关联」的补全项评分比纯表名高，Tab 自然选中了它。现在纯表名选项永远排在前面（115 分），补全按前缀插入的只是你正在打的这个名字。
- **带 ON 的版本仍然可用**，但改成显式条目：列表里直接显示 `dbo.ZZJGB ON A.DjGuid = ZZJGB.DjGuid`，描述为 `join with ON (same-name column)` / `join with ON (foreign key)`，用下方向键选中才插。
- 回归测试 308 项全部通过（新增：默认项不含 ON、ON 项仍存在且评分更低、ON 项标题带条件）。

### English

- **JOIN completion now inserts only the table name.** The smart-join entries (`same-name column` / `foreign key`) used to outrank the plain table, so Tab pulled in `ON A.DjGuid = ZZJGB.DjGuid` unasked. The plain entry always ranks first now (score 115), so completion inserts exactly the name being typed.
- **The ON variants are still available** but are explicit entries: they display as `dbo.ZZJGB ON A.DjGuid = ZZJGB.DjGuid` with the description `join with ON (same-name column)` / `join with ON (foreign key)`, and only insert when picked with the arrow keys.
- All 308 regression tests pass (new: default entry has no ON, ON entries still exist and rank lower, ON entries show their condition).

## 5.436

### 中文

- **修复「重复别名」误报**：三个 `SELECT … FROM …` 之间没写分号时，语句索引会把整批当成一段，作用域树把所有 FROM 的来源收进同一个列表，于是 `FROM WDDDH` 和另一个语句里的 `FROM dbo.WDDDH` 因为共用隐式别名而被误判为重复别名。现在作用域树会给每个顶层查询块编号（括号外每遇到一个 `SELECT`、包括 UNION 的每个分支），重复别名只在**同一块、同一层括号**内比较。
- 真实的重复仍会报：`FROM dbo.A x JOIN dbo.B x` 依旧告警；`UNION ALL` 两个分支各自用 `x` 不再误报。
- 回归测试 307 项全部通过（新增：跨语句不误报、UNION 分支不误报、同一 FROM 列表仍告警）。

### English

- **Fixed the false "duplicate alias" warning**: when several `SELECT … FROM …` statements are written without semicolons the statement index treats them as one range, and the scope tree collected every FROM source into one list — so `FROM WDDDH` and `FROM dbo.WDDDH` in another statement shared the implicit alias and were reported as duplicates. The scope tree now numbers top-level query blocks (every `SELECT` outside parentheses, including each UNION branch) and the rule only compares sources in the same block at the same nesting depth.
- Real duplicates still warn (`FROM dbo.A x JOIN dbo.B x`), and two UNION branches that both use `x` no longer do.
- All 307 regression tests pass (three new assertions: across statements, UNION branches, same FROM list).

## 5.435

### 中文

- **修复变量赋值目标不高亮**：`DECLARE @CODE INT` 与 `SELECT @CODE = DjLsh` 是同一个变量，但在 `SELECT @CODE = …` 左侧的赋值目标上停光标时，只有上面的 `DECLARE` 会被高亮，光标所在处反而不亮。原因：分析器把「SELECT 列表里的赋值目标」也当成了非变量引用排除了。现在只排除 `EXEC 过程名 @p = @v` 里的参数名（那确实不是变量），赋值目标和其它引用一起高亮。
- `EXEC @rc = 过程` 的返回码变量仍然与声明一起高亮，`EXEC dbo.Proc @P = @v` 的 `@P` 仍然不高亮。
- 重命名本来就按这个语义工作（赋值目标一起改，参数名不改），现在高亮与重命名一致了。
- 回归测试 306 项全部通过（新增 2 项：赋值目标高亮、返回码变量高亮）。

### English

- **Fixed variable assignment targets not highlighting**: `DECLARE @CODE INT` and `SELECT @CODE = DjLsh` are the same variable, but placing the caret on the assignment target in the SELECT list highlighted only the DECLARE and nothing under the caret. The analyzer was excluding SELECT-list assignment targets as if they were not variable references. Only `EXEC proc @p = @v` parameter names are excluded now.
- `EXEC @rc = proc` keeps highlighting the return-code variable, and `@P` in `EXEC dbo.Proc @P = @v` stays excluded.
- Rename already followed these semantics (assignment targets are renamed, parameter names are not), so highlighting and rename now agree.
- All 306 regression tests pass (two added: assignment target, return-code variable).

## 5.434

### 中文

- **右键菜单接通了**：真正的 T-SQL 编辑器右键菜单在 DTE 里叫 `SQL Files Editor Context` / `SQL 文件编辑器上下文`（弹出菜单，24 项）。前几版一直在给 `Code Window` 菜单和 `SQLEditor` 工具条加项：前者是 VS 通用编辑器菜单（剪切、Git、快速操作、Copilot），后者是工具条——所以看不到，还多出一条误加的工具条。现在只认这一个菜单，其余一律不碰。
- **工具栏干净了**：不再向任何非弹出菜单注入，误加的 `SQLEditor` 工具条条目会随本次升级消失。
- **菜单项位置保留在末尾**（在「查询选项」之后），避免与 SSMS 自带条目抢位。
- 诊断日志保留：`CTX-MENU hit bar='SQL 文件编辑器上下文' name='SQL Files Editor Context' type=msoBarTypePopup`，以后排查一眼定位。
- 回归测试 305 项全部通过。

### English

- **The context menu now works.** The T-SQL editor's real right-click menu is the DTE popup `SQL Files Editor Context` (24 items). Earlier builds kept adding to the `Code Window` menu and the `SQLEditor` bar: the first is Visual Studio's generic editor menu (Cut, Git, Quick Actions, Copilot), the second is a toolbar, which is why nothing appeared and a stray toolbar showed up. Only this popup is targeted now.
- **No more toolbar pollution**: nothing is injected into non-popup bars, so the accidental `SQLEditor` toolbar entries disappear with this build.
- **Items stay at the end of the menu** (after 查询选项) rather than competing with the built-in entries.
- Diagnostics kept: `CTX-MENU hit bar='SQL 文件编辑器上下文' name='SQL Files Editor Context' type=msoBarTypePopup` pins the menu down next time.
- All 305 regression tests pass.

## 5.433

### 中文

- **确认了前两次失败的原因**：日志显示注入确实成功（`代码窗口 before=29 after=32`，三项 `ok=True`），但 DTE 里这个「代码窗口」菜单装的是 VS 通用编辑项（剪切、Git、快速操作、Copilot 等），不是 SSMS SQL 编辑器实际弹出的那个菜单。所以无论 VSCT 挂载还是 DTE 注入，都碰不到真正的菜单。
- **新增 WPF 层菜单钩子**：在编辑器 `VisualElement` 上挂 `ContextMenuOpening`，菜单真正打开时检查是否含「查询选项 / 显示估计的执行计划 / IntelliSense / 包括实际执行计划」这类 T-SQL 专属条目；是就在末尾插入「修复光标处的问题 / 重命名 / 为对象名添加架构」，重复添加会被跳过。
- **DTE 扫描扩大到所有命令栏类型**，不再只看 popup，并按 T-SQL 专属条目标题识别；命中时记录 `CTX-MENU hit bar=… type=… count=…`，持续 5 分钟。
- **诊断日志**：`CTX-WPF` 记录钩子是看到了 WPF 菜单、还是没有 WPF 菜单（后者说明菜单是原生的，只能走命令表）。
- 回归测试 305 项全部通过。

### English

- **Root cause of the two earlier failures confirmed**: injection did work (`代码窗口 before=29 after=32`, all three `ok=True`), but that DTE "Code Window" popup holds generic Visual Studio editor items (Cut, Git, Quick Actions, Copilot) rather than the menu the SSMS SQL editor actually shows. Neither command-table placement nor DTE injection reaches the real menu.
- **New WPF-level menu hook**: `ContextMenuOpening` is attached to the editor `VisualElement`; when a menu opens it checks for T-SQL-only items ("查询选项", "显示估计的执行计划", "IntelliSense", "包括实际执行计划") and appends the three commands, skipping duplicates.
- **DTE scanning widened to every command-bar type**, no longer only popups, and recognizes the menu by T-SQL-specific captions; hits log `CTX-MENU hit bar=… type=… count=…` for five minutes.
- **Diagnostics**: `CTX-WPF` records whether a WPF context menu was seen at all; no such line means the menu is native and only a command-table route can work.
- All 305 regression tests pass.

## 5.432

### 中文

- **右键菜单定位方式修正**：日志显示启动时有 410 个弹出菜单，但一个都不含「执行」项——SSMS 的右键菜单是**显示时才填充**的，按标题匹配永远不会命中。改为直接定位名称是 `Code Window` / `代码窗口` 的弹出菜单（SQL 编辑器用的就是它），再往里注入三项。
- **注入会持续补齐**：注入成功后 2 分钟内每 10 秒幂等检查一次，若 SSMS 重建了菜单就重新加上，已有条目按标题去重。
- **新增诊断**：第一次看到菜单内容时记录 `CTX-MENU composition ... count=N captions: ...`，可以直接看出这个菜单到底是不是 SQL 编辑器右键菜单、我们的三项在不在里面。
- 回归测试 305 项全部通过。

### English

- **Correct popup targeting**: the log showed 410 popups at startup and none containing the "执行" item, because SSMS fills the context menu only when it is shown. Matching by caption could never hit. The installer now targets the popup named `Code Window` / `代码窗口` — the one the SQL editor shows — and injects the three commands there.
- **Idempotent reinforcement**: for two minutes after a successful injection it re-checks every ten seconds and re-adds the items if SSMS rebuilt the menu; existing captions are skipped.
- **New diagnostics**: the first non-empty pass logs `CTX-MENU composition ... count=N captions: ...`, showing whether this really is the SQL editor menu and whether the three items survived.
- All 305 regression tests pass.

## 5.431

### 中文

- **右键菜单改用运行时注入**：SSMS 22 的 SQL 编辑器会自己构建右键菜单，其他包在 VSCT 里声明的挂载不会生效（标准的 `IDM_VS_CTXT_CODEWIN` 与 SQLEditors 的 `IDM_SQLWB_SQLSCRIPT_CONTEXT` 都验证过）。启动时改为枚举命令栏，找到包含「执行」命令的查询编辑器弹出菜单，再通过 proffer 服务把「修复光标处的问题 / 重命名 / 为对象名添加架构」三项加进去。
- **注入有重试与日志**：弹出菜单是懒创建的，启动时看不到会间隔 3 秒重试（最多 5 次）；日志标记 `CTX-MENU`，会记录发现的弹出菜单数量、是否匹配到查询编辑器菜单，以及每一项是否添加成功。若日志显示 `popups=0`，说明命令栏集合不包含弹出菜单，需要改用编辑器消息钩子方案。
- 已确认工具栏的「执行历史 / 查询历史」各一项，标题修复生效。
- 回归测试 305 项全部通过。

### English

- **Context menu via runtime injection**: SSMS 22 builds the SQL editor context menu itself, so placements from another package's VSCT never appear (both the standard `IDM_VS_CTXT_CODEWIN` and SQLEditors' `IDM_SQLWB_SQLSCRIPT_CONTEXT` were tried). At startup the extension now walks the command bars, finds the query-editor popup that contains the query "执行" command and adds the three refactoring commands through the proffer service.
- **Retries and diagnostics**: popups are created lazily, so a startup pass that finds none retries every 3 seconds (up to 5 times). The `CTX-MENU` log records how many popups were found, whether the query editor menu matched, and whether each item was added. `popups=0` means the command-bar collection does not expose popups and an editor message hook is required instead.
- The toolbar now shows exactly one 执行历史 and one 查询历史.
- All 305 regression tests pass.

## 5.430

### 中文

- **工具下拉框重复「查询历史」修复**：菜单源里只有一个「查询历史」（命令 4165）和一个「执行历史」（命令 4144），重复来自运行时的标题处理。现在会在每个命令栏里检测重复的「查询历史」条目，用命令提示（仅查询文本命令带有提示）区分两项，把执行历史那一项恢复为「执行历史」。
- **新增诊断日志**：启动时会把两个历史命令的 `id / caption / tooltip` 写入 `HISTORY-MENU` 日志，万一菜单仍不正确，可以直接定位是哪个控制被改写。
- 回归测试 305 项全部通过。

### English

- **Duplicate "Query History" in the tools menu**: the command table contains exactly one "Query History" (4165) and one "Execution History" (4144); the duplicate was produced by runtime caption handling. Each command bar is now checked for repeated "Query History" captions, the query-text entry is identified by its tooltip (the only one that has one), and the execution-history entry gets its own caption back.
- **Diagnostics**: startup now logs `id / caption / tooltip` for both history controls as `HISTORY-MENU`, so a remaining mismatch can be traced to the exact control.
- All 305 regression tests pass.

## 5.429

### 中文

- **右键菜单修复（根因）**：SSMS 的 SQL 编辑器右键菜单不在 VS 标准的 `guidSHLMainMenu:IDM_VS_CTXT_CODEWIN`，而在 SQLEditors 包 `{4058755A-8FBE-41C7-BC99-3DBF5C74BA62}` 的 `IDM_SQLWB_SQLSCRIPT_CONTEXT`（0x50）。「修复光标处的问题 / 重命名 / 为对象名添加架构」三个菜单项已改挂到该菜单。
- **变量重命名不再改坏脚本**：
  - `EXEC dbo.Proc @CODE = @CODE` 只改右侧变量，左侧参数名保持不变；`EXEC @rc = dbo.Proc` 的返回码变量仍会正常改名。
  - 应用改动的方式由「绝对字符偏移」改为「行列定位」，避免 DTE 与脚本文本的换行/偏移口径不一致时越改越偏，把 `VARCHAR(50)` 变成 `VAR@CODE150)` 这类损坏。
  - 同一位置只应用一次，防止重叠 span 重复插入。
- **新增回归**：EXEC 参数名不变、`@CODE` 与 `@CODE1` 全名边界、偏移↔行列往返一致；当前 305 项全部通过。

### English

- **Right-click menu root cause**: SSMS's SQL editor context menu is not the VS-standard `guidSHLMainMenu:IDM_VS_CTXT_CODEWIN`; it is `IDM_SQLWB_SQLSCRIPT_CONTEXT` (0x50) of the SQLEditors package `{4058755A-8FBE-41C7-BC99-3DBF5C74BA62}`. The three context items (fix at caret, rename, qualify names) now target that menu.
- **Variable rename no longer corrupts scripts**:
  - `EXEC dbo.Proc @CODE = @CODE` renames only the variable on the right; the parameter name on the left is preserved, while `EXEC @rc = dbo.Proc` still renames the return-code variable.
  - Changes are applied through line/column positions instead of absolute character offsets, so a different newline/offset accounting between DTE and the script text can no longer shift edits and turn `VARCHAR(50)` into `VAR@CODE150)`.
  - Each offset is applied once, so overlapping spans cannot duplicate text.
- **New regression tests**: EXEC parameter names stay intact, `@CODE` vs `@CODE1` whole-name matching, offset↔line/column round-trip; all 305 tests pass.

## 5.428

### 中文

- **新增内联预览（ghost text）**：补全列表打开时，在光标处用灰色斜体显示按 Tab 将要补全的剩余部分，例如输入 `SEL` 后光标后直接显示 `ECT`。只预览第一行、最长 60 字符，纯空白不显示。
- **预览与弹出列表同步消失**：方向键/鼠标改变选中项、Esc、提交、失焦或结果为空时立即清除，不会留下“幽灵文字”。
- **使用编辑器装饰层绘制**：预览随滚动、缩放、换行自然移动，不额外创建悬浮窗口，也不改动文档；Tab 仍走原有插入链路。
- **可选开关**：设置 → 补全中新增“在光标处预览最佳匹配（Tab 接受）”，默认开启，可随时关闭。
- **新增守卫测试**：内联预览的后缀计算（大小写、完全匹配、多行、超长、纯空白）纳入回归，当前 302 项全部通过。

### English

- **Inline completion preview (ghost text)**: while the completion list is open, the part Tab would add is drawn at the caret in dim italic text (`SEL` shows `ECT` right after the cursor). Only the first line is previewed, at most 60 characters, and blank-only suffixes are skipped.
- **Preview disappears with the list**: arrow/mouse navigation, Escape, commit, focus loss or an empty result clear it immediately, so no stale ghost text is left behind.
- **Drawn as an editor adornment**: the preview scrolls, zooms and wraps with the text, creates no extra floating window and never edits the document; Tab still uses the existing insert path.
- **Optional toggle**: Settings → Completion adds "Preview the best match inline (Tab accepts)", on by default.
- **New guard test**: the suffix rule (case-insensitive prefix, exact match, multi-line, over-long, whitespace-only) is covered by the regression suite; all 302 tests pass.

## 5.427

### 中文

- **逐键输入不再复制整篇文档**：补全会话缓存改由编辑器快照版本 + 缓冲区变更事件判定“这次只改了光标处的词”，命中后直接用缓存候选按新前缀过滤并刷新列表；遇到空格、标点、粘贴、撤销、多处编辑则立即回到完整分析，保证结果始终正确。
- **会话轮询降噪**：200ms 定时器只比较光标行列，不再读取整篇文本、不再每次重排弹窗；滚动/缩放/换行改由 `LayoutChanged` 事件驱动。
- **上下文有效性判断轻量化**：由“整篇文本逐字比较”改为“光标行列 + 快照版本号 + 连接/数据库作用域”。
- **latest-wins 分析调度**：排队中的旧请求拿到分析锁后先校验是否已被新按键取代，过期直接放弃，不再出现补全结果慢半拍的问题。
- **前缀过滤缓存**：同一前缀的过滤结果复用（上限 64 条），候选集变化时自动清空。
- **设置读取缓存**：补全设置进程内缓存 3 秒，保存设置即失效，避免每个按键反复读注册表 JSON。
- **弹窗布局最小化**：详情面板折叠状态、参数条高度只在内容或宽度真正变化时调整，减少每次按键的整窗布局。
- **诊断与相关高亮降噪**：一次分析只复制一次文本；`TagsChanged` 统一回 UI 线程触发；相关高亮把整篇复制移到后台线程，较大脚本输入不再卡顿。
- **测试**：301 项回归测试全部通过（1 项集成测试按需跳过）。

### English

- **No more full-document copies per keystroke**: the completion session cache is now validated by the editor snapshot version plus buffer change events; a keystroke that only edits the word at the caret reuses the cached candidates and re-filters them by the new prefix. Spaces, punctuation, paste, undo and multi-character edits fall back to full analysis immediately, so results stay correct.
- **Quieter session polling**: the 200 ms timer now compares caret line/column only — no document read and no popup re-layout; scrolling, zooming and wrapping are handled by `LayoutChanged`.
- **Cheaper staleness checks**: caret position + snapshot version + connection/database scope replace the previous character-by-character comparison of the whole document.
- **Latest-wins analysis scheduling**: a queued request re-checks whether a newer keystroke replaced it after acquiring the analysis gate and bails out instead of producing a stale list.
- **Prefix filter cache**: filtered results are reused per prefix (capped at 64 entries) and cleared whenever the candidate set changes.
- **Settings cache**: completion settings are cached for 3 seconds and invalidated on save, so no registry JSON read on every key.
- **Leaner popup layout**: the details panel and parameter strip only re-measure when the content or width actually changes.
- **Diagnostics and related highlights**: one text copy per analysis, `TagsChanged` always raised on the UI thread, and the related-highlight text copy moved off the UI thread.
- **Tests**: all 301 regression tests pass (1 integration test skipped by default).

## 5.426

### 中文

- **补全默认选中项修复**：输入 `ZDSKDH` 时，高亮不再停留在上一轮过滤时选中的 `ZDSKDB` 上，而是始终回到第一行（最匹配项）；只有当用户用方向键或鼠标主动选择过，刷新时才保留那一行。
- 新增回归：`Completion: typing keeps the best match selected`，覆盖「纯输入回到首行」「主动选择保留」「选中项被过滤后回到首行」三种情况。
- 回归测试 301 项全过。

### English

- **Default completion selection fixed**: typing `ZDSKDH` no longer leaves the highlight on the row that was selected during an earlier keystroke (`ZDSKDB`); the list returns to the first row, which is the best match. A row is preserved only when the user moved to it with the arrow keys or the mouse.
- New regression: `Completion: typing keeps the best match selected` (typing returns to row 0, explicit choice survives, filtered-out choice falls back to row 0).
- 301 regression tests pass.

### 安装 / Install

关闭所有 SSMS 窗口，双击 `MSSQLTool_SSMS22_5.426.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click `MSSQLTool_SSMS22_5.426.vsix`, then restart SSMS.

## 5.425

### 中文

- **消除重复的「查询历史」**：两个历史命令按命令 ID 强制设置标题（4144 = 执行历史，4165 = 查询历史），启动和切换语言时都会纠正，不再出现两个同名菜单。
- **结果集面板不再卡顿**：统计改为后台线程计算，界面先显示「正在分析结果集…」，算完再绑定；刷新加了 400ms 防抖，避免窗口打开时重复分析。
- **按钮不再被截断**：工具栏拆成「筛选」和「操作」两行，按钮改紧凑尺寸；默认窗口 1100×760，最小 760×460。
- **新增选中项统计**：选中单元格后实时显示选中单元格/行/列数、非空与空值数、去重数、最长字符数，以及数值列的合计、平均、最小、最大。
- 回归测试 300 项全过。

### English

- **Duplicate Query History entry removed**: both history commands get their caption enforced by command id (4144 = Execution History, 4165 = Query History) on startup and on language changes.
- **Results panel no longer stalls**: statistics run on a worker thread (the panel shows “Analyzing the result set…”), and refreshes are debounced so opening the window does not analyse twice.
- **Toolbar buttons always visible**: split into a filter row and an action row with compact buttons; default window 1100×760, minimum 760×460.
- **Selection statistics**: the panel now summarises the selected cells live (cells/rows/columns, non-null, empty, distinct, longest value, plus sum/average/min/max for numeric values).
- 300 regression tests pass.

### 安装 / Install

关闭所有 SSMS 窗口，双击 `MSSQLTool_SSMS22_5.425.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click `MSSQLTool_SSMS22_5.425.vsix`, then restart SSMS.

## 5.424

### 中文

- **结果集面板一次性补齐**：字段名显示真实列名（不再是 `Column_0`）；首次打开默认居中于当前显示器；支持点击表头排序（Shift 多列）、按列筛选（包含/等于/不等于/开头/结尾/大于/小于）、选择显示哪些列、冻结列、行号、双击单元格查看完整值与长度、多个结果集切换、CSV/JSON/INSERT 三种导出、复制统计、双击统计行查看取值分布；列宽沿用 SSMS 结果网格的宽度。
- **多语言补齐**：中文词典缺失项清零（新增 60 余条，含 Excel 导入、结构对比、结果面板文案）；新增菜单项的六语言映射（执行历史、代码分析、修复光标处的问题、重命名、为对象名添加架构）；非中文界面缺少翻译时统一回退英文，不再出现中文残留。
- **数据库更名**：执行历史库 `query-history.db` → `execution-history.db`；查询历史库 `query-text-history.db` → `query-history.db`。首次启动自动改名，并同时迁移 `-wal`/`-shm`，不会丢数据。
- **SQL Prompt 导入用真实表结构**：直接按 `Queries` / `QueryVersions` / `QueryVersionProperties` 读取（文本、保存时间、所在文件、服务器、数据库），不再靠猜表名与列名；进度按真实行数推进。
- 回归测试 298 项全过（1 项集成测试按环境跳过）。

### English

- **Results panel completed**: real field names instead of `Column_0`, centred on first open, header sorting (Shift for multi-column), per-column filters, column show/hide, frozen columns, row numbers, double-click cell viewer, multiple result sets, CSV/JSON/INSERT export, copy statistics and a top-values list; column widths follow the SSMS grid.
- **Localization completed**: no missing Chinese entries (60+ added for Excel import, schema compare and the results panel); the five additional languages now cover the new menu commands (Execution History, Code Analysis, Fix Problem at Caret, Rename, Qualify Object Names); every other language falls back to English instead of showing Chinese.
- **Database rename**: execution history `query-history.db` → `execution-history.db`; query history `query-text-history.db` → `query-history.db`, migrated automatically on first start including the `-wal`/`-shm` sidecars.
- **SQL Prompt import uses the real schema**: reads `Queries` / `QueryVersions` / `QueryVersionProperties` (text, save time, file, server, database) instead of guessing table and column names, so progress reflects the actual row count.
- 298 regression tests pass (one integration test skipped without a server).

### 安装 / Install

关闭所有 SSMS 窗口，双击 `MSSQLTool_SSMS22_5.424.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click `MSSQLTool_SSMS22_5.424.vsix`, then restart SSMS.

## 5.423

### 中文

- **变量高亮修复**：光标放到 `@变量` 上时，现在会把整批（`DECLARE` / `SET` / 所有引用）一起高亮，而不是只亮光标处一个 token。`EXEC @p = ...` 的参数名、`SELECT @v = ...` 的赋值目标仍会被排除。
- **变量重命名**：重命名变量时默认作用域改为整批（DECLARE/SET/引用一起改），表/列/别名仍默认只在当前语句内。
- **菜单更名**：原「查询历史」（执行历史）改为 **执行历史**；原「查询文本历史」改为 **查询历史**，与 SQL Prompt 的叫法一致（窗口标题、XAML 标题同步）。
- **SQL Prompt 导入不再卡死**：导入改到后台线程执行，弹出进度窗口实时显示「表进度 / 已读行数 / 已导入 / 已存在」，支持取消；整批导入包在一个事务里，速度更快，取消时回滚。
- 新增 2 项回归用例（变量高亮覆盖整批、变量重命名使用批范围），测试总数 296 → 298。
- 已知未完成：SSMS 22 的 SQL 编辑器**右键菜单仍未出现「重命名」**（VSCT 的 `IDM_VS_CTXT_CODEWIN` 父级在该宿主未生效）；这轮先保证命令可用：工具菜单或 `Ctrl+Shift+Alt+R`。

### English

- **Variable highlight fixed**: with the caret on `@name`, the whole batch (`DECLARE` / `SET` / every reference) is highlighted instead of only the token under the caret. `EXEC @p = ...` parameter names and `SELECT @v = ...` assignment targets stay excluded.
- **Variable rename now uses batch scope**: DECLARE/SET/references are renamed together; table, column and alias renames still default to the current statement.
- **Menu labels**: the execution history is now **Execution History**; the text history is now **Query History** (window caption and XAML title updated).
- **SQL Prompt import no longer looks hung**: it runs on a worker thread behind a progress window that shows tables, rows read, imported and already-existing counts, with a cancel button. Rows are written inside one transaction, and a cancel rolls back.
- 2 new regression tests (batch-wide variable highlight and variable rename), suite 296 → 298.
- Known gap: the SQL editor **right-click menu still does not show Rename** in this host (the `IDM_VS_CTXT_CODEWIN` parent is not picked up by SSMS 22); the command remains available from the Tools menu and `Ctrl+Shift+Alt+R`.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.423.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.423.vsix` asset below, then restart SSMS.

## 5.422

### 中文

- **新增重构功能（第一批）**
  - **重命名**：把光标放在表、列、别名或变量上，`Ctrl+Shift+Alt+R` 或右键「重命名」即可改名。默认**只在光标所在语句内**替换（UNION 各分支、相邻语句复用同名别名时不会误改），对话框里可以勾选「应用到整篇脚本」。替换会保留原有的 `[方括号]` / `"引号"` 写法；目标名称在作用域内已存在时会直接拒绝，避免产生歧义。整个改名是一次撤销单元，`Ctrl+Z` 一步回退。
  - **为对象名添加架构**：给 `FROM` / `JOIN` / `UPDATE` / `INTO` 后未带架构的表名补上架构前缀（默认 `dbo`，可改）。CTE、临时表（`#t`）、表变量（`@t`）、表值函数、派生表和已限定的名称都会跳过。
  - 两项都走共享的 `SqlDocumentModel`/分词器，不依赖整篇 ScriptDom 解析，大脚本下也不会卡住输入。
- **新增「查询文本历史」工具窗口**（工具菜单 → 查询文本历史）
  - 与原有的「查询历史」（执行历史：耗时/行数/结果）互补：这里保存的是**查询文本本身**，没执行过的语句也会保存。
  - 捕获时机：保存文档、关闭标签页、启动时抓取已保存的文档，以及每 2 分钟抓取一次当前标签页；文本哈希去重，相同内容只更新时间与命中次数。
  - SQLite 独立库 `query-text-history.db`（与执行历史共用 SSMS 自带的 SQLite 引擎、互不干扰），支持关键字搜索、预览、插入到当前查询、在新查询窗口打开、复制、删除。
  - **支持导入 SQL Prompt 历史**：选择 SQL Prompt 的 SQLite 文件即可；导入器会自检表结构（优先 `history`/`query`/`tab` 类表，识别文本列与时间列），已存在的文本自动跳过，并显示导入/跳过条数。
- **结果网格统计升级为独立分析窗口**
  - 窗口里直接显示结果网格的数据（可虚拟化、可筛选），下方是按列统计：类型、非空、空值、空值率、去重数、最小、最大、平均、合计、最常见值（含出现次数）。
  - 顶部汇总行数、列数、空值单元格比例与重复行数；统计默认基于前 5 万行并在超出时明确提示，保证大结果集不卡。
  - 支持把统计复制为制表符文本、把当前（筛选后的）结果导出为 CSV。
- 新增 13 项回归用例（重命名作用域/冲突/引号保留、架构限定的跳过规则、网格统计与行数上限、查询文本哈希与预览），测试总数 283 → 296，全部通过。

### English

- **Refactoring, first batch**
  - **Rename**: put the caret on a table, column, alias or variable and press `Ctrl+Shift+Alt+R` (or use the editor context menu). It renames inside the caret statement by default, so UNION branches and neighbouring statements that reuse the same alias stay untouched; the dialog offers "apply to the whole script". Original `[bracket]` / `"quote"` style is preserved, a name already used in the same scope is rejected instead of creating an ambiguity, and the whole rename is one undo step.
  - **Qualify object names**: prefixes bare table references after `FROM` / `JOIN` / `UPDATE` / `INTO` with a schema (default `dbo`). CTEs, temp tables, table variables, table-valued functions, derived tables and already-qualified names are skipped.
  - Both use the shared `SqlDocumentModel` tokenizer instead of a full ScriptDom parse, so large scripts stay responsive.
- **New "Query Text History" tool window** (Tools → Query Text History)
  - Complements the existing execution history: this stores the **query text itself**, including statements that were never executed.
  - Captured on save, on tab close, for already-saved documents at startup, and every two minutes for the active tab. Text is de-duplicated by hash; repeats only refresh the timestamp and hit count.
  - Stored in its own SQLite database (`query-text-history.db`, same engine that ships with SSMS, execution history untouched). Search, preview, insert into the current query, open in a new window, copy and delete are supported.
  - **SQL Prompt history import**: pick the SQL Prompt SQLite file; the importer inspects the schema (preferring `history`/`query`/`tab` style tables and detecting the text and timestamp columns), skips texts that already exist, and reports how many rows were imported.
- **Results statistics became a standalone analytics window**
  - Shows the result grid data (virtualized and filterable) together with per-column statistics: type, non-null, nulls, null %, distinct, min, max, average, sum and most common value (with its count).
  - The header summarizes rows, columns, empty-cell ratio and duplicate rows. Statistics use the first 50,000 rows and say so when the grid is larger, keeping big result sets responsive.
  - Copy the statistics as tab-separated text or export the current (filtered) result to CSV.
- 13 new regression tests (rename scope/conflicts/quoting, qualify skip rules, grid statistics and row cap, query text hash and preview). Suite 283 → 296, all passing.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.422.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.422.vsix` asset below, then restart SSMS.

## 5.421

### 中文

- **完整输入的名称排到第一位**：排序分是“候选基础分 + 匹配分 + 历史加权”。以前“首字母前缀”一律 40 分，`WDDDH` 与 `WDDDH_BAK` 同分，于是那个用过很多次的长名字会靠历史加权（最多 +12）跑到前面。现在加了一档精确匹配分 60，比任何前缀匹配高 20 分，高于历史加权的上限，所以完整输入的名字必定排在同类候选前面。
- 新增回归用例：`WDDDH` 必须排在 `WDDDH_BAK` 前面（测试总数 283）。

### English

- **A fully typed name now ranks first**: the order is "base score + match score + history boost". Prefix matches all scored 40, so `WDDDH` and `WDDDH_BAK` tied and a heavily used longer name could jump ahead on its history boost (capped at +12). An exact match now scores 60 — 20 points above any prefix match and above the history cap — so the fully typed name always wins within the same candidate class.
- New regression test: `WDDDH` must rank above `WDDDH_BAK` (suite 282 → 283).

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.421.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.421.vsix` asset below, then restart SSMS.

## 5.420

### 中文

- **新增「代码分析」设置页**：主设置窗口里可以直接开关四件事：启用代码分析、在编辑器里画波浪线（总开关）、提示级（Info）问题也画线、悬停在波浪线上显示规则说明；下面还有「管理规则与严重度…」按钮，直接打开规则列表（与 Code Analysis 工具窗口、工具-选项里的规则页共用同一份设置）。
- 波浪线总开关关掉后只在编辑器停下画线，Code Analysis 工具窗口仍然列出问题，分析照常进行。
- 规则页/设置页的所有开关都写入同一份 `AnalysisSettings`，保存后立即对下一个打开的编辑器生效。
- 新增回归用例：三个波浪线开关任一变化都必须改变 settings 签名（否则 tagger 不会重新加载），测试总数 282。

### English

- **New "Code Analysis" settings page**: the main settings window now exposes four switches — enable code analysis, underline findings in the editor (master switch), also underline Info-level findings, show the rule explanation on hover — plus a "Manage rules and severities..." button that opens the rule list (the same settings the Code Analysis tool window and the Tools > Options page use).
- With the master switch off, only the editor stops drawing; the Code Analysis tool window still lists findings and analysis keeps running.
- Every switch writes the same `AnalysisSettings`, and a save takes effect for the next editor immediately.
- New regression test: all three squiggle switches must change the settings signature (otherwise the tagger would not reload); suite 281 → 282.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.420.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.420.vsix` asset below, then restart SSMS.

## 5.419

### 中文

- **灰色背景调淡**：这个宿主绘制分类背景时不使用画刷的透明度（`#40808080` 被画成了实心 `#808080`），所以改为直接使用浅灰 `#E9E9E9` 实色。
- **高亮范围改为“光标所在的 SELECT 块”**：`CREATE VIEW ... UNION ALL ...` 在语法树里是**一条** `CREATE VIEW` 语句，而每个分支又都复用了同一个别名 `A`，于是“同语句 + 同别名”就把所有分支的 `A.DJLSH` 都点亮了。现在先定位包含光标的**最内层 `QuerySpecification`**，只在该块内匹配；没有查询块（如 `EXEC`）时仍回退到整条语句。
- 新增回归用例：同别名 `A` 的三段 `UNION ALL` 视图，光标在第一段时不得点亮后面的分支（测试总数 280 → 281）。
- 波浪线、悬停说明不变。

### English

- **Lighter grey**: this host ignores the brush alpha when painting classification backgrounds (`#40808080` came out as solid `#808080`), so the format now uses a solid light grey `#E9E9E9`.
- **Highlight scope is now the SELECT block under the caret**: `CREATE VIEW ... UNION ALL ...` is a *single* `CREATE VIEW` statement, and every branch reuses the alias `A`, so "same statement + same qualifier" lit up every branch's `A.DJLSH`. The analyzer now finds the innermost `QuerySpecification` containing the caret and matches only inside it, falling back to the whole statement when there is no query block (e.g. `EXEC`).
- New regression test: a three-branch `UNION ALL` view that reuses alias `A` must not highlight later branches when the caret is in the first (suite 280 → 281).
- Squiggles and hover tips are unchanged.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.419.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.419.vsix` asset below, then restart SSMS.

## 5.418

### 中文

- **修复灰色高亮不显示（真正原因）**：日志里连一条 `related highlight tagger created` 都没有——这个 SSMS 编辑器宿主根本不会为视图级 tagger（`IViewTaggerProvider`）创建实例，所以 `ClassificationTag` 和 `TextMarkerTag` 两种写法都没被调用（`IErrorTag` 能画波浪线是因为它在 buffer 层）。现改用 **分类器管线**（`IClassifierProvider` + `IClassificationType`），也就是编辑器做语法着色走的那条路，一定会被调用。
- 分类定义按 Roslyn 的方式导出密封类型的 **实例**（带 `[Name]` 元数据），格式只设背景刷 `#40808080`，不碰前景色。
- 新增 buffer→视图 的弱引用登记（`SqlEditorServices.TryGetView`），分类器据此拿到光标位置，光标不动时不重算，重算在后台线程、120ms 防抖，完成后只向编辑器发一次 `ClassificationChanged`。
- 日志新增：`related highlight classifier created`、首次有结果时的 `related highlight spans=<n>`，若类型未注册则记 `related highlight skipped, classification type is not registered`。

### English

- **Fixes the missing grey highlight (actual cause)**: the log contained not a single `related highlight tagger created` line — this SSMS editor host never instantiates view-level taggers (`IViewTaggerProvider`), so neither `ClassificationTag` nor `TextMarkerTag` was ever called (squiggles work because `IErrorTag` lives at the buffer level). The highlight now goes through the **classifier pipeline** (`IClassifierProvider` + `IClassificationType`), the same path the editor uses for syntax colouring, so it is always queried.
- The classification type is exported as a member returning a **sealed-type instance** with `[Name]` metadata (the Roslyn shape); the format sets only a background brush (`#40808080`) and leaves the foreground alone.
- A weak buffer→view registry (`SqlEditorServices.TryGetView`) gives the classifier the caret position; recomputation is debounced 120 ms, runs on a background thread and raises a single `ClassificationChanged` when done.
- New logs: `related highlight classifier created`, `related highlight spans=<n>` on the first non-empty result, and `related highlight skipped, classification type is not registered` when the type is missing.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.418.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.418.vsix` asset below, then restart SSMS.

## 5.417

### 中文

- **修复相关词灰色高亮不显示**：5.416 里波浪线（`IErrorTag`）已经正常，但灰色高亮用的是视图级 `ClassificationTag`——这种 tag 只有在分类器聚合时才被查询，SQL 编辑器视图并没有向它要 tag，所以 tagger 从未被创建（日志里只有 diagnostics tagger 的记录，没有 related highlight 的记录）。现改用编辑器 **marker** 机制：`TextMarkerTag` + 自定义 `MarkerFormatDefinition`（与 VS 的查找高亮、Roslyn 的引用高亮同一套渲染路径），并删除分类类型导出，彻底摆脱 5.413 那个 sealed 类型组合陷阱。
- 新增日志：`related highlight tagger created (native TextMarkerTag)` 与首次绘制时的 `related highlight tags=<n>`，下次一眼能看出是“没创建”还是“创建了但 0 个 span”。
- 波浪线与悬停说明保持不变（原生 `IErrorTag` / `IAsyncQuickInfoSource`）。

### English

- **Fixes the missing grey related-token highlight**: in 5.416 the squiggles (`IErrorTag`) worked, but the grey highlight used a view-level `ClassificationTag`; that tag is only queried during classifier aggregation, and the SQL editor view never asks for it, so the tagger was never even created (the log shows only the diagnostics tagger, never the highlight one). It now uses the editor **marker** mechanism — `TextMarkerTag` plus a custom `MarkerFormatDefinition`, the same rendering path as VS Find highlighting and Roslyn reference highlighting — and the classification type exports are gone, so the sealed-type composition trap from 5.413 cannot come back.
- New log lines: `related highlight tagger created (native TextMarkerTag)` and `related highlight tags=<n>` on the first draw, so the next report immediately shows whether it was never created or created with zero spans.
- Squiggles and hover tips are unchanged (native `IErrorTag` / `IAsyncQuickInfoSource`).

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.417.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.417.vsix` asset below, then restart SSMS.

## 5.416

### 中文

- **修复 5.413 的启动/新建查询崩溃根因**：`RelatedHighlightClassification` 被当成一个类导出，而 `ClassificationTypeDefinition` 是 sealed 类型，`ClassificationTypeRegistryService` 拿到的导出值无法赋值，MEF 组合失败 → EditorPackage 的 `SetSite` 失败，点“新建查询”就报“调用的目标发生了异常”。现在改为导出该类型的实例（与 Roslyn 声明分类的方式一致），并把 `IClassificationTypeRegistryService` 的导入改成 `AllowDefault`，单点失败不再拖垮整个组合。
- **波浪线、灰色相关词高亮、悬停说明全部交给编辑器原生渲染**：波浪线用 `IErrorTag`，相关词高亮用 `ClassificationTag`，悬停说明用 `IAsyncQuickInfoSource`。位置、重绘、滚动、缩放、换行、拆分窗口都由 SSMS 负责，插件不再自己画窗口、不再按毫秒重绘，因此不会再出现“跟着文字飘”或提示窗闪烁。
- 关掉了原来的两个窗口叠加层（灰色高亮 overlay 与诊断波浪线 overlay），避免同一处内容被画两遍、分析结果被发布两次。
- 诊断分析仍在后台线程进行（650ms 防抖、设置变化自动重跑），结果同时供波浪线、悬停说明和 Code Analysis 工具窗口使用。
- 新增启动期日志：`diagnostics tagger created (native IErrorTag)`、`related highlight tagger created (native ClassificationTag)`、`diagnostics analyzed findings=… squiggles=…`，便于定位环境问题。
- 如果 5.416 又出现“未能正确加载编辑器包”，请把 `%LOCALAPPDATA%\Microsoft\SSMS\22.0_6d5d8555\ComponentModelCache\Microsoft.VisualStudio.Default.err`、`%APPDATA%\Microsoft\SSMS\22.0_6d5d8555\ActivityLog.xml` 和 `D:\SQLTOOLDATA\MSSQLToolLog\log_*.log` 回传。

### English

- **Fixes the root cause of the 5.413 startup / New Query failure**: `RelatedHighlightClassification` was exported as a class, but `ClassificationTypeDefinition` is sealed, so the value the `ClassificationTypeRegistryService` received could not be assigned — MEF composition failed, `EditorPackage.SetSite` failed, and New Query surfaced "TargetInvocationException". The export is now an instance of that type (the same shape Roslyn uses), and the registry import is `AllowDefault` so one unsatisfied import can no longer break the whole composition.
- **Squiggles, grey related-token highlighting and hover tips are rendered natively by the editor**: squiggles use `IErrorTag`, related tokens use `ClassificationTag`, hover tips use `IAsyncQuickInfoSource`. Positioning, repainting, scrolling, zooming, word wrap and split views belong to SSMS, so the extension no longer paints its own windows or repaints on a timer.
- The two window overlays (grey highlight and diagnostic squiggles) are no longer created, so nothing is drawn twice and analysis results are published once.
- Analysis still runs on a background thread (650 ms debounce, re-runs when settings change) and feeds the squiggles, the hover tip and the Code Analysis tool window from the same result set.
- Startup now logs `diagnostics tagger created (native IErrorTag)`, `related highlight tagger created (native ClassificationTag)` and `diagnostics analyzed findings=… squiggles=…`.
- If 5.416 again fails to load the editor package, send `%LOCALAPPDATA%\Microsoft\SSMS\22.0_6d5d8555\ComponentModelCache\Microsoft.VisualStudio.Default.err`, `%APPDATA%\Microsoft\SSMS\22.0_6d5d8555\ActivityLog.xml` and `D:\SQLTOOLDATA\MSSQLToolLog\log_*.log`.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.416.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.416.vsix` asset below, then restart SSMS.

## 5.415

### 中文

- **波浪线、灰色相关词高亮、悬停说明全部交给编辑器原生渲染**：波浪线用 `IErrorTag`，相关词高亮用 `ClassificationTag`，悬停说明用 `IAsyncQuickInfoSource`。位置、重绘、滚动、缩放、换行、拆分窗口都由 SSMS 负责，插件不再自己画窗口、不再按毫秒重绘，因此不会再出现“跟着文字飘”或提示窗闪烁。
- 关掉了原来的两个窗口叠加层（灰色高亮 overlay 与诊断波浪线 overlay），避免同一处内容被画两遍、分析结果被发布两次。
- 诊断分析仍在后台线程进行（650ms 防抖、设置变化自动重跑），结果同时供波浪线、悬停说明和 Code Analysis 工具窗口使用。
- 新增启动期日志：`diagnostics tagger created (native IErrorTag)`、`related highlight tagger created (native ClassificationTag)`、`diagnostics analyzed findings=… squiggles=…`，便于定位环境问题。
- 如果 5.415 又出现“未能正确加载编辑器包”，请把 `%APPDATA%\Microsoft\SSMS\22.0_6d5d8555\ActivityLog.xml` 和 `D:\SQLTOOLDATA\MSSQLToolLog\log_*.log` 回传，直接按日志修。

### English

- **Squiggles, grey related-token highlighting and hover tips are now rendered natively by the editor**: squiggles use `IErrorTag`, related tokens use `ClassificationTag`, hover tips use `IAsyncQuickInfoSource`. Positioning, repainting, scrolling, zooming, word wrap and split views belong to SSMS, so the extension no longer paints its own windows or repaints on a timer — no more highlights trailing the text and no more flickering tips.
- The two window overlays (grey highlight and diagnostic squiggles) are no longer created, so nothing is drawn twice and analysis results are published once.
- Analysis still runs on a background thread (650 ms debounce, re-runs when settings change) and feeds the squiggles, the hover tip and the Code Analysis tool window from the same result set.
- Startup now logs `diagnostics tagger created (native IErrorTag)`, `related highlight tagger created (native ClassificationTag)` and `diagnostics analyzed findings=… squiggles=…`.
- If 5.415 again fails to load the editor package, send `%APPDATA%\Microsoft\SSMS\22.0_6d5d8555\ActivityLog.xml` and `D:\SQLTOOLDATA\MSSQLToolLog\log_*.log`; the failure will be fixed from those logs.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.415.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.415.vsix` asset below, then restart SSMS.

## 5.414

### 中文

- **修复 5.413 导致的 SSMS 启动失败**（“未能正确加载 EditorPackage”）：5.413 新增的分类标记导出（`ClassificationTypeDefinition` / `ClassificationFormatDefinition` / `IViewTaggerProvider`）会让编辑器包在 SetSite 阶段组合失败。已全部撤回，恢复原有的窗口高亮路径。
- 保留 5.413 的提示窗修复：鼠标停在波浪线上约 0.6 秒会显示严重度、规则名、消息与说明（之前 `SetWindowPos` 误带 `SWP_HIDEWINDOW`，提示窗每次都被立即隐藏）。
- 相关词高亮在拖动滚动条期间先隐藏，停下约 200ms 后重绘，减少“跟着文字飘”的观感。

### English

- **Fixes the SSMS startup failure introduced by 5.413** ("EditorPackage could not be loaded"): the classification exports added in 5.413 (`ClassificationTypeDefinition` / `ClassificationFormatDefinition` / `IViewTaggerProvider`) made the editor package fail during SetSite. They are removed and the previous window-based highlight path is back.
- Keeps the 5.413 tooltip fix: resting the pointer on a squiggle for about 0.6 s shows severity, rule name, message and description (the old `SetWindowPos` call included `SWP_HIDEWINDOW`, so every tip was hidden immediately).
- Related-token highlights hide while the scrollbar is dragged and redraw about 200 ms after it settles, so they no longer visibly trail the text.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.414.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.414.vsix` asset below, then restart SSMS.

## 5.413

### 中文

- **悬停说明能显示了**：提示窗的 `SetWindowPos` 参数里误带了 `SWP_HIDEWINDOW(0x0080)`，每次“显示”都会在同一次调用里被隐藏，所以光标或鼠标停在波浪线上都没有任何反应。已修正，鼠标停约 0.6 秒即可看到严重度、规则名、消息与说明。
- **快速拖动滚动条时不再“飘”**：相关词高亮改用编辑器的分类标记（与 VS 引用高亮同一机制），位置与重绘都由编辑器负责，滚动、缩放、换行、拆分窗口都不会脱离字词。
- 波浪线在拖动滚动条期间先隐藏，停下约 300ms 后按新视口重绘，避免追不上文本的拖影。

### English

- **The hover explanation finally appears**: the tip's `SetWindowPos` call also passed `SWP_HIDEWINDOW (0x0080)`, so every "show" was hidden within the same call and neither the caret nor the mouse produced a tip. Fixed: resting the pointer about 0.6 s now shows the severity, rule name, message and description.
- **No more drifting on fast scrollbar drags**: related-token highlighting uses an editor classification tag now (the same mechanism as VS reference highlighting), so positioning and repainting belong to the editor — scrolling, zooming, word wrap and split views cannot detach it from the words.
- Squiggles hide while the scrollbar is being dragged and are redrawn against the new viewport about 300 ms after it settles, instead of trailing the text.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.413.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.413.vsix` asset below, then restart SSMS.

## 5.412

### 中文

- **相关词高亮改由编辑器渲染**：不再由插件计算屏幕坐标去画装饰层，改为注册 `IViewTaggerProvider` + `TextMarkerTag`，定位、滚动、缩放、换行与拆分窗口全部交给 VS。滚动后不再残留或错位的灰色方块。
- 解析移到后台线程，光标移动与滚轮滚动不再被 ScriptDom 解析阻塞；同一位置的重复结果不再触发重绘。
- 灰色底纹使用自带的 `EditorFormatDefinition`（半透明灰），由编辑器叠加在文本背景上。

### English

- **Related-token highlighting is rendered by the editor now**: the extension no longer computes screen rectangles. It registers an `IViewTaggerProvider` with `TextMarkerTag`, so positioning, scrolling, zooming, word wrap and split views are owned by VS. The stray grey box left behind after scrolling is gone.
- The ScriptDom pass runs on a background thread, so caret movement and wheel scrolling are no longer blocked, and identical results no longer trigger a redraw.
- The marker uses its own `EditorFormatDefinition` (semi-transparent grey) composited over the text background.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.412.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.412.vsix` asset below, then restart SSMS.

## 5.411

### 中文

- **悬停说明改为跟随鼠标**：之前的提示只认插入符位置，鼠标停在波浪线上不会出现说明；现在直接以绘制出的波浪线矩形做命中测试，鼠标在任何一条波浪线上停约 0.6 秒即显示严重度、规则名、消息与规则说明，不需要先点一下。
- 指针在编辑器内但不在波浪线上时不再误弹提示；在同一处诊断内移动鼠标不会重置等待时间。
- 提示框错定在波浪线所在行的下方，不再跳到插入符位置。
- 备注：`WITH (NOLOCK)` 的黄色波浪线来自 `nolock-hint` 规则（默认 Warning、默认开启），可在“工具 → 选项 → MSSQL Tool → Code Analysis”关闭该规则，或将严重度降为 Info（只进列表、不画线）。

### English

- **Hover explanations now follow the mouse**: the tip used to be caret-driven, so resting the pointer on a squiggle showed nothing. It now hit-tests the rectangles that were just drawn, so pausing about 0.6 s on any squiggle shows the severity, rule name, message and rule description without clicking first.
- The tip no longer pops up when the pointer is inside the editor but not on a squiggle, and moving within the same diagnostic does not restart the delay.
- The tip is anchored to the squiggle's line instead of jumping to the caret.
- Note: the yellow squiggles on `WITH (NOLOCK)` come from the `nolock-hint` rule (Warning, on by default). Turn it off under **Tools → Options → MSSQL Tool → Code Analysis**, or lower its severity to Info (list only, no squiggle).

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.411.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.411.vsix` asset below, then restart SSMS.

## 5.410

### 中文

- **修复相关词高亮串到其它语句**：光标放在 `DDLSZH.DDHM` 这类限定列的表名上时，未起别名的表会因 `null == null` 被当成同一张表，脚本上方无关的 `DjLsh`、`View_WDDDYWDJ` 也跟着高亮。现在只有光标所在 token 精确命中表名/别名时才走表关系高亮，别名比较不再对空值生效，表拥有的列也只认带该限定符的列。
- **光标上下移动更丝滑**：高亮分析改到后台线程执行，ScriptDom 解析不再占用 UI 线程；带版本号丢弃过期结果；结果与上次完全相同时不再清空重画，避免闪烁。

### English

- **Fix related-token highlighting leaking into other statements**: with the caret on a table name used as a column qualifier (`DDLSZH.DDHM`), unaliased tables compared equal through `null == null`, so unrelated tokens such as `DjLsh` and `View_WDDDYWDJ` higher up the script lit up too. Table-relationship highlighting now requires the caret token to match a table name/alias by exact position, alias comparison ignores empty values, and only columns that really carry the qualifier are included.
- **Smoother caret movement**: highlight analysis runs on a background thread so the ScriptDom parse no longer occupies the UI thread, stale results are discarded by version, and identical results no longer clear and redraw the adornments.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.410.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.410.vsix` asset below, then restart SSMS.

## 5.409

### 中文

- **补全手感**：候选池与分析解耦；在同一个词内继续输入时直接复用上一次的候选做本地过滤，不再重新解析、不再走 120ms 防抖，列表跟手更新。
- 弹出列表刷新时保持当前选中项：按 `类型|名称` 在同一会话内找回，找不到时就近选择，不再每次跳回第一行。
- 列表中高亮匹配到的前缀，选中行与深浅色主题分别取色。
- **最近使用**：采纳过的建议按“库/类型/名称”记录频率与时间，最多 400 条；只给有界加权（频率 ≤8 + 新鲜度 ≤4），不会压过更好的文本匹配；设置页可查看、删除单项、清空历史，也可整体关闭“优先显示最近使用的建议”。
- 函数项提交时自动补 `()` 并把光标放进括号内。
- 加载占位从 400ms 缩短到 150ms；文本读取优先使用编辑器快照（`ITextSnapshot`），弹窗定位改由编辑器 `LayoutChanged` 事件驱动，200ms 轮询仅作兜底。
- 分析改为单飞：同一时刻只有一个后台解析在跑，连续输入不会再堆叠过期任务。

### English

- **Completion feel**: candidates are now decoupled from analysis. Typing inside the same word filters the previous candidate list in place — no re-parse, no 120 ms debounce, so the list keeps up with the keystroke.
- The selected row survives list refreshes: it is found again by `kind|name` within the same session, or by nearest index when it disappeared, instead of jumping back to the first row.
- The matched prefix is highlighted in the list, with separate colours for the selected row and for light/dark themes.
- **Recently used**: accepted suggestions are recorded per database/kind/name with count and timestamp (400 entries max). Ranking gets a bounded bonus only (frequency ≤ 8 + recency ≤ 4), never enough to beat a better textual match; the settings page lists them, removes one, clears the history, or turns the preference off.
- Function items commit with `()` and place the caret inside the parentheses.
- The loading placeholder drops from 400 ms to 150 ms; document text is read from the editor snapshot (`ITextSnapshot`) when available and the popup follows editor `LayoutChanged` events, with the 200 ms poll kept only as a fallback.
- Analysis is single-flight now: one background parse at a time, so fast typing no longer stacks stale tasks.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.409.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.409.vsix` asset below, then restart SSMS.

## 5.408

### 中文

- **代码分析（首个产品级规则库）**：30 条规则，覆盖正确性（`DELETE`/`UPDATE` 缺 `WHERE`、`= NULL`、`NOT IN` + NULL、`JOIN` 缺 `ON`、显式事务未配对、`MERGE` 缺终止符等）、性能（`SELECT *`、前导通配符 `LIKE`、非 SARGable 谓词、`NOLOCK`、隐式转换、动态 SQL 拼接）与可维护性（未使用变量/CTE、临时表未清理、`sp_` 前缀、`COUNT(1)` 等）。
- 编辑器内波浪线（Error/Warning）+ 悬停说明；`Info` 级默认只进列表不画线；`-- mssqltool:disable` 可抑制下一条语句。
- 新增“代码分析”工具窗口：整篇问题列表、严重度/规则/消息/位置、跳转与重新分析。
- 3 个零风险快速修复：`= NULL → IS NULL`、`SELECT * → 列清单`、缺 `AS` 别名补齐。
- 规则默认开 24 条，风格类 6 条默认关；严重度、开关、`Info` 波浪线均可在分析设置中覆盖（注册表 `AnalysisSettings`）。
- 谓词补全改为表达式状态机：条件写完后行尾直接给出 `AND` / `OR`，随后是 `GROUP BY`、`ORDER BY`、`HAVING`、`UNION`；刚输入 `WHERE` 时只给列、`NOT`、`EXISTS`、`(`；输入列名后给比较运算符；`BETWEEN 1` 之后 `AND` 排第一；未闭合括号内优先给 `)`。
- 灰色相关词高亮改为编辑器原生装饰层（adornment）：滚动、缩放、换行、拆分窗口时由 VS 自身重排，不再依赖轮询与屏幕坐标，空闲时零开销。
- 悬停说明窗不再每 400ms 重设一次窗口几何（该行为导致闪烁），并补上规则详细说明与“可用 Ctrl+. 修复”的提示，不再只有一行消息。
- `@参数` 高亮修正：`EXEC ... @p = @v` 左侧的参数名、`SELECT @v = ...` 赋值目标不再与真正的变量引用一起高亮。
- **灯泡（快速修复）**：编辑器行号旁出现建议动作，直接应用 `= NULL → IS NULL` 与缺 `AS` 别名两个文本修复；`SELECT *` 展开仍走带元数据的工具窗口/命令。
- 设置同时接入 **工具 → 选项 → MSSQL Tool → Code Analysis**，与“规则…”对话框共用同一套设置与控件。
- 新增命令 **Fix Problem at Caret**（工具菜单）：不打开分析窗口，直接修复光标所在语句中可修的问题；光标处无可修问题时状态栏提示。
- 同一命令出现在 SQL 编辑器右键菜单，并绑定 `Ctrl+.`（仅 SQL 编辑器内生效）。
- 分析在空闲 650ms 后防抖执行，单规则 50ms、整篇 300ms 预算，超过 500KB 的脚本自动跳过，绝不阻塞输入。
- **编辑器适配层**：新增 `IEditorHost` + 能力探测，安装目标放宽到 SSMS `[22.0,25.0)`；新流水线（`Model`/`Analysis`/`Rendering`）不再直接依赖 VS 编辑器类型。
- 语法模型：线性分词器、语句/批次索引、作用域树（FROM/JOIN/APPLY、CTE、派生表别名按深度可见），解析结果 LRU 缓存且不常驻 AST。
- **高亮与补全修复**：高亮几何签名改为“客户区 + 首可见行 + 行高 + 光标”，滚动时不再停留在旧位置；`SELECT ... INTO` 扫描改为线性 token 扫描，消除大脚本上的二次复杂度卡顿；补全窗口随视口/光标重排。
- 高亮改用共享文档模型：解析失败时不再全文同名词泛亮，只在光标所在语句内匹配；补全的别名解析合并作用域树，嵌套子查询里的 `d.` 能解析到正确的表，且连接列不会重复出现。
- `SELECT` 列表与 `ORDER BY` 的候选也改用作用域树：嵌套子查询的“(all SELECT columns)”与 `SELECT` 别名不再依赖文本正则推断。
- `JOIN` 候选合并作用域来源，不会再推荐已在嵌套子查询中使用的表；`FROM`/`JOIN` 会列出当前语句的 CTE 名。
- 新增线性目标扫描器：`INSERT`/`UPDATE`/`DELETE`/`MERGE` 的写入目标在 `TargetObject` 解析失败时回退到 token 扫描，方括号表名、CTE 前置的 `INSERT` 都能定位。
- `EXEC`/`EXECUTE` 同样进入扫描器（含 `@rc =` 返回码赋值），过程参数与参数值补全在正则失效时也能定位到过程。
- `CREATE TABLE`/`ALTER TABLE`/`CREATE INDEX ... ON` 的目标表也由扫描器提供回退，方括号表名的 DDL 列补全能定位。

### English

- **Code analysis: a product-grade rule catalog** (30 rules) across correctness (`DELETE`/`UPDATE` without `WHERE`, `= NULL`, `NOT IN` with NULLs, `JOIN` without `ON`, unbalanced transactions, `MERGE` without terminator), performance (`SELECT *`, leading-wildcard `LIKE`, non-SARGable predicates, `NOLOCK`, implicit conversion, dynamic SQL concatenation) and maintainability (unused variables/CTEs, temp tables not dropped, `sp_` prefix, `COUNT(1)`).
- In-editor squiggles (Error/Warning) with hover explanations; Info-level findings stay list-only by default; `-- mssqltool:disable` suppresses the next statement.
- New **Code Analysis** tool window: document-wide list with severity/rule/message/location, navigation and re-analysis.
- Three zero-risk quick fixes: `= NULL → IS NULL`, `SELECT * → column list`, missing `AS` alias.
- 24 rules on by default; the 6 style rules are opt-in. Severity, enablement and Info squiggles are configurable in the analysis settings.
- Predicate completion is now an expression state machine: a finished condition offers `AND`/`OR` first, then `GROUP BY`, `ORDER BY`, `HAVING` and `UNION`; a fresh `WHERE` offers columns, `NOT`, `EXISTS` and `(`; a column name offers comparison operators; `BETWEEN 1` puts `AND` first; an unclosed group offers `)`.
- The grey related-token highlight is a real editor adornment now: scrolling, zooming, word wrap and split views are repositioned by the editor itself, with no polling and no screen-coordinate window, and no idle cost.
- The hover tip no longer rewrites its window geometry on every 400 ms tick (that caused the visible blink) and now includes the rule description plus a "Ctrl+. applies this fix" hint instead of a single line.
- `@parameter` highlighting: the parameter name on the left of `EXEC ... @p = @v` and a `SELECT @v = ...` assignment target no longer light up together with real variable references.
- **Light bulb quick fixes**: suggested actions now appear at the caret for the two text-only fixes (`= NULL → IS NULL`, missing `AS` alias); `SELECT *` expansion stays in the metadata-aware tool window/command.
- The same settings are available under **Tools → Options → MSSQL Tool → Code Analysis**, sharing one grid and one registry JSON with the dialog.
- New **Fix Problem at Caret** command: applies the first available quick fix for the caret statement without opening the analysis window, with a status-bar message when there is nothing to fix.
- The same command is on the SQL editor context menu and bound to `Ctrl+.` (SQL editor scope only).
- Analysis runs after a 650 ms idle debounce with a 50 ms per-rule and 300 ms per-document budget; scripts over 500 KB are skipped. Typing is never blocked.
- **Editor adapter layer**: `IEditorHost` plus capability probing, and the installation target is now `[22.0,25.0)`; the new pipeline (`Model`/`Analysis`/`Rendering`) no longer references VS editor types.
- Syntax model: linear tokenizer, statement/batch index, scope tree (FROM/JOIN/APPLY, CTEs, derived-table aliases scoped by depth), LRU-cached results, no resident AST.
- **Highlight and completion fixes**: the highlight overlay now keys on client area + first visible line + line height + caret, so it follows scrolling; `SELECT ... INTO` scanning is linear; the completion popup repositions with the viewport.
- Highlighting now uses the shared document model: when the parser fails it no longer lights up every same-named word in the file, and checks stay inside the caret statement. Completion merges the scope tree into alias resolution, so `d.` inside a nested subquery resolves to the right table and joined columns are not duplicated.
- SELECT-list and ORDER BY candidates use the scope tree too, so nested-subquery "(all SELECT columns)" and SELECT aliases no longer depend on text regex heuristics.
- JOIN candidates merge the scoped sources, so a table already used inside a nested subquery is not suggested again, and FROM/JOIN now list the statement's CTE names.
- New linear target scanner: when `TargetObject` cannot be resolved, INSERT/UPDATE/DELETE/MERGE fall back to a token scan, so bracketed names and an INSERT preceded by a CTE still find their target.
- EXEC/EXECUTE is scanned too (including an `@rc =` return-code assignment), so procedure parameter and argument completion resolves the procedure even when the regex fails.
- CREATE TABLE / ALTER TABLE / CREATE INDEX ... ON targets are scanned as well, so DDL column completion resolves bracketed table names.

## 5.407

### 中文

- **修复 `SELECT *` 展开所有字段时混入其他表和临时表列**：会话临时表仍保留在本地对象列表中以供补全，但旧语句的别名会被裁剪，不再进入当前 SELECT 的列展开。
- `SELECT *` 后只展开当前 `FROM`（如 `YGDAH`）的列。
- 保留 5.406 的日志降级和渲染优化。

### English

- **Fix "expand all columns" after `SELECT *` pulling in other tables/temp tables**: session temp objects stay available as local objects, but aliases from earlier statements are pruned from the current SELECT.
- Only the current `FROM` table's columns are expanded.
- Keeps the 5.406 logging and render optimizations.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.407.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.407.vsix` asset below, then restart SSMS.

## 5.406

### 中文

- **修复 `SELECT *` 后补全只显示临时表列**：`FROM dbo.YGDAH` 在光标之后时，旧逻辑只解析光标前的语句，且临时表别名已存在，导致 `YGDAH` 不进入别名表。现在把语义后缀中的 FROM/JOIN 别名一起合并。
- **降低高亮卡顿**：`SQL-HIGHLIGHT` / `COMPLETION-CONTEXT` 诊断从 Info 降为 Debug；高亮窗口在 spans 和光标行未变化时不再重复重建矩形。

### English

- **Fix `SELECT *` completion showing only temp-table columns** when `FROM dbo.YGDAH` is after the caret. FROM/JOIN aliases from the semantic suffix are now merged.
- **Reduce highlight lag**: `SQL-HIGHLIGHT` / `COMPLETION-CONTEXT` diagnostics moved from Info to Debug; the overlay no longer rebuilds rectangles when spans and caret line are unchanged.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.406.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.406.vsix` asset below, then restart SSMS.

## 5.405

### 中文

- **按限定符区分同名列**：光标在 `B.DJLSH` 时只高亮 B 的 `DJLSH`，不再同时高亮 A 的；光标在 `A.DJLSH` 时只高亮 A 的两次引用。裸列名仍按同名关联。
- 保留 `INSERT ... SELECT` / `VALUES` 的目标列与位置对应值高亮。

### English

- **Qualified columns are scoped by qualifier**: on `B.DJLSH` only B's `DJLSH` is highlighted; on `A.DJLSH` only A's two references are highlighted. Bare column names still relate by name.
- `INSERT ... SELECT` / `VALUES` target-to-position value pairing remains.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.405.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.405.vsix` asset below, then restart SSMS.

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
