# Code analysis rules / 代码分析规则

Generated from `RuleCatalog` (do not edit by hand). Severity is the default; the `Enabled` column is the default state. Every rule can be overridden in the analysis settings.

| Id | Category | Severity | Enabled | Quick fix |
| --- | --- | --- | --- | --- |
| `delete-without-where` | correctness | Error | yes | - |
| `update-without-where` | correctness | Error | yes | - |
| `equals-null` | correctness | Warning | yes | yes |
| `not-in-null` | correctness | Warning | yes | - |
| `join-without-on` | correctness | Warning | yes | - |
| `select-star` | performance | Info | yes | yes |
| `non-sargable-predicate` | performance | Warning | yes | - |
| `leading-wildcard-like` | performance | Warning | yes | - |
| `implicit-conversion` | performance | Warning | yes | - |
| `dynamic-sql-concatenation` | security | Warning | yes | - |
| `transaction-without-try-catch` | correctness | Warning | yes | - |
| `unbalanced-transaction` | correctness | Error | yes | - |
| `nolock-hint` | correctness | Warning | yes | - |
| `cursor-usage` | performance | Warning | yes | - |
| `merge-without-terminator` | correctness | Warning | yes | - |
| `meaningless-order-by` | performance | Warning | yes | - |
| `duplicate-alias` | correctness | Warning | yes | - |
| `implicit-cross-join` | correctness | Warning | yes | - |
| `missing-schema-prefix` | correctness | Info | yes | - |
| `unused-variable` | maintainability | Info | yes | - |
| `unused-cte` | maintainability | Info | yes | - |
| `temp-table-not-dropped` | maintainability | Info | yes | - |
| `select-into` | maintainability | Info | yes | - |
| `sp-prefix` | maintainability | Info | yes | - |
| `count-one` | style | Info | no | - |
| `missing-as-alias` | style | Info | no | yes |
| `redundant-distinct-in-exists` | style | Info | no | - |
| `group-by-missing-column` | style | Warning | no | - |
| `having-without-group-by` | style | Warning | no | - |
| `varchar-without-length` | style | Warning | no | - |

Total: 30 rules, 24 enabled by default.
