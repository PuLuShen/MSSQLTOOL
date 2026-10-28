# MSSQL Tool for SSMS 22

[English](README.md) | [简体中文](README.zh-CN.md)

[![Install from VSIX Gallery](https://ssmsgallery.azurewebsites.net/badge/MSSQLTool.svg)](https://ssmsgallery.azurewebsites.net/extension/MSSQLTool/) [![Open VSIX Gallery](https://www.vsixgallery.com/badge/MSSQLTool.svg)](https://www.vsixgallery.com/extension/MSSQLTool)

## Links

GitHub repository

https://github.com/PuLuShen/MSSQLTOOL

Latest release (ZIP + VSIX)

https://github.com/PuLuShen/MSSQLTOOL/releases/latest

SSMS Gallery (for SQL Server Management Studio)

https://ssmsgallery.azurewebsites.net/extension/MSSQLTool

VSIX Gallery (for Visual Studio)

https://www.vsixgallery.com/extension/MSSQLTool

Documentation (Wiki)

https://github.com/PuLuShen/MSSQLTOOL/wiki

## Install

1. Download the ZIP from the latest release above and run `安装或重装.cmd` (recommended).
2. Or install the newest VSIX from the SSMS Gallery above.
3. Updates are built in: **Settings -> Updates** can schedule "install when SSMS closes".

> **MSSQL Tool is a free, open-source SSMS (SQL Server Management Studio) extension for SSMS 22** -
> an add-in for SQL Server Management Studio that adds context-aware SQL completion, T-SQL formatting,
> query history, server health monitoring, and result export to Excel/Google Sheets/email.
>
> SSMS 22 (SQL Server Management Studio version 22) users often look for free plugins to replace
> SQL Prompt; MSSQL Tool is meant to be exactly that: a free SSMS extension with SQL completion,
> formatting and result processing built in, for SQL Server developers and DBAs who want faster query
> writing and richer result processing inside SQL Server Management Studio.

> MSSQL Tool is a fork of [Axial-SQL/AxialSqlTools](https://github.com/Axial-SQL/AxialSqlTools) (Apache-2.0). The SQL completion engine, the formatter options, and the settings and localization layers were reworked on top of it.

## Highlights

- **Full bilingual UI (中文/English)** - most SSMS extensions are English-only.
- **Free alternative to SQL Prompt** for SQL completion, T-SQL formatting, query history, and result export to Excel/email.
- **Built on Axial SQL Tools** (maintained since 2016), with an enhanced completion engine and localization.

*Note: Google Sheets export and GitHub script sync are experimental and under active development.*

## Screenshots

**Smart SQL completion** - context-aware suggestions while you type: schemas, tables, views, columns, procedures, functions and parameters, each with a kind icon, a category filter and the details of the highlighted object.

<img src="pics/1.png" width="620" alt="SQL completion list while typing SELECT * FROM">

**Settings** - every feature has its own options and is saved immediately. The interface follows the SSMS language (Simplified Chinese shown here, English is available too).

<img src="pics/2.png" width="760" alt="MSSQL Tool settings window with the SQL completion tab">

**Tools menu** - the extension's commands, with their keyboard shortcuts where one is configured.

<img src="pics/3.png" width="520" alt="MSSQL Tool commands in the SSMS Tools menu">

**Result statistics** - select cells in the result grid to see count, sum, average, min, max and distinct; the same panel can locate a column, find a value and freeze columns.

<img src="pics/4.png" width="820" alt="MSSQL Tool result statistics panel beside a result grid">

## Features

### SQL editing

- **Smart SQL completion:** Context-aware suggestions for databases, schemas, tables, views, columns, procedures, functions, parameters, synonyms, and linked-server objects.
- **Completion that follows your aliases:** `c.` lists only that table's columns and inserts the bare column, so the alias you typed stays in front of it.
- **UPDATE support:** `UPDATE t SET` suggests the writable columns of the target, including when the target is an alias declared further down in the `FROM` clause.
- **Data types and hints:** `INT`, `NVARCHAR(50)`, `DATETIME2` and the usual `WITH (NOLOCK)` / `OPTION (RECOMPILE)` forms are offered where they belong.
- **Function help in the popup:** 36+ built-in functions show their signature, a one-line explanation and a copy-ready example.
- **Extensible keywords:** the built-in keyword list can be extended from Settings → SQL completion (one custom keyword per line).
- **Completion metadata cache:** Background loading with step-by-step progress, manual clear/refresh, and optional SQL Server certificate trust.
- **F12 Go to Definition:** Opens database object definitions and navigates to local SQL definitions.
- **T-SQL formatting** (kept for existing habits; SSMS 22 formats natively - see *Formatting* below)
- **Query templates:** inline editing with Ctrl+S, favourites, **tags** (`-- tags: 报表, 日结`) with tag search (`#tag`), and a **Recent 10** list at the top of the picker.
- **Snippets**
- **Expand `SELECT *` to a column list**
- **Select current statement**
- **Toggle block comments**
- **Quick Search:** multi-database object search with a target adopted from Object Explorer, tags-free filters, scripting and open-in-new-query.
- **Script object definition**

### Result set tools

- **Statistics panel (floating):** opens on its own when you click in the results and closes when you click outside. Select cells and it reports **count, sum, average, min, max and distinct** for that selection, plus the real column name; it is resizable, remembers its size and position, and can **freeze the first N columns**.
- **Column search:** type part of a column name and the results scroll to it (`DDZ` never resolves to `DDZT`).
- **Result set tools window:** the same search plus per-column statistics in a docked dialog (Tools → *结果集搜索与统计*), with copy-to-clipboard output.

### Result processing and data transfer

- **Export grid to Excel**
- **Send grid results by email**
- **Export grid as a temporary table**
- **Copy grid column names and values as INSERT, CSV, JSON, XML, or HTML**
- **Bulk data transfer**

### Monitoring and workflow

- **Query History**
- **Server Health Dashboard**
- **Execution statistics summary**
- **Transaction and Always Encrypted warnings**
- **Connection colors for server and database environments**
- **Millisecond query execution time**
- **Right-aligned numeric grid values**
- **SQL Server version information**
- **Configurable data and settings folder** (another drive, a synced folder or a portable device)

## Experimental features

These work, but their design is still moving: expect changes between releases and re-check the
related settings after upgrading.

| Feature | Status |
| --- | --- |
| **Google Sheets export** | Beta - OAuth setup and column mapping are still changing. |
| **GitHub script sync** | Beta - whole-file push and pull, conflict resolution is manual. |

## Formatting

SSMS 22 ships its own T-SQL formatter, so **the formatting feature of this extension is no longer
developed**. The command still works and is kept for existing habits - it now lives in the
**Tools** menu (Ctrl+Shift+Alt+F) - but new formatting options will not be added.

## Support

<img src="pics/赞赏码.png" width="320" alt="Appreciation QR code" />

## FAQ

**What is MSSQL Tool?**
A free SSMS 22 extension for SQL completion, formatting, and result processing.

**Is MSSQL Tool free?**
Yes, it is open-source under Apache-2.0.

**How does it compare to SQL Prompt or dbForge?**
MSSQL Tool is free and open-source, with built-in GitHub sync and Google Sheets export. SQL Prompt and dbForge are commercial alternatives.
