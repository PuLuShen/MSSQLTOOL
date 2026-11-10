using System;
using System.Collections.Generic;
using System.Linq;

namespace MSSQLTool.Analysis
{
    /// <summary>
    /// The product-facing rule catalogue.  Every rule has a stable id (used by settings,
    /// suppression comments and tests), a severity, a default enable state, a localised title,
    /// message and explanation.  A rule is only "product level" if all of those exist, which
    /// the regression suite enforces.
    /// </summary>
    internal static class RuleCatalog
    {
        private static readonly List<RuleDefinition> Rules = new List<RuleDefinition>
        {
            // ---- A: correctness / safety / performance, enabled by default -------------------
            Rule("delete-without-where", "correctness", AnalysisSeverity.Error, true,
                "DELETE 缺少 WHERE", "DELETE without WHERE",
                "DELETE 语句没有 WHERE 条件，会删除表中的所有行。", "This DELETE has no WHERE clause and removes every row.",
                "确认是否要删除全表；如非本意，补上 WHERE 条件。", "Confirm the intent or add a WHERE clause."),
            Rule("update-without-where", "correctness", AnalysisSeverity.Error, true,
                "UPDATE 缺少 WHERE", "UPDATE without WHERE",
                "UPDATE 语句没有 WHERE 条件，会更新表中的所有行。", "This UPDATE has no WHERE clause and updates every row.",
                "确认是否要更新全表；如非本意，补上 WHERE 条件。", "Confirm the intent or add a WHERE clause."),
            Rule("equals-null", "correctness", AnalysisSeverity.Warning, true,
                "与 NULL 用等号比较", "Comparison with NULL",
                "与 NULL 使用 = 或 <> 比较永远不成立，应使用 IS NULL / IS NOT NULL。",
                "Comparing with = or <> to NULL never matches; use IS NULL / IS NOT NULL.",
                "改写为 IS NULL 或 IS NOT NULL。", "Rewrite as IS NULL or IS NOT NULL."),
            Rule("not-in-null", "correctness", AnalysisSeverity.Warning, true,
                "NOT IN 遇到 NULL 会返回空集", "NOT IN is unsafe with NULLs",
                "子查询结果含 NULL 时，NOT IN 不会返回任何行。", "If the subquery yields NULL, NOT IN returns no rows at all.",
                "改用 NOT EXISTS，或排除子查询中的 NULL。", "Use NOT EXISTS, or exclude NULLs in the subquery."),
            Rule("join-without-on", "correctness", AnalysisSeverity.Warning, true,
                "JOIN 缺少 ON 条件", "JOIN without ON",
                "JOIN 没有连接条件，会产生笛卡尔积。", "This JOIN has no ON predicate and produces a Cartesian product.",
                "补上 ON 条件；确实需要笛卡尔积时改用 CROSS JOIN。", "Add the ON predicate, or use CROSS JOIN deliberately."),
            Rule("select-star", "performance", AnalysisSeverity.Info, true,
                "SELECT * 读取全部列", "SELECT * reads every column",
                "SELECT * 会读取并返回所有列，绑定到具体列更容易维护且能命中覆盖索引。",
                "SELECT * reads every column; listing columns is easier to maintain and enables covering indexes.",
                "展开为需要的列清单。", "Expand to the columns that are actually needed."),
            Rule("non-sargable-predicate", "performance", AnalysisSeverity.Warning, true,
                "谓词中的列被函数包裹", "Column wrapped in a function",
                "WHERE/ON 中对列使用函数或运算会让索引失效（非 SARGable）。",
                "Applying a function to a column in WHERE/ON prevents an index seek (non-SARGable).",
                "把函数移到常量一侧，或使用计算列/范围条件。", "Move the function to the constant side, or use a range predicate."),
            Rule("leading-wildcard-like", "performance", AnalysisSeverity.Warning, true,
                "LIKE 以通配符开头", "LIKE starts with a wildcard",
                "以 % 开头的 LIKE 无法使用索引查找。", "A LIKE pattern beginning with % cannot use an index seek.",
                "改为前缀匹配，或使用全文检索。", "Use a prefix pattern or full-text search."),
            Rule("implicit-conversion", "performance", AnalysisSeverity.Warning, true,
                "字符串与数值比较可能隐式转换", "Possible implicit conversion",
                "用字符串常量与数值列比较会触发隐式转换，索引可能失效。",
                "Comparing a numeric column with a string literal forces an implicit conversion and may bypass the index.",
                "改用匹配的数据类型常量。", "Use a constant of the matching data type."),
            Rule("dynamic-sql-concatenation", "security", AnalysisSeverity.Warning, true,
                "动态 SQL 字符串拼接", "Dynamic SQL built by concatenation",
                "拼接执行的动态 SQL 容易产生注入风险，也难以复用执行计划。",
                "Concatenated dynamic SQL is an injection risk and defeats plan reuse.",
                "改用 sp_executesql 参数化。", "Use parameterised sp_executesql."),
            Rule("transaction-without-try-catch", "correctness", AnalysisSeverity.Warning, true,
                "事务没有 TRY/CATCH", "Transaction without TRY/CATCH",
                "显式事务未包在 TRY/CATCH 中，出错时可能留下未回滚的事务。",
                "An explicit transaction outside TRY/CATCH can be left open when a statement fails.",
                "用 BEGIN TRY / BEGIN CATCH 包裹并回滚。", "Wrap it in BEGIN TRY / BEGIN CATCH and roll back."),
            Rule("unbalanced-transaction", "correctness", AnalysisSeverity.Error, true,
                "BEGIN TRAN 与 COMMIT/ROLLBACK 数量不匹配", "Unbalanced BEGIN TRAN",
                "同一批次中的 BEGIN TRAN 数量与 COMMIT/ROLLBACK 不一致。",
                "The number of BEGIN TRAN statements in this batch does not match COMMIT/ROLLBACK.",
                "检查每条 BEGIN TRAN 是否都有对应的提交或回滚。", "Make sure every BEGIN TRAN is committed or rolled back."),

            // ---- B: warnings, enabled by default --------------------------------------------
            Rule("nolock-hint", "correctness", AnalysisSeverity.Warning, true,
                "使用了 NOLOCK", "NOLOCK hint used",
                "NOLOCK 会读到未提交的数据并可能产生重复/缺失行。",
                "NOLOCK can read uncommitted data and produce missing or duplicated rows.",
                "改用 READ COMMITTED SNAPSHOT 或快照隔离。", "Prefer RCSI or snapshot isolation."),
            Rule("cursor-usage", "performance", AnalysisSeverity.Warning, true,
                "使用了游标", "Cursor used",
                "游标逐行处理通常比基于集合的写法慢很多。",
                "Row-by-row cursor processing is usually far slower than a set-based rewrite.",
                "考虑用集合操作、窗口函数或 MERGE 替代。", "Consider a set-based rewrite, window function or MERGE."),
            Rule("merge-without-terminator", "correctness", AnalysisSeverity.Warning, true,
                "MERGE 语句缺少分号", "MERGE without terminator",
                "MERGE 语句必须以分号结束，否则后续语句会被错误解析。",
                "A MERGE statement must end with a semicolon, otherwise the following statement is parsed incorrectly.",
                "在 MERGE 语句末尾补分号。", "Terminate the MERGE statement with a semicolon."),
            Rule("meaningless-order-by", "performance", AnalysisSeverity.Warning, true,
                "子查询中的 ORDER BY 无意义", "ORDER BY in a subquery",
                "派生表或子查询中的 ORDER BY 不保证输出顺序，还会增加排序开销。",
                "ORDER BY inside a derived table or subquery does not guarantee order and adds a sort.",
                "除非配合 TOP/OFFSET，否则移除。", "Remove it unless it is paired with TOP/OFFSET."),
            Rule("duplicate-alias", "correctness", AnalysisSeverity.Warning, true,
                "同一查询中重复的别名", "Duplicate alias",
                "同一作用域内两个数据源使用了相同别名，列引用会产生歧义。",
                "Two sources in the same scope use the same alias, making column references ambiguous.",
                "为其中一个数据源换一个别名。", "Rename one of the sources."),
            Rule("implicit-cross-join", "correctness", AnalysisSeverity.Warning, true,
                "逗号连接形成隐式笛卡尔积", "Implicit cross join",
                "FROM 中用逗号连接多个表且缺少连接条件，容易产生笛卡尔积。",
                "Comma-separated tables without a join predicate easily produce a Cartesian product.",
                "改用显式 JOIN ... ON。", "Use an explicit JOIN ... ON."),
            Rule("missing-schema-prefix", "correctness", AnalysisSeverity.Info, true,
                "对象缺少架构前缀", "Missing schema prefix",
                "未写架构名的对象会按默认架构解析，可能命中错误的表。",
                "An object without a schema resolves against the default schema and may hit the wrong table.",
                "补上 dbo. 或其他架构名。", "Qualify the object with dbo. or its real schema."),

            // ---- C: informational, enabled by default (list only) ----------------------------
            Rule("unused-variable", "maintainability", AnalysisSeverity.Info, true,
                "声明了未使用的变量", "Unused variable",
                "变量声明后从未被引用。", "This variable is declared but never referenced.",
                "删除声明，或补上缺失的引用。", "Remove the declaration or use the variable."),
            Rule("unused-cte", "maintainability", AnalysisSeverity.Info, true,
                "定义了未使用的 CTE", "Unused CTE",
                "CTE 定义后没有被任何语句引用。", "This CTE is defined but never referenced.",
                "删除该 CTE。", "Remove the CTE."),
            Rule("temp-table-not-dropped", "maintainability", AnalysisSeverity.Info, true,
                "临时表未显式删除", "Temporary table not dropped",
                "脚本创建的临时表没有对应的 DROP，长时间会话中会持续占用 tempdb。",
                "A temporary table created by this script is never dropped and keeps using tempdb.",
                "结束时执行 DROP TABLE IF EXISTS。", "Drop it with DROP TABLE IF EXISTS when finished."),
            Rule("select-into", "maintainability", AnalysisSeverity.Info, true,
                "使用 SELECT INTO 建表", "SELECT INTO creates a table",
                "SELECT INTO 无法预先定义索引和约束，列类型也只能推断。",
                "SELECT INTO cannot define indexes/constraints up front and infers column types.",
                "需要控制结构时改用 CREATE TABLE + INSERT。", "Use CREATE TABLE + INSERT when the shape matters."),
            Rule("sp-prefix", "maintainability", AnalysisSeverity.Info, true,
                "调用了 sp_ 前缀的存储过程", "sp_ prefixed procedure",
                "sp_ 前缀会先到 master 查找，既慢又可能与系统过程冲突。",
                "The sp_ prefix triggers a lookup in master and can collide with system procedures.",
                "改用自定义前缀，如 usp_。", "Use a project prefix such as usp_."),

            // ---- Style: disabled by default ------------------------------------------------
            Rule("count-one", "style", AnalysisSeverity.Info, false,
                "COUNT(1) 建议改为 COUNT(*)", "Prefer COUNT(*) over COUNT(1)",
                "两者性能相同，COUNT(*) 更清楚地表达“统计行数”。",
                "Both perform identically; COUNT(*) states the intent more clearly.",
                "改写为 COUNT(*)。", "Rewrite as COUNT(*)."),
            Rule("missing-as-alias", "style", AnalysisSeverity.Info, false,
                "列别名缺少 AS", "Alias without AS",
                "省略 AS 会让列别名和表达式混在一起，降低可读性。",
                "Omitting AS makes the alias harder to distinguish from the expression.",
                "补上 AS。", "Add AS."),
            Rule("redundant-distinct-in-exists", "style", AnalysisSeverity.Info, false,
                "EXISTS 中的 DISTINCT 是多余的", "DISTINCT inside EXISTS is redundant",
                "EXISTS 只关心是否存在行，DISTINCT 不改变结果。",
                "EXISTS only tests for a row; DISTINCT cannot change the result.",
                "删除 DISTINCT。", "Remove DISTINCT."),
            Rule("group-by-missing-column", "style", AnalysisSeverity.Warning, false,
                "GROUP BY 可能缺少列", "GROUP BY may miss a column",
                "SELECT 列表中的非聚合列未出现在 GROUP BY 中。",
                "A non-aggregated column in the SELECT list is missing from GROUP BY.",
                "把该列加入 GROUP BY，或改为聚合。", "Add the column to GROUP BY or aggregate it."),
            Rule("having-without-group-by", "style", AnalysisSeverity.Warning, false,
                "HAVING 缺少 GROUP BY", "HAVING without GROUP BY",
                "没有 GROUP BY 时 HAVING 等价于 WHERE，容易误读。",
                "Without GROUP BY, HAVING behaves like WHERE and is easy to misread.",
                "改用 WHERE。", "Use WHERE instead."),
            Rule("varchar-without-length", "style", AnalysisSeverity.Warning, false,
                "VARCHAR 未指定长度", "VARCHAR without length",
                "CAST/CONVERT 中省略长度会得到默认长度 30，可能静默截断。",
                "Omitting the length in CAST/CONVERT yields the default 30 and may silently truncate.",
                "显式写出长度，如 VARCHAR(100)。", "Specify the length explicitly, e.g. VARCHAR(100).")
        };

        private static readonly Dictionary<string, RuleDefinition> ById =
            Rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<RuleDefinition> All => Rules;

        public static RuleDefinition Find(string id)
            => !string.IsNullOrEmpty(id) && ById.TryGetValue(id, out RuleDefinition rule) ? rule : null;

        public static IEnumerable<RuleDefinition> Enabled(AnalysisSettings settings)
        {
            foreach (RuleDefinition rule in Rules)
            {
                if (settings != null && settings.IsRuleDisabled(rule.Id)) continue;
                bool explicitlyEnabled = settings != null && settings.SeverityOverride(rule.Id) != null;
                if (rule.EnabledByDefault || explicitlyEnabled) yield return rule;
            }
        }

        public static AnalysisSeverity SeverityOf(RuleDefinition rule, AnalysisSettings settings)
        {
            AnalysisSeverity? overridden = settings?.SeverityOverride(rule.Id);
            return overridden ?? rule.DefaultSeverity;
        }

        private static RuleDefinition Rule(string id, string category, AnalysisSeverity severity, bool enabled,
            string titleZh, string titleEn, string messageZh, string messageEn, string descriptionZh, string descriptionEn)
        {
            return new RuleDefinition
            {
                Id = id,
                Category = category,
                DefaultSeverity = severity,
                EnabledByDefault = enabled,
                TitleZh = titleZh,
                TitleEn = titleEn,
                MessageZh = messageZh,
                MessageEn = messageEn,
                DescriptionZh = descriptionZh,
                DescriptionEn = descriptionEn
            };
        }
    }
}
