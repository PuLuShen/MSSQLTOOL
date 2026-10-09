using System;
using System.Collections.Generic;

namespace MSSQLTool.RegressionTests
{
    /// <summary>Prints the token range of the nodes the layout rules depend on.</summary>
    internal sealed class NodeProbe : Microsoft.SqlServer.TransactSql.ScriptDom.TSqlFragmentVisitor
    {
        private static void Report(string name, Microsoft.SqlServer.TransactSql.ScriptDom.TSqlFragment node)
            => Console.WriteLine(string.Format("  {0,-28} {1,4}..{2,-4}", name, node.FirstTokenIndex, node.LastTokenIndex));

        public override void ExplicitVisit(Microsoft.SqlServer.TransactSql.ScriptDom.ScalarSubquery node)
        {
            Report("ScalarSubquery", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(Microsoft.SqlServer.TransactSql.ScriptDom.CreateTableStatement node)
        {
            Report("CreateTableStatement", node);
            Console.WriteLine("    Options.Count = " + node.Options.Count);
            foreach (var option in node.Options) Report("  Option " + option.OptionKind, option);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(Microsoft.SqlServer.TransactSql.ScriptDom.MergeActionClause node)
        {
            Report("MergeActionClause", node);
            if (node.Action != null) Report("  Action", node.Action);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(Microsoft.SqlServer.TransactSql.ScriptDom.TriggerStatementBody node)
        {
            Report("TriggerStatementBody", node);
            if (node.StatementList != null) Report("  StatementList", node.StatementList);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(Microsoft.SqlServer.TransactSql.ScriptDom.ProcedureStatementBody node)
        {
            Report("ProcedureStatementBody", node);
            if (node.StatementList != null) Report("  StatementList", node.StatementList);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(Microsoft.SqlServer.TransactSql.ScriptDom.BeginEndBlockStatement node)
        {
            Report("BeginEndBlockStatement", node);
            if (node.StatementList != null) Report("  StatementList", node.StatementList);
            base.ExplicitVisit(node);
        }
    }

    /// <summary>
    /// Development aid: prints what the formatter produces for a few representative statements.
    /// Run with <c>MSSQLTool.RegressionTests.exe --format-probe</c>.
    /// </summary>
    internal static class FormatterProbe
    {
        private static readonly List<KeyValuePair<string, string>> Samples = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("select", "select c.Id, c.Name from dbo.Customer c join dbo.[Order] o on o.CustomerId=c.Id where c.Id>1 and c.Name='x' group by c.Id, c.Name order by c.Name"),
            new KeyValuePair<string, string>("insert", "insert into dbo.Log (Id, Message) values (1, 'a'), (2, 'b')"),
            new KeyValuePair<string, string>("update", "update dbo.Customer set Name='x', City='y' from dbo.Customer c where c.Id=1"),
            new KeyValuePair<string, string>("delete", "delete from dbo.Customer where Id=1"),
            new KeyValuePair<string, string>("merge", "merge into dbo.Target as t using dbo.Source as s on t.Id=s.Id when matched then update set t.Name=s.Name when not matched then insert (Id, Name) values (s.Id, s.Name);"),
            new KeyValuePair<string, string>("case", "select case when a=1 then 'x' when a=2 then 'y' else 'z' end as Label from dbo.T"),
            new KeyValuePair<string, string>("block", "if @a=1 begin select 1 end else begin select 2 end while @i<10 begin set @i=@i+1 end"),
            new KeyValuePair<string, string>("declare", "declare @a int, @b nvarchar(20), @c datetime"),
            new KeyValuePair<string, string>("subquery", "select * from dbo.T where Id in (select Id from dbo.Other where Name='x') and Exists (select 1 from dbo.Third)"),
            new KeyValuePair<string, string>("routine", "create procedure dbo.usp_Test @Id int, @Name nvarchar(20) = null as begin select @Id, @Name end"),
            new KeyValuePair<string, string>("function", "create function dbo.fn_Test (@Id int, @Name nvarchar(20)) returns int as begin return @Id end"),
            new KeyValuePair<string, string>("trigger", "create trigger dbo.trg_Test on dbo.T after insert as begin select 1 end"),
            new KeyValuePair<string, string>("view", "create view dbo.v_Test as select Id, Name from dbo.T"),
            new KeyValuePair<string, string>("createtable", "create table dbo.T (Id int not null, Name nvarchar(20) null, constraint PK_T primary key (Id)) with (memory_optimized = on, durability = schema_only)"),
            new KeyValuePair<string, string>("subquery2", "select * from t where Id in (select Id, Name from Other where Name = 'a very long one')"),
            new KeyValuePair<string, string>("cursor", "declare c cursor for select Id, Name from dbo.T where Id > 1 open c")
        };

        public static int Run(string[] args)
        {
            FormatterOptions preset = new FormatterOptions();
            string only = null;
            var assignments = new List<string>();
            bool tokens = false;
            bool nodes = false;
            foreach (string arg in args)
            {
                if (arg.StartsWith("--preset=", StringComparison.Ordinal))
                {
                    FormatPreset parsed;
                    if (Enum.TryParse(arg.Substring(9), true, out parsed)) preset = FormatterOptions.CreatePreset(parsed);
                }
                else if (arg.StartsWith("--set=", StringComparison.Ordinal))
                {
                    assignments.AddRange(arg.Substring(6).Split(';'));
                }
                else if (arg.StartsWith("--only=", StringComparison.Ordinal)) only = arg.Substring(7);
                else if (arg == "--tokens") tokens = true;
                else if (arg == "--nodes") nodes = true;
            }

            foreach (string assignment in assignments) ApplyProbeOption(preset, assignment);

            if (nodes)
            {
                foreach (KeyValuePair<string, string> sample in Samples)
                {
                    if (only != null && !string.Equals(only, sample.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    Console.WriteLine("===== nodes for " + sample.Key + " =====");
                    var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql170Parser(false);
                    Microsoft.SqlServer.TransactSql.ScriptDom.TSqlFragment fragment;
                    using (var reader = new System.IO.StringReader(sample.Value))
                    {
                        System.Collections.Generic.IList<Microsoft.SqlServer.TransactSql.ScriptDom.ParseError> errors;
                        fragment = parser.Parse(reader, out errors);
                        foreach (var error in errors) Console.WriteLine("  PARSE ERROR: " + error.Message);
                    }

                    var visitor = new NodeProbe();
                    fragment.Accept(visitor);
                }

                return 0;
            }

            if (tokens)
            {
                foreach (KeyValuePair<string, string> sample in Samples)
                {
                    if (only != null && !string.Equals(only, sample.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    Console.WriteLine("===== tokens for " + sample.Key + " =====");
                    string formatted = TSqlFormatter.FormatCode(sample.Value, preset);
                    var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql170Parser(false);
                    Microsoft.SqlServer.TransactSql.ScriptDom.TSqlFragment fragment;
                    using (var reader = new System.IO.StringReader(formatted))
                    {
                        System.Collections.Generic.IList<Microsoft.SqlServer.TransactSql.ScriptDom.ParseError> errors;
                        fragment = parser.Parse(reader, out errors);
                    }

                    int index = 0;
                    foreach (var token in fragment.ScriptTokenStream)
                    {
                        string text = (token.Text ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");
                        Console.WriteLine(string.Format("{0,4} {1,-24} [{2}]", index++, token.TokenType, text));
                    }
                }

                return 0;
            }

            if (only != null && string.Equals(only, "input", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string assignment in assignments) Console.WriteLine("  " + assignment);
            }

            foreach (KeyValuePair<string, string> sample in Samples)
            {
                if (only != null && !string.Equals(only, sample.Key, StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine("===== " + sample.Key + " =====");
                if (assignments.Count == 0) Console.WriteLine("--- input\n" + sample.Value);
                Console.WriteLine("--- output");
                try
                {
                    Console.WriteLine(TSqlFormatter.FormatCode(sample.Value, preset));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ERROR: " + ex.Message);
                }
                Console.WriteLine();
            }

            return 0;
        }

        private static void ApplyProbeOption(FormatterOptions options, string assignment)
        {
            string[] pair = assignment.Split('=');
            if (pair.Length != 2 || pair[0].Length == 0) return;
            string[] path = pair[0].Split('.');
            object target = options;
            for (int i = 0; i < path.Length - 1; i++)
            {
                System.Reflection.FieldInfo group = target.GetType().GetField(path[i]);
                if (group == null) { Console.WriteLine("unknown option group " + path[i]); return; }
                target = group.GetValue(target);
            }
            System.Reflection.FieldInfo field = target.GetType().GetField(path[path.Length - 1]);
            if (field == null) { Console.WriteLine("unknown option " + assignment); return; }
            if (field.FieldType == typeof(bool)) field.SetValue(target, bool.Parse(pair[1]));
            else if (field.FieldType == typeof(int)) field.SetValue(target, int.Parse(pair[1]));
            else if (field.FieldType.IsEnum) field.SetValue(target, Enum.Parse(field.FieldType, pair[1], true));
        }
    }
}
