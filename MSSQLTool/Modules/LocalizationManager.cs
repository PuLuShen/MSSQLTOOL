using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
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
        public const string EnglishLanguage = "en-US";

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
            ["MSSQL Tool - Settings"] = "MSSQL Tool - 设置",
            ["Settings"] = "设置", ["About"] = "关于", ["Tools"] = "工具",
            ["Language"] = "语言", ["Interface language:"] = "界面语言：",
            ["Simplified Chinese"] = "简体中文", ["English"] = "English",
            ["The language setting applies immediately to MSSQL Tool windows. Toolbar command labels use the package default language."] = "语言设置会立即应用到 MSSQL Tool 窗口。工具栏命令标签使用扩展包的默认语言。",
            ["Apply"] = "应用", ["Save"] = "保存", ["Cancel"] = "取消", ["Close"] = "关闭",
            ["OK"] = "确定", ["Yes"] = "是", ["No"] = "否", ["Edit"] = "编辑",
            ["Delete"] = "删除", ["Remove"] = "移除", ["Refresh"] = "刷新",
            ["Search"] = "搜索", ["Loading..."] = "正在加载…", ["Error"] = "错误",
            ["Opening definition script: {0}"] = "正在打开定义脚本：{0}",
            ["Loading SQL completion metadata..."] = "正在加载 SQL 补全缓存…",
            ["Loading SQL completion metadata for database {0}..."] = "正在加载数据库 {0} 的 SQL 补全缓存…",
            ["Loading SQL completion metadata from linked server {0}..."] = "正在加载链接服务器 {0} 的 SQL 补全缓存…",
            ["Refreshing SQL completion metadata..."] = "正在刷新 SQL 补全缓存…",
            ["SQL completion metadata refreshed."] = "SQL 补全缓存已刷新。",
            ["Warning"] = "警告", ["Success"] = "成功", ["Status"] = "状态",
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
            ["Indent size:"] = "缩进空格数：",
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
            ["Source query"] = "源查询", ["Formatted query"] = "格式化后的查询",
            ["Excel Export"] = "Excel 导出", ["Google Sheets"] = "Google 表格",
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
            ["Trust the SQL Server certificate for completion metadata"] = "补全元数据连接信任 SQL Server 证书",
            ["Applies only to the background connection used to load completion metadata."] = "仅应用于加载补全元数据的后台连接。",
            ["Insert square brackets"] = "插入方括号",
            ["Learn completion ranking locally"] = "在本地学习补全排序",
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
            ["MSSQL Tool keeps its settings, query history, completion ranking, GitHub sync profiles, snippets and log files in one folder. Point that folder at another drive, a synced folder or a portable device to keep the data outside your Windows profile. The pointer itself stays in the registry, and every setting is mirrored there, so the default location keeps working if the folder is unavailable."] = "MSSQL Tool 会把设置、查询历史、补全排序、GitHub 同步配置、代码片段和日志统一存放在一个目录中。将该目录指向其他磁盘、同步文件夹或便携设备，即可让这些数据脱离 Windows 用户配置文件。目录指针本身仍保存在注册表中，并且每项设置都会在注册表留一份镜像，因此该目录不可用时会自动回到默认位置继续工作。",
            ["Data folder:"] = "数据目录：",
            ["Restore default"] = "恢复默认值",
            ["Select the folder MSSQL Tool should keep its data in"] = "选择 MSSQL Tool 存放数据的目录",
            ["Copy existing data to the new folder"] = "将现有数据复制到新目录",
            ["Locations in use"] = "当前使用的位置",
            ["Changes apply immediately: the completion ranking, query history, snippets and settings move to the new folder at once. The log file follows after the next SSMS start."] = "修改立即生效：补全排序、查询历史、代码片段和设置会立即切换到新目录；日志文件在下次启动 SSMS 后跟随。",
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
            ["Completion ranking"] = "补全排序",
            ["GitHub sync profiles"] = "GitHub 同步配置",
            ["Snippets"] = "代码片段",
        };

        public static string CurrentLanguage { get; private set; } = ChineseLanguage;
        public static bool IsChinese => CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

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
            CurrentLanguage = string.Equals(language, EnglishLanguage, StringComparison.OrdinalIgnoreCase)
                ? EnglishLanguage
                : ChineseLanguage;
            var culture = CultureInfo.GetCultureInfo(CurrentLanguage);
            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            if (persist)
            {
                SettingsManager.SaveUiLanguage(CurrentLanguage);
            }
        }

        public static string T(string text)
        {
            if (string.IsNullOrEmpty(text) || !IsChinese)
            {
                return text;
            }

            if (Chinese.TryGetValue(text, out string translated)) return translated;
            return TranslateDynamic(text);
        }

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
            string localized = IsChinese ? T(original) : original;
            // Assigning even an identical value replaces a WPF Binding with a local
            // value. Avoid doing that for untranslated or already-localized text.
            if (!string.Equals(current, localized, StringComparison.Ordinal))
            {
                setter(localized);
            }
            originals.LastApplied[property] = localized;
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
