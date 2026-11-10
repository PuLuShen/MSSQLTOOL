using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MSSQLTool
{
    /// <summary>
    /// Centralizes UI localization. English XAML text is used as the stable key so
    /// existing views can be localized without duplicating every XAML file.
    /// </summary>
    internal static class LocalizationManager
    {
        public const string ChineseLanguage = "zh-CN";
        public const string TraditionalChineseLanguage = "zh-TW";
        public const string EnglishLanguage = "en-US";
        public const string JapaneseLanguage = "ja-JP";
        public const string KoreanLanguage = "ko-KR";
        public const string GermanLanguage = "de-DE";
        public const string FrenchLanguage = "fr-FR";
        public const string SpanishLanguage = "es-ES";

        public static readonly string[] SupportedLanguages =
        {
            ChineseLanguage, TraditionalChineseLanguage, EnglishLanguage,
            JapaneseLanguage, KoreanLanguage, GermanLanguage, FrenchLanguage, SpanishLanguage
        };

        private sealed class OriginalValues
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public readonly Dictionary<string, string> LastApplied = new Dictionary<string, string>();
        }

        private sealed class ReferenceComparer : IEqualityComparer<DependencyObject>
        {
            public bool Equals(DependencyObject x, DependencyObject y) => ReferenceEquals(x, y);
            public int GetHashCode(DependencyObject obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static readonly ConditionalWeakTable<DependencyObject, OriginalValues> Originals =
            new ConditionalWeakTable<DependencyObject, OriginalValues>();
        private static bool automaticLocalizationEnabled;

        private static readonly Dictionary<string, string> Chinese = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Code analysis"] = "代码分析",
            ["Analyze current query"] = "分析当前查询",
            ["Re-analyze"] = "重新分析",
            ["Apply fix"] = "应用修复",
            ["Severity"] = "严重度",
            ["Rule"] = "规则",
            ["Message"] = "消息",
            ["Default"] = "默认",
            ["Off"] = "关闭",
            ["Category"] = "类别",
            ["Rules"] = "规则…",
            ["Code analysis rules"] = "代码分析规则",
            ["Enable code analysis"] = "启用代码分析",
            ["Show squiggles for Info findings"] = "为提示级问题画波浪线",
            ["Show hover explanations"] = "悬停时显示说明",
            ["Changes apply to the next analysis."] = "修改将在下一次分析时生效。",
            ["Nothing to fix at the caret."] = "光标处没有可自动修复的问题。",
            ["Line {0}"] = "第 {0} 行",
            ["Info"] = "提示",
            ["No problems found."] = "未发现问题。",
            ["Code analysis is disabled. Enable it in Settings > Code Analysis."] = "代码分析已关闭，请在“设置 > 代码分析”中启用。",
            ["Fix applied."] = "已应用修复。",
            ["No automatic fix is available for this rule."] = "该规则没有可用的自动修复。",
            ["The caret is not inside a problem."] = "光标不在任何问题所在的位置。",
            ["MSSQL Tool - Settings"] = "MSSQL Tool - 设置",
            ["About"] = "关于", ["Tools"] = "工具",
            ["Language"] = "语言", ["Interface language:"] = "界面语言：",
            ["Simplified Chinese"] = "简体中文", ["Traditional Chinese"] = "繁体中文", ["English"] = "English",
            ["日本語"] = "日本語", ["한국어"] = "한국어", ["Deutsch"] = "Deutsch", ["Français"] = "Français", ["Español"] = "Español",
            ["The language setting applies immediately to MSSQL Tool windows and menu/toolbar labels."] = "语言设置会立即应用到 MSSQL Tool 窗口以及菜单/工具栏标签。",
            ["Apply"] = "应用", ["Save"] = "保存", ["Cancel"] = "取消", ["Close"] = "关闭",
            ["OK"] = "确定", ["Yes"] = "是", ["No"] = "否", ["Edit"] = "编辑",
            ["Delete"] = "删除", ["Remove"] = "移除", ["Refresh"] = "刷新",
            ["Search"] = "搜索", ["Loading..."] = "正在加载…", ["Error"] = "错误",
            ["Fields"] = "字段",
            ["Filter fields by name or type"] = "按字段名或类型过滤",
            ["Show or hide this column"] = "显示或隐藏该列",
            ["{0} ({1}) - click to jump to this column"] = "{0}（{1}）- 点击跳转到该列",
            ["{0} column(s)"] = "{0} 列",
            ["Selection"] = "选区",
            ["All rows: {0}, columns: {1}   ({2} cells)"] = "全部行：{0}，列：{1}（{2} 个单元格）",
            ["Selection: {0} cell(s), {1} row(s), {2} column(s)"] = "选中 {0} 个单元格，{1} 行，{2} 列",
            ["Format:"] = "格式：",
            ["Original"] = "原始",
            ["Not valid {0}; showing the raw value."] = "不是合法的 {0}，已显示原始内容。",
            ["Cell statistics are not computed for the whole result set."] = "整表不计算单元格统计。",
            ["{0}  ({1})   non-null {2}   empty {3} ({4})   distinct {5}   longest {6}   sum {7}   average {8}   min {9}   max {10}"] =
                "{0}（{1}）  非空 {2}  空值 {3}（{4}）  去重 {5}  最长 {6}  合计 {7}  平均 {8}  最小 {9}  最大 {10}",
            ["Select cells to see their statistics."] = "选中单元格可查看其统计。",
            ["Ctrl+. applies this fix."] = "按 Ctrl+. 应用此修复。",
            ["Opening definition script: {0}"] = "正在打开定义脚本：{0}",
            ["Loading SQL completion metadata..."] = "正在加载 SQL 补全缓存…",
            ["Loading SQL completion metadata for database {0}..."] = "正在加载数据库 {0} 的 SQL 补全缓存…",
            ["Loading SQL completion metadata from linked server {0}..."] = "正在加载链接服务器 {0} 的 SQL 补全缓存…",
            ["Refreshing SQL completion metadata..."] = "正在刷新 SQL 补全缓存…",
            ["SQL completion metadata refreshed."] = "SQL 补全缓存已刷新。",
            ["Warning"] = "警告", ["Success"] = "成功", ["Status"] = "状态",
            ["Code Analysis"] = "代码分析", ["SQL analysis"] = "SQL 分析",
            ["Underline findings in the editor (squiggles)"] = "在编辑器中用波浪线标出问题",
            ["Also underline Info-level findings"] = "提示级（Info）问题也画波浪线",
            ["Show the rule explanation when hovering a squiggle"] = "鼠标悬停在波浪线上时显示规则说明",
            ["The Code Analysis tool window always lists findings; these switches only control what the editor draws."] = "代码分析工具窗口始终会列出问题；上面几个开关只控制编辑器里画什么。",
            ["Manage rules and severities..."] = "管理规则与严重度…",
            ["Show squiggles in the editor"] = "在编辑器中显示波浪线",
            ["The code analysis settings could not be saved."] = "代码分析设置保存失败。",
            ["Feature description in"] = "功能说明：", ["Wiki"] = "Wiki",
            ["Query Templates"] = "查询模板", ["Templates Folder:"] = "模板文件夹：",
            ["Each .sql file is a template and subfolders are categories. Files stay in your folder and remain usable without the extension."] = "每个 .sql 文件都是一个模板，子文件夹就是分类。文件始终保存在你的文件夹中，即使没有安装插件也可直接使用。",
            ["Select folder..."] = "选择文件夹…", ["Useful TSQL scripts"] = "实用 T-SQL 脚本",
            ["Download TSQL scripts from GitHub"] = "从 GitHub 下载 T-SQL 脚本",
            ["Query Template Picker"] = "查询模板选择器", ["Search:"] = "搜索：",
            ["Search template names and folders"] = "搜索模板名称和文件夹",
            ["All templates"] = "全部模板", ["Favorites"] = "收藏", ["Recent"] = "最近使用",
            ["Templates folder:"] = "模板文件夹：", ["Choose folder..."] = "选择文件夹…",
            ["Template"] = "模板", ["Folder"] = "文件夹", ["Modified"] = "修改时间",
            ["Insert"] = "插入", ["Open in new query"] = "在新查询中打开",
            ["Add favorite"] = "添加收藏", ["Remove favorite"] = "取消收藏", ["Show file"] = "定位文件",
            ["Frequently used"] = "常用",
            ["Add to frequently used"] = "添加到常用",
            ["Remove from frequently used"] = "从常用移除",
            ["Pin or unpin the selected statement in the Frequently used folder"] = "将选中语句固定到常用目录，或从常用目录移除",
            ["Added to frequently used."] = "已添加到常用目录。",
            ["Removed from frequently used."] = "已从常用目录移除。",
            ["Enter: insert   Ctrl+Enter: new query"] = "Enter：插入   Ctrl+Enter：新建查询",
            ["{0} template(s)"] = "{0} 个模板", ["Preview unavailable"] = "无法预览",
            ["Template inserted."] = "模板已插入。", ["Template opened in a new query."] = "模板已在新查询中打开。",
            ["Could not insert the query template:"] = "无法插入查询模板：", ["Select templates folder"] = "选择模板文件夹",
            ["Create template..."] = "创建模板…", ["Migrate folder..."] = "迁移文件夹…",
            ["Creation uses the selected SQL, or the entire active query when nothing is selected."] = "优先使用当前选中的 SQL；没有选区时使用活动查询的全部内容。",
            ["Create Query Template"] = "创建查询模板", ["Template name:"] = "模板名称：", ["Category:"] = "分类：",
            ["Use a subfolder such as 运维\\阻塞; leave empty to save in the root folder."] = "可填写“运维\\阻塞”等子文件夹；留空则保存在根目录。",
            ["Save template"] = "保存模板", ["New query template"] = "新建查询模板",
            ["Enter a template name."] = "请输入模板名称。",
            ["The template name contains characters that cannot be used in a file name."] = "模板名称包含文件名不允许使用的字符。",
            ["Category must be a relative subfolder and cannot contain invalid path characters or '..'."] = "分类必须是相对路径，不能包含非法路径字符或“..”。",
            ["A template with this name already exists. Replace it?"] = "同名模板已经存在，是否替换？",
            ["Template created: "] = "模板已创建：", ["Could not create the query template:"] = "无法创建查询模板：",
            ["Select the new templates folder"] = "选择新的模板文件夹",
            ["Migrated {0} template(s) to the new folder. The original folder was kept at:\n{1}"] = "已将 {0} 个模板迁移到新文件夹。原文件夹仍保留在：\n{1}",
            ["Code Snippets"] = "代码片段", ["Use code snippets (SSMS restart required)"] = "启用代码片段（需要重启 SSMS）",
            ["Snippets Location:"] = "代码片段位置：", ["Replace snippets when pressing:"] = "按下以下按键时替换代码片段：",
            ["Replace SELECT * with column list"] = "将 SELECT * 替换为列列表",
            ["Replace asterisk when pressing:"] = "按下以下按键时替换星号：",
            ["Query History"] = "查询历史", ["Storage Type:"] = "存储类型：",
            ["Disabled"] = "禁用", ["Database table"] = "数据库表", ["Text files (JSONL)"] = "文本文件（JSONL）",
            ["Text Files:"] = "文本文件：", ["Open folder"] = "打开文件夹", ["Connection Info:"] = "连接信息：",
            ["< not configured >"] = "< 未配置 >", ["Use connection from Object Explorer"] = "使用对象资源管理器中的连接",
            ["Target Table Name:"] = "目标表名：", ["Creation script (for information only)"] = "创建脚本（仅供参考）",
            ["Code Format"] = "代码格式", ["Preserve comments"] = "保留注释",
            ["Remove new line after JOIN"] = "移除 JOIN 后的换行", ["Add tab after JOIN..ON"] = "在 JOIN…ON 后添加缩进",
            ["Place CROSS/OUTER JOIN/APPLY on a new line"] = "将 CROSS/OUTER JOIN/APPLY 放在新行",
            ["Format CASE expression as multiline"] = "将 CASE 表达式格式化为多行",
            ["Add new line between statements in code blocks"] = "在代码块的语句之间添加空行",
            ["Break exec sproc parameters per line"] = "存储过程执行参数逐行显示",
            ["Always upper-case built-in functions"] = "内置函数始终大写",
            ["Unindent Begin..End blocks"] = "取消 BEGIN…END 块缩进",
            ["Break variable definitions per line"] = "变量定义逐行显示",
            ["Break sproc definition parameters per line"] = "存储过程定义参数逐行显示",
            ["Comments are always preserved."] = "注释始终会被保留。",
            ["General"] = "常规", ["Preview"] = "预览",
            ["Keyword casing:"] = "关键字大小写：",
            ["Uppercase"] = "全部大写", ["Lowercase"] = "全部小写", ["PascalCase"] = "首字母大写",
            ["Align clause bodies (SELECT/SET lists)"] = "对齐子句主体（SELECT/SET 列表）",
            ["Add semicolons after statements"] = "为语句添加分号",
            ["Save as default"] = "保存为默认",
            ["Query Format Options"] = "查询格式化选项",
            ["Select formatting options"] = "选择格式化选项",
            ["Formatting SQL..."] = "正在格式化 SQL…",
            ["SQL formatted."] = "SQL 已格式化。",
            ["Error formatting the code"] = "格式化代码时出错",
            ["Unable to format T-SQL"] = "无法格式化 T-SQL",
            ["The selection is not valid T-SQL on its own. Format the current statement instead?"] = "选区本身不是完整合法的 T-SQL。是否改为格式化光标所在的完整语句？",
            ["The document changed while it was being formatted. Formatting was canceled to avoid losing any code."] = "文档在格式化期间被修改。为避免丢失代码，已取消本次格式化。",
            ["Source query"] = "源查询", ["Excel Export"] = "Excel 导出", ["Google Sheets"] = "Google 表格",
            ["Default Directory:"] = "默认目录：", ["Default Filename:"] = "默认文件名：",
            ["Default Spreadsheet Title:"] = "默认电子表格标题：", ["Client ID:"] = "客户端 ID：",
            ["Client Secret:"] = "客户端密钥：", ["Authorization Status:"] = "授权状态：",
            ["Authorize Google Sheets"] = "授权 Google 表格", ["Authorized"] = "已授权",
            ["Connection Colors"] = "连接颜色", ["Add new rule"] = "添加新规则",
            ["Keyboard shortcuts"] = "键盘快捷键", ["Script Object Definition"] = "对象定义脚本",
            ["Open definition script:"] = "打开定义脚本：",
            ["Select an object name in the SQL editor, then use this shortcut to open its definition script. Enter None to remove the shortcut."] = "在 SQL 编辑器中选择对象名称，然后使用此快捷键打开其定义脚本。输入 None 可解除快捷键。",
            ["Examples: F12, Ctrl+F12, Ctrl+Shift+O, or None"] = "示例：F12、Ctrl+F12、Ctrl+Shift+O 或 None",
            ["Enter a shortcut such as F12, Ctrl+F12, or Ctrl+Shift+O. Enter None to remove it."] = "请输入 F12、Ctrl+F12 或 Ctrl+Shift+O 等快捷键。输入 None 可解除快捷键。",
            ["The shortcut could not be applied: "] = "无法应用快捷键：",
            ["The shortcut was applied, but the setting could not be saved."] = "快捷键已应用，但无法保存该设置。",
            ["Configured rules"] = "已配置的规则", ["Server name contains:"] = "服务器名称包含：",
            ["Database name contains:"] = "数据库名称包含：", ["Status bar and tab color:"] = "状态栏和标签页颜色：",
            ["Pick color..."] = "选择颜色…", ["+ Add"] = "+ 添加", ["Edit selected"] = "编辑所选项",
            ["Remove selected"] = "移除所选项", ["Server contains"] = "服务器包含", ["Database contains"] = "数据库包含",
            ["Enabled"] = "启用", ["Move up"] = "上移", ["Move down"] = "下移", ["Edit rule"] = "编辑规则",
            ["Save changes"] = "保存修改", ["Cancel edit"] = "取消编辑", ["Unsaved changes"] = "有未保存的更改",
            ["No unsaved changes"] = "没有未保存的更改",
            ["The connection color rules could not be saved. Please try again."] = "连接颜色规则无法保存，请重试。",
            ["SQL completion"] = "SQL 自动补全", ["SQL Completion"] = "SQL 自动补全",
            ["Smart SQL completion"] = "智能 SQL 自动补全",
            ["Enable smart SQL completion"] = "启用智能 SQL 自动补全",
            ["Show completion automatically while typing"] = "输入时自动显示补全列表",
            ["Trust the SQL Server certificate"] = "补全元数据连接信任 SQL Server 证书",
            ["Applies only to the background connection used to load completion metadata."] = "仅应用于加载补全元数据的后台连接。",
            ["Insert square brackets"] = "插入方括号",
            ["Insert with:"] = "插入方式：", ["Tab"] = "Tab", ["Enter"] = "Enter", ["Tab or Enter"] = "Tab 或 Enter",
           ["Popup delay (ms):"] = "弹出延迟（毫秒）：", ["Maximum matches:"] = "最大匹配数：",
            ["Refresh completion metadata now"] = "立即刷新补全元数据",
            ["Clear and refresh completion cache"] = "清除并刷新补全缓存",
            ["The setting applies immediately. Ctrl+Space can still open completion when automatic popup is disabled."] = "设置立即生效。关闭自动弹出后，仍可使用 Ctrl+Space 打开补全列表。",
            ["Popup delay must be between 0 and 1000 ms, and maximum matches between 20 and 1000."] = "弹出延迟必须介于 0 到 1000 毫秒之间，最大匹配数必须介于 20 到 1000 之间。",
            ["The SQL completion setting could not be saved."] = "无法保存 SQL 自动补全设置。",
            ["Up/Down select | Tab insert | Esc close"] = "上下键选择 | Tab 插入 | Esc 关闭",
            ["Showing first {0} matches - keep typing to narrow results"] = "显示前 {0} 个匹配项，请继续输入以缩小范围",
            ["{0} matches | Up/Down select | Tab insert | Esc close"] = "{0} 个匹配项 | 上下键选择 | Tab 插入 | Esc 关闭",
            ["Color"] = "颜色", ["SMTP Settings"] = "SMTP 设置", ["Sender email address:"] = "发件人邮箱：",
            ["SMTP user name:"] = "SMTP 用户名：", ["SMTP password:"] = "SMTP 密码：",
            ["SMTP server:"] = "SMTP 服务器：", ["SMTP port:"] = "SMTP 端口：", ["Enable SSL/TLS"] = "启用 SSL/TLS",
            ["Updates"] = "更新", ["Check for MSSQL Tool updates on startup"] = "启动时检查 MSSQL Tool 更新",
            ["Check for updates"] = "检查更新", ["Update status"] = "更新状态", ["GitHub Integration"] = "GitHub 集成",
            ["Install when SSMS closes"] = "关闭 SSMS 时安装",
            ["Downloads the new version if needed and shows the installer window as soon as SSMS has closed."] = "需要时先下载新版本，并在 SSMS 关闭后立即显示安装窗口。",

            // Settings -> Updates status text and the up-to-date prompt.
            ["Information"] = "信息",
            ["Update check skipped because it is disabled in settings."] = "已跳过更新检查：设置中已关闭。",            ["Startup update check scheduled."] = "已安排在启动后检查更新。",
            ["Running startup update check."] = "正在执行启动时的更新检查。",
            ["Startup update check failed: {0}"] = "启动时的更新检查失败：{0}",
            ["Manual update check skipped because it is disabled in settings."] = "已跳动手动更新检查：设置中已关闭。",
            ["Manual update check started."] = "已开始手动检查更新。",
            ["Manual update check failed: {0}"] = "手动更新检查失败：{0}",
            ["Update check failed because current version could not be determined."] = "更新检查失败：无法确定当前版本。",
            ["Update check failed because latest release info was unavailable."] = "更新检查失败：无法获取最新发布信息。",
            ["Update check failed because latest release info was unavailable ({0})."] = "更新检查失败：无法获取最新发布信息（{0}）。",
            ["Update check failed because latest release version could not be parsed."] = "更新检查失败：无法解析最新发布的版本号。",
            ["Up to date ({0}). Latest release is {1}."] = "已是最新版本（{0}），最新发布为 {1}。",
            ["Update available: {0} -> {1}."] = "发现新版本：{0} → {1}。",
            ["{0} is up to date ({1})."] = "{0} 已是最新版本（{1}）。",
            ["Debug update test forced. Showing latest release {0} while current version is {1}."] = "调试用的强制更新：当前版本 {1}，显示最新发布 {0}。",
            ["Update is available, but the InfoBar was unavailable. Opened release page."] = "发现新版本，但信息栏不可用，已打开发布页面。",
            ["Update will install when SSMS closes."] = "关闭 SSMS 后将弹出安装窗口完成更新。",
            ["Update will install when SSMS closes; the download continues until then."] = "关闭 SSMS 后将弹出安装窗口；下载会持续到关闭前。",
            ["The update runs the installer once SSMS closes."] = "关闭 SSMS 后会弹出安装窗口，确认后即完成更新。",
            ["The update is downloaded and installed once SSMS closes."] = "关闭 SSMS 后会下载并弹出安装窗口，确认后即完成更新。",
            ["Could not start the update installer ({0}); opened release page instead."] = "无法启动更新安装程序（{0}），已改为打开发布页面。",
            ["Update will install when SSMS closes after the download finishes."] = "下载完成后，关闭 SSMS 时会弹出安装窗口。",
            ["Deferred update skipped because the VSIX download did not finish before SSMS closed."] = "已跳过关闭时更新：更新包未能在 SSMS 关闭前下载完成。",
            ["Deferred update skipped because the staged VSIX was not ready."] = "已跳过关闭时更新：更新包尚未就绪。",
            ["Could not launch VSIXInstaller automatically; opened release page instead."] = "无法自动启动 VSIXInstaller，已改为打开发布页面。",
            ["Update download failed; opened release page."] = "更新包下载失败，已打开发布页面。",
            ["Update package is not ready; opened release page."] = "更新包尚未就绪，已打开发布页面。",
            ["Downloading update package in background."] = "正在后台下载更新包。",
            ["Downloading update package: {0}% ({1} of {2})"] = "正在下载更新包：{0}%（{1} / {2}）",
            ["Downloading update package: {0}"] = "正在下载更新包：{0}",
            ["downloaded and verified"] = "下载并校验完成",
            ["downloaded without checksum verification"] = "下载完成（未校验）",
            ["Update package {0}. It will install when SSMS closes."] = "更新包已{0}，将在关闭 SSMS 后安装。",
            ["Update package {0}. Ready to install on close."] = "更新包已{0}，关闭后即可安装。",
            ["Update download failed: no ZIP or VSIX release asset was found."] = "更新包下载失败：发布中未找到 ZIP 或 VSIX 资产。",
            ["Update download failed: invalid GitHub release digest."] = "更新包下载失败：GitHub 发布的摘要无效。",
            ["Update download failed: checksum verification failed."] = "更新包下载失败：校验和不匹配。",
            ["Update download failed: {0}"] = "更新包下载失败：{0}",
            ["VSIXInstaller launched."] = "已启动 VSIXInstaller。",
            ["GitHub Token:"] = "GitHub 令牌：", ["API key:"] = "API 密钥：",
            ["Quick Search"] = "快速搜索", ["Snippet Manager"] = "代码片段管理器",
            ["Statistics Summary"] = "统计信息摘要", ["SQL Server Builds"] = "SQL Server 版本",
            ["Data Transfer"] = "数据传输", ["Sync to GitHub"] = "同步到 GitHub",
            ["Data Import"] = "数据导入", ["Grid to Email"] = "结果网格转邮件",
            ["Health Dashboard | Server"] = "健康面板 | 服务器", ["MSSQL Tool | Settings"] = "MSSQL Tool | 设置",
            ["Profiles"] = "配置文件", ["+ Add New Profile"] = "+ 新建配置文件", ["Target Repo Info"] = "目标仓库信息",
            ["Commit"] = "提交", ["Commit Msg:"] = "提交信息：", ["Confirm before pushing"] = "推送前确认",
            ["Script And Commit"] = "生成脚本并提交", ["Edit Sync Profile"] = "编辑同步配置",
            ["Server Health Dashboard"] = "服务器健康面板",
            ["Export"] = "导出", ["Copy"] = "复制", ["Select All"] = "全选", ["Clear"] = "清除",
            ["Database:"] = "数据库：", ["Server:"] = "服务器：", ["Username:"] = "用户名：", ["Password:"] = "密码：",
            ["Connection string:"] = "连接字符串：", ["Test connection"] = "测试连接", ["Connect"] = "连接",
            ["Query"] = "查询", ["Results"] = "结果", ["Duration"] = "耗时", ["Rows"] = "行数",
            ["Name"] = "名称", ["Description"] = "说明", ["Created"] = "创建时间",
            ["Database"] = "数据库", ["Server"] = "服务器", ["Username"] = "用户名", ["Password"] = "密码",
            ["Port"] = "端口", ["Table"] = "表", ["Script"] = "脚本", ["Import"] = "导入", ["Open"] = "打开",
            ["Select"] = "选择", ["Reset"] = "重置", ["Copy as"] = "复制为", ["Description:"] = "说明：",
            ["Data Source:"] = "数据源：", ["Server name:"] = "服务器名称：", ["Service name:"] = "服务名称：",
            ["Target database"] = "目标数据库", ["Rename to:"] = "重命名为：", ["Cursor Marker:"] = "光标标记：",
            ["Diagnostic log folder: "] = "诊断日志文件夹：", ["Saved..."] = "已保存…",
            ["A new version is now available!"] = "有新版本可用！", ["All user databases on the server"] = "服务器上的所有用户数据库",
            ["Export Logins and Permissions"] = "导出登录名和权限",
            ["Export SQL Server Agent Parameters - Jobs, Operators, Alerts"] = "导出 SQL Server 代理参数——作业、操作员和警报",
            ["Export SQL Server Configuration Values"] = "导出 SQL Server 配置值",
            ["Enter database name (or part of it)"] = "输入数据库名称（或其中一部分）",
            ["Enter server name (or part of it)"] = "输入服务器名称（或其中一部分）",
            ["Enter text to filter QueryText"] = "输入文本以筛选查询内容",
            ["Use Ola Hallengren keywords such as ALL_DATABASES, USER_DATABASES, etc."] = "可使用 Ola Hallengren 关键字，例如 ALL_DATABASES、USER_DATABASES 等。",
            ["OpenAI - ChatGPT integration"] = "OpenAI - ChatGPT 集成",
            ["Done"] = "完成", ["Something went wrong"] = "发生错误", ["An error occurred"] = "发生错误",
            ["Invalid Email"] = "邮箱地址无效", ["Subject Required"] = "需要填写主题", ["Open in Excel"] = "在 Excel 中打开",
            ["Script Object"] = "生成对象脚本", ["WIP"] = "开发中", ["DataTransferWindow"] = "数据传输",
            ["No Profile Selected"] = "未选择配置文件", ["No Repo Selected"] = "未选择仓库", ["Confirm Delete"] = "确认删除",
            ["Confirm Commit"] = "确认提交",
            ["TSQL script copied to clipboard!"] = "T-SQL 脚本已复制到剪贴板！",
            ["Prefix is required."] = "必须填写前缀。", ["Import completed."] = "导入完成。", ["Settings saved."] = "设置已保存。",
            ["The exported file could not be found."] = "找不到已导出的文件。",
            ["Email has been sent!"] = "邮件已发送！", ["Email has been queued via Database Mail!"] = "邮件已通过数据库邮件进入发送队列！",
            ["Can't parse the recipient's email address."] = "无法解析收件人邮箱地址。", ["Please provide the email subject."] = "请填写邮件主题。",
            ["Invalid mail config"] = "邮件配置无效", ["Select a saved connection."] = "请选择一个已保存的连接。",
            ["Select a connection to save."] = "请选择要保存的连接。",
            ["Please select a server or database node in Object Explorer first."] = "请先在对象资源管理器中选择服务器或数据库节点。",
            ["Every server currently connected in Object Explorer. Pick one to search it."] = "这里列出对象资源管理器中已连接的所有服务器，挑一个即可搜索。",
            ["Recent searches"] = "最近的搜索",
            ["Find column:"] = "查找列：",
            ["Freeze first N columns:"] = "固定前 N 列：",
            ["The result grid is not available right now."] = "当前无法访问结果网格。",
            ["Click a cell in the results first, then locate again."] = "请先点一下结果集中的单元格，再点定位。",
            ["Click a cell in the results first, then find again."] = "请先点一下结果集中的单元格，再点查找。",
            ["The result grid did not report its current cell."] = "结果网格没有返回当前单元格位置。",
            ["Moved {0} column(s): now at column {1} ({2}), row {3}."] = "已移动 {0} 列：现在在第 {1} 列（{2}），第 {3} 行。",
            ["caret at row {0}, column {1}."] = "光标位于第 {0} 行、第 {1} 列。",
            ["Locate column:"] = "定位列：",
            ["Locate"] = "定位",
            ["Find value:"] = "查找值：",
            ["Find"] = "查找",
            ["No result set is available."] = "没有可用的结果集。",
            ["Type part of a column name."] = "请输入列名的一部分。",
            ["Type something to find."] = "请输入要查找的内容。",
            ["Column {0} ({1}) is now at the left edge."] = "第 {0} 列（{1}）已定位到最左侧。",
            ["Row {0}, column {1} ({2}): {3}"] = "第 {0} 行，第 {1} 列（{2}）：{3}",
            ["Nothing matches '{0}'."] = "没有找到「{0}」。",
            ["No column matches '{0}'."] = "没有列匹配「{0}」。",
            ["Column {0} is pinned at the left ({1} column(s) fixed). Use Unfreeze to release it."] = "已把第 {0} 列固定到可见位置（左侧 {1} 列被固定），点「取消固定」可恢复。",
            ["Date/time selection: {0} value(s), earliest {1}, latest {2}."] = "日期时间选��：{0} 个值，最早 {1}，最晚 {2}。",
            ["The result grid did not accept the key presses. Click a cell and try again."] = "结果网格没有接受按键，请先点一下某个单元格再试。",
            ["Freeze"] = "固定",
            ["Unfreeze"] = "取消固定",
            ["Columns unfrozen."] = "已取消列固定。",
            ["First {0} column(s) frozen."] = "已固定前 {0} 列。",
            ["Selected column '{0}'."] = "已选中列「{0}」。",
            ["Column(s): {0}"] = "统计列：{0}",
            ["Go"] = "跳转",
            ["Scrolled to column '{0}'."] = "已跳转到列「{0}」。",
            ["Count:"] = "数量：",
            ["Sum:"] = "合计：",
            ["Average:"] = "平均：",
            ["Min:"] = "最小：",
            ["Max:"] = "最大：",
            ["Distinct:"] = "去重：",
            ["Selection: rows {0}-{1}, columns {2}-{3}   ({4} cells)"] = "选区：第 {0}-{1} 行，第 {2}-{3} 列（{4} 个单元格）",
            ["Nothing is selected - select cells in the result grid."] = "当前没有选中任何单元格，请在结果集中选择区域。",
            ["No numbers in the selection."] = "选区中没有可统计的数字。",
            ["{0} cell(s) with text or empty values were ignored."] = "另有 {0} 个文本或空单元格未计入。",
            ["Selection rows {0}-{1}, columns {2}-{3}   ({4} x {5} cells)"] = "选区：第 {0}-{1} 行，第 {2}-{3} 列（{4} × {5} 个单元格）",
            ["Result statistics panel"] = "结果集统计面板",
            ["Result panel"] = "结果集面板",
            ["Show the result statistics panel when I click the result grid"] = "点击结果集时自动显示统计面板",
            ["When this is off, you can still open the panel with the [Result panel] toolbar button."] = "关闭后仍可通过工具栏按钮【结果集面板】打开面板。",
            ["The result panel setting could not be saved."] = "结果集面板设置保存失败。",
            ["The change has been saved"] = "修改已保存",
            ["Setting saved"] = "设置已保存",
            ["Jump to column:"] = "跳转到列：",
            ["Column '{0}'   (row {1} of {2})"] = "列「{0}」   （第 {1} 行 / 共 {2} 行）",
            ["Result set tools"] = "结果集工具",
            ["Refresh from the results grid"] = "从结果网格重新读取",
            ["Copy statistics"] = "复制统计结果",
            ["Find:"] = "查找：",
            ["Find next"] = "查找下一个",
            ["Find all"] = "查找全部",
            ["Statistics"] = "统计",
            ["Rows: {0}   Columns: {1}   Grids: {2}"] = "行数：{0}   列数：{1}   结果集：{2}",
            ["Rows: {0}   Columns: {1}"] = "行数：{0}   列数：{1}",
            ["Searching {0} rows x {1} columns."] = "正在搜索 {0} 行 × {1} 列。",
            ["The result set could not be read."] = "无法读取结果集。",
            ["'{0}' was not found."] = "未找到「{0}」。",
            ["Found in row {0}, column '{1}'."] = "命中：第 {0} 行，列「{1}」。",
            ["Row {0} | {1} | {2}"] = "第 {0} 行 ｜ {1} ｜ {2}",
            ["{0} match(es). Double-click a row to jump to the cell."] = "共 {0} 处匹配。双击某行可跳转到对应单元格。",
            ["Column\tType\tNon-null\tNull\tDistinct\tMin\tMax\tAvg\tMax length"] = "列\t类型\t非空\t空值\t去重\t最小\t最大\t平均\t最大长度",
            ["Recent (10)"] = "最近 10 条",
            ["Template tags"] = "模板标签",
            ["Tags (comma separated):"] = "标签（用逗号分隔）：",
            ["Select a template first."] = "请先选择一个模板。",
            ["Tags…"] = "标签…",
            ["(not connected)"] = "（未连接）",
            ["Example:"] = "示例：",
            ["No usage notes are available for this function yet."] = "这个函数暂时还没有用法说明。",
            ["{0}: SQL Server built-in function."] = "{0}：SQL Server 内置函数。",

            // Usage notes shown in the completion details pane.  One entry per line: the analyzer
            // translates line by line (SqlCompletionAnalyzer.Doc), so the layout stays stable and
            // zh-TW keeps working through the same table.
            ["absolute value"] = "绝对值",
            ["average of a group, ignores NULL"] = "分组求平均，忽略 NULL",
            ["converts to another type; error when the value does not fit"] = "转换成另一种类型；值放不下时报错",
            ["smallest integer >= the value"] = "不小于该值的最小整数",
            ["1-based position, 0 when not found"] = "从 1 开始的位置，找不到返回 0",
            ["first non-NULL argument"] = "第一个非 NULL 的参数",
            ["joins values, NULL becomes an empty string"] = "拼接各值，NULL 视为空串",
            ["like CAST, plus style codes for dates and money"] = "同 CAST，另外可为日期和金额指定样式码",
            ["number of rows, COUNT(*) includes NULLs"] = "行数，COUNT(*) 把 NULL 行也算进去",
            ["table-valued: one row per JSON array element; WITH turns named paths into columns"] = "表值函数：JSON 数组每个元素一行，WITH 把指定路径变成列",
            ["table-valued: reads XML through a handle from sp_xml_preparedocument; release it with sp_xml_removedocument"] = "表值函数：通过 sp_xml_preparedocument 得到的句柄读 XML，用完要用 sp_xml_removedocument 释放",
            ["table-valued: one row per part, column value (plus ordinal when enabled_ordinal = 1)"] = "表值函数：每一段一行，列名 value（enable_ordinal = 1 时还有 ordinal）",
            ["table-valued: numeric sequence, column value (SQL Server 2022, compatibility level 160)"] = "表值函数：数字序列，列名 value（SQL Server 2022，兼容级别 160）",
            ["Table-valued function: call it in FROM/JOIN with its arguments, for example SELECT * FROM dbo.MyFunction(1) AS f."] = "表值函数：在 FROM/JOIN 里带参数调用，例如 SELECT * FROM dbo.MyFunction(1) AS f。",
            ["A few fixed rows written inline, without a temporary table."] = "用 VALUES 直接写出几行固定数据，不必建临时表。",
            ["SQL Server needs the column list after AS; every VALUES row must carry the same number of values."] = "SQL Server 要求 AS 后面跟列名清单；每一行 VALUES 的值的个数必须一致。",
            ["adds an interval; datepart is year/month/day/hour/minute/second"] = "按间隔加减；datepart 取 year/month/day/hour/minute/second",
            ["difference in whole dateparts"] = "按 datepart 计的整数差",
            ["name of the datepart as text"] = "把日期部分取成文本名称",
            ["datepart as an integer"] = "把日期部分取成整数",
            ["last day of the month"] = "所在月份的最后一天",
            ["largest integer <= the value"] = "不大于该值的最大整数",
            ["formats using .NET format strings (slower than CONVERT)"] = "用 .NET 格式串格式化（比 CONVERT 慢）",
            ["current date and time of the server"] = "服务器当前的日期时间",
            ["current UTC date and time"] = "当前 UTC 日期时间",
            ["inline if/else"] = "行内 if/else",
            ["replaces NULL (two arguments only)"] = "把 NULL 换成替代值（只能两个参数）",
            ["previous row in the window"] = "窗口内的上一行",
            ["next row in the window"] = "窗口内的下一行",
            ["first n characters"] = "取前 n 个字符",
            ["length without trailing spaces"] = "长度，不计尾部空格",
            ["lower case"] = "转小写",
            ["new uniqueidentifier"] = "生成新的 uniqueidentifier",
            ["NULL when both are equal"] = "两值相等时返回 NULL",
            ["1-based position of a LIKE pattern, 0 when absent"] = "LIKE 模式的位置（从 1 起），不存在返回 0",
            ["replaces every occurrence"] = "替换全部出现处",
            ["rounds to length digits"] = "四舍五入到指定小数位",
            ["1-based row number inside the window"] = "窗口内从 1 开始的行号",
            ["removes trailing spaces"] = "去掉尾部空格",
            ["last identity value inserted in the current scope"] = "当前作用域最后插入的标识值",
            ["concatenates rows into one string (SQL Server 2017+)"] = "把多行拼成一个字符串（SQL Server 2017+）",
            ["1-based start"] = "起始位置从 1 开始",
            ["sum of a group, ignores NULL"] = "分组求和，忽略 NULL",
            ["current datetime2 with higher precision"] = "当前 datetime2，精度更高",
            ["like CAST but returns NULL instead of failing"] = "同 CAST，转换失败时返回 NULL 而不报错",
            ["like CONVERT but returns NULL instead of failing"] = "同 CONVERT，转换失败时返回 NULL 而不报错",
            ["upper case"] = "转大写",
            ["largest value in the group, ignores NULL"] = "分组内最大值，忽略 NULL",
            ["smallest value in the group, ignores NULL"] = "分组内最小值，忽略 NULL",
            ["code point of the first character"] = "第一个字符的 ASCII 码",
            ["character for an ASCII code (0-255)"] = "ASCII 码对应的字符（0–255）",
            ["Unicode character for a code point"] = "码位对应的 Unicode 字符",
            ["Unicode code point of the first character"] = "第一个字符的 Unicode 码位",
            ["bytes used, including trailing spaces and Unicode width"] = "占用字节数，含尾部空格和 Unicode 双字节",
            ["0-4 how similar two SOUNDEX values are"] = "两个 SOUNDEX 值的相似度（0–4）",
            ["phonetic code for English names"] = "英文姓名的读音编码",
            ["wraps an identifier in brackets so it is safe to build SQL"] = "把标识符加方括号，拼动态 SQL 时不会出错",
            ["repeats a string"] = "把字符串重复若干次",
            ["the string backwards"] = "反转字符串",
            ["a run of spaces"] = "生成指定数量的空格",
            ["number to text, padded to a fixed width"] = "数字转文本，并补齐到固定宽度",
            ["escapes characters for JSON output"] = "转义成 JSON 安全的字符串",
            ["splits a delimited string into rows (2016+)"] = "把分隔字符串拆成多行（2016+）",
            ["overwrites part of a string with another"] = "用另一段文本覆盖字符串的一部分",
            ["replaces characters one for one, like REPLACE per character"] = "逐字符替换，等于对每个字符做 REPLACE",
            ["removes leading and trailing spaces (2017+)"] = "去掉首尾空格（2017+）",
            ["removes leading spaces"] = "去掉前导空格",
            ["last n characters"] = "取后 n 个字符",
            ["builds a date, error when the parts are invalid"] = "用年月日拼出日期，非法日期报错",
            ["builds a datetime"] = "拼出 datetime",
            ["builds a datetime2"] = "拼出 datetime2",
            ["builds a smalldatetime (minute precision)"] = "拼出 smalldatetime（分钟精度）",
            ["builds a datetimeoffset with its time zone offset"] = "拼出带时区偏移的 datetimeoffset",
            ["builds a time value"] = "拼出 time 值",
            ["day of the month (1-31)"] = "月内第几天（1–31）",
            ["month number (1-12)"] = "月份数字（1–12）",
            ["four digit year"] = "四位年份",
            ["1 when the value converts to a date, 0 otherwise"] = "能转成日期返回 1，否则 0",
            ["same instant in another offset"] = "把同一时刻换算到另一个时区偏移",
            ["attaches the offset that makes a datetime a datetimeoffset"] = "给 datetime 附上时区偏移，得到 datetimeoffset",
            ["current datetimeoffset of the server"] = "服务器当前 datetimeoffset",
            ["current UTC datetime2, higher precision than GETUTCDATE"] = "当前 UTC 的 datetime2，精度高于 GETUTCDATE",
            ["1-based pick from a list, NULL outside the range"] = "按序号从列表取值（从 1 起），越界返回 NULL",
            ["1 when the text is valid JSON"] = "文本是合法 JSON 时返回 1",
            ["returns a new JSON text with one value changed"] = "返回改了某个值的新 JSON 文本",
            ["an object or array out of JSON, as JSON"] = "从 JSON 中取出对象或数组（保持 JSON 形式）",
            ["scalar value out of JSON as text"] = "从 JSON 中取出标量值（以文本返回）",
            ["application name of the current connection"] = "当前连接的应用程序名",
            ["connection attributes such as net_transport or client_net_address"] = "连接属性，如 net_transport、client_net_address",
            ["database id, current database when omitted"] = "数据库 id，省略参数时取当前库",
            ["database name by id"] = "按 id 取数据库名",
            ["client computer name"] = "客户端计算机名",
            ["object id; NULL when the object does not exist"] = "对象 id；对象不存在时返回 NULL",
            ["object name by id"] = "按 id 取对象名",
            ["the login that opened the connection, even after EXECUTE AS"] = "最初建立连接的登录名（EXECUTE AS 后也不变）",
            ["schema id"] = "架构 id",
            ["schema name by id"] = "按 id 取架构名",
            ["server attributes such as ProductVersion or Collation"] = "服务器属性，如 ProductVersion、Collation",
            ["login name behind the security context"] = "当前安全上下文对应的登录名",
            ["database user name of the current context"] = "当前上下文对应的数据库用户名",
            ["1 open, -1 doomed, 0 none: decides whether to commit or roll back"] = "1 表示事务可提交，-1 已无法提交只能回滚，0 表示无事务",

            ["Read uncommitted: no shared locks, so readers never wait for writers."] = "未提交读：不加共享锁，读不需要等写。",
            ["Use it on a busy OLTP table for reports where approximate counts are fine."] = "用于繁忙的事务表做报表，统计数字有点偏差可以接受。",
            ["Risk: dirty reads, and rows read twice or skipped during page splits."] = "风险：脏读；页分裂时同一行可能读到两次或被跳过。",
            ["Never use it for balances or anything you will write back."] = "对账金额、还要写回库里的数据绝对不能用。",
            ["Same as WITH (READUNCOMMITTED)."] = "等同于 WITH (READUNCOMMITTED)。",
            ["Skips rows another transaction has locked instead of waiting for them."] = "跳过被其他事务锁住的行，不等待。",
            ["Use it on work queues together with UPDLOCK and ROWLOCK."] = "用于工作队列，通常与 UPDLOCK、ROWLOCK 一起用。",
            ["Two workers then never pick up the same row."] = "这样两个 worker 不会取到同一行。",
            ["Skipped rows stay in the queue and must be retried later."] = "被跳过的行仍留在队列里，下一轮要再取。",
            ["Ignored under the SNAPSHOT isolation level."] = "SNAPSHOT 隔离级别下不起作用。",
            ["Reads the row with an update lock held until the transaction ends."] = "读行时加更新锁，并保持到事务结束。",
            ["Use it when you SELECT a row and then UPDATE it."] = "适用于先 SELECT 再 UPDATE 同一行的场景。",
            ["It stops two sessions upgrading to exclusive locks at once (a deadlock)."] = "它能阻止两个会话同时升级为排他锁（即死锁）。",
            ["Other readers still get through: it does not block plain SELECTs."] = "普通 SELECT 不受影响，其他读操作仍能通过。",
            ["Add HOLDLOCK when the pattern is check-then-insert."] = "配合 HOLDLOCK 就是完整的“先查后插”。",
            ["Applies SERIALIZABLE to this table for the whole transaction."] = "对该表在本事务内套用 SERIALIZABLE。",
            ["Range locks stop anyone inserting a row that matches what you just read."] = "范围锁能挡住别人插入与你刚读到的数据匹配的行。",
            ["Use it for 'insert if not exists' on a unique value."] = "用于唯一值上的“不存在才插入”。",
            ["Without it two sessions can both find nothing and both insert."] = "不加的话两个会话可能都查不到、都插入。",
            ["Keep the transaction short: it blocks inserts in the locked range."] = "事务要短：它会阻塞锁定范围内的插入。",
            ["Asks for row-level locks instead of page or table locks."] = "要求行级锁，而不是页锁或表锁。",
            ["Unrelated rows on the same page then stop waiting on each other."] = "同一页上无关的行就不用互相等待。",
            ["Use it on queue tables together with UPDLOCK and READPAST."] = "队列表上常与 UPDLOCK、READPAST 一起用。",
            ["It is a preference: a scan can still escalate to a table lock."] = "这只是倾向：扫描时仍可能升级为表锁。",
            ["Takes a shared table lock for the statement."] = "对本语句加共享表锁。",
            ["Use it for a full-table read that must see one consistent snapshot."] = "用于必须看到同一个快照的全表读取。",
            ["It also stops lock escalation deciding when to take the table lock."] = "也用来阻止锁升级机制自行决定何时加表锁。",
            ["Blocks every writer while it runs; on a hot table prefer NOLOCK or SNAPSHOT."] = "运行期间阻塞所有写入；热表请改用 NOLOCK 或 SNAPSHOT。",
            ["TABLOCKX takes an exclusive lock instead."] = "TABLOCKX 则加排他锁。",
            ["Reads the last committed version of each row from tempdb."] = "从 tempdb 读每一行最后一次提交的版本。",
            ["Readers see one consistent snapshot and never block writers."] = "读操作看到一致快照，且不阻塞写入。",
            ["Requires ALLOW_SNAPSHOT_ISOLATION ON for the database."] = "要求数据库开启 ALLOW_SNAPSHOT_ISOLATION。",
            ["The consistent replacement for NOLOCK; it costs tempdb version-store space."] = "是 NOLOCK 的一致性替代，代价是占用 tempdb 版本存储。",
            ["Fails at once with error 1222 when a needed lock is held."] = "需要的锁被占用时立即报错 1222。",
            ["Without it the statement waits for LOCK_TIMEOUT and then fails."] = "不加的话语句会一直等 LOCK_TIMEOUT 才失败。",
            ["Use it for screens or jobs that should report 'busy, retry'."] = "用于宁可提示“忙，请重试”也不要卡住的界面或作业。",
            ["Forces an index seek, on the given index or on any usable one."] = "强制走索引查找，可指定索引，也可让优化器任选可用的索引。",
            ["Use it when stale statistics push the optimizer into a scan."] = "统计信息过旧导致优化器选扫描时使用。",
            ["It fails with error 8622 when no seek plan exists, so keep it out of shared code."] = "找不到查找计划会报错 8622，所以不要写进公共代码。",
            ["Pins the index this statement uses, e.g. WITH (INDEX(IX_Orders_CustomerId))."] = "固定本语句使用的索引，例如 WITH (INDEX(IX_Orders_CustomerId))。",
            ["Use it to test a plan or to work around a bad one."] = "用于验证计划，或绕开一个坏计划。",
            ["Type the index name, then the closing parenthesis."] = "输入索引名后补上右括号。",
            ["INDEX(0) forces a heap scan."] = "INDEX(0) 表示强制堆扫描。",

            ["Keeps only rows that match on both sides."] = "只保留两侧都能匹配上的行。",
            ["Write the ON condition right after it."] = "紧接着写 ON 条件。",
            ["Keeps every row of the left source; the right side is NULL when nothing matches."] = "保留左表的每一行；右表没匹配上时补 NULL。",
            ["A filter on the right table belongs in ON, not in WHERE."] = "针对右表的过滤条件要写在 ON 里，不要写在 WHERE 里。",
            ["Keeps every row of the right source; the left side is NULL when nothing matches."] = "保留右表的每一行；左表没匹配上时补 NULL。",
            ["Most code rewrites it as a LEFT JOIN."] = "大多数代码会改写成 LEFT JOIN。",
            ["Keeps unmatched rows from both sides, NULL-filled."] = "两侧没匹配上的行都保留，缺的一侧补 NULL。",
            ["Rarely what a report wants: it hides missing keys."] = "报表里很少真要这样：它会把缺失的键藏起来。",
            ["Pairs every row of the left source with every row of the right one."] = "把左表的每一行与右表的每一行两两组合。",
            ["It takes no ON clause and multiplies the row count."] = "它没有 ON 子句，结果行数是两边行数的乘积。",
            ["OUTER is optional: LEFT JOIN means the same thing to SQL Server."] = "OUTER 可以省略：对 SQL Server 来说 LEFT JOIN 完全等价。",
            ["OUTER is optional: RIGHT JOIN means the same thing to SQL Server."] = "OUTER 可以省略：对 SQL Server 来说 RIGHT JOIN 完全等价。",
            ["OUTER is optional: FULL JOIN means the same thing to SQL Server."] = "OUTER 可以省略：对 SQL Server 来说 FULL JOIN 完全等价。",
            ["Runs the right side once for every row of the left source."] = "对左表的每一行执行一次右侧。",
            ["The right side can reference columns of the left source, so a derived table or a table-valued function sees the current row."] = "右侧可以引用左表的列，所以派生表或表值函数能看到当前行。",
            ["It takes no ON clause; left rows the right side returns nothing for are dropped."] = "它没有 ON 子句；右侧没有返回行时，对应的左表行会被丢掉。",
            ["Runs the right side once for every row of the left source, like CROSS APPLY."] = "和 CROSS APPLY 一样，对左表的每一行执行一次右侧。",
            ["A left row whose right side returns nothing is kept with NULLs instead of being dropped."] = "右侧没有返回行时保留该左表行，缺的列补 NULL，而不是丢掉。",
            ["It takes no ON clause."] = "它没有 ON 子句。",
            ["Recompiles the plan for this statement instead of reusing the cached one."] = "本语句重新编译计划，不复用缓存中的计划。",
            ["Use it when parameter sniffing produces a bad plan."] = "参数嗅探导致坏计划时使用。",
            ["It costs a compile on every execution."] = "代价是每次执行都要编译。",
            ["Runs this statement on one CPU."] = "本语句只用一个 CPU 执行。",
            ["Use it to keep a small OLTP query from going parallel."] = "用于避免小的 OLTP 查询走并行。",
            ["The cap applies only to this statement."] = "这个上限只作用于本语句。",
            ["Returns the first 100 rows as soon as they are ready."] = "前 100 行一就绪就立即返回。",
            ["Use it for the first screen of a large report."] = "用于大报表的首屏。",
            ["The full result still takes just as long."] = "完整结果仍然要等同样长的时间。",
            ["Forces hash joins for this statement."] = "本语句强制使用哈希连接。",
            ["Use it to test a plan; the optimizer usually chooses better."] = "用于验证计划；通常优化器自己选得更好。",
            ["Forces merge joins for this statement."] = "本语句强制使用合并连接。",
            ["Both inputs must already be sorted on the join key."] = "两侧都必须已按连接键排好序。",
            ["Forces nested-loop joins for this statement."] = "本语句强制使用嵌套循环连接。",
            ["Works well for a few rows with an index on the inner side."] = "行数少且内层有索引时效果最好。",
            ["Keeps the join order exactly as written."] = "完全按书写顺序连接。",
            ["Use it to compare plans, not as a permanent fix."] = "用于对比计划，不要当作长期修复。",
            ["Stops the plan being recompiled because of statistics updates."] = "统计信息更新时也不重新编译计划。",
            ["Use it for a statement whose plan keeps changing."] = "用于计划老是变化的语句。",
            ["Compiles with averaged statistics instead of the parameter values."] = "按平均统计信息编译，而不是按实际参数值。",
            ["Use it when one plan has to serve very different values."] = "用于同一计划要服务差异很大的参数值时。",
            ["Names this source so its columns can be written as alias.Column."] = "给这个来源起名，列就可以写成 别名.列名。",
            ["A short alias keeps long joins readable."] = "短别名能让长 JOIN 更好读。",
            ["Filters rows after the joins."] = "联完之后过滤行。",
            ["On an outer join a WHERE condition on the right table turns it into an inner join."] = "外连接时，对右表写 WHERE 条件会把它变成内连接。",
            ["Write that filter in ON instead."] = "这类过滤应写进 ON。",
            ["Collapses rows into groups before the aggregates run."] = "先分组，再算聚合。",
            ["Every non-aggregated SELECT column must be listed here."] = "所有没被聚合的 SELECT 列都必须写在这里。",
            ["Sorts the final result."] = "对最终结果排序。",
            ["TOP and paging need it to be stable."] = "TOP 和分页需要它才有稳定顺序。",
            ["Starts the join condition."] = "开始写连接条件。",
            ["Tab accepts it and the key columns of both sources are offered right away."] = "按 Tab 接受后，两侧的键列会立即列出。",
            ["Pair the new table's key with the existing one."] = "把新表的键与已有的键对上。",
            ["Scripting returned nothing for this object."] = "该对象没有生成任何脚本内容。",
            ["Loading SQL completion metadata"] = "正在加载 SQL 补全元数据",
            ["{0} ({1}: {2}, {3:0.0}s)"] = "{0}（{1}：{2}，已用 {3:0.0} 秒）",
            ["Additional completion keywords (one per line or separated by ;):"] = "额外补全关键字（每行一个，或用 ; 分隔）：",
            ["Added to the built-in keyword list, so constructs such as WITH (NOLOCK) or your own house-style statements can be completed too."] = "追加到内置关键字表中，因此像 WITH (NOLOCK) 这样的写法或你自己的常用语句也能被补全。",
            ["metadata partially loaded"] = "元数据仅部分加载（对象列表可能不全）",
            ["metadata unavailable"] = "元数据不可用",
            ["metadata {0} objects / {1} columns"] = "元数据：{0} 个对象 / {1} 列",
            ["Match options:"] = "匹配选项：",
            ["Definition preview"] = "定义预览",
            ["Database: [{0}]"] = "数据库：[{0}]",
            ["Select a server or database node in Object Explorer."] = "请在对象资源管理器中选择服务器或数据库节点。",
            ["Could not use the connection to {0}. Select a node in Object Explorer and use the button instead."] = "无法使用到 {0} 的连接。请在对象资源管理器中选择节点后改用按钮选择。",
            ["Select a connection from Object Explorer first."] = "请先从对象资源管理器中选择连接。",
            ["Enter text to search."] = "请输入要搜索的文本。", ["Select at least one object type."] = "请至少选择一种对象类型。",
            ["Search canceled"] = "搜索已取消", ["Search failed"] = "搜索失败", ["Searching..."] = "正在搜索…",
            ["Select the object to script."] = "请选择要生成脚本的对象。", ["Select an object to script."] = "请选择要生成脚本的对象。",
            ["Please select a database in Object Explorer first."] = "请先在对象资源管理器中选择数据库。",
            ["Select an Excel workbook first."] = "请先选择 Excel 工作簿。", ["Select a target database from Object Explorer."] = "请从对象资源管理器中选择目标数据库。",
            ["Provide a destination table name."] = "请输入目标表名。", ["Data transfer has been cancelled."] = "数据传输已取消。",
            ["MSSQL Tool Query Library has been downloaded"] = "MSSQL Tool 查询库已下载",
            ["Client ID and Client Secret are required before authorizing Google Sheets."] = "授权 Google 表格前必须填写客户端 ID 和客户端密钥。",
            ["Not authorized"] = "未授权", ["Fill in at least the server name or the database name."] = "请至少填写服务器名称或数据库名称。",
            ["Please select a profile to edit."] = "请选择要编辑的配置文件。", ["No profile selected."] = "未选择配置文件。",
            ["Please select a GitHub repo first."] = "请先选择 GitHub 仓库。",
            ["Profile Name, Owner, Repo Name, Branch, and Token are required."] = "必须填写配置名称、所有者、仓库名称、分支和令牌。",
            ["Retrieving data from the source..."] = "正在从数据源读取数据…",
            ["Copied"] = "已复制", ["Copied Column Names"] = "已复制列名", ["No Column Names to Copy"] = "没有可复制的列名",
            ["No cells selected to copy"] = "未选择要复制的单元格", ["No data to copy"] = "没有可复制的数据",
            ["No column selected to copy"] = "未选择要复制的列", ["Copy All As ..."] = "全部复制为…",
            ["Copy Selected As ..."] = "将所选内容复制为…", ["Copy Selected Column Names"] = "复制所选列名",
            ["Copy All Column Names"] = "复制全部列名", ["Values as IN (...) - hold Shift for compact list"] = "将值复制为 IN (...)（按住 Shift 生成紧凑列表）",

            // Settings: snippets, Excel, Google Sheets and connection colors.
            ["Enable Snippets"] = "启用代码片段", ["Save Settings"] = "保存设置",
            ["Save Snippet"] = "保存代码片段", ["Import .sql"] = "导入 .sql", ["Duplicate"] = "复制副本",
            ["Prefix:"] = "前缀：", ["Prefix"] = "前缀", ["Snippet"] = "代码片段", ["Trigger Key:"] = "触发按键：",
            ["Add filter dropdowns to header row (AutoFilter)"] = "在标题行添加筛选下拉框（自动筛选）",
            ["Include content of attached query-window on its own sheet (hold Shift to do the opposite)"] = "将关联查询窗口的内容放入独立工作表（按住 Shift 执行相反操作）",
            ["Export booleans as numbers (TRUE/FALSE -> 1/0)"] = "将布尔值导出为数字（TRUE/FALSE → 1/0）",
            ["Leave blank → Desktop"] = "留空则使用桌面", ["Browse..."] = "浏览…",
            ["Authorize MSSQLTool to create Google Sheets using an OAuth client ID from "] = "使用来自以下位置的 OAuth 客户端 ID，授权 MSSQLTool 创建 Google 表格：",
            ["Color query window status bars and document tabs based on the server and/or database name. If both fields are filled, both must match. Leave a field empty to match anything."] = "根据服务器和/或数据库名称设置查询窗口状态栏与文档标签页颜色。若两个字段都填写，则必须同时匹配；留空表示匹配任意值。",
            ["Examples: Server='PROD' matches SQL-PROD-01. Database='master' matches any connection to master. Server='PROD' + Database='Sales' matches only Sales on PROD servers. First matching rule wins."] = "示例：服务器“PROD”可匹配 SQL-PROD-01；数据库“master”可匹配所有 master 连接；服务器“PROD”加数据库“Sales”仅匹配 PROD 服务器上的 Sales。优先使用第一条匹配规则。",
            ["Click to pick a color"] = "单击选择颜色",
            ["e.g. PROD, DEV, localhost, 192.168.1 (leave empty to match any server)"] = "例如 PROD、DEV、localhost、192.168.1（留空则匹配任意服务器）",
            ["e.g. master, MyDB, _prod (leave empty to match any database)"] = "例如 master、MyDB、_prod（留空则匹配任意数据库）",
            ["Leave blank to use the Desktop"] = "留空则使用桌面",
            ["Leave blank to use the default [dbo].[QueryHistory] table."] = "留空则使用默认的 [dbo].[QueryHistory] 表。",
            ["Break SELECT fields after TOP and unindent"] = "在 TOP 后将 SELECT 字段逐行显示并取消缩进",

            // Data transfer and import.
            ["Add New"] = "新建", ["+ New"] = "+ 新建", ["Edit Saved Connections"] = "编辑已保存连接",
            ["Saved Connections"] = "已保存连接", ["Select Saved Connection"] = "选择已保存连接",
            ["No connection selected"] = "未选择连接", ["Set Connection"] = "设置连接",
            ["Source Description"] = "源说明", ["Target Description"] = "目标说明",
            ["Source Query"] = "源查询", ["Target Table"] = "目标表", ["Source"] = "源", ["Destination table"] = "目标表",
            ["Select Source from Object Explorer"] = "从对象资源管理器选择源",
            ["Select Target from Object Explorer"] = "从对象资源管理器选择目标",
            ["SQL Server -> SQL Server"] = "SQL Server → SQL Server",
            ["Copy Data"] = "复制数据", ["(copy progress)"] = "（复制进度）", ["(have not been updated yet)"] = "（尚未更新）",
            ["Clear target table before inserting new records"] = "插入新记录前清空目标表",
            ["Create table if it does not exist"] = "表不存在时创建", ["Automatically create the table if it does not exist"] = "表不存在时自动创建",
            ["Create table structure only (skip data copying)"] = "仅创建表结构（跳过数据复制）",
            ["Additional SqlBulkCopy Options"] = "其他 SqlBulkCopy 选项", ["Treat first row as column headers"] = "将第一行视为列标题",
            ["Truncate the table before importing"] = "导入前截断目标表", ["Excel file"] = "Excel 文件",
            ["Choose an Excel file to get started."] = "请选择一个 Excel 文件开始。", ["Worksheet name"] = "工作表名称",
            ["Optional override when the workbook contains multiple sheets."] = "工作簿包含多个工作表时可在此指定。",
            ["Process checklist"] = "操作步骤", ["Connection Details"] = "连接详情",
            ["1. Choose the Excel workbook that contains the data."] = "1. 选择包含数据的 Excel 工作簿。",
            ["2. Point to the Object Explorer database that will receive the data."] = "2. 在对象资源管理器中选择接收数据的数据库。",
            ["3. Provide the destination table name and confirm optional behaviors."] = "3. 输入目标表名并确认可选设置。",
            ["4. Click Import to perform the one-click upload."] = "4. 单击“导入”执行一键上传。",
            ["Use this window to import Excel spreadsheets into the database currently selected in Object Explorer."] = "使用此窗口将 Excel 电子表格导入对象资源管理器中当前选定的数据库。",

            // Search, history, statistics and common columns.
            ["Search for:"] = "搜索内容：", ["Object types:"] = "对象类型：", ["Match whole words only"] = "仅匹配完整单词",
            ["Use wildcards"] = "使用通配符", ["Check all"] = "全选", ["Uncheck all"] = "取消全选",
            ["Tables"] = "表", ["Views"] = "视图", ["Stored Procedures"] = "存储过程", ["Functions"] = "函数",
            ["Query:"] = "查询：", ["Query (short)"] = "查询（摘要）", ["Elapsed"] = "耗时", ["Elapsed time"] = "耗时",
            ["StartTime"] = "开始时间", ["FinishTime"] = "结束时间", ["Result"] = "结果", ["Login"] = "登录名",
            ["Workstation"] = "工作站", ["Logical reads"] = "逻辑读取", ["Total logical reads"] = "逻辑读取总数",
            ["Scans"] = "扫描次数", ["CPU time"] = "CPU 时间", ["Captured at"] = "捕获时间",
            ["Copy as TSQL"] = "复制为 T-SQL",
            ["Select Object"] = "选择对象", ["Schema"] = "架构",
            ["Object"] = "对象", ["Type"] = "类型", ["Location"] = "位置", ["Provider"] = "提供程序",
            ["Column 1"] = "第 1 列", ["Column 2"] = "第 2 列", ["Match Preview"] = "匹配预览",

            // Health dashboard.
            ["Server Metrics Summary"] = "服务器指标摘要", ["Performance (15m)"] = "性能（15 分钟）",
            ["Database Backups"] = "数据库备份", ["Agent Jobs"] = "代理作业", ["SQL Agent Jobs"] = "SQL Server 代理作业",
            ["Active Connections:"] = "活动连接数：", ["Encrypted Connections:"] = "加密连接数：",
            ["Batch Requests/sec:"] = "每秒批处理请求数：", ["SQL Compilations/sec:"] = "每秒 SQL 编译数：",
            ["CPU %:"] = "CPU %：", ["Memory:"] = "内存：", ["Page Life Expectancy:"] = "页面预期寿命：",
            ["Response Time (ms):"] = "响应时间（毫秒）：", ["Lock Wait Time (sec):"] = "锁等待时间（秒）：",
            ["Total Data File Size (Gb):"] = "数据文件总大小（GB）：", ["Queue Sizes (Gb):"] = "队列大小（GB）：",
            ["Uptime:"] = "运行时间：", ["days"] = "天", ["Refresh Graph"] = "刷新图表", ["See Current Activity:"] = "查看当前活动：",
            ["For the past"] = "过去", ["Include FULL"] = "包含完整备份", ["Include DIFF"] = "包含差异备份",
            ["Include LOG"] = "包含日志备份", ["Unsuccessful executions only"] = "仅失败的执行", ["Be quiet"] = "静默模式",
            ["= server name ="] = "= 服务器名称 =", ["= health ="] = "= 健康状态 =", ["= service name ="] = "= 服务名称 =",
            ["= server uptime ="] = "= 服务器运行时间 =", ["= open connections ="] = "= 打开连接数 =",
            ["= enc connections ="] = "= 加密连接数 =", ["= response time ="] = "= 响应时间 =", ["= wait time ="] = "= 等待时间 =",
            ["= current CPU load ="] = "= 当前 CPU 负载 =", ["= used / total memory ="] = "= 已用/总内存 =",
            ["= PLE ="] = "= 页面预期寿命 =", ["= Batch Requests/sec ="] = "= 每秒批处理请求数 =",
            ["= SQL Compilations/sec ="] = "= 每秒 SQL 编译数 =", ["= blocked request ="] = "= 阻塞请求 =",
            ["= data file size ="] = "= 数据文件大小 =", ["= log file size ="] = "= 日志文件大小 =",
            ["= db status ="] = "= 数据库状态 =", ["= log send queue ="] = "= 日志发送队列 =", ["= redo queue ="] = "= 重做队列 =",

            // GitHub sync, mail and dialogs.
            ["Repository Details:"] = "仓库详情：", ["Owner:"] = "所有者：", ["Repo Name:"] = "仓库名称：",
            ["Branch:"] = "分支：", ["Token:"] = "令牌：", ["Databases:"] = "数据库：", ["Delete Profile"] = "删除配置文件",
            ["Save Profile"] = "保存配置文件", ["Add or update scripted objects"] = "添加或更新已生成脚本的对象",
            ["Sync options will appear here"] = "同步选项将在此显示", ["List of databases will appear here"] = "数据库列表将在此显示",
            ["Email Body:"] = "邮件正文：", ["Recipient Email Address(es). Use semicolons (;) to separate addresses."] = "收件人邮箱地址（多个地址用分号分隔）。",
            ["CC myself"] = "抄送给自己", ["Subject"] = "主题", ["Body:"] = "正文：", ["Body (preview)"] = "正文（预览）",
            ["From:"] = "发件人：", ["To:"] = "收件人：", ["Send"] = "发送", ["File:"] = "文件：",
            ["Export Complete"] = "导出完成", ["The data has been successfully exported."] = "数据已成功导出。",
            ["The data has been exported to Google Sheets."] = "数据已导出到 Google 表格。", ["Saved file:"] = "已保存文件：",

            // Query history window.
            ["Today"] = "今天", ["30 days"] = "最近 30 天", ["7 days"] = "最近 7 天",
            ["Apply filters"] = "应用筛选", ["Clear filters"] = "清除筛选",
            ["Separate keywords with spaces; all terms must match"] = "多个关键字用空格分隔，所有关键字都需匹配",
            ["SQL keywords"] = "SQL 关键字", ["Server contains..."] = "服务器名称包含…",
            ["Database contains..."] = "数据库名称包含…", ["Login contains..."] = "登录名包含…",
            ["Date:"] = "日期：", ["Result:"] = "结果：", ["Started"] = "开始时间",
            ["Current connection"] = "当前连接",
            ["SQL details"] = "SQL 详情", ["Wrap lines"] = "自动换行",
            ["Previous"] = "上一页", ["Next"] = "下一页", ["Copy SQL"] = "复制 SQL",
            ["Open in new query window"] = "在新查询窗口中打开",

            // Data transfer and data import.
            ["SQL Server Data Transfer"] = "SQL Server 数据传输",
            ["Select source"] = "选择源", ["Select target"] = "选择目标",
            ["No source selected"] = "未选择源", ["No target selected"] = "未选择目标",
            ["No SQL Server source selected"] = "未选择 SQL Server 源", ["No SQL Server target selected"] = "未选择 SQL Server 目标",
            ["Target table"] = "目标表", ["Copy data"] = "复制数据", ["Create if missing"] = "表不存在时创建",
            ["Truncate first"] = "先截断表", ["Keep identity"] = "保留标识列",
            ["Check constraints"] = "检查约束", ["Fire triggers"] = "启用触发器", ["Ready."] = "就绪。",
            ["Validate SQL Server constraints during import"] = "导入时验证 SQL Server 约束",

            // Grid to email, quick search and dashboards.
            ["Export the grid to a file and send it via email"] = "将结果网格导出为文件并通过邮件发送",
            ["Search for Object Definitions Across Multiple Databases"] = "跨多个数据库搜索对象定义",
            ["Health Dashboard - Servers"] = "服务器健康面板", ["Version:"] = "版本：",

            // SQL completion suggestions.
            ["SQL completion suggestions"] = "SQL 补全建议",
            ["Parameters"] = "参数",
            ["Current version:"] = "当前版本：",
            ["Latest release:"] = "最新版本：",

            // Settings window.
            ["Search settings:"] = "搜索设置：",
            ["Filter settings pages by name. Clear the box to show all pages."] = "按名称筛选设置页；清空后显示全部页面。",
            ["Copy report"] = "复制报告", ["Diagnostics"] = "诊断",
            ["Restore defaults for this page"] = "恢复本页默认设置",
            ["Restores the default values of the current settings page and saves them. Supported pages: SQL Completion, Query History, Code Format, Excel Export, SMTP Settings. Other pages keep their current values."] = "恢复当前设置页的默认值并保存。支持的页面：SQL 自动补全、查询历史、代码格式、Excel 导出、SMTP 设置；其他页面保持当前值。",
            ["Retention days (0 = unlimited):"] = "保留天数（0 = 不限制）：",
            ["Whole number between 0 and 3650; 0 = unlimited."] = "0–3650 之间的整数；0 表示不限制。",
            ["Refresh metadata after schema changes"] = "架构变更后刷新元数据",
            ["Enable categorized column picker"] = "启用分类列选择器",
            ["Add table aliases automatically"] = "自动添加表别名",
            ["Alias prefixes to ignore (comma separated):"] = "忽略的别名前缀（逗号分隔）：",
            ["Custom aliases (Object=alias; ...):"] = "自定义别名（对象=别名；…）：",
            ["JOIN rules (Source.Column=Target.Column; ...):"] = "JOIN 规则（源.列=目标.列；…）：",
            ["Show object details"] = "显示对象详情",
            ["Prefer recently used suggestions"] = "优先显示最近使用的建议",
            ["Adds a small ranking bonus to suggestions accepted before in the same database."] = "为同一数据库中此前采纳过的建议增加少量排序权重。",
            ["Behaviour"] = "行为",
            ["Alias and JOIN rules"] = "别名与 JOIN 规则",
            ["Keywords and timing"] = "关键字与响应",
            ["Recently used suggestions"] = "最近使用的建议",
            ["Clear history"] = "清空历史",
            ["Open Query History from anywhere in SSMS"] = "在 SSMS 任意位置打开查询历史",
            ["Open shortcut:"] = "打开快捷键：",
            ["Redact passwords, tokens and keys"] = "脱敏密码、令牌和密钥",
            ["Test configuration"] = "测试配置",
            ["TCP port between 1 and 65535 (default 587)"] = "TCP 端口范围为 1–65535（默认 587）",
            ["Leave blank to use DataExport_{yyyyMMdd_HHmmss}"] = "留空则使用 DataExport_{yyyyMMdd_HHmmss}",
            ["Leave blank to use DataExport_{yyyyMMdd_HHmmss}.xlsx; date-wildcards supported"] = "留空则使用 DataExport_{yyyyMMdd_HHmmss}.xlsx；支持日期通配符",
            ["Leave blank → DataExport__{yyyyMMdd__HHmmss}.xlsx; use {...} for date-wildcards"] = "留空 → DataExport__{yyyyMMdd__HHmmss}.xlsx；用 {...} 表示日期通配符",

            // GitHub sync and mail.
            ["No server connection selected"] = "未选择服务器连接",
            ["Unable to send the email because SMTP wasn't configured and no Database Mail Profiles have been found on the server."] = "无法发送邮件：未配置 SMTP，且服务器上没有可用的数据库邮件配置文件。",

            // About window.
            ["MSSQL Tool | About"] = "MSSQL Tool | 关于",
            ["SSMS 22 productivity extension"] = "SSMS 22 效率扩展",
            ["SSMS extension version {0}"] = "SSMS 扩展版本 {0}",
            ["Current features"] = "当前功能",
            ["SQL editing and navigation"] = "SQL 编辑与导航",
            ["Smart completion · F12 Go to Definition · T-SQL formatting · snippets · SELECT * expansion"] = "智能补全 · F12 转到定义 · T-SQL 格式化 · 代码片段 · SELECT * 字段展开",
            ["Results and data transfer"] = "结果处理与数据传输",
            ["Excel · Google Sheets · email · temporary-table export · bulk data transfer"] = "Excel · Google 表格 · 邮件 · 临时表导出 · 批量数据传输",
            ["Monitoring and diagnostics"] = "监控与诊断",
            ["Query history · statistics summary · server health · connection colors · execution time"] = "查询历史 · 统计摘要 · 服务器健康 · 连接颜色 · 执行时间",
            ["Workflow tools"] = "工作流工具",
            ["Quick Search · object scripting · SQL Server version information · GitHub sync"] = "快速搜索 · 对象脚本 · SQL Server 版本信息 · GitHub 同步",
            ["Current project"] = "当前项目",
            ["Source repository"] = "源代码仓库",
            ["Issues and feature requests"] = "问题与功能建议",
            ["Releases"] = "版本发布",
            ["Support the project"] = "赞赏支持",
            ["Exporting to Excel..."] = "正在导出到 Excel…",
            ["Generating INSERT statements..."] = "正在生成 INSERT 语句…",
            ["Uploading to Google Sheets..."] = "正在上传到 Google 表格…",
            ["Sending..."] = "正在发送…",
            ["Open a SQL query window with content first."] = "请先打开一个有内容的 SQL 查询窗口。",
            ["Place the cursor inside a block comment first."] = "请先把光标放在块注释内。",
            ["Place the cursor inside a SQL statement first."] = "请先把光标放在 SQL 语句内。",
            ["Could not export to Excel: {0}"] = "无法导出到 Excel：{0}",
            ["Could not generate INSERT statements: {0}"] = "无法生成 INSERT 语句：{0}",
            ["Scripting object: {0}"] = "正在为对象生成脚本：{0}",
            ["Could not script the object: {0}"] = "无法为对象生成脚本：{0}",
            ["Could not export to Google Sheets: {0}"] = "无法导出到 Google 表格：{0}",
            ["Could not insert the query template: {0}"] = "无法插入查询模板：{0}",
            ["Could not save the template:"] = "无法保存模板：",
            ["The SQL could not be parsed, so it was left unchanged: {0}"] = "SQL 解析失败，原文保持不变：{0}",
            ["Could not toggle the block comment: {0}"] = "无法切换块注释：{0}",
            ["Could not select the current statement: {0}"] = "无法选择当前语句：{0}",
            ["Copy failed: {0}"] = "复制失败：{0}",
            ["Unable to open settings: {0}"] = "无法打开设置窗口：{0}",
            ["No active connection was found. Connect to a server in the query window first."] = "未找到活动连接，请先在查询窗口中连接服务器。",
            ["No result grid is available to export. Run a query that returns a result set first."] = "没有可导出的结果网格，请先运行一个返回结果集的查询。",
            ["Export as INSERT Statements"] = "导出为 INSERT 语句",
            ["Select a table, view, or stored procedure name in the editor first."] = "请先在编辑器中选中表、视图或存储过程的名称。",
            ["Open the Settings window to configure Google Sheets export now?"] = "是否现在打开设置窗口配置 Google 表格导出？",
            ["Export to Excel"] = "导出到 Excel",
            ["Block Comment"] = "块注释",
            ["Select Current Statement"] = "选择当前语句",
            ["No result sets are available for export."] = "没有可导出的结果集。",
            ["No Data Available"] = "没有可用数据",
            ["Missing Google Sheets configuration"] = "缺少 Google 表格配置",
            ["Authorization Required"] = "需要授权",
            ["Export Failed"] = "导出失败",
            ["File ({0}):"] = "文件（{0}）：",
            ["Connect to a server first — open a connected query window, then run refresh again."] = "请先连接服务器——打开一个已连接的查询窗口后再刷新。",
            ["Could not refresh SQL completion metadata:"] = "无法刷新 SQL 补全元数据：",
            ["SQL completion metadata refresh failed."] = "SQL 补全元数据刷新失败。",
            ["SQL completion metadata refresh failed: {0}"] = "SQL 补全元数据刷新失败：{0}",
            ["SQL completion metadata refreshed ({0} objects, {1} columns)."] = "SQL 补全元数据已刷新（{0} 个对象，{1} 列）。",
            ["SELECT * expansion skipped — the text changed while metadata was loading."] = "已跳过 SELECT * 展开——元数据加载期间文本发生了变化。",
            ["Could not expand SELECT * — the columns of the referenced tables could not be resolved."] = "无法展开 SELECT *——无法解析所引用表的列。",
            ["Metadata loading — suggestions will appear when ready."] = "元数据加载中——就绪后将显示候选项。",
            ["The statement has unsaved changes. Discard them?"] = "该语句有未保存的修改，确定丢弃吗？",
            ["Saved: "] = "已保存：",
            ["Category created: "] = "分类已创建：",
            ["Statement created: "] = "语句已创建：",
            ["Category renamed: "] = "分类已重命名：",
            ["Statement renamed: "] = "语句已重命名：",
            ["Category deleted: "] = "分类已删除：",
            ["Statement deleted: "] = "语句已删除：",
            ["New category"] = "新建分类",
            ["New statement"] = "新建语句",
            ["Rename"] = "重命名",
            ["Create a category in the templates root:"] = "在模板根目录创建分类：",
            ["Create a category under '{0}':"] = "在分类“{0}”下创建分类：",
            ["Create a statement in the templates root:"] = "在模板根目录创建语句：",
            ["Create a statement under '{0}':"] = "在分类“{0}”下创建语句：",
            ["Category name:"] = "分类名称：",
            ["Statement name:"] = "语句名称：",
            ["Rename category"] = "重命名分类",
            ["Rename statement"] = "重命名语句",
            ["Delete category '{0}' and its {1} statement(s)?"] = "删除分类“{0}”及其 {1} 条语句？",
            ["Delete statement '{0}'?"] = "删除语句“{0}”？",
            ["{0} statement(s)"] = "{0} 条语句",
            ["Filter statements by name or folder"] = "按名称或文件夹筛选语句",
            ["Categories mirror the templates folder structure"] = "分类即模板文件夹层级",
            ["Save edits made in the preview (Ctrl+S)"] = "保存预览中的修改（Ctrl+S）",
            ["Create a subfolder in the selected category (or in the root when nothing is selected)"] = "在选中分类下创建子文件夹（未选中时在根目录创建）",
            ["Create a new statement file in the selected category, seeded with the current editor selection when available"] = "在选中分类下创建语句文件，如有编辑器选区则以其为初始内容",
            ["A category cannot be an absolute path."] = "分类不能是绝对路径。",
            ["The category contains characters that cannot be used in a folder name."] = "分类包含不能用于文件夹名称的字符。",
            ["The name contains characters that cannot be used in a file name."] = "名称包含不能用于文件名的字符。",
            ["A template with this name already exists in this category."] = "该分类下已存在同名语句。",
            ["The root category cannot be renamed."] = "根分类无法重命名。",
            ["The root category cannot be deleted."] = "根分类无法删除。",
            ["Enter a name."] = "请输入名称。",
            ["(skip)"] = "（跳过）",
            ["Page 0 / 0"] = "第 0 / 0 页",
            ["Page {0} / {1}"] = "第 {0} / {1} 页",
            ["The From date cannot be later than the To date."] = "起始日期不能晚于结束日期。",
            ["Loading query history..."] = "正在加载查询历史…",
            ["No matching queries"] = "没有匹配的查询",
            ["Showing {0}-{1} of {2:N0}"] = "显示 {0}-{1}，共 {2:N0} 条",
            ["Last refreshed {0}"] = "上次刷新 {0}",
            ["History loaded, but the latest background save failed: "] = "历史已加载，但最近一次后台保存失败：",
            ["Recording is active, but no records match the current filters."] = "记录功能已启用，但没有符合当前筛选条件的记录。",
            ["Recording is active · {0}"] = "记录功能已启用 · {0}",
            ["Unable to load history"] = "无法加载历史",
            ["Query history could not be loaded: "] = "无法加载查询历史：",
            ["Source query cannot be empty."] = "源查询不能为空。",
            ["Import cancelled; destination changes were rolled back."] = "导入已取消；目标端的更改已回滚。",
            ["Import failed. Review the error and try again."] = "导入失败，请查看错误后重试。",
            ["Cancelling import..."] = "正在取消导入…",
            ["Cancelled. SQL Server rolled back the target transaction."] = "已取消。SQL Server 已回滚目标端事务。",
            ["Transfer failed: "] = "传输失败：",
            ["Scanned '{0}' with {1} rows. Preparing destination table..."] = "已读取“{0}”，共 {1} 行。正在准备目标表…",
            ["Imported {0} rows into {1} on {2}."] = "已将 {0} 行导入 {2} 的 {1}。",
            ["Successfully imported {0} rows from {1} into {2} ({3})."] = "已成功将 {0} 行从 {1} 导入 {2}（{3}）。",
            ["Imported {0} of {1} rows..."] = "已导入 {0} / {1} 行…",
            ["Reading Excel file..."] = "正在读取 Excel 文件…",
            ["The source query did not return a tabular result."] = "源查询未返回表格结果。",
            ["Completed: {0} rows committed in {1} seconds."] = "已完成：{0} 行提交，耗时 {1} 秒。",
            ["Restore defaults"] = "恢复默认值",
            ["Downloading..."] = "正在下载…",
            ["Enter a valid SMTP port number between 1 and 65535."] = "请输入 1-65535 之间的有效 SMTP 端口号。",
            ["Retention days must be a whole number between 0 and 3650 (0 = unlimited)."] = "保留天数必须是 0-3650 的整数（0 表示不限制）。",
            ["Default values are not available for this page."] = "此页面暂不支持恢复默认值。",
            ["MSSQL Tool v{0} update available.  "] = "MSSQL Tool 有新版本 v{0}。  ",
            ["Update on Close"] = "关闭时更新",
            ["Release Notes"] = "发布说明",
            ["The list of templates has been updated."] = "模板列表已更新。",
            ["MSSQL Tool could not fully initialize; some features may be unavailable. Details were written to the activity log (Settings → Diagnostics)."] = "MSSQL Tool 未能完全初始化，部分功能可能不可用。详细信息已写入活动日志（设置 → 诊断）。",

            // Settings that moved from the settings window into the snippet manager and the
            // query template window.
            ["Saved"] = "已保存",
            ["The settings could not be saved."] = "设置无法保存。",
            ["Select snippets folder"] = "选择代码片段文件夹",
            ["Refresh templates"] = "刷新模板",
            ["Folder that stores the template .sql files"] = "存放模板 .sql 文件的文件夹",
            ["MSSQL Tool Query Library has been downloaded. Added: {0}; existing files kept: {1}."] = "已下载 MSSQL Tool 查询库。新增文件：{0} 个；保留已有文件：{1} 个。",
            ["The downloaded archive does not contain the query-library folder."] = "下载的压缩包中不包含 query-library 文件夹。",
            ["An error occurred: {0}"] = "发生错误：{0}",

            // Quick Search: database scope, matching options, progress and clipboard actions.
            ["Specific databases"] = "指定数据库",
            ["Fuzzy match"] = "模糊匹配",
            ["Databases"] = "数据库",
            ["Server and database"] = "服务器和数据库",
            ["Search conditions"] = "搜索条件",
            ["Copy results"] = "复制结果",
            ["Copy selected result"] = "复制所选结果",
            ["Copy all results"] = "复制全部结果",
            ["Copy every result row as text (database, type, schema, object, location, match preview)."] = "以文本形式复制所有结果行（数据库、类型、架构、对象、位置、匹配预览）。",
            ["Up/Down recall recent searches. Enter searches, Ctrl+Enter searches every database, Esc clears."] = "上下键调出最近搜索；Enter 立即搜索；Ctrl+Enter 搜索所有数据库；Esc 清空。",
            ["Enter: search | Ctrl+Enter: search all databases | Esc: clear | Ctrl+Down: jump to results"] = "Enter：搜索 | Ctrl+Enter：搜索所有数据库 | Esc：清空 | Ctrl+Down：跳到结果列表",
            ["Reload the database list and states from the server."] = "从服务器重新加载数据库列表与状态。",
            ["Select at least one database."] = "请至少选择一个数据库。",
            ["Select a result to copy first."] = "请先选择要复制的结果。",
            ["Could not read the database list: {0}"] = "无法读取数据库列表：{0}",
            ["All databases ({0})"] = "全部数据库（{0}）",
            ["{0} of {1} selected"] = "已选择 {0}/{1}",
            ["{0} unavailable"] = "{0} 个不可用",
            ["no access"] = "无访问权限",
            ["system"] = "系统",
            ["({0})"] = "（{0}）",
            ["{0} result(s)"] = "{0} 条结果",
            ["{0} result(s) | {1} database(s) could not be searched"] = "{0} 条结果 | {1} 个数据库无法搜索",
            ["Searching {0}/{1} databases..."] = "正在搜索 {0}/{1} 个数据库…",
            ["Searching {0}/{1} databases... ({2})"] = "正在搜索 {0}/{1} 个数据库…（{2}）",
            ["{0}: {1}"] = "{0}：{1}",
            ["{0} ms"] = "{0} 毫秒",
            ["Copied {0} result(s) to the clipboard"] = "已复制 {0} 条结果到剪贴板",
            ["The term must match a whole identifier word. Combined with fuzzy matching, every word of the term must match a distinct word of the identifier."] = "搜索词必须匹配完整的标识符单词。与模糊匹配同时使用时，搜索词的每个单词都必须匹配标识符中不同的单词。",
            ["SQL LIKE wildcards: % for any text, _ for a single character. Cannot be combined with fuzzy matching."] = "SQL LIKE 通配符：% 表示任意文本，_ 表示单个字符。不能与模糊匹配同时使用。",
            ["Tolerates typos and separator differences, so uspGetOrd also finds usp_GetOrder. Cannot be combined with wildcards."] = "容忍拼写错误和分隔符差异，例如输入 uspGetOrd 也能找到 usp_GetOrder。不能与通配符同时使用。",

            // Code formatting: the options dialog, the Code Format settings page and the profile files.
            ["Formatting profile"] = "格式化配置",
            ["Preset"] = "预设",
            ["Preset:"] = "预设：",
            ["Standard"] = "标准",
            ["Compact"] = "紧凑",
            ["Readable"] = "易读",
            ["Team standard"] = "团队标准",
            ["Custom"] = "自定义",
            ["Start from a preset, then fine tune any individual option on the other tabs. Changing any option switches the profile to Custom."] = "先选择一种预设，再在其他选项卡中微调各项设置。修改任意选项后，配置会切换为“自定义”。",
            ["Common"] = "常规",
            ["Legacy rewrites"] = "兼容旧版的重写选项",
            ["Add new line between statements in blocks"] = "代码块内语句之间添加空行",
            ["Unindent BEGIN..END blocks"] = "不缩进 BEGIN..END 块",
            ["Text casing"] = "文本大小写",
            ["Keywords:"] = "关键字：",
            ["Built-in functions:"] = "内置函数：",
            ["Built-in function casing:"] = "内置函数大小写：",
            ["Data types:"] = "数据类型：",
            ["Data type casing:"] = "数据类型大小写：",
            ["Identifiers:"] = "标识符：",
            ["Identifier casing:"] = "标识符大小写：",
            ["Variables:"] = "变量：",
            ["Aliases:"] = "别名：",
            ["Keep as written"] = "保持原样",
            ["Spacing"] = "空格",
            ["Space after comma"] = "逗号后加空格",
            ["Space before comma"] = "逗号前加空格",
            ["Space around operators"] = "运算符两侧加空格",
            ["Space between a function name and its parenthesis"] = "函数名与括号之间加空格",
            ["Line breaks"] = "换行",
            ["New line for each SELECT column"] = "每个 SELECT 列单独一行",
            ["New line for each FROM table"] = "每个 FROM 表单独一行",
            ["New line before JOIN"] = "JOIN 前换行",
            ["New line before ON"] = "ON 前换行",
            ["New line before WHERE"] = "WHERE 前换行",
            ["New line for each AND/OR condition"] = "每个 AND/OR 条件单独一行",
            ["One AND/OR condition per line"] = "每个 AND/OR 条件单独一行",
            ["New line before GROUP BY"] = "GROUP BY 前换行",
            ["New line before ORDER BY"] = "ORDER BY 前换行",
            ["Indent"] = "缩进",
            ["Indent width (spaces):"] = "缩进宽度（空格数）：",
            ["Indent size:"] = "缩进空格数：",
            ["Indent subqueries"] = "缩进子查询",
            ["Indent CASE bodies"] = "缩进 CASE 主体",
            ["Indent code blocks"] = "缩进代码块",
            ["Single line"] = "单行",
            ["Keep short queries on a single line"] = "短查询保持单行",
            ["Keep short queries on one line"] = "短查询保持单行",
            ["Keep short subqueries on a single line"] = "短子查询保持单行",
            ["Keep single lines within the right margin"] = "单行长度不超过右边距",
            ["Single line threshold (characters):"] = "单行阈值（字符数）：",
            ["Short statement threshold (characters):"] = "短语句阈值（字符数）：",
            ["Subquery single line threshold (characters):"] = "子查询单行阈值（字符数）：",
            ["One column per line (SELECT list)"] = "每列一行（SELECT 列表）",
            ["One column per line (column list)"] = "每列一行（列清单）",
            ["One column per line"] = "每列一行",
            ["One column definition per line"] = "每个列定义一行",
            ["One table per line (FROM list)"] = "每个表一行（FROM 列表）",
            ["One select column per line"] = "每个 SELECT 列单独一行",
            ["One value per line"] = "每个值一行",
            ["One VALUES row per line"] = "每个 VALUES 行单独一行",
            ["One assignment per line"] = "每个赋值一行",
            ["One storage option per line"] = "每个存储选项一行",
            ["One variable per line"] = "每个变量一行",
            ["One parameter per line"] = "每个参数一行",
            ["One statement per line"] = "每条语句一行",
            ["One GROUP BY column per line"] = "每个 GROUP BY 列单独一行",
            ["One ORDER BY column per line"] = "每个 ORDER BY 列单独一行",
            ["Indent the ON condition"] = "缩进 ON 条件",
            ["Indent the WHERE condition"] = "缩进 WHERE 条件",
            ["Break conditions:"] = "条件换行位置：",
            ["Before the operator"] = "运算符之前",
            ["After the operator"] = "运算符之后",
            ["Subquery"] = "子查询",
            ["Inherit the main query format"] = "继承主查询格式",
            ["Allow single line subqueries"] = "允许子查询单行",
            ["New line before the opening parenthesis"] = "左括号前换行",
            ["New line after the opening parenthesis"] = "左括号后换行",
            ["New line before the closing parenthesis"] = "右括号前换行",
            ["New line after the closing parenthesis"] = "右括号后换行",
            ["Indent the subquery body"] = "缩进子查询主体",
            ["New line before VALUES"] = "VALUES 前换行",
            ["New line before SET"] = "SET 前换行",
            ["New line before FROM"] = "FROM 前换行",
            ["New line before OUTPUT"] = "OUTPUT 前换行",
            ["Format the FROM clause like a SELECT"] = "FROM 子句按 SELECT 的格式处理",
            ["Format the WHERE clause like a SELECT"] = "WHERE 子句按 SELECT 的格式处理",
            ["New line before INTO"] = "INTO 前换行",
            ["New line before USING"] = "USING 前换行",
            ["New line before WHEN"] = "WHEN 前换行",
            ["New line before THEN"] = "THEN 前换行",
            ["New line before ELSE"] = "ELSE 前换行",
            ["New line after BEGIN"] = "BEGIN 后换行",
            ["New line before END"] = "END 前换行",
            ["New line after an IF condition"] = "IF 条件后换行",
            ["New line after a WHILE condition"] = "WHILE 条件后换行",
            ["Indent the block content"] = "缩进代码块内容",
            ["Indent the CASE body"] = "缩进 CASE 主体",
            ["Procedure / function"] = "存储过程/函数",
            ["New line before RETURNS"] = "RETURNS 前换行",
            ["New line before AS"] = "AS 前换行",
            ["Indent the BEGIN..END keywords"] = "缩进 BEGIN..END 关键字",
            ["Indent the routine body"] = "缩进例程主体",
            ["Trigger"] = "触发器",
            ["New line before FOR"] = "FOR 前换行",
            ["Indent the trigger body"] = "缩进触发器主体",
            ["View"] = "视图",
            ["Indent the view query"] = "缩进视图查询",
            ["Block"] = "语句块",
            ["Keep the cursor query on a single line"] = "游标查询保持单行",
            ["Indent the cursor query"] = "缩进游标查询",
            ["Export profile..."] = "导出配置…",
            ["Import profile..."] = "导入配置…",
            ["Advanced options..."] = "高级选项…",
            ["Formatted"] = "格式化结果",
            ["Add semicolons"] = "添加分号",
            ["Align clause bodies"] = "对齐子句主体",
            ["Filter settings pages by name"] = "按名称筛选设置页",
            ["Reset the settings on the current page to their default values"] = "将当前设置页恢复为默认值",
            ["Formatted query"] = "格式化后的查询",
            ["The formatting settings could not be saved. Please try again."] = "格式化设置无法保存，请重试。",
            ["SQL Completion Metadata"] = "SQL 补全元数据",

            // Storage page: data and configuration folder.
            ["Storage"] = "数据与配置",
            ["Data and configuration folder"] = "数据与配置目录",
            ["MSSQL Tool keeps its settings, query history, GitHub sync profiles, snippets, query templates and log files in one folder. Point that folder at another drive, a synced folder or a portable device to keep the data outside your Windows profile. The pointer itself stays in the registry, and every setting is mirrored there, so the default location keeps working if the folder is unavailable. A query template folder you picked yourself is left where it is."] = "MSSQL Tool 会把设置、查询历史、GitHub 同步配置、代码片段、查询模板和日志统一存放在一个目录中。将该目录指向其他磁盘、同步文件夹或便携设备，即可让这些数据脱离 Windows 用户配置文件。目录指针本身仍保存在注册表中，并且每项设置都会在注册表留一份镜像，因此该目录不可用时会自动回到默认位置继续工作。你自行指定的查询模板目录会保持原位不动。",
            ["Data folder:"] = "数据目录：",
            ["Restore default"] = "恢复默认值",
            ["Select the folder MSSQL Tool should keep its data in"] = "选择 MSSQL Tool 存放数据的目录",
            ["Copy existing data to the new folder"] = "将现有数据复制到新目录",
            ["Locations in use"] = "当前使用的位置",
            ["Changes apply immediately: the query history, snippets, query templates and settings move to the new folder at once. The log file follows after the next SSMS start."] = "修改立即生效：查询历史、代码片段、查询模板和设置会立即切换到新目录；日志文件在下次启动 SSMS 后跟随。",
            ["Location: {0}. {1}"] = "来源：{0}。{1}",
            ["set by the MSSQLTOOL_DATA_ROOT environment variable"] = "由环境变量 MSSQLTOOL_DATA_ROOT 指定",
            ["chosen in this window"] = "在本窗口中指定",
            ["the default location"] = "默认位置",
            ["Settings are written to settings.json in this folder and mirrored into the Windows registry."] = "设置会写入该目录下的 settings.json，并在 Windows 注册表中保留镜像。",
            ["Settings are kept in the Windows registry."] = "设置保存在 Windows 注册表中。",
            ["The log file follows after the next SSMS start; everything else uses the new folder immediately."] = "日志文件将在下次启动 SSMS 后跟随；其他数据立即使用新目录。",
            ["Data folder: {0}"] = "数据目录：{0}",
            ["Use the default data folder again? The files in the current folder are kept."] = "是否改回默认数据目录？当前目录中的文件会原样保留。",
            ["The default folder is used again: {0}"] = "已改回默认目录：{0}",
            ["The files in the previous folder were left untouched: {0}"] = "原目录中的文件未被改动：{0}",
            ["The folder could not be opened: {0}"] = "无法打开该文件夹：{0}",
            ["Enter a valid absolute folder path, for example D:\\MSSQLToolData."] = "请输入有效的绝对路径，例如 D:\\MSSQLToolData。",
            ["The folder cannot be used: {0}"] = "该目录不可用：{0}",
            ["The data folder setting could not be saved."] = "数据目录设置无法保存。",
            ["The configured data folder is not a valid absolute path: {0}"] = "配置的数据目录不是有效的绝对路径：{0}",
            ["The configured data folder is unavailable, so the default folder is used: {0}"] = "配置的数据目录不可用，已改用默认目录：{0}",
            ["Moved {0} file(s) to the new data folder:"] = "已复制 {0} 个文件到新数据目录：",
            ["Existing data could not be copied: {0}"] = "现有数据无法复制：{0}",
            ["file(s)"] = "个文件",
            ["Data folder"] = "数据目录",
            ["Settings"] = "设置",
            ["Logs"] = "日志",
            ["Query history"] = "查询历史",
           ["GitHub sync profiles"] = "GitHub 同步配置",
            ["Snippets"] = "代码片段",
            ["Query templates"] = "查询模板",

            // Column mapping failures that used to surface as a raw duplicate-key error.
            ["More than one source column is mapped to the same target column: {0}. Give every source column its own target column."] = "有多个源列映射到了同一个目标列：{0}。请为每个源列选择各自的目标列。",
            ["More than one Excel column is mapped to the same destination column: {0}. Give every Excel column its own destination column."] = "有多个 Excel 列映射到了同一个目标列：{0}。请为每个 Excel 列选择各自的目标列。",

            // Refactoring
            ["Qualify object names"] = "为对象名添加架构",
            ["Schema to add to unqualified table references:"] = "要添加到未限定表名的架构：",
            ["Apply to the whole script instead of the current statement"] = "应用到整篇脚本（而不是当前语句）",
            ["Rename \"{0}\" to:"] = "重命名 \"{0}\" 为：",
            ["There is no SQL text to rename."] = "没有可重命名的 SQL 文本。",
            ["Place the caret on a table, column, alias or variable name first."] = "请先把光标放在表、列、别名或变量名上。",
            ["Enter a new name."] = "请输入新名称。",
            ["The new name is too long (128 characters maximum)."] = "新名称过长（最多 128 个字符）。",
            ["The new name is the same as the current name."] = "新名称与当前名称相同。",
            ["There is no SQL text to qualify."] = "没有可为对象名添加架构的 SQL 文本。",
            ["Enter the schema name."] = "请输入架构名。",
            ["No unqualified table reference was found."] = "没有找到未限定架构的表引用。",
            ["Renamed {0} reference(s). Press Ctrl+Z to undo."] = "已重命名 {0} 处引用，按 Ctrl+Z 可撤销。",
            ["Could not rename: {0}"] = "无法重命名：{0}",
            ["Qualified {0} table reference(s) with the schema \"{1}\"."] = "已为 {0} 处表引用补上架构 \"{1}\"。",
            ["Could not qualify object names: {0}"] = "无法为对象名添加架构：{0}",

            // Query text history
            ["Execution History"] = "执行历史",
            ["Query Text History"] = "查询历史",
            ["Reading the SQL Prompt history database..."] = "正在读取 SQL Prompt 历史数据库…",
            ["{0}/{1} table(s) · {2} row(s) read · {3} imported · {4} already existed"] = "{0}/{1} 个表 · 已读 {2} 行 · 已导入 {3} 条 · 已存在 {4} 条",
            ["Cancelling..."] = "正在取消…",
            ["The import was cancelled."] = "导入已取消。",
            ["Last seen"] = "最近出现",
            ["Title"] = "标题",
            ["Connection"] = "连接",
            ["Insert into current query"] = "插入到当前查询",
            ["Open in a new query window"] = "在新查询窗口打开",
            ["Import SQL Prompt history..."] = "导入 SQL Prompt 历史...",
            ["Select the SQL Prompt history database"] = "选择 SQL Prompt 历史数据库",
            ["SQLite database (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|All files (*.*)|*.*"] = "SQLite 数据库 (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|所有文件 (*.*)|*.*",
            ["{0} query text(s) stored."] = "已存储 {0} 条查询文本。",
            ["{0} match(es) of {1} stored query text(s)."] = "在 {1} 条已存文本中匹配到 {0} 条。",
            ["Versions"] = "版本",
            ["Versions (select one to view)"] = "版本（点选可查看）",
            ["Unsaved tab"] = "未保存标签",
            ["File"] = "文件",
            ["Time:"] = "时间：",
            ["All time"] = "全部时间",
            ["Last 7 days"] = "近 7 天",
            ["Last 30 days"] = "近 30 天",
            ["Last 90 days"] = "近 90 天",
            ["This year"] = "今年",
            ["All servers"] = "全部服务器",
            ["All databases"] = "全部数据库",
            ["All sources"] = "全部来源",
            ["Editor snapshots"] = "编辑器快照",
            ["SQL Prompt imports"] = "SQL Prompt 导入",
            ["Delete version"] = "删除版本",
            ["Delete query"] = "删除该查询",
            ["Delete this version from the history?"] = "要从历史中删除这个版本吗？",
            ["Delete all {0} versions of this query?"] = "要删除这个查询的全部 {0} 个版本吗？",
            ["{0} queries, {1} versions stored."] = "共 {0} 个查询、{1} 个版本。",
            ["Matched {0} queries / {1} versions of {2} stored versions."] = "匹配 {0} 个查询 / {1} 个版本，共存储 {2} 个版本。",
            ["The versions could not be read: {0}"] = "无法读取版本：{0}",
            ["The query could not be deleted: {0}"] = "无法删除该查询：{0}",
            ["The history could not be read: {0}"] = "无法读取历史：{0}",
            ["Open a query window first."] = "请先打开一个查询窗口。",
            ["Inserted into the current query window."] = "已插入到当前查询窗口。",
            ["Opened in a new query window."] = "已在新查询窗口打开。",
            ["Could not open a new query window: {0}"] = "无法打开新查询窗口：{0}",
            ["Copied."] = "已复制。",
            ["Delete this query text from the history?"] = "要从历史中删除这条查询文本吗？",
            ["The entry could not be deleted: {0}"] = "无法删除该条目：{0}",
            ["The import failed: {0}"] = "导入失败：{0}",
            ["Imported {0} query text(s); {1} already existed."] = "已导入 {0} 条查询文本，{1} 条已存在。",

            // Results analytics
            ["Results analytics"] = "结果网格分析",
            ["No results grid with data is available."] = "当前没有包含数据的结果网格。",
            ["{0} row(s), {1} column(s) | empty cells {2}% | duplicate rows {3}"] = "{0} 行，{1} 列 ｜ 空值单元格 {2}% ｜ 重复行 {3}",
            ["Statistics use the first {0} row(s) to stay responsive."] = "为保证响应速度，统计基于前 {0} 行。",
            ["Statistics cover every row in the grid."] = "统计覆盖网格中的全部行。",
            ["Could not analyze the results grid: {0}"] = "无法分析结果网格：{0}",
            ["Filter rows:"] = "筛选行：",
            ["Export CSV..."] = "导出 CSV...",
            ["Statistics copied to the clipboard."] = "统计已复制到剪贴板。",
            ["Export results to CSV"] = "导出结果为 CSV",
            ["Exported {0} row(s) to {1}."] = "已导出 {0} 行到 {1}。",
            ["Showing the first {0} rows."] = "仅显示前 {0} 行。",
            ["{0} matching row(s)."] = "匹配 {0} 行。",
            ["{0} matching row(s), showing the first {1}."] = "匹配 {0} 行，仅显示前 {1} 行。",
            ["Column"] = "列",
            ["Non-null"] = "非空",
            ["Nulls"] = "空值",
            ["Null %"] = "空值%",
            ["Distinct"] = "去重",
            ["Min"] = "最小",
            ["Max"] = "最大",
            ["Average"] = "平均",
            ["Sum"] = "合计",
            ["Most common"] = "最常见",
        };



        // Core UI strings for the additional languages.  Anything not listed falls back to the
        // English key, so adding a language never leaves the UI blank.
        private static readonly Dictionary<string, Dictionary<string, string>> OtherUiTexts =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                [JapaneseLanguage] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Language"] = "言語", ["Interface language:"] = "表示言語：", ["Apply"] = "適用",
                    ["Simplified Chinese"] = "簡体字中国語", ["Traditional Chinese"] = "繁体字中国語", ["English"] = "英語",
                    ["The language setting applies immediately to MSSQL Tool windows and menu/toolbar labels."] = "言語設定は MSSQL Tool のウィンドウとメニュー/ツールバーにすぐに適用されます。",
                    ["Save"] = "保存", ["Settings"] = "設定", ["Storage"] = "保存先", ["Keyboard shortcuts"] = "キーボード ショートカット",
                    ["SQL Completion"] = "SQL 入力候補", ["Result panel"] = "結果パネル", ["Code Format"] = "コード書式",
                    ["Connection Colors"] = "接続の色", ["Updates"] = "更新", ["Diagnostics"] = "診断",
                    ["OpenAI - ChatGPT integration"] = "OpenAI - ChatGPT 連携",
                    ["Result statistics panel"] = "結果セット統計パネル",
                    ["Show the result statistics panel when I click the result grid"] = "結果グリッドをクリックしたときに結果セット統計パネルを表示する",
                    ["When this is off, you can still open the panel with the [Result panel] toolbar button."] = "オフでも、ツールバーの [結果パネル] ボタンからパネルを開けます。",
                    ["The result panel setting could not be saved."] = "結果パネルの設定を保存できませんでした。",
                    ["The change has been saved"] = "変更を保存しました", ["Setting saved"] = "設定を保存しました",
                    ["Recent (10)"] = "最近 (10)", ["Frequently used"] = "よく使う",
                    ["Add to frequently used"] = "よく使うに追加", ["Remove from frequently used"] = "よく使うから削除",
                    ["Added to frequently used."] = "よく使うに追加しました。", ["Removed from frequently used."] = "よく使うから削除しました。",
                    ["Query Templates"] = "クエリ テンプレート", ["Insert"] = "挿入", ["Open in new query"] = "新しいクエリで開く",
                    ["Close"] = "閉じる", ["Refresh"] = "更新", ["Options"] = "オプション", ["Tags…"] = "タグ…",
                    ["New category"] = "新しいカテゴリ", ["New statement"] = "新しいステートメント", ["Rename"] = "名前の変更",
                    ["Delete"] = "削除", ["Open folder"] = "フォルダーを開く", ["Search:"] = "検索：",
                    ["Previous"] = "前へ", ["Next"] = "次へ", ["Copy SQL"] = "SQL をコピー", ["Wrap lines"] = "折り返し"
                },
                [KoreanLanguage] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Language"] = "언어", ["Interface language:"] = "인터페이스 언어:", ["Apply"] = "적용",
                    ["Simplified Chinese"] = "중국어(간체)", ["Traditional Chinese"] = "중국어(번체)", ["English"] = "영어",
                    ["The language setting applies immediately to MSSQL Tool windows and menu/toolbar labels."] = "언어 설정은 MSSQL Tool 창과 메뉴/도구 모음 레이블에 즉시 적용됩니다.",
                    ["Save"] = "저장", ["Settings"] = "설정", ["Storage"] = "저장소", ["Keyboard shortcuts"] = "키보드 단축키",
                    ["SQL Completion"] = "SQL 완성", ["Result panel"] = "결과 패널", ["Code Format"] = "코드 서식",
                    ["Connection Colors"] = "연결 색", ["Updates"] = "업데이트", ["Diagnostics"] = "진단",
                    ["OpenAI - ChatGPT integration"] = "OpenAI - ChatGPT 통합",
                    ["Result statistics panel"] = "결과 집합 통계 패널",
                    ["Show the result statistics panel when I click the result grid"] = "결과 표를 클릭하면 결과 집합 통계 패널 표시",
                    ["When this is off, you can still open the panel with the [Result panel] toolbar button."] = "꺼져 있어도 도구 모음의 [결과 패널] 버튼으로 패널을 열 수 있습니다.",
                    ["The result panel setting could not be saved."] = "결과 패널 설정을 저장하지 못했습니다.",
                    ["The change has been saved"] = "변경 사항이 저장되었습니다", ["Setting saved"] = "설정이 저장되었습니다",
                    ["Recent (10)"] = "최근 (10)", ["Frequently used"] = "자주 사용",
                    ["Add to frequently used"] = "자주 사용에 추가", ["Remove from frequently used"] = "자주 사용에서 제거",
                    ["Added to frequently used."] = "자주 사용에 추가했습니다.", ["Removed from frequently used."] = "자주 사용에서 제거했습니다.",
                    ["Query Templates"] = "쿼리 템플릿", ["Insert"] = "삽입", ["Open in new query"] = "새 쿼리에서 열기",
                    ["Close"] = "닫기", ["Refresh"] = "새로 고침", ["Options"] = "옵션", ["Tags…"] = "태그…",
                    ["New category"] = "새 범주", ["New statement"] = "새 문", ["Rename"] = "이름 바꾸기",
                    ["Delete"] = "삭제", ["Open folder"] = "폴더 열기", ["Search:"] = "검색:",
                    ["Previous"] = "이전", ["Next"] = "다음", ["Copy SQL"] = "SQL 복사", ["Wrap lines"] = "줄 바꿈"
                },
                [GermanLanguage] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Language"] = "Sprache", ["Interface language:"] = "Oberflächensprache:", ["Apply"] = "Anwenden",
                    ["Simplified Chinese"] = "Vereinfachtes Chinesisch", ["Traditional Chinese"] = "Traditionelles Chinesisch", ["English"] = "Englisch",
                    ["The language setting applies immediately to MSSQL Tool windows and menu/toolbar labels."] = "Die Spracheinstellung gilt sofort für MSSQL Tool-Fenster sowie Menü- und Symbolleistenbeschriftungen.",
                    ["Save"] = "Speichern", ["Settings"] = "Einstellungen", ["Storage"] = "Speicher", ["Keyboard shortcuts"] = "Tastenkombinationen",
                    ["SQL Completion"] = "SQL-Vervollständigung", ["Result panel"] = "Ergebnisbereich", ["Code Format"] = "Codeformat",
                    ["Connection Colors"] = "Verbindungsfarben", ["Updates"] = "Updates", ["Diagnostics"] = "Diagnose",
                    ["OpenAI - ChatGPT integration"] = "OpenAI - ChatGPT-Integration",
                    ["Result statistics panel"] = "Ergebnisstatistik",
                    ["Show the result statistics panel when I click the result grid"] = "Ergebnisstatistik beim Klick auf das Ergebnisraster anzeigen",
                    ["When this is off, you can still open the panel with the [Result panel] toolbar button."] = "Wenn dies deaktiviert ist, lässt sich der Bereich weiterhin über die Symbolleisten-Schaltfläche [Ergebnisbereich] öffnen.",
                    ["The result panel setting could not be saved."] = "Die Einstellung für den Ergebnisbereich konnte nicht gespeichert werden.",
                    ["The change has been saved"] = "Die Änderung wurde gespeichert", ["Setting saved"] = "Einstellung gespeichert",
                    ["Recent (10)"] = "Zuletzt (10)", ["Frequently used"] = "Häufig verwendet",
                    ["Add to frequently used"] = "Zu häufig verwendet hinzufügen", ["Remove from frequently used"] = "Aus häufig verwendet entfernen",
                    ["Added to frequently used."] = "Zu häufig verwendet hinzugefügt.", ["Removed from frequently used."] = "Aus häufig verwendet entfernt.",
                    ["Query Templates"] = "Abfragevorlagen", ["Insert"] = "Einfügen", ["Open in new query"] = "In neuer Abfrage öffnen",
                    ["Close"] = "Schließen", ["Refresh"] = "Aktualisieren", ["Options"] = "Optionen", ["Tags…"] = "Tags…",
                    ["New category"] = "Neue Kategorie", ["New statement"] = "Neue Anweisung", ["Rename"] = "Umbenennen",
                    ["Delete"] = "Löschen", ["Open folder"] = "Ordner öffnen", ["Search:"] = "Suchen:",
                    ["Previous"] = "Zurück", ["Next"] = "Weiter", ["Copy SQL"] = "SQL kopieren", ["Wrap lines"] = "Zeilen umbrechen"
                },
                [FrenchLanguage] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Language"] = "Langue", ["Interface language:"] = "Langue de l'interface :", ["Apply"] = "Appliquer",
                    ["Simplified Chinese"] = "Chinois simplifié", ["Traditional Chinese"] = "Chinois traditionnel", ["English"] = "Anglais",
                    ["The language setting applies immediately to MSSQL Tool windows and menu/toolbar labels."] = "Le paramètre de langue s'applique immédiatement aux fenêtres MSSQL Tool et aux libellés des menus/barres d'outils.",
                    ["Save"] = "Enregistrer", ["Settings"] = "Paramètres", ["Storage"] = "Stockage", ["Keyboard shortcuts"] = "Raccourcis clavier",
                    ["SQL Completion"] = "Complétion SQL", ["Result panel"] = "Panneau de résultats", ["Code Format"] = "Format du code",
                    ["Connection Colors"] = "Couleurs de connexion", ["Updates"] = "Mises à jour", ["Diagnostics"] = "Diagnostics",
                    ["OpenAI - ChatGPT integration"] = "Intégration OpenAI - ChatGPT",
                    ["Result statistics panel"] = "Statistiques du jeu de résultats",
                    ["Show the result statistics panel when I click the result grid"] = "Afficher les statistiques lorsque je clique sur la grille de résultats",
                    ["When this is off, you can still open the panel with the [Result panel] toolbar button."] = "Lorsque cette option est désactivée, le panneau reste accessible via le bouton [Panneau de résultats] de la barre d'outils.",
                    ["The result panel setting could not be saved."] = "Le paramètre du panneau de résultats n'a pas pu être enregistré.",
                    ["The change has been saved"] = "La modification a été enregistrée", ["Setting saved"] = "Paramètre enregistré",
                    ["Recent (10)"] = "Récents (10)", ["Frequently used"] = "Fréquemment utilisés",
                    ["Add to frequently used"] = "Ajouter aux fréquemment utilisés", ["Remove from frequently used"] = "Retirer des fréquemment utilisés",
                    ["Added to frequently used."] = "Ajouté aux fréquemment utilisés.", ["Removed from frequently used."] = "Retiré des fréquemment utilisés.",
                    ["Query Templates"] = "Modèles de requête", ["Insert"] = "Insérer", ["Open in new query"] = "Ouvrir dans une nouvelle requête",
                    ["Close"] = "Fermer", ["Refresh"] = "Actualiser", ["Options"] = "Options", ["Tags…"] = "Étiquettes…",
                    ["New category"] = "Nouvelle catégorie", ["New statement"] = "Nouvelle instruction", ["Rename"] = "Renommer",
                    ["Delete"] = "Supprimer", ["Open folder"] = "Ouvrir le dossier", ["Search:"] = "Rechercher :",
                    ["Previous"] = "Précédent", ["Next"] = "Suivant", ["Copy SQL"] = "Copier le SQL", ["Wrap lines"] = "Retour à la ligne"
                },
                [SpanishLanguage] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Language"] = "Idioma", ["Interface language:"] = "Idioma de la interfaz:", ["Apply"] = "Aplicar",
                    ["Simplified Chinese"] = "Chino simplificado", ["Traditional Chinese"] = "Chino tradicional", ["English"] = "Inglés",
                    ["The language setting applies immediately to MSSQL Tool windows and menu/toolbar labels."] = "La configuración de idioma se aplica inmediatamente a las ventanas de MSSQL Tool y a las etiquetas de menús/barras de herramientas.",
                    ["Save"] = "Guardar", ["Settings"] = "Configuración", ["Storage"] = "Almacenamiento", ["Keyboard shortcuts"] = "Atajos de teclado",
                    ["SQL Completion"] = "Finalización SQL", ["Result panel"] = "Panel de resultados", ["Code Format"] = "Formato de código",
                    ["Connection Colors"] = "Colores de conexión", ["Updates"] = "Actualizaciones", ["Diagnostics"] = "Diagnóstico",
                    ["OpenAI - ChatGPT integration"] = "Integración OpenAI - ChatGPT",
                    ["Result statistics panel"] = "Estadísticas del conjunto de resultados",
                    ["Show the result statistics panel when I click the result grid"] = "Mostrar el panel de estadísticas al hacer clic en la cuadrícula de resultados",
                    ["When this is off, you can still open the panel with the [Result panel] toolbar button."] = "Si está desactivado, aún puede abrir el panel con el botón [Panel de resultados] de la barra de herramientas.",
                    ["The result panel setting could not be saved."] = "No se pudo guardar la configuración del panel de resultados.",
                    ["The change has been saved"] = "El cambio se ha guardado", ["Setting saved"] = "Configuración guardada",
                    ["Recent (10)"] = "Recientes (10)", ["Frequently used"] = "Usados con frecuencia",
                    ["Add to frequently used"] = "Añadir a usados con frecuencia", ["Remove from frequently used"] = "Quitar de usados con frecuencia",
                    ["Added to frequently used."] = "Añadido a usados con frecuencia.", ["Removed from frequently used."] = "Quitado de usados con frecuencia.",
                    ["Query Templates"] = "Plantillas de consulta", ["Insert"] = "Insertar", ["Open in new query"] = "Abrir en nueva consulta",
                    ["Close"] = "Cerrar", ["Refresh"] = "Actualizar", ["Options"] = "Opciones", ["Tags…"] = "Etiquetas…",
                    ["New category"] = "Nueva categoría", ["New statement"] = "Nueva instrucción", ["Rename"] = "Cambiar nombre",
                    ["Delete"] = "Eliminar", ["Open folder"] = "Abrir carpeta", ["Search:"] = "Buscar:",
                    ["Previous"] = "Anterior", ["Next"] = "Siguiente", ["Copy SQL"] = "Copiar SQL", ["Wrap lines"] = "Ajustar líneas"
                }
            };

        /// <summary>
        /// The language used until the user picks one.  The XAML carries English text as the key, so
        /// English is the zero-translation default and needs no dictionary entry.
        /// </summary>
        public static string CurrentLanguage { get; private set; } = EnglishLanguage;
        public static bool IsChinese => CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        public static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return EnglishLanguage;
            foreach (string supported in SupportedLanguages)
                if (string.Equals(supported, language.Trim(), StringComparison.OrdinalIgnoreCase))
                    return supported;
            // An unsupported or corrupted value must not silently switch the UI to Chinese.
            return EnglishLanguage;
        }

        public static void Initialize()
        {
            SetLanguage(SettingsManager.GetUiLanguage(), false);
            EnableAutomaticLocalization();
        }

        private static void EnableAutomaticLocalization()
        {
            if (automaticLocalizationEnabled) return;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnAutomaticLocalizationLoaded));
            EventManager.RegisterClassHandler(typeof(UserControl), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnAutomaticLocalizationLoaded));
            automaticLocalizationEnabled = true;
        }

        private static void OnAutomaticLocalizationLoaded(object sender, RoutedEventArgs args)
        {
            var root = sender as DependencyObject;
            if (root != null && IsExtensionUiType(root.GetType()))
            {
                Apply(root);
            }
        }

        internal static bool IsExtensionUiType(Type type)
        {
            return type != null && type.Assembly == typeof(LocalizationManager).Assembly;
        }

        public static void SetLanguage(string language, bool persist = true)
        {
            CurrentLanguage = NormalizeLanguage(language);
            var culture = CultureInfo.GetCultureInfo(CurrentLanguage);
            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            if (persist)
            {
                SettingsManager.SaveUiLanguage(CurrentLanguage);
            }
            ApplyOpenWindows();
        }

        private static void ApplyOpenWindows()
        {
            try
            {
                if (System.Windows.Application.Current == null) return;
                foreach (Window window in System.Windows.Application.Current.Windows)
                {
                    if (window != null && IsExtensionUiType(window.GetType())) Apply(window);
                }
            }
            catch (Exception)
            {
            }
        }

        public static string T(string text)
        {
            if (string.IsNullOrEmpty(text) || CurrentLanguage == EnglishLanguage)
            {
                return text;
            }

            if (CurrentLanguage == ChineseLanguage)
            {
                if (Chinese.TryGetValue(text, out string chinese)) return chinese;
                return TranslateDynamic(text);
            }

            if (CurrentLanguage == TraditionalChineseLanguage)
            {
                if (Chinese.TryGetValue(text, out string chinese)) return ToTraditional(chinese);
                return text;
            }

            if (OtherUiTexts.TryGetValue(CurrentLanguage, out var strings)
                && strings.TryGetValue(text, out string translated))
            {
                return translated;
            }

            if (QuickSearchLocalization.TryGet(CurrentLanguage, text, out string quickSearch))
            {
                return quickSearch;
            }

            // New languages fall back to the English source for strings that are not translated yet.
            return text;
        }

        private static string ToTraditional(string text)
        {
            try
            {
                var buffer = new StringBuilder(text.Length * 2 + 16);
                int length = LCMapStringEx("zh-TW", LcmapTraditionalChinese, text, text.Length,
                    buffer, buffer.Capacity, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                return length > 0 ? buffer.ToString(0, length) : text;
            }
            catch (Exception)
            {
                return text;
            }
        }

        private const uint LcmapTraditionalChinese = 0x04000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
            StringBuilder destination, int destinationLength, IntPtr version, IntPtr reserved, IntPtr parameter);

        /// <summary>
        /// True when <paramref name="text"/> has an entry in the Chinese dictionary.  The entry may
        /// deliberately be identical to the key (product names, "English", "Wiki"), so this is not
        /// the same as "the translation differs from the key".
        /// </summary>
        internal static bool HasChineseEntry(string text)
            => !string.IsNullOrEmpty(text) && Chinese.ContainsKey(text);

        /// <summary>The Chinese text for <paramref name="text"/>, or null when there is no entry.</summary>
        internal static string ChineseTranslation(string text)
            => !string.IsNullOrEmpty(text) && Chinese.TryGetValue(text, out string value) ? value : null;

        private static string TranslateDynamic(string text)
        {
            // Messages that carry a trailing detail: the prefix is translated, the detail is kept.
            string prefix;
            prefix = "The advanced formatting options could not be opened: ";
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return "无法打开高级格式化选项：" + text.Substring(prefix.Length);
            prefix = "The profile could not be imported: ";
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return "无法导入配置：" + text.Substring(prefix.Length);
            prefix = "The profile could not be exported: ";
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return "无法导出配置：" + text.Substring(prefix.Length);
            prefix = "Refresh failed: ";
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return "刷新失败：" + text.Substring(prefix.Length);

            if (text.StartsWith("Authorization failed: ", StringComparison.Ordinal)) return "授权失败：" + text.Substring(22);
            if (text.StartsWith("Import failed: ", StringComparison.Ordinal)) return "导入失败：" + text.Substring(15);
            if (text.StartsWith("Search failed: ", StringComparison.Ordinal)) return "搜索失败：" + text.Substring(15);
            if (text.StartsWith("Scripting failed: ", StringComparison.Ordinal)) return "生成脚本失败：" + text.Substring(18);
            if (text.StartsWith("The shortcut could not be applied: ", StringComparison.Ordinal)) return "无法应用快捷键：" + text.Substring(35);
            if (text.StartsWith("Something went wrong: ", StringComparison.Ordinal)) return "发生错误：" + text.Substring(22);
            if (text.StartsWith("Error loading data: ", StringComparison.Ordinal)) return "加载数据时出错：" + text.Substring(20);
            if (text.StartsWith("Server: ", StringComparison.Ordinal)) return text.Replace("Server: ", "服务器：").Replace("Database: ", "数据库：").Replace("User ID: ", "用户 ID：");
            if (text.StartsWith("Rows copied: ", StringComparison.Ordinal)) return text.Replace("Rows copied: ", "已复制行数：").Replace(" in ", "，耗时 ").Replace(" sec.", " 秒");
            if (text.StartsWith("Completed | Total rows copied: ", StringComparison.Ordinal)) return text.Replace("Completed | Total rows copied: ", "已完成 | 总复制行数：").Replace(" in ", "，耗时 ").Replace(" sec.", " 秒");
            if (text.StartsWith("Searching [", StringComparison.Ordinal)) return text.Replace("Searching [", "正在搜索 [");
            if (text.StartsWith("File (", StringComparison.Ordinal)) return text.Replace("File (", "文件（").Replace("):", "）：");
            if (text.StartsWith("The category no longer exists: ", StringComparison.Ordinal)) return "分类已不存在：" + text.Substring(31);
            if (text.StartsWith("Templates folder is currently unavailable: ", StringComparison.Ordinal)) return "模板文件夹当前不可用：" + text.Substring(43);
            if (text.StartsWith("The path is outside the configured templates folder: ", StringComparison.Ordinal)) return "路径超出模板文件夹范围：" + text.Substring(53);
            if (text.StartsWith("Could not read the current connection: ", StringComparison.Ordinal)) return "无法读取当前连接：" + text.Substring(39);
            if (text.StartsWith("Could not open the query: ", StringComparison.Ordinal)) return "无法打开查询：" + text.Substring(26);
            if (text.StartsWith("SQL completion metadata refresh failed: ", StringComparison.Ordinal)) return "SQL 补全元数据刷新失败：" + text.Substring(40);
            if (text.StartsWith("Copy failed: ", StringComparison.Ordinal)) return "复制失败：" + text.Substring(13);
            return text;
        }

        public static string Format(string text, params object[] args) => string.Format(T(text), args);

        public static void Apply(DependencyObject root)
        {
            ApplyCore(root, new HashSet<DependencyObject>(new ReferenceComparer()));
        }

        private static void ApplyCore(DependencyObject root, HashSet<DependencyObject> visited)
        {
            if (root == null || !visited.Add(root)) return;
            TranslateObject(root);

            // Translating a TextBlock/Run or Header can rebuild its logical child
            // collection. Snapshot both trees before recursion so a descendant
            // translation cannot invalidate the parent's live enumerator.
            var children = new List<DependencyObject>();
            if (root is Visual || root is System.Windows.Media.Media3D.Visual3D)
            {
                int count = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < count; i++) children.Add(VisualTreeHelper.GetChild(root, i));
            }

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject dependencyObject) children.Add(dependencyObject);
            }

            // Context menus live in their own popup tree, so they are never
            // reached through the window's visual/logical children.
            if (root is FrameworkElement frameworkElement && frameworkElement.ContextMenu != null)
            {
                ApplyCore(frameworkElement.ContextMenu, visited);
            }

            foreach (DependencyObject child in children) ApplyCore(child, visited);
        }

        private static void TranslateObject(DependencyObject item)
        {
            if (item is Window window) Translate(item, "Title", () => window.Title, v => window.Title = v);
            if (item is TextBlock textBlock) Translate(item, "Text", () => textBlock.Text, v => textBlock.Text = v);
            if (item is Run run) Translate(item, "RunText", () => run.Text, v => run.Text = v);
            if (item is ContentControl contentControl && contentControl.Content is string)
                Translate(item, "Content", () => (string)contentControl.Content, v => contentControl.Content = v);
            if (item is HeaderedContentControl headered && headered.Header is string)
                Translate(item, "Header", () => (string)headered.Header, v => headered.Header = v);
            if (item is HeaderedItemsControl headeredItems && headeredItems.Header is string)
                Translate(item, "Header", () => (string)headeredItems.Header, v => headeredItems.Header = v);
            if (item is FrameworkElement element && element.ToolTip is string)
                Translate(item, "ToolTip", () => (string)element.ToolTip, v => element.ToolTip = v);
            if (item is GridViewColumnHeader columnHeader && columnHeader.Content is string)
                Translate(item, "ColumnHeader", () => (string)columnHeader.Content, v => columnHeader.Content = v);
        }

        private static void Translate(DependencyObject item, string property, Func<string> getter, Action<string> setter)
        {
            OriginalValues originals = Originals.GetValue(item, _ => new OriginalValues());
            string current = getter();
            if (!originals.Values.TryGetValue(property, out string original)
                || !originals.LastApplied.TryGetValue(property, out string lastApplied)
                || !string.Equals(current, lastApplied, StringComparison.Ordinal))
            {
                original = current;
                originals.Values[property] = original;
            }
            string localized = CurrentLanguage == EnglishLanguage ? original : T(original);
            // Assigning even an identical value replaces a WPF Binding with a local
            // value. Avoid doing that for untranslated or already-localized text.
            if (!string.Equals(current, localized, StringComparison.Ordinal))
            {
                setter(localized);
            }
            originals.LastApplied[property] = localized;
        }
        // Strings added after the main table was frozen.  Kept separate so a key that was already
        // translated is simply overwritten instead of throwing a duplicate-key exception.
        private static readonly Dictionary<string, string> ExtraChinese = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Result set"] = "结果集",
            ["Result {0}: {1} row(s) x {2} column(s)"] = "结果集 {0}：{1} 行 × {2} 列",
            ["Filter every column (Ctrl+F)"] = "在所有列中筛选（Ctrl+F）",
            ["Apply"] = "应用",
            ["Clear"] = "清除",
            ["Columns..."] = "显示列…",
            ["Export JSON..."] = "导出 JSON...",
            ["Export INSERT..."] = "导出 INSERT 脚本...",
            ["Sort ascending"] = "升序排序",
            ["Sort descending"] = "降序排序",
            ["Hide column"] = "隐藏列",
            ["Copy column name"] = "复制列名",
            ["Freeze up to this column"] = "冻结到此列",
            ["Unfreeze columns"] = "取消冻结",
            ["Show all columns"] = "显示所有列",
            ["Top values"] = "取值分布",
            ["Top values: {0}"] = "取值分布：{0}",
            ["Column {0} has no values to list."] = "列 {0} 没有可列出的取值。",
            ["Column statistics"] = "列统计",
            ["Max length"] = "最大长度",
            ["Cell value"] = "单元格值",
            ["Cell value: {0}"] = "单元格值：{0}",
            ["{0} | {1} | {2} character(s)"] = "{0} | {1} | {2} 个字符",
            ["Copy"] = "复制",
            ["Close"] = "关闭",
            ["OK"] = "确定",
            ["Cancel"] = "取消",
            ["Table name"] = "表名",
            ["Export INSERT script"] = "导出 INSERT 脚本",
            ["Export results to JSON"] = "导出结果为 JSON",
            ["Export results to an INSERT script"] = "导出结果为 INSERT 脚本",
            ["Contains"] = "包含",
            ["Equals"] = "等于",
            ["Not equals"] = "不等于",
            ["Starts with"] = "开头是",
            ["Ends with"] = "结尾是",
            ["Greater than"] = "大于",
            ["Less than"] = "小于",
            ["All columns"] = "所有列",
            ["Choose which columns are visible."] = "选择要显示的列。",
            ["Freeze columns:"] = "固定列：",
            ["No fixed columns"] = "不固定",
            ["First {0} column(s)"] = "前 {0} 列",
            ["Pin the first N visible columns so they stay on screen while scrolling right."] = "固定最左侧的 N 个可见列，向右滚动时保持可见。",
            ["Find a column by name or type; press Enter to jump to the next matching column."] = "按列名或类型查找列，按 Enter 跳到下一个匹配列。",
            ["Column '{0}'  ({1} of {2})"] = "列「{0}」（第 {1} 列 / 共 {2} 列）",

            ["Analyzing the result set..."] = "正在分析结果集…",
            ["Select cells to see their statistics."] = "选中单元格可查看其统计。",
            ["Selection: {0} cell(s), {1} row(s), {2} column(s), {3} non-null, {4} empty, {5} distinct, longest {6} character(s){7}"] = "选中 {0} 个单元格，{1} 行，{2} 列，非空 {3}，空值 {4}，去重 {5}，最长 {6} 个字符{7}",
            [" | numeric {0}: sum {1}, average {2}, min {3}, max {4}"] = "｜数值 {0} 个：合计 {1}，平均 {2}，最小 {3}，最大 {4}",

            ["Choose an Excel file and worksheet to see a preview."] = "选择 Excel 文件和 sheet 才能预览。",
            ["Choose an Excel file and worksheet, then select an existing destination table to map its columns."] = "选择 Excel 文件和 sheet，并选择一个已存在的目标表来映射列。",
            ["Common causes: a column name does not exist, or the query syntax is invalid."] = "常见原因：列名不存在，或查询语法无效。",
            ["Could not read the workbook: "] = "无法读取工作簿：",
            ["Fix"] = "修复",
            ["Jump to column"] = "跳转到列",
            ["No result set is available. Run a query first."] = "没有可用的结果集。请先运行查询。",
            ["Preview cancelled."] = "预览已取消。",
            ["Preview failed"] = "预览失败",
            ["Preview failed: "] = "预览失败：",
            ["Previewing..."] = "正在预览...",
            ["Schema Compare"] = "结构对比",
            ["Select a source first."] = "请先选择源。",
            ["Select source and target, then enter an existing target table to map columns."] = "选择源和目标，然后输入已存在的目标表以映射列。",
            ["The destination table must exist (with 'Automatically create the table' cleared) to configure column mapping."] = "目标表必须已存在（取消勾选“自动创建表”）才能配置列映射。",
            ["The destination table will be created automatically with the Excel column names; column mapping is not required."] = "目标表将按 Excel 列名自动创建，无需列映射。",
            ["The target table will be created from the source query columns; column mapping is not applied."] = "目标表将按源查询的列创建，不使用列映射。",
            ["The workbook does not contain any worksheets."] = "工作簿不包含任何工作表。",
            ["The worksheet does not contain any data rows."] = "工作表不包含任何数据行。",

            ["Editor"] = "编辑器",
            ["Editor background image"] = "编辑器背景图",
            ["Show a background image in SQL editor windows"] = "在 SQL 编辑器窗口中显示背景图",
            ["Picture"] = "图片",
            ["Sage breeze (default)"] = "青绿晨风（默认）",
            ["Crimson horizon"] = "绯红天际",
            ["Noir gold"] = "暗夜镏金",
            ["Violet starlight"] = "紫夜星海",
            ["Spark notebook"] = "灵感手记",
            ["Rose dawn"] = "玫瑰晨曦",
            ["Custom picture..."] = "自定义图片…",
            ["Transparency"] = "透明度",
            ["Fill mode"] = "填充方式",
            ["Crop to fill (recommended)"] = "等比铺满（推荐）",
            ["Stretch to fill"] = "拉伸铺满",
            ["Fit inside the window"] = "等比适应",
            ["Tile"] = "平铺",
            ["Dim the picture on dark themes"] = "暗色主题下再压暗一点",
            ["Draw the picture as one layer behind the editor"] = "整张图作为一层放在编辑器背后（推荐）",
            ["One picture covers the code area, the margins and the strip below the text, so there is no seam. Turn this off if the editor turns black on this build; the picture is then painted into the text layer instead."] =
                "一张图同时覆盖正文、左边距和正文下方，不会出现接缝。若这个版本下编辑器变成全黑，取消勾选即可改为把图画进文本层。",
            ["Use the theme background"] = "恢复主题背景",
            ["Choose a background picture"] = "选择背景图片",
            ["The editor background setting could not be saved."] = "编辑器背景设置保存失败。",
            ["The picture is painted behind the code only; the query text and its colours are unchanged. Keep the transparency high so syntax colours stay readable. The setting applies to every open SQL editor immediately."] =
                "图片只画在代码后面，查询文本和语法颜色不受影响。透明度建议保持较高，以免影响可读性；设置立即应用到所有已打开的 SQL 编辑器。",
        };

        static LocalizationManager()
        {
            foreach (KeyValuePair<string, string> pair in ExtraChinese)
            {
                Chinese[pair.Key] = pair.Value;
            }
        }

    }

    internal static class LocalizedMessageBox
    {
        public static MessageBoxResult Show(string messageBoxText) => MessageBox.Show(LocalizationManager.T(messageBoxText));
        public static MessageBoxResult Show(string messageBoxText, string caption) => MessageBox.Show(LocalizationManager.T(messageBoxText), LocalizationManager.T(caption));
        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) => MessageBox.Show(LocalizationManager.T(messageBoxText), LocalizationManager.T(caption), button);
        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) => MessageBox.Show(LocalizationManager.T(messageBoxText), LocalizationManager.T(caption), button, icon);
        public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) => MessageBox.Show(owner, LocalizationManager.T(messageBoxText), LocalizationManager.T(caption), button, icon);
    }
}