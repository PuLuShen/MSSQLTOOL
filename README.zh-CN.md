# MSSQL Tool：SSMS 22 效率扩展

[English](README.md) | [简体中文](README.zh-CN.md)

[![从 VSIX Gallery 安装](https://ssmsgallery.azurewebsites.net/badge/MSSQLTool.svg)](https://ssmsgallery.azurewebsites.net/extension/MSSQLTool/) [![Open VSIX Gallery](https://www.vsixgallery.com/badge/MSSQLTool.svg)](https://www.vsixgallery.com/extension/MSSQLTool)

## 链接

GitHub 仓库

https://github.com/PuLuShen/MSSQLTOOL

最新发布（ZIP + VSIX）

https://github.com/PuLuShen/MSSQLTOOL/releases/latest

SSMS 图库（面向 SQL Server Management Studio）

https://ssmsgallery.azurewebsites.net/extension/MSSQLTool

VSIX Gallery（面向 Visual Studio）

https://www.vsixgallery.com/extension/MSSQLTool

使用文档（Wiki）

https://github.com/PuLuShen/MSSQLTOOL/wiki

## 安装

1. 从上面的「最新发布」下载 ZIP，运行 `安装或重装.cmd`（推荐）。
2. 或从上面的「SSMS 图库」下载最新 VSIX。
3. 插件内置更新检查：**设置 → 更新** 里可一键排期「关闭 SSMS 时安装」。

> **MSSQL Tool 是一款免费、开源的 SSMS（SQL Server Management Studio）扩展，适用于 SSMS 22** ——
> 作为 SQL Server Management Studio 的插件，它为 SSMS 带来上下文感知的 SQL 智能补全、T-SQL 格式化、
> 查询历史、服务器健康监控，以及结果导出到 Excel / Google Sheets / 邮件。
>
> SSMS 22（SQL Server Management Studio 22 版）的用户常常在找可以替代 SQL Prompt 的免费插件；
> MSSQL Tool 正是为此而生：一个内置 SQL 补全、格式化与结果处理的免费 SSMS 扩展，
> 面向希望更快编写查询、在 SQL Server Management Studio 内完成更多结果处理的 SQL Server 开发者与 DBA。

> MSSQL Tool 是 [Axial-SQL/AxialSqlTools](https://github.com/Axial-SQL/AxialSqlTools)（Apache-2.0）的 fork，在此基础上重构了 SQL 自动补全、格式化选项以及设置与多语言层。

## 亮点

- **完整的中英双语界面** —— 多数 SSMS 插件的界面只有英文。
- **SQL Prompt 的免费替代**：SQL 智能补全、T-SQL 格式化、查询历史，以及导出到 Excel / 邮件。
- **基于 Axial SQL Tools**（2016 年起持续维护），并增强了补全引擎与多语言层。

*说明：Google 表格导出与 GitHub 脚本同步仍属实验性功能，正在开发中。*

## 界面截图

**智能 SQL 自动补全**——边输入边提示：架构、表、视图、字段、存储过程、函数、参数，带类型图标、分类筛选，以及当前选中对象的详情。

<img src="pics/1.png" width="620" alt="输入 SELECT * FROM 时的 SQL 补全列表">

**设置**——每项功能都有独立选项，保存后立即生效；界面跟随 SSMS 语言（此处为简体中文，也可切换英文）。

<img src="pics/2.png" width="760" alt="MSSQL Tool 设置窗口的 SQL 自动补全页">

**工具菜单**——扩展提供的全部命令，配置了快捷键的会一并显示。

<img src="pics/3.png" width="520" alt="SSMS 工具菜单中的 MSSQL Tool 命令">

## 功能

### SQL 编辑

- **智能 SQL 自动补全：** 根据上下文提示数据库、架构、表、视图、字段、存储过程、函数、参数、同义词和链接服务器对象。
- **跟着别名走：** 输入 `c.` 只列该表的字段，并插入**裸字段名**，你打的别名保持原样（不再插入真实表名）。
- **UPDATE 支持：** `UPDATE t SET` 会提示目标表的可写字段，即使目标别名写在后面的 `FROM` 里也有效。
- **数据类型与提示：** 在合适的位置提示 `INT`、`NVARCHAR(50)`、`DATETIME2`，以及 `WITH (NOLOCK)`、`OPTION (RECOMPILE)` 等常用写法。
- **补全窗口内显示函数用法：** 36+ 个内置函数给出**签名 + 说明 + 可直接复制的示例**。
- **可扩展关键字：** 设置 → SQL 补全 里可追加自定义关键字（每行一个）。
- **补全元数据缓存：** 后台分步加载并显示进度、手动清除与刷新，并可选择信任 SQL Server 证书。
- **F12 转到定义：** 打开数据库对象定义脚本，并支持跳转到 SQL 中的本地定义。
- **T-SQL 格式化**（为兼容旧习惯保留；SSMS 22 已自带格式化，见下文「格式化」）
- **查询模板：** 预览内直接编辑并 Ctrl+S 保存、收藏、**标签**（`-- tags: 报表, 日结`）与 `#标签` 检索、选择器顶部**最近 10 条**。
- **代码片段**
- **将 `SELECT *` 展开为字段列表**
- **选择当前语句**
- **切换块注释**
- **快速搜索：** 跨库对象搜索，目标自动取自对象资源管理器，支持生成脚本与在新查询中打开。
- **生成对象定义脚本**

### 结果集工具

- **浮动统计面板：** 在结果集中点击时自动出现、点结果集以外自动收起。选中单元格后显示该选区的**数量 / 合计 / 平均 / 最小 / 最大 / 去重**，并标明**真实字段名**；面板可拖拽调整大小、**记住尺寸与位置**，还支持**固定前 N 列**。
- **列名跳转：** 输入列名的一部分即可让结果集滚到该列（`DDZ` 不会误跳到 `DDZT`）。
- **结果集工具窗口：** 同样的查找 + 逐列统计，位于 工具 → *结果集搜索与统计*，可一键复制统计结果。

### 结果处理与数据传输

- **将结果网格导出到 Excel**
- **通过邮件发送结果网格**
- **将结果网格导出为临时表**
- **复制网格字段名和单元格值，支持 INSERT、CSV、JSON、XML、HTML 格式**
- **批量数据传输**

### 监控与工作流

- **查询历史**
- **服务器健康面板**
- **执行统计信息摘要**
- **事务与 Always Encrypted 警告**
- **按服务器和数据库显示连接颜色**
- **毫秒级查询执行时间**
- **结果网格数字右对齐**
- **SQL Server 版本信息**
- **可自定义数据与设置目录**（可指向其他磁盘、同步文件夹或便携设备）

## 实验性功能

这些功能可用，但仍在演进：不同版本之间行为可能变化，升级后请重新检查相关设置。

| 功能 | 状态 |
| --- | --- |
| **Google 表格导出** | Beta —— OAuth 配置与列映射仍在调整。 |
| **GitHub 脚本同步** | Beta —— 以整文件推送/拉取，冲突需手动处理。 |

## 格式化

SSMS 22 已自带 T-SQL 格式化功能，因此**本插件的格式化功能不再继续更新**。
命令仍然可用、并为兼容旧习惯保留 —— 现已移到 **工具** 菜单（Ctrl+Shift+Alt+F）—— 但不会再增加新的格式化选项。
## 赞赏

<img src="pics/赞赏码.png" width="320" alt="赞赏码" />

## 常见问题（FAQ）

**MSSQL Tool 是什么？**
一个免费的 SSMS 22 扩展，提供 SQL 智能补全、T-SQL 格式化与结果处理。

**MSSQL Tool 免费吗？**
免费，基于 Apache-2.0 开源。

**和 SQL Prompt、dbForge 相比如何？**
MSSQL Tool 免费且开源，内置 GitHub 同步与 Google Sheets 导出；SQL Prompt 与 dbForge 是商业产品。
