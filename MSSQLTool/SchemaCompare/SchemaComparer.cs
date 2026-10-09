// Schema Compare - pure comparison logic and sync script generation.
// This file has no dependency on WPF or on SSMS so it can be exercised by a test
// harness (see RegressionTests) or a script.
//
// Direction: Source is the baseline, Target is the database that should be brought
// to the Source definition. The generated script therefore creates what exists in
// the Source but not in the Target, and drops what exists only in the Target.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace MSSQLTool.SchemaCompare
{
    /// <summary>Quotes identifiers the SQL Server way: [a]]b].</summary>
    internal static class SchemaScriptName
    {
        public static string Quote(string identifier)
        {
            return "[" + (identifier ?? string.Empty).Replace("]", "]]") + "]";
        }

        public static string Quote(string schema, string name)
        {
            return Quote(schema) + "." + Quote(name);
        }

        public static string Quote(string schema, string parent, string name)
        {
            return Quote(schema) + "." + Quote(parent) + "." + Quote(name);
        }
    }

    /// <summary>One difference between the source and the target.</summary>
    public sealed class SchemaDifference
    {
        public SchemaObjectType ObjectType { get; set; }

        /// <summary>Fully quoted display name, e.g. [dbo].[Orders].[Total].</summary>
        public string ObjectName { get; set; }

        public SchemaDifferenceStatus Status { get; set; }
        public SchemaDifferenceSeverity Severity { get; set; }

        /// <summary>Short human readable description of the change.</summary>
        public string Summary { get; set; }

        public List<PropertyDifference> Properties { get; set; } = new List<PropertyDifference>();

        /// <summary>True when the two definitions only differ by whitespace or comments.</summary>
        public bool IsWhitespaceOnly { get; set; }

        public string SchemaName { get; set; }

        /// <summary>Owning table for columns, indexes and constraints.</summary>
        public string ParentName { get; set; }

        /// <summary>Name of the object itself.</summary>
        public string ChildName { get; set; }

        /// <summary>Quoted [schema].[table] of the owning object.</summary>
        public string ParentQualifiedName { get; set; }

        [JsonIgnore]
        public object SourceObject { get; set; }

        [JsonIgnore]
        public object TargetObject { get; set; }

        [JsonIgnore]
        public SchemaTable SourceTable
        {
            get { return SourceObject as SchemaTable; }
        }

        [JsonIgnore]
        public SchemaTable TargetTable
        {
            get { return TargetObject as SchemaTable; }
        }

        [JsonIgnore]
        public SchemaColumn SourceColumn
        {
            get { return SourceObject as SchemaColumn; }
        }

        [JsonIgnore]
        public SchemaColumn TargetColumn
        {
            get { return TargetObject as SchemaColumn; }
        }

        [JsonIgnore]
        public SchemaIndex SourceIndex
        {
            get { return SourceObject as SchemaIndex; }
        }

        [JsonIgnore]
        public SchemaIndex TargetIndex
        {
            get { return TargetObject as SchemaIndex; }
        }

        [JsonIgnore]
        public SchemaConstraint SourceConstraint
        {
            get { return SourceObject as SchemaConstraint; }
        }

        [JsonIgnore]
        public SchemaConstraint TargetConstraint
        {
            get { return TargetObject as SchemaConstraint; }
        }

        [JsonIgnore]
        public SchemaView SourceView
        {
            get { return SourceObject as SchemaView; }
        }

        [JsonIgnore]
        public SchemaRoutine SourceRoutine
        {
            get { return SourceObject as SchemaRoutine; }
        }

        /// <summary>"Create", "Drop" or "Alter".</summary>
        [JsonIgnore]
        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case SchemaDifferenceStatus.MissingInTarget:
                        return "Create";
                    case SchemaDifferenceStatus.MissingInSource:
                        return "Drop";
                    default:
                        return "Alter";
                }
            }
        }

        /// <summary>Object type as shown in the grid.</summary>
        [JsonIgnore]
        public string TypeText
        {
            get
            {
                switch (ObjectType)
                {
                    case SchemaObjectType.Table:
                        return "Table";
                    case SchemaObjectType.Column:
                        return "Column";
                    case SchemaObjectType.Index:
                        return "Index";
                    case SchemaObjectType.Constraint:
                        return "Constraint";
                    case SchemaObjectType.View:
                        return "View";
                    default:
                        return "Routine";
                }
            }
        }

        public override string ToString()
        {
            return StatusText + " " + ObjectName + " - " + Summary;
        }
    }

    public sealed class SchemaComparisonResult
    {
        public SchemaSnapshot Source { get; set; }
        public SchemaSnapshot Target { get; set; }
        public SchemaCompareOptions Options { get; set; }
        public DateTime ComparedUtc { get; set; }
        public List<SchemaDifference> Differences { get; set; } = new List<SchemaDifference>();

        [JsonIgnore]
        public int AddedCount
        {
            get { return Differences.Count(d => d.Status == SchemaDifferenceStatus.MissingInTarget); }
        }

        [JsonIgnore]
        public int RemovedCount
        {
            get { return Differences.Count(d => d.Status == SchemaDifferenceStatus.MissingInSource); }
        }

        [JsonIgnore]
        public int ModifiedCount
        {
            get { return Differences.Count(d => d.Status == SchemaDifferenceStatus.Different); }
        }

        [JsonIgnore]
        public int InformationCount
        {
            get { return Differences.Count(d => d.Severity == SchemaDifferenceSeverity.Information); }
        }

        [JsonIgnore]
        public bool IsIdentical
        {
            get { return Differences.Count == 0; }
        }

        public string BuildSummary()
        {
            if (Differences.Count == 0)
            {
                return "No differences were found.";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} difference(s): {1} to create, {2} to drop, {3} to alter{4}.",
                Differences.Count,
                AddedCount,
                RemovedCount,
                ModifiedCount,
                InformationCount > 0 ? " (" + InformationCount.ToString(CultureInfo.InvariantCulture) + " informational)" : string.Empty);
        }
    }

    public static class SchemaComparer
    {
        public static SchemaComparisonResult Compare(SchemaSnapshot source, SchemaSnapshot target, SchemaCompareOptions options)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var effectiveOptions = options ?? new SchemaCompareOptions();
            var differences = new List<SchemaDifference>();

            if (effectiveOptions.CompareTables || effectiveOptions.CompareColumns || effectiveOptions.CompareIndexes || effectiveOptions.CompareConstraints)
            {
                CompareTables(source, target, effectiveOptions, differences);
            }

            if (effectiveOptions.CompareViews)
            {
                CompareViews(source, target, effectiveOptions, differences);
            }

            if (effectiveOptions.CompareRoutines)
            {
                CompareRoutines(source, target, effectiveOptions, differences);
            }

            var result = new SchemaComparisonResult
            {
                Source = source,
                Target = target,
                Options = effectiveOptions,
                ComparedUtc = DateTime.UtcNow,
                Differences = differences
            };

            result.Differences = differences
                .OrderBy(d => TypeOrder(d.ObjectType))
                .ThenBy(d => d.ObjectName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.ChildName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return result;
        }

        public static string GenerateSyncScript(SchemaComparisonResult result, SchemaScriptOptions options)
        {
            return SchemaScriptWriter.GenerateSyncScript(result, options);
        }

        private static int TypeOrder(SchemaObjectType type)
        {
            switch (type)
            {
                case SchemaObjectType.Table:
                    return 0;
                case SchemaObjectType.Column:
                    return 1;
                case SchemaObjectType.Index:
                    return 2;
                case SchemaObjectType.Constraint:
                    return 3;
                case SchemaObjectType.View:
                    return 4;
                default:
                    return 5;
            }
        }

        private static void CompareTables(SchemaSnapshot source, SchemaSnapshot target, SchemaCompareOptions options, List<SchemaDifference> differences)
        {
            var targetTables = new Dictionary<string, SchemaTable>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in target.Tables)
            {
                if (!targetTables.ContainsKey(table.Key))
                {
                    targetTables[table.Key] = table;
                }
            }

            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceTable in source.Tables)
            {
                SchemaTable targetTable;
                if (!targetTables.TryGetValue(sourceTable.Key, out targetTable))
                {
                    differences.Add(new SchemaDifference
                    {
                        ObjectType = SchemaObjectType.Table,
                        Status = SchemaDifferenceStatus.MissingInTarget,
                        Severity = SchemaDifferenceSeverity.Warning,
                        ObjectName = sourceTable.QualifiedName,
                        SchemaName = sourceTable.Schema,
                        ChildName = sourceTable.Name,
                        ParentQualifiedName = sourceTable.QualifiedName,
                        Summary = "Table exists in the source only, it will be created",
                        SourceObject = sourceTable
                    });
                    continue;
                }

                matched.Add(targetTable.Key);

                if (options.CompareColumns)
                {
                    CompareColumns(sourceTable, targetTable, options, differences);
                }

                if (options.CompareIndexes)
                {
                    CompareIndexes(sourceTable, targetTable, options, differences);
                }

                if (options.CompareConstraints)
                {
                    CompareConstraints(sourceTable, targetTable, options, differences);
                }
            }

            foreach (var targetTable in target.Tables)
            {
                if (matched.Contains(targetTable.Key))
                {
                    continue;
                }

                string summary = "Table exists in the target only, it will be dropped";
                if (targetTable.RowCount.HasValue)
                {
                    summary += " (about " + targetTable.RowCount.Value.ToString("N0", CultureInfo.InvariantCulture) + " rows)";
                }

                differences.Add(new SchemaDifference
                {
                    ObjectType = SchemaObjectType.Table,
                    Status = SchemaDifferenceStatus.MissingInSource,
                    Severity = SchemaDifferenceSeverity.Warning,
                    ObjectName = targetTable.QualifiedName,
                    SchemaName = targetTable.Schema,
                    ChildName = targetTable.Name,
                    ParentQualifiedName = targetTable.QualifiedName,
                    Summary = summary,
                    TargetObject = targetTable
                });
            }
        }

        private static void CompareColumns(SchemaTable sourceTable, SchemaTable targetTable, SchemaCompareOptions options, List<SchemaDifference> differences)
        {
            var targetColumns = new Dictionary<string, SchemaColumn>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in targetTable.Columns)
            {
                if (!targetColumns.ContainsKey(column.Name))
                {
                    targetColumns[column.Name] = column;
                }
            }

            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceColumn in sourceTable.Columns.OrderBy(c => c.Ordinal))
            {
                SchemaColumn targetColumn;
                if (!targetColumns.TryGetValue(sourceColumn.Name, out targetColumn))
                {
                    differences.Add(new SchemaDifference
                    {
                        ObjectType = SchemaObjectType.Column,
                        Status = SchemaDifferenceStatus.MissingInTarget,
                        Severity = SchemaDifferenceSeverity.Warning,
                        ObjectName = SchemaScriptName.Quote(sourceTable.Schema, sourceTable.Name, sourceColumn.Name),
                        SchemaName = sourceTable.Schema,
                        ParentName = sourceTable.Name,
                        ChildName = sourceColumn.Name,
                        ParentQualifiedName = sourceTable.QualifiedName,
                        Summary = "Column is missing in the target (" + sourceColumn.TypeDisplay + ")",
                        SourceObject = sourceColumn
                    });
                    continue;
                }

                matched.Add(targetColumn.Name);

                var properties = CompareColumnProperties(sourceColumn, targetColumn);
                var difference = CreateDifference(SchemaObjectType.Column, sourceTable, sourceColumn.Name, properties, sourceColumn, targetColumn, options);
                if (difference != null)
                {
                    differences.Add(difference);
                }
            }

            foreach (var targetColumn in targetTable.Columns.OrderBy(c => c.Ordinal))
            {
                if (matched.Contains(targetColumn.Name))
                {
                    continue;
                }

                differences.Add(new SchemaDifference
                {
                    ObjectType = SchemaObjectType.Column,
                    Status = SchemaDifferenceStatus.MissingInSource,
                    Severity = SchemaDifferenceSeverity.Warning,
                    ObjectName = SchemaScriptName.Quote(targetTable.Schema, targetTable.Name, targetColumn.Name),
                    SchemaName = targetTable.Schema,
                    ParentName = targetTable.Name,
                    ChildName = targetColumn.Name,
                    ParentQualifiedName = targetTable.QualifiedName,
                    Summary = "Column exists in the target only, it will be dropped (" + targetColumn.TypeDisplay + ")",
                    TargetObject = targetColumn
                });
            }

            if (options.ReportColumnOrder && sourceTable.Columns.Count > 0 && sourceTable.Columns.Count == targetTable.Columns.Count)
            {
                var sourceOrder = sourceTable.Columns.OrderBy(c => c.Ordinal).Select(c => c.Name).ToList();
                var targetOrder = targetTable.Columns.OrderBy(c => c.Ordinal).Select(c => c.Name).ToList();
                bool sameSet = sourceOrder.All(name => targetOrder.Contains(name, StringComparer.OrdinalIgnoreCase));
                if (sameSet && !sourceOrder.SequenceEqual(targetOrder, StringComparer.OrdinalIgnoreCase))
                {
                    differences.Add(new SchemaDifference
                    {
                        ObjectType = SchemaObjectType.Column,
                        Status = SchemaDifferenceStatus.Different,
                        Severity = SchemaDifferenceSeverity.Information,
                        ObjectName = sourceTable.QualifiedName,
                        SchemaName = sourceTable.Schema,
                        ChildName = sourceTable.Name,
                        ParentQualifiedName = sourceTable.QualifiedName,
                        Summary = "Column order differs (informational, not scripted): source " + string.Join(", ", sourceOrder) +
                                  " / target " + string.Join(", ", targetOrder),
                        SourceObject = sourceTable,
                        TargetObject = targetTable
                    });
                }
            }
        }

        private static List<PropertyDifference> CompareColumnProperties(SchemaColumn source, SchemaColumn target)
        {
            var properties = new List<PropertyDifference>();

            AddProperty(properties, "Data type", source.TypeDisplay, target.TypeDisplay, true, StringComparison.OrdinalIgnoreCase);
            AddProperty(properties, "Nullability", source.IsNullable ? "NULL" : "NOT NULL", target.IsNullable ? "NULL" : "NOT NULL", false, StringComparison.OrdinalIgnoreCase);
            AddProperty(properties, "Identity", IdentityText(source), IdentityText(target), false, StringComparison.OrdinalIgnoreCase);
            AddProperty(properties, "Computed", source.IsComputed ? (source.IsPersisted ? "Yes (persisted)" : "Yes") : "No",
                target.IsComputed ? (target.IsPersisted ? "Yes (persisted)" : "Yes") : "No", false, StringComparison.OrdinalIgnoreCase);
            AddProperty(properties, "Collation", source.Collation, target.Collation, false, StringComparison.OrdinalIgnoreCase);
            AddProperty(properties, "Default", source.DefaultDefinition, target.DefaultDefinition, false, StringComparison.Ordinal);
            AddProperty(properties, "Computed expression", source.ComputedDefinition, target.ComputedDefinition, true, StringComparison.Ordinal);

            return properties;
        }

        private static string IdentityText(SchemaColumn column)
        {
            if (!column.IsIdentity)
            {
                return "No";
            }

            decimal seed = column.IdentitySeed ?? 1;
            decimal increment = column.IdentityIncrement ?? 1;
            return "Yes (" + seed.ToString(CultureInfo.InvariantCulture) + "," + increment.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private static void CompareIndexes(SchemaTable sourceTable, SchemaTable targetTable, SchemaCompareOptions options, List<SchemaDifference> differences)
        {
            var targetIndexes = new Dictionary<string, SchemaIndex>(StringComparer.OrdinalIgnoreCase);
            foreach (var index in targetTable.Indexes)
            {
                if (!index.BacksConstraint && !targetIndexes.ContainsKey(index.Name))
                {
                    targetIndexes[index.Name] = index;
                }
            }

            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceIndex in sourceTable.Indexes.Where(i => !i.BacksConstraint))
            {
                SchemaIndex targetIndex;
                if (!targetIndexes.TryGetValue(sourceIndex.Name, out targetIndex))
                {
                    differences.Add(new SchemaDifference
                    {
                        ObjectType = SchemaObjectType.Index,
                        Status = SchemaDifferenceStatus.MissingInTarget,
                        Severity = SchemaDifferenceSeverity.Warning,
                        ObjectName = SchemaScriptName.Quote(sourceTable.Schema, sourceTable.Name, sourceIndex.Name),
                        SchemaName = sourceTable.Schema,
                        ParentName = sourceTable.Name,
                        ChildName = sourceIndex.Name,
                        ParentQualifiedName = sourceTable.QualifiedName,
                        Summary = "Index is missing in the target (" + DescribeIndex(sourceIndex) + ")",
                        SourceObject = sourceIndex
                    });
                    continue;
                }

                matched.Add(targetIndex.Name);

                var properties = new List<PropertyDifference>();
                AddProperty(properties, "Unique", sourceIndex.IsUnique ? "Yes" : "No", targetIndex.IsUnique ? "Yes" : "No", false, StringComparison.OrdinalIgnoreCase);
                AddProperty(properties, "Type", sourceIndex.Type, targetIndex.Type, false, StringComparison.OrdinalIgnoreCase);
                AddProperty(properties, "Key columns", sourceIndex.ColumnSignature, targetIndex.ColumnSignature, false, StringComparison.OrdinalIgnoreCase);
                AddProperty(properties, "Included columns", string.Join(", ", sourceIndex.IncludedColumns), string.Join(", ", targetIndex.IncludedColumns), false, StringComparison.OrdinalIgnoreCase);
                AddProperty(properties, "Filter", sourceIndex.FilterDefinition, targetIndex.FilterDefinition, true, StringComparison.Ordinal);

                var difference = CreateDifference(SchemaObjectType.Index, sourceTable, sourceIndex.Name, properties, sourceIndex, targetIndex, options);
                if (difference != null)
                {
                    differences.Add(difference);
                }
            }

            foreach (var targetIndex in targetTable.Indexes.Where(i => !i.BacksConstraint))
            {
                if (matched.Contains(targetIndex.Name))
                {
                    continue;
                }

                differences.Add(new SchemaDifference
                {
                    ObjectType = SchemaObjectType.Index,
                    Status = SchemaDifferenceStatus.MissingInSource,
                    Severity = SchemaDifferenceSeverity.Warning,
                    ObjectName = SchemaScriptName.Quote(targetTable.Schema, targetTable.Name, targetIndex.Name),
                    SchemaName = targetTable.Schema,
                    ParentName = targetTable.Name,
                    ChildName = targetIndex.Name,
                    ParentQualifiedName = targetTable.QualifiedName,
                    Summary = "Index exists in the target only, it will be dropped (" + DescribeIndex(targetIndex) + ")",
                    TargetObject = targetIndex
                });
            }
        }

        private static string DescribeIndex(SchemaIndex index)
        {
            return (index.IsUnique ? "unique " : string.Empty) + (index.IsClustered ? "clustered" : "nonclustered") +
                   (string.IsNullOrWhiteSpace(index.FilterDefinition) ? string.Empty : ", filtered");
        }

        private static void CompareConstraints(SchemaTable sourceTable, SchemaTable targetTable, SchemaCompareOptions options, List<SchemaDifference> differences)
        {
            var targetConstraints = new Dictionary<string, SchemaConstraint>(StringComparer.OrdinalIgnoreCase);
            foreach (var constraint in targetTable.Constraints)
            {
                if (!targetConstraints.ContainsKey(constraint.Name))
                {
                    targetConstraints[constraint.Name] = constraint;
                }
            }

            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceConstraint in sourceTable.Constraints)
            {
                SchemaConstraint targetConstraint;
                if (!targetConstraints.TryGetValue(sourceConstraint.Name, out targetConstraint))
                {
                    differences.Add(new SchemaDifference
                    {
                        ObjectType = SchemaObjectType.Constraint,
                        Status = SchemaDifferenceStatus.MissingInTarget,
                        Severity = SchemaDifferenceSeverity.Warning,
                        ObjectName = SchemaScriptName.Quote(sourceTable.Schema, sourceTable.Name, sourceConstraint.Name),
                        SchemaName = sourceTable.Schema,
                        ParentName = sourceTable.Name,
                        ChildName = sourceConstraint.Name,
                        ParentQualifiedName = sourceTable.QualifiedName,
                        Summary = "Constraint is missing in the target (" + DescribeConstraint(sourceConstraint) + ")",
                        SourceObject = sourceConstraint
                    });
                    continue;
                }

                matched.Add(targetConstraint.Name);

                var properties = CompareConstraintProperties(sourceConstraint, targetConstraint);
                var difference = CreateDifference(SchemaObjectType.Constraint, sourceTable, sourceConstraint.Name, properties, sourceConstraint, targetConstraint, options);
                if (difference != null)
                {
                    differences.Add(difference);
                }
            }

            foreach (var targetConstraint in targetTable.Constraints)
            {
                if (matched.Contains(targetConstraint.Name))
                {
                    continue;
                }

                differences.Add(new SchemaDifference
                {
                    ObjectType = SchemaObjectType.Constraint,
                    Status = SchemaDifferenceStatus.MissingInSource,
                    Severity = SchemaDifferenceSeverity.Warning,
                    ObjectName = SchemaScriptName.Quote(targetTable.Schema, targetTable.Name, targetConstraint.Name),
                    SchemaName = targetTable.Schema,
                    ParentName = targetTable.Name,
                    ChildName = targetConstraint.Name,
                    ParentQualifiedName = targetTable.QualifiedName,
                    Summary = "Constraint exists in the target only, it will be dropped (" + DescribeConstraint(targetConstraint) + ")",
                    TargetObject = targetConstraint
                });
            }
        }

        private static List<PropertyDifference> CompareConstraintProperties(SchemaConstraint source, SchemaConstraint target)
        {
            var properties = new List<PropertyDifference>();

            if (source.Kind != target.Kind)
            {
                properties.Add(new PropertyDifference
                {
                    Property = "Kind",
                    SourceValue = source.Kind.ToString(),
                    TargetValue = target.Kind.ToString()
                });

                return properties;
            }

            switch (source.Kind)
            {
                case SchemaConstraintKind.PrimaryKey:
                case SchemaConstraintKind.Unique:
                    AddProperty(properties, "Columns", source.ColumnSignature, target.ColumnSignature, false, StringComparison.OrdinalIgnoreCase);
                    AddProperty(properties, "Clustered", source.IsClustered ? "Yes" : "No", target.IsClustered ? "Yes" : "No", false, StringComparison.OrdinalIgnoreCase);
                    break;
                case SchemaConstraintKind.ForeignKey:
                    AddProperty(properties, "Columns", source.ColumnSignature, target.ColumnSignature, false, StringComparison.OrdinalIgnoreCase);
                    AddProperty(properties, "Referenced table", SchemaScriptName.Quote(source.ReferencedSchema, source.ReferencedTable),
                        SchemaScriptName.Quote(target.ReferencedSchema, target.ReferencedTable), false, StringComparison.OrdinalIgnoreCase);
                    AddProperty(properties, "Referenced columns", string.Join(", ", source.ReferencedColumns), string.Join(", ", target.ReferencedColumns), false, StringComparison.OrdinalIgnoreCase);
                    AddProperty(properties, "On delete", source.DeleteAction, target.DeleteAction, false, StringComparison.OrdinalIgnoreCase);
                    AddProperty(properties, "On update", source.UpdateAction, target.UpdateAction, false, StringComparison.OrdinalIgnoreCase);
                    AddProperty(properties, "Trusted", source.IsNotTrusted ? "No" : "Yes", target.IsNotTrusted ? "No" : "Yes", false, StringComparison.OrdinalIgnoreCase);
                    AddProperty(properties, "Disabled", source.IsDisabled ? "Yes" : "No", target.IsDisabled ? "Yes" : "No", false, StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    AddProperty(properties, "Definition", source.Definition, target.Definition, true, StringComparison.Ordinal);
                    AddProperty(properties, "Trusted", source.IsNotTrusted ? "No" : "Yes", target.IsNotTrusted ? "No" : "Yes", false, StringComparison.OrdinalIgnoreCase);
                    AddProperty(properties, "Disabled", source.IsDisabled ? "Yes" : "No", target.IsDisabled ? "Yes" : "No", false, StringComparison.OrdinalIgnoreCase);
                    break;
            }

            return properties;
        }

        private static string DescribeConstraint(SchemaConstraint constraint)
        {
            string kind;
            switch (constraint.Kind)
            {
                case SchemaConstraintKind.PrimaryKey:
                    kind = "primary key";
                    break;
                case SchemaConstraintKind.Unique:
                    kind = "unique";
                    break;
                case SchemaConstraintKind.ForeignKey:
                    kind = "foreign key to " + SchemaScriptName.Quote(constraint.ReferencedSchema, constraint.ReferencedTable);
                    break;
                case SchemaConstraintKind.Check:
                    kind = "check";
                    break;
                default:
                    kind = "default";
                    break;
            }

            return kind + (constraint.Columns.Count > 0 ? " on " + constraint.ColumnSignature : string.Empty);
        }

        private static void CompareViews(SchemaSnapshot source, SchemaSnapshot target, SchemaCompareOptions options, List<SchemaDifference> differences)
        {
            var targetViews = new Dictionary<string, SchemaView>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in target.Views)
            {
                if (!targetViews.ContainsKey(view.Key))
                {
                    targetViews[view.Key] = view;
                }
            }

            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceView in source.Views)
            {
                SchemaView targetView;
                if (!targetViews.TryGetValue(sourceView.Key, out targetView))
                {
                    differences.Add(new SchemaDifference
                    {
                        ObjectType = SchemaObjectType.View,
                        Status = SchemaDifferenceStatus.MissingInTarget,
                        Severity = sourceView.IsEncrypted ? SchemaDifferenceSeverity.Error : SchemaDifferenceSeverity.Warning,
                        ObjectName = SchemaScriptName.Quote(sourceView.Schema, sourceView.Name),
                        SchemaName = sourceView.Schema,
                        ChildName = sourceView.Name,
                        Summary = sourceView.IsEncrypted
                            ? "View is missing in the target but its definition is not available (encrypted)"
                            : "View exists in the source only, it will be created",
                        SourceObject = sourceView
                    });
                    continue;
                }

                matched.Add(targetView.Key);

                var properties = new List<PropertyDifference>();
                AddProperty(properties, "Definition", sourceView.Definition, targetView.Definition, true, StringComparison.Ordinal);

                var difference = CreateDifference(SchemaObjectType.View, null, sourceView.Name, properties, sourceView, targetView, options);
                if (difference != null)
                {
                    if (string.IsNullOrWhiteSpace(sourceView.Definition))
                    {
                        difference.Severity = SchemaDifferenceSeverity.Error;
                        difference.Summary = "Definition is not available (encrypted view)";
                    }

                    differences.Add(difference);
                }
            }

            foreach (var targetView in target.Views)
            {
                if (matched.Contains(targetView.Key))
                {
                    continue;
                }

                differences.Add(new SchemaDifference
                {
                    ObjectType = SchemaObjectType.View,
                    Status = SchemaDifferenceStatus.MissingInSource,
                    Severity = SchemaDifferenceSeverity.Warning,
                    ObjectName = SchemaScriptName.Quote(targetView.Schema, targetView.Name),
                    SchemaName = targetView.Schema,
                    ChildName = targetView.Name,
                    Summary = "View exists in the target only, it will be dropped",
                    TargetObject = targetView
                });
            }
        }

        private static void CompareRoutines(SchemaSnapshot source, SchemaSnapshot target, SchemaCompareOptions options, List<SchemaDifference> differences)
        {
            var targetRoutines = new Dictionary<string, SchemaRoutine>(StringComparer.OrdinalIgnoreCase);
            foreach (var routine in target.Routines)
            {
                if (!IsTrigger(routine) && !options.CompareTriggers)
                {
                    continue;
                }

                if (!targetRoutines.ContainsKey(routine.Key))
                {
                    targetRoutines[routine.Key] = routine;
                }
            }

            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceRoutine in source.Routines)
            {
                if (IsTrigger(sourceRoutine) && !options.CompareTriggers)
                {
                    continue;
                }

                SchemaRoutine targetRoutine;
                if (!targetRoutines.TryGetValue(sourceRoutine.Key, out targetRoutine))
                {
                    differences.Add(new SchemaDifference
                    {
                        ObjectType = SchemaObjectType.Routine,
                        Status = SchemaDifferenceStatus.MissingInTarget,
                        Severity = sourceRoutine.IsEncrypted ? SchemaDifferenceSeverity.Error : SchemaDifferenceSeverity.Warning,
                        ObjectName = RoutineDisplayName(sourceRoutine),
                        SchemaName = sourceRoutine.Schema,
                        ParentName = sourceRoutine.ParentName,
                        ChildName = sourceRoutine.Name,
                        Summary = sourceRoutine.IsEncrypted
                            ? RoutineKindText(sourceRoutine.Kind) + " is missing in the target but its definition is not available (encrypted)"
                            : RoutineKindText(sourceRoutine.Kind) + " exists in the source only, it will be created",
                        SourceObject = sourceRoutine
                    });
                    continue;
                }

                matched.Add(targetRoutine.Key);

                var properties = new List<PropertyDifference>();
                AddProperty(properties, "Kind", RoutineKindText(sourceRoutine.Kind), RoutineKindText(targetRoutine.Kind), false, StringComparison.OrdinalIgnoreCase);
                AddProperty(properties, "Definition", sourceRoutine.Definition, targetRoutine.Definition, true, StringComparison.Ordinal);
                if (sourceRoutine.Kind == SchemaRoutineKind.Trigger)
                {
                    AddProperty(properties, "Disabled", sourceRoutine.IsDisabled ? "Yes" : "No", targetRoutine.IsDisabled ? "Yes" : "No", false, StringComparison.OrdinalIgnoreCase);
                }

                var difference = CreateDifference(SchemaObjectType.Routine, null, sourceRoutine.Name, properties, sourceRoutine, targetRoutine, options);
                if (difference != null)
                {
                    difference.ObjectName = RoutineDisplayName(sourceRoutine);
                    if (string.IsNullOrWhiteSpace(sourceRoutine.Definition))
                    {
                        difference.Severity = SchemaDifferenceSeverity.Error;
                        difference.Summary = "Definition is not available (encrypted module)";
                    }

                    differences.Add(difference);
                }
            }

            foreach (var targetRoutine in target.Routines)
            {
                if (matched.Contains(targetRoutine.Key))
                {
                    continue;
                }

                if (IsTrigger(targetRoutine) && !options.CompareTriggers)
                {
                    continue;
                }

                differences.Add(new SchemaDifference
                {
                    ObjectType = SchemaObjectType.Routine,
                    Status = SchemaDifferenceStatus.MissingInSource,
                    Severity = SchemaDifferenceSeverity.Warning,
                    ObjectName = RoutineDisplayName(targetRoutine),
                    SchemaName = targetRoutine.Schema,
                    ParentName = targetRoutine.ParentName,
                    ChildName = targetRoutine.Name,
                    Summary = RoutineKindText(targetRoutine.Kind) + " exists in the target only, it will be dropped",
                    TargetObject = targetRoutine
                });
            }
        }

        private static bool IsTrigger(SchemaRoutine routine)
        {
            return routine.Kind == SchemaRoutineKind.Trigger;
        }

        private static string RoutineDisplayName(SchemaRoutine routine)
        {
            if (routine.Kind == SchemaRoutineKind.Trigger && !string.IsNullOrWhiteSpace(routine.ParentName))
            {
                return SchemaScriptName.Quote(routine.ParentSchema ?? routine.Schema, routine.ParentName, routine.Name);
            }

            return SchemaScriptName.Quote(routine.Schema, routine.Name);
        }

        private static string RoutineKindText(SchemaRoutineKind kind)
        {
            switch (kind)
            {
                case SchemaRoutineKind.Procedure:
                    return "Procedure";
                case SchemaRoutineKind.ScalarFunction:
                    return "Scalar function";
                case SchemaRoutineKind.InlineTableValuedFunction:
                    return "Inline table valued function";
                case SchemaRoutineKind.TableValuedFunction:
                    return "Table valued function";
                case SchemaRoutineKind.Aggregate:
                    return "Aggregate";
                default:
                    return "Trigger";
            }
        }

        private static SchemaDifference CreateDifference(
            SchemaObjectType objectType,
            SchemaTable table,
            string childName,
            List<PropertyDifference> properties,
            object sourceObject,
            object targetObject,
            SchemaCompareOptions options)
        {
            if (properties.Count == 0)
            {
                return null;
            }

            bool whitespaceOnly = properties.All(p => p.IsWhitespaceOnly);
            if (whitespaceOnly && options != null && options.IgnoreWhitespaceDifferences)
            {
                return null;
            }

            string schema = table != null ? table.Schema : null;
            string parent = table != null ? table.Name : null;
            string objectName;
            if (table != null)
            {
                objectName = SchemaScriptName.Quote(table.Schema, table.Name, childName);
            }
            else if (sourceObject is SchemaView)
            {
                var view = (SchemaView)sourceObject;
                schema = view.Schema;
                objectName = SchemaScriptName.Quote(view.Schema, view.Name);
            }
            else if (sourceObject is SchemaRoutine)
            {
                var routine = (SchemaRoutine)sourceObject;
                schema = routine.Schema;
                objectName = RoutineDisplayName(routine);
            }
            else
            {
                objectName = SchemaScriptName.Quote(childName);
            }

            var difference = new SchemaDifference
            {
                ObjectType = objectType,
                Status = SchemaDifferenceStatus.Different,
                Severity = whitespaceOnly ? SchemaDifferenceSeverity.Information : SchemaDifferenceSeverity.Warning,
                ObjectName = objectName,
                SchemaName = schema,
                ParentName = parent,
                ChildName = childName,
                ParentQualifiedName = table != null ? table.QualifiedName : objectName,
                IsWhitespaceOnly = whitespaceOnly,
                Properties = properties,
                Summary = BuildPropertySummary(properties),
                SourceObject = sourceObject,
                TargetObject = targetObject
            };

            return difference;
        }

        private static string BuildPropertySummary(List<PropertyDifference> properties)
        {
            var builder = new StringBuilder();
            foreach (var property in properties)
            {
                if (builder.Length > 0)
                {
                    builder.Append("; ");
                }

                // \u2192 is the arrow used between the source and the target value.
                builder.Append(property.Property).Append(": ")
                    .Append(Value(property.SourceValue)).Append(" \u2192 ").Append(Value(property.TargetValue));
            }

            return Shorten(builder.ToString(), 500);
        }

        private static string Value(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "NULL" : Shorten(value, 120);
        }

        private static string Shorten(string value, int maximum)
        {
            string text = (value ?? "NULL").Replace("\r", " ").Replace("\n", " ");
            if (text.Length <= maximum)
            {
                return text;
            }

            return text.Substring(0, maximum) + "...";
        }

        private static void AddProperty(List<PropertyDifference> properties, string name, string sourceValue, string targetValue, bool normalize, StringComparison comparison)
        {
            string source = sourceValue ?? string.Empty;
            string target = targetValue ?? string.Empty;

            if (normalize)
            {
                string normalizedSource = SchemaDefinitionNormalizer.Normalize(source);
                string normalizedTarget = SchemaDefinitionNormalizer.Normalize(target);
                if (string.Equals(normalizedSource, normalizedTarget, StringComparison.Ordinal))
                {
                    if (!string.Equals(source, target, StringComparison.Ordinal))
                    {
                        properties.Add(new PropertyDifference
                        {
                            Property = name,
                            SourceValue = Shorten(source, 4000),
                            TargetValue = Shorten(target, 4000),
                            IsWhitespaceOnly = true
                        });
                    }

                    return;
                }
            }
            else if (string.Equals(source, target, comparison))
            {
                return;
            }

            properties.Add(new PropertyDifference
            {
                Property = name,
                SourceValue = Shorten(source, 4000),
                TargetValue = Shorten(target, 4000),
                IsWhitespaceOnly = false
            });
        }
    }

    /// <summary>
    /// Turns a comparison result into a T-SQL script that brings the target to the
    /// source definition. The order is chosen so that the script has the best chance
    /// of running front to back without manual reordering.
    /// </summary>
    public static class SchemaScriptWriter
    {
        private static readonly Regex CreateKeyword = new Regex("\\bCREATE\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AlterKeyword = new Regex("\\bALTER\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string GenerateSyncScript(SchemaComparisonResult result, SchemaScriptOptions options)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            var effectiveOptions = options ?? new SchemaScriptOptions();
            var actionable = result.Differences == null
                ? new List<SchemaDifference>()
                : result.Differences.Where(d => d.Severity != SchemaDifferenceSeverity.Information).ToList();

            var newTables = actionable.Where(d => d.ObjectType == SchemaObjectType.Table && d.Status == SchemaDifferenceStatus.MissingInTarget)
                .OrderBy(d => d.ObjectName, StringComparer.OrdinalIgnoreCase).ToList();
            var droppedTables = actionable.Where(d => d.ObjectType == SchemaObjectType.Table && d.Status == SchemaDifferenceStatus.MissingInSource)
                .OrderBy(d => d.ObjectName, StringComparer.OrdinalIgnoreCase).ToList();
            var addedColumns = actionable.Where(d => d.ObjectType == SchemaObjectType.Column && d.Status == SchemaDifferenceStatus.MissingInTarget)
                .OrderBy(d => d.ObjectName, StringComparer.OrdinalIgnoreCase).ToList();
            var changedColumns = actionable.Where(d => d.ObjectType == SchemaObjectType.Column && d.Status == SchemaDifferenceStatus.Different)
                .OrderBy(d => d.ObjectName, StringComparer.OrdinalIgnoreCase).ToList();
            var droppedColumns = actionable.Where(d => d.ObjectType == SchemaObjectType.Column && d.Status == SchemaDifferenceStatus.MissingInSource)
                .OrderBy(d => d.ObjectName, StringComparer.OrdinalIgnoreCase).ToList();
            var indexDifferences = effectiveOptions.IncludeIndexes
                ? actionable.Where(d => d.ObjectType == SchemaObjectType.Index).ToList()
                : new List<SchemaDifference>();
            var constraintDifferences = effectiveOptions.IncludeConstraints
                ? actionable.Where(d => d.ObjectType == SchemaObjectType.Constraint).ToList()
                : new List<SchemaDifference>();
            var viewDifferences = effectiveOptions.IncludeViews
                ? actionable.Where(d => d.ObjectType == SchemaObjectType.View).OrderBy(d => d.ObjectName, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<SchemaDifference>();
            var routineDifferences = effectiveOptions.IncludeRoutines
                ? actionable.Where(d => d.ObjectType == SchemaObjectType.Routine).OrderBy(d => d.ObjectName, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<SchemaDifference>();

            var builder = new StringBuilder();
            int statementCount = 0;

            WriteHeader(builder, result, effectiveOptions, newTables, droppedTables, droppedColumns, viewDifferences, routineDifferences);

            if (effectiveOptions.IncludeTransaction)
            {
                builder.AppendLine("BEGIN TRANSACTION;");
                builder.AppendLine();
            }

            WriteCreateMissingTables(builder, result, effectiveOptions, newTables, ref statementCount);

            WriteAddColumns(builder, result, addedColumns, ref statementCount);

            WriteAlterColumns(builder, result, effectiveOptions, changedColumns, ref statementCount);

            WriteConstraintsAndIndexes(builder, result, effectiveOptions, constraintDifferences, indexDifferences, newTables, ref statementCount);

            WriteViewsAndRoutines(builder, effectiveOptions, viewDifferences, routineDifferences, ref statementCount);

            WriteDrops(builder, effectiveOptions, droppedTables, droppedColumns, viewDifferences, routineDifferences, ref statementCount);

            if (effectiveOptions.IncludeTransaction)
            {
                builder.AppendLine("-- COMMIT TRANSACTION;");
                builder.AppendLine("-- ROLLBACK TRANSACTION;");
            }

            builder.AppendLine("-- ===== End of the generated script =====");

            return builder.ToString();
        }

        private static void WriteHeader(
            StringBuilder builder,
            SchemaComparisonResult result,
            SchemaScriptOptions options,
            List<SchemaDifference> newTables,
            List<SchemaDifference> droppedTables,
            List<SchemaDifference> droppedColumns,
            List<SchemaDifference> viewDifferences,
            List<SchemaDifference> routineDifferences)
        {
            builder.AppendLine("-- =====================================================================");
            builder.AppendLine("-- MSSQL Tool - Schema Compare synchronisation script");
            builder.AppendLine("-- Source (baseline) : " + DescribeSnapshot(result.Source));
            builder.AppendLine("-- Target (to update): " + DescribeSnapshot(result.Target));
            builder.AppendLine("-- Generated (UTC)   : " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            builder.AppendLine("-- Differences       : " + (result.Differences == null ? 0 : result.Differences.Count).ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("--");
            builder.AppendLine("-- Review this script before running it. It is not wrapped in a transaction.");
            builder.AppendLine("-- Statements that change an existing column type or nullability can fail or lose");
            builder.AppendLine("-- data; they are grouped in section 3 with a warning.");
            builder.AppendLine("-- =====================================================================");

            var droppedViews = viewDifferences.Where(d => d.Status == SchemaDifferenceStatus.MissingInSource).ToList();
            var droppedRoutines = routineDifferences.Where(d => d.Status == SchemaDifferenceStatus.MissingInSource).ToList();

            if (!options.IncludeDrops)
            {
                builder.AppendLine("-- DROP statements are disabled in the options: nothing is removed from the target.");
            }
            else if (droppedTables.Count + droppedColumns.Count + droppedViews.Count + droppedRoutines.Count > 0)
            {
                builder.AppendLine("-- ---------------------------------------------------------------------");
                builder.AppendLine("-- The following objects exist in the target only and will be DROPPED:");
                foreach (var difference in droppedTables)
                {
                    builder.AppendLine("--   TABLE     " + difference.ObjectName + (string.IsNullOrEmpty(difference.Summary) ? string.Empty : "  (" + difference.Summary + ")"));
                }

                foreach (var difference in droppedColumns)
                {
                    builder.AppendLine("--   COLUMN    " + difference.ObjectName);
                }

                foreach (var difference in droppedViews)
                {
                    builder.AppendLine("--   VIEW      " + difference.ObjectName);
                }

                foreach (var difference in droppedRoutines)
                {
                    builder.AppendLine("--   MODULE    " + difference.ObjectName);
                }

                builder.AppendLine("-- ---------------------------------------------------------------------");
            }

            if (newTables.Count == 0 && droppedTables.Count == 0 && viewDifferences.Count == 0 && routineDifferences.Count == 0)
            {
                builder.AppendLine("-- No create/drop statements are needed; only existing objects are changed.");
            }

            builder.AppendLine();
        }

        private static string DescribeSnapshot(SchemaSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "(not available)";
            }

            return "[" + (snapshot.ServerName ?? "?") + "] \\ [" + (snapshot.DatabaseName ?? "?") + "]";
        }

        private static void WriteCreateMissingTables(StringBuilder builder, SchemaComparisonResult result, SchemaScriptOptions options, List<SchemaDifference> newTables, ref int statementCount)
        {
            if (newTables.Count == 0)
            {
                return;
            }

            builder.AppendLine("-- ===== 1. Create missing objects =====");
            builder.AppendLine();

            string databaseCollation = result.Source == null ? null : result.Source.DatabaseCollation;

            foreach (var difference in newTables)
            {
                var table = difference.SourceTable;
                if (table == null)
                {
                    continue;
                }

                builder.AppendLine("-- [" + difference.StatusText + "] " + difference.ObjectName + " - " + difference.Summary);
                builder.AppendLine("CREATE TABLE " + table.QualifiedName);
                builder.AppendLine("(");

                var lines = new List<string>();
                foreach (var column in table.Columns.OrderBy(c => c.Ordinal))
                {
                    lines.Add("    " + ColumnDefinition(column, databaseCollation));
                }

                foreach (var constraint in table.Constraints.Where(c => c.Kind != SchemaConstraintKind.ForeignKey))
                {
                    lines.Add("    " + InlineConstraintDefinition(constraint));
                }

                builder.AppendLine(string.Join("," + Environment.NewLine, lines));
                builder.AppendLine(");");
                builder.AppendLine("GO");
                builder.AppendLine();
                statementCount++;
            }
        }

        private static void WriteAddColumns(StringBuilder builder, SchemaComparisonResult result, List<SchemaDifference> addedColumns, ref int statementCount)
        {
            if (addedColumns.Count == 0)
            {
                return;
            }

            builder.AppendLine("-- ===== 2. Add missing columns =====");
            builder.AppendLine();

            string databaseCollation = result.Source == null ? null : result.Source.DatabaseCollation;

            foreach (var difference in addedColumns)
            {
                var column = difference.SourceColumn;
                if (column == null || string.IsNullOrWhiteSpace(difference.ParentQualifiedName))
                {
                    continue;
                }

                builder.AppendLine("-- [" + difference.StatusText + "] " + difference.ObjectName + " - " + difference.Summary);
                string definition = ColumnDefinition(column, databaseCollation);
                string statement = "ALTER TABLE " + difference.ParentQualifiedName + " ADD " + definition;

                if (!column.IsNullable && !column.IsComputed && !string.IsNullOrWhiteSpace(column.DefaultDefinition))
                {
                    statement += " WITH VALUES";
                }

                builder.AppendLine(statement + ";");

                if (!column.IsNullable && !column.IsComputed && string.IsNullOrWhiteSpace(column.DefaultDefinition))
                {
                    builder.AppendLine("-- WARNING: the column is NOT NULL without a default value; the statement fails when the");
                    builder.AppendLine("--          table already contains rows. Add a default value or make the column nullable.");
                }

                builder.AppendLine("GO");
                builder.AppendLine();
                statementCount++;
            }
        }

        private static void WriteAlterColumns(StringBuilder builder, SchemaComparisonResult result, SchemaScriptOptions options, List<SchemaDifference> changedColumns, ref int statementCount)
        {
            if (changedColumns.Count == 0)
            {
                return;
            }

            builder.AppendLine("-- ===== 3. Modify existing columns (review carefully) =====");
            builder.AppendLine("-- ---------------------------------------------------------------------");
            builder.AppendLine("-- WARNING: the statements in this section are not safe by default.");
            builder.AppendLine("--   * Changing NULL to NOT NULL fails while rows contain NULL values.");
            builder.AppendLine("--   * Shortening a column or narrowing a data type fails when values do not fit");
            builder.AppendLine("--     and can silently truncate data in edge cases.");
            builder.AppendLine("--   * A column that is part of an index, a constraint, a computed column or a");
            builder.AppendLine("--     schema bound module cannot be altered before that object is dropped.");
            builder.AppendLine("--   * IDENTITY and computed columns cannot be altered at all.");
            builder.AppendLine("-- Verify every statement against the target data before executing.");
            if (!options.IncludeAlterColumn)
            {
                builder.AppendLine("-- The ALTER COLUMN option is disabled: the statements below are commented out.");
            }

            builder.AppendLine("-- ---------------------------------------------------------------------");
            builder.AppendLine();

            string databaseCollation = result.Source == null ? null : result.Source.DatabaseCollation;

            foreach (var difference in changedColumns)
            {
                if (string.IsNullOrWhiteSpace(difference.ParentQualifiedName) || difference.SourceColumn == null)
                {
                    continue;
                }

                var sourceColumn = difference.SourceColumn;
                var targetColumn = difference.TargetColumn;
                builder.AppendLine("-- [" + difference.StatusText + "] " + difference.ObjectName + " - " + difference.Summary);

                bool typeChanged = HasProperty(difference, "Data type") || HasProperty(difference, "Nullability") ||
                                   HasProperty(difference, "Collation") || HasProperty(difference, "Computed expression") ||
                                   HasProperty(difference, "Identity");
                bool defaultChanged = HasProperty(difference, "Default");
                bool computed = sourceColumn.IsComputed || (targetColumn != null && targetColumn.IsComputed);
                bool identityChanged = targetColumn != null && sourceColumn.IsIdentity != targetColumn.IsIdentity;

                foreach (var note in DescribeColumnDependencies(result, difference))
                {
                    builder.AppendLine("-- NOTE: " + note);
                }

                if (identityChanged)
                {
                    builder.AppendLine("-- The IDENTITY property cannot be changed with ALTER COLUMN.");
                    builder.AppendLine("-- The table has to be rebuilt (or the column dropped and recreated) if this is required.");
                }

                if (computed && HasProperty(difference, "Computed expression"))
                {
                    builder.AppendLine("-- A computed expression cannot be altered: the column is dropped and added again.");
                    builder.AppendLine("-- DROP " + difference.ObjectName);
                    builder.AppendLine("ALTER TABLE " + difference.ParentQualifiedName + " DROP COLUMN " + SchemaScriptName.Quote(sourceColumn.Name) + ";");
                    builder.AppendLine("ALTER TABLE " + difference.ParentQualifiedName + " ADD " + ColumnDefinition(sourceColumn, databaseCollation) + ";");
                    builder.AppendLine("GO");
                    builder.AppendLine();
                    statementCount += 2;
                    continue;
                }

                // A default constraint blocks ALTER COLUMN, so it is removed first and restored after.
                bool dropDefault = targetColumn != null && !string.IsNullOrEmpty(targetColumn.DefaultConstraintName) && (defaultChanged || (typeChanged && !computed));
                bool addDefault = !string.IsNullOrWhiteSpace(sourceColumn.DefaultDefinition) && !computed && (defaultChanged || dropDefault);

                if (dropDefault)
                {
                    builder.AppendLine("ALTER TABLE " + difference.ParentQualifiedName + " DROP CONSTRAINT " + SchemaScriptName.Quote(targetColumn.DefaultConstraintName) + ";");
                    statementCount++;
                }

                if (typeChanged && !computed && !identityChanged)
                {
                    if (!sourceColumn.IsNullable && targetColumn != null && targetColumn.IsNullable)
                    {
                        builder.AppendLine("-- RISK: the column becomes NOT NULL. Update the existing NULL values first:");
                        builder.AppendLine("--   UPDATE " + difference.ParentQualifiedName + " SET " + SchemaScriptName.Quote(sourceColumn.Name) +
                                           " = <value> WHERE " + SchemaScriptName.Quote(sourceColumn.Name) + " IS NULL;");
                    }

                    if (targetColumn != null && IsNarrowing(sourceColumn, targetColumn))
                    {
                        builder.AppendLine("-- RISK: the new type is narrower than the current one; rows that do not fit make this fail.");
                    }

                    string statement = "ALTER TABLE " + difference.ParentQualifiedName + " ALTER COLUMN " +
                                       AlterColumnDefinition(sourceColumn, targetColumn) + ";";
                    if (options.IncludeAlterColumn)
                    {
                        builder.AppendLine(statement);
                    }
                    else
                    {
                        builder.AppendLine("-- " + statement);
                    }

                    statementCount++;
                }

                if (addDefault)
                {
                    builder.AppendLine("ALTER TABLE " + difference.ParentQualifiedName + " ADD CONSTRAINT " + SchemaScriptName.Quote(DefaultConstraintName(sourceColumn)) +
                                       " DEFAULT " + sourceColumn.DefaultDefinition.Trim() + " FOR " + SchemaScriptName.Quote(sourceColumn.Name) + ";");
                    statementCount++;
                }

                if (!typeChanged && !defaultChanged && !identityChanged)
                {
                    builder.AppendLine("-- No statement could be generated automatically for this difference.");
                }

                builder.AppendLine("GO");
                builder.AppendLine();
            }
        }

        private static IEnumerable<string> DescribeColumnDependencies(SchemaComparisonResult result, SchemaDifference difference)
        {
            var table = result.Source == null
                ? null
                : result.Source.Tables.FirstOrDefault(t => string.Equals(t.QualifiedName, difference.ParentQualifiedName, StringComparison.OrdinalIgnoreCase));
            if (table == null || difference.SourceColumn == null)
            {
                yield break;
            }

            string columnName = difference.SourceColumn.Name;

            foreach (var index in table.Indexes.Where(i =>
                         i.Columns.Any(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase)) ||
                         i.IncludedColumns.Any(name => string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))))
            {
                if (index.BacksConstraint)
                {
                    yield return "the column is part of constraint '" + index.Name + "', drop it before altering the column.";
                }
                else
                {
                    yield return "the column is part of index '" + index.Name + "', drop it before altering the column.";
                }
            }

            foreach (var constraint in table.Constraints.Where(c => c.Kind != SchemaConstraintKind.PrimaryKey && c.Kind != SchemaConstraintKind.Unique)
                         .Where(c => c.Columns.Any(name => string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))))
            {
                yield return "the column is used by constraint '" + constraint.Name + "', drop it before altering the column.";
            }

            foreach (var computed in table.Columns.Where(c => c.IsComputed && !string.IsNullOrWhiteSpace(c.ComputedDefinition))
                         .Where(c => c.ComputedDefinition.IndexOf(columnName, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                yield return "the computed column '" + computed.Name + "' references the column.";
            }
        }

        private static bool HasProperty(SchemaDifference difference, string property)
        {
            return difference.Properties != null && difference.Properties.Any(p => string.Equals(p.Property, property, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsNarrowing(SchemaColumn source, SchemaColumn target)
        {
            if (!string.Equals(source.SystemTypeName, target.SystemTypeName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (source.MaxLength >= 0 && target.MaxLength > source.MaxLength)
            {
                return true;
            }

            return source.Precision < target.Precision || source.Scale < target.Scale;
        }

        private static void WriteConstraintsAndIndexes(
            StringBuilder builder,
            SchemaComparisonResult result,
            SchemaScriptOptions options,
            List<SchemaDifference> constraintDifferences,
            List<SchemaDifference> indexDifferences,
            List<SchemaDifference> newTables,
            ref int statementCount)
        {
            var dropConstraints = constraintDifferences.Where(d => d.Status != SchemaDifferenceStatus.MissingInTarget).ToList();
            var addConstraints = constraintDifferences.Where(d => d.Status != SchemaDifferenceStatus.MissingInSource).ToList();
            var dropIndexes = indexDifferences.Where(d => d.Status != SchemaDifferenceStatus.MissingInTarget).ToList();
            var addIndexes = indexDifferences.Where(d => d.Status != SchemaDifferenceStatus.MissingInSource).ToList();

            // Foreign keys and plain indexes of the tables created in section 1 are added here so
            // that every referenced table already exists.
            foreach (var difference in newTables)
            {
                var table = difference.SourceTable;
                if (table == null)
                {
                    continue;
                }

                foreach (var constraint in table.Constraints.Where(c => c.Kind == SchemaConstraintKind.ForeignKey))
                {
                    addConstraints.Add(new SchemaDifference
                    {
                        ObjectType = SchemaObjectType.Constraint,
                        Status = SchemaDifferenceStatus.MissingInTarget,
                        Severity = SchemaDifferenceSeverity.Warning,
                        ObjectName = SchemaScriptName.Quote(table.Schema, table.Name, constraint.Name),
                        ParentQualifiedName = table.QualifiedName,
                        ParentName = table.Name,
                        ChildName = constraint.Name,
                        Summary = "Foreign key of the new table",
                        SourceObject = constraint
                    });
                }

                foreach (var index in table.Indexes.Where(i => !i.BacksConstraint))
                {
                    addIndexes.Add(new SchemaDifference
                    {
                        ObjectType = SchemaObjectType.Index,
                        Status = SchemaDifferenceStatus.MissingInTarget,
                        Severity = SchemaDifferenceSeverity.Warning,
                        ObjectName = SchemaScriptName.Quote(table.Schema, table.Name, index.Name),
                        ParentQualifiedName = table.QualifiedName,
                        ParentName = table.Name,
                        ChildName = index.Name,
                        Summary = "Index of the new table",
                        SourceObject = index
                    });
                }
            }

            if (dropConstraints.Count == 0 && addConstraints.Count == 0 && dropIndexes.Count == 0 && addIndexes.Count == 0)
            {
                return;
            }

            builder.AppendLine("-- ===== 4. Constraints and indexes =====");
            builder.AppendLine("-- Constraints and indexes are dropped and recreated: an existing object cannot be altered.");
            builder.AppendLine();

            if (dropConstraints.Count > 0 || dropIndexes.Count > 0)
            {
                builder.AppendLine("-- --- 4.1 Drop the constraints and indexes that have to be replaced ---");
                builder.AppendLine();

                foreach (var difference in dropConstraints)
                {
                    var constraint = difference.TargetConstraint;
                    string parent = difference.ParentQualifiedName;
                    if (constraint == null || string.IsNullOrWhiteSpace(parent))
                    {
                        continue;
                    }

                    if (constraint.Kind == SchemaConstraintKind.PrimaryKey || constraint.Kind == SchemaConstraintKind.Unique)
                    {
                        builder.AppendLine("-- NOTE: foreign keys referencing this key must be dropped first.");
                    }

                    builder.AppendLine("-- [" + difference.StatusText + "] " + difference.ObjectName + " - " + difference.Summary);
                    builder.AppendLine("ALTER TABLE " + parent + " DROP CONSTRAINT " + SchemaScriptName.Quote(constraint.Name) + ";");
                    statementCount++;
                }

                foreach (var difference in dropIndexes)
                {
                    var index = difference.TargetIndex;
                    if (index == null || string.IsNullOrWhiteSpace(difference.ParentQualifiedName))
                    {
                        continue;
                    }

                    builder.AppendLine("-- [" + difference.StatusText + "] " + difference.ObjectName + " - " + difference.Summary);
                    builder.AppendLine("DROP INDEX " + SchemaScriptName.Quote(index.Name) + " ON " + difference.ParentQualifiedName + ";");
                    statementCount++;
                }

                builder.AppendLine("GO");
                builder.AppendLine();
            }

            if (addConstraints.Count > 0 || addIndexes.Count > 0)
            {
                builder.AppendLine("-- --- 4.2 Create the missing constraints and indexes ---");
                builder.AppendLine();

                foreach (var difference in addConstraints)
                {
                    var constraint = difference.SourceConstraint;
                    if (constraint == null || string.IsNullOrWhiteSpace(difference.ParentQualifiedName))
                    {
                        continue;
                    }

                    builder.AppendLine("-- [" + difference.StatusText + "] " + difference.ObjectName + " - " + difference.Summary);
                    builder.AppendLine(AddConstraintStatement(difference.ParentQualifiedName, constraint) + ";");
                    statementCount++;
                }

                foreach (var difference in addIndexes)
                {
                    var index = difference.SourceIndex;
                    if (index == null || string.IsNullOrWhiteSpace(difference.ParentQualifiedName))
                    {
                        continue;
                    }

                    builder.AppendLine("-- [" + difference.StatusText + "] " + difference.ObjectName + " - " + difference.Summary);
                    builder.AppendLine(CreateIndexStatement(difference.ParentQualifiedName, index) + ";");
                    statementCount++;
                }

                builder.AppendLine("GO");
                builder.AppendLine();
            }
        }

        private static void WriteViewsAndRoutines(
            StringBuilder builder,
            SchemaScriptOptions options,
            List<SchemaDifference> viewDifferences,
            List<SchemaDifference> routineDifferences,
            ref int statementCount)
        {
            var modules = viewDifferences.Where(d => d.Status != SchemaDifferenceStatus.MissingInSource).ToList();
            foreach (var difference in routineDifferences.Where(d => d.Status != SchemaDifferenceStatus.MissingInSource))
            {
                modules.Add(difference);
            }

            if (modules.Count == 0)
            {
                return;
            }

            builder.AppendLine("-- ===== 5. Views and programmable objects =====");
            builder.AppendLine("-- CREATE OR ALTER needs SQL Server 2016 SP1 or later. Dependencies between modules");
            builder.AppendLine("-- are not resolved: run the script again if a module fails because another one is missing.");
            builder.AppendLine();

            foreach (var difference in modules.OrderBy(d => ModuleOrder(d.ObjectType)).ThenBy(d => d.ObjectName, StringComparer.OrdinalIgnoreCase))
            {
                string definition = null;
                bool usesAnsiNulls = true;
                bool usesQuotedIdentifier = true;

                if (difference.SourceView != null)
                {
                    definition = difference.SourceView.Definition;
                    usesAnsiNulls = difference.SourceView.UsesAnsiNulls;
                    usesQuotedIdentifier = difference.SourceView.UsesQuotedIdentifier;
                }
                else if (difference.SourceRoutine != null)
                {
                    definition = difference.SourceRoutine.Definition;
                    usesAnsiNulls = difference.SourceRoutine.UsesAnsiNulls;
                    usesQuotedIdentifier = difference.SourceRoutine.UsesQuotedIdentifier;
                }

                builder.AppendLine("-- [" + difference.StatusText + "] " + difference.ObjectName + " - " + difference.Summary);

                if (string.IsNullOrWhiteSpace(definition))
                {
                    builder.AppendLine("-- SKIPPED: the definition is not available (encrypted module or missing permission).");
                    builder.AppendLine();
                    continue;
                }

                builder.AppendLine("SET ANSI_NULLS " + (usesAnsiNulls ? "ON" : "OFF") + ";");
                builder.AppendLine("SET QUOTED_IDENTIFIER " + (usesQuotedIdentifier ? "ON" : "OFF") + ";");
                builder.AppendLine("GO");
                builder.AppendLine(difference.Status == SchemaDifferenceStatus.Different ? ToCreateOrAlter(definition) : definition.Trim());
                builder.AppendLine("GO");
                builder.AppendLine();
                statementCount++;
            }
        }

        private static int ModuleOrder(SchemaObjectType type)
        {
            return type == SchemaObjectType.View ? 0 : 1;
        }

        private static void WriteDrops(
            StringBuilder builder,
            SchemaScriptOptions options,
            List<SchemaDifference> droppedTables,
            List<SchemaDifference> droppedColumns,
            List<SchemaDifference> viewDifferences,
            List<SchemaDifference> routineDifferences,
            ref int statementCount)
        {
            if (!options.IncludeDrops)
            {
                return;
            }

            var droppedViews = viewDifferences.Where(d => d.Status == SchemaDifferenceStatus.MissingInSource).ToList();
            var droppedRoutines = routineDifferences.Where(d => d.Status == SchemaDifferenceStatus.MissingInSource).ToList();

            if (droppedTables.Count == 0 && droppedColumns.Count == 0 && droppedViews.Count == 0 && droppedRoutines.Count == 0)
            {
                return;
            }

            builder.AppendLine("-- ===== 6. Drop objects that exist only in the target =====");
            builder.AppendLine("-- ---------------------------------------------------------------------");
            builder.AppendLine("-- WARNING: dropping is destructive. Verify the list against the header before");
            builder.AppendLine("-- running this section, and take a backup when in doubt.");
            builder.AppendLine("-- ---------------------------------------------------------------------");
            builder.AppendLine();

            foreach (var difference in droppedColumns)
            {
                if (difference.TargetColumn == null || string.IsNullOrWhiteSpace(difference.ParentQualifiedName))
                {
                    continue;
                }

                builder.AppendLine("-- " + difference.ObjectName + " - " + difference.Summary);
                builder.AppendLine("-- NOTE: indexes or constraints that use the column have to be dropped first.");
                builder.AppendLine("ALTER TABLE " + difference.ParentQualifiedName + " DROP COLUMN " + SchemaScriptName.Quote(difference.TargetColumn.Name) + ";");
                statementCount++;
            }

            foreach (var difference in droppedViews)
            {
                if (difference.TargetObject == null)
                {
                    continue;
                }

                builder.AppendLine("-- " + difference.ObjectName + " - " + difference.Summary);
                builder.AppendLine("DROP VIEW " + difference.ObjectName + ";");
                statementCount++;
            }

            foreach (var difference in droppedRoutines)
            {
                var routine = difference.TargetObject as SchemaRoutine;
                if (routine == null)
                {
                    continue;
                }

                builder.AppendLine("-- " + difference.ObjectName + " - " + difference.Summary);
                builder.AppendLine("DROP " + DropKeyword(routine.Kind) + " " + difference.ObjectName + ";");
                statementCount++;
            }

            foreach (var difference in droppedTables)
            {
                builder.AppendLine("-- " + difference.ObjectName + " - " + difference.Summary);
                builder.AppendLine("-- NOTE: foreign keys pointing at this table have to be dropped first.");
                builder.AppendLine("DROP TABLE " + difference.ObjectName + ";");
                statementCount++;
            }

            builder.AppendLine("GO");
            builder.AppendLine();
        }

        private static string DropKeyword(SchemaRoutineKind kind)
        {
            switch (kind)
            {
                case SchemaRoutineKind.Procedure:
                    return "PROCEDURE";
                case SchemaRoutineKind.Aggregate:
                    return "AGGREGATE";
                case SchemaRoutineKind.Trigger:
                    return "TRIGGER";
                default:
                    return "FUNCTION";
            }
        }

        private static string ColumnDefinition(SchemaColumn column, string databaseCollation)
        {
            var builder = new StringBuilder();
            builder.Append(SchemaScriptName.Quote(column.Name)).Append(' ');

            if (column.IsComputed)
            {
                builder.Append("AS ").Append((column.ComputedDefinition ?? "/* expression not available */").Trim());
                if (column.IsPersisted)
                {
                    builder.Append(" PERSISTED");
                }

                return builder.ToString();
            }

            builder.Append(column.TypeDisplay);

            if (!string.IsNullOrEmpty(column.Collation) && !string.Equals(column.Collation, databaseCollation, StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(" COLLATE ").Append(column.Collation);
            }

            if (column.IsIdentity)
            {
                decimal seed = column.IdentitySeed ?? 1;
                decimal increment = column.IdentityIncrement ?? 1;
                builder.Append(" IDENTITY(").Append(seed.ToString(CultureInfo.InvariantCulture)).Append(",").Append(increment.ToString(CultureInfo.InvariantCulture)).Append(")");
            }

            builder.Append(column.IsNullable ? " NULL" : " NOT NULL");

            if (!string.IsNullOrWhiteSpace(column.DefaultDefinition))
            {
                builder.Append(" CONSTRAINT ").Append(SchemaScriptName.Quote(DefaultConstraintName(column)))
                    .Append(" DEFAULT ").Append(column.DefaultDefinition.Trim());
            }

            return builder.ToString();
        }

        private static string AlterColumnDefinition(SchemaColumn source, SchemaColumn target)
        {
            var builder = new StringBuilder();
            builder.Append(SchemaScriptName.Quote(source.Name)).Append(' ').Append(source.TypeDisplay);

            if (!string.IsNullOrEmpty(source.Collation) &&
                (target == null || !string.Equals(source.Collation, target.Collation, StringComparison.OrdinalIgnoreCase)))
            {
                builder.Append(" COLLATE ").Append(source.Collation);
            }

            builder.Append(source.IsNullable ? " NULL" : " NOT NULL");
            return builder.ToString();
        }

        private static string DefaultConstraintName(SchemaColumn column)
        {
            if (!string.IsNullOrWhiteSpace(column.DefaultConstraintName))
            {
                return column.DefaultConstraintName;
            }

            return "DF_" + (column.Table ?? "table") + "_" + (column.Name ?? "column");
        }

        private static string InlineConstraintDefinition(SchemaConstraint constraint)
        {
            var builder = new StringBuilder();
            builder.Append("CONSTRAINT ").Append(SchemaScriptName.Quote(constraint.Name)).Append(' ');

            switch (constraint.Kind)
            {
                case SchemaConstraintKind.PrimaryKey:
                    builder.Append("PRIMARY KEY ").Append(constraint.IsClustered ? "CLUSTERED" : "NONCLUSTERED");
                    break;
                case SchemaConstraintKind.Unique:
                    builder.Append("UNIQUE ").Append(constraint.IsClustered ? "CLUSTERED" : "NONCLUSTERED");
                    break;
                case SchemaConstraintKind.Check:
                    builder.Append("CHECK ").Append((constraint.Definition ?? string.Empty).Trim());
                    return builder.ToString();
                default:
                    builder.Append("DEFAULT");
                    return builder.ToString();
            }

            builder.Append(" (").Append(string.Join(", ", constraint.Columns.Select(c => SchemaScriptName.Quote(c) + " ASC"))).Append(")");
            return builder.ToString();
        }

        private static string AddConstraintStatement(string parentQualifiedName, SchemaConstraint constraint)
        {
            var builder = new StringBuilder();
            switch (constraint.Kind)
            {
                case SchemaConstraintKind.PrimaryKey:
                case SchemaConstraintKind.Unique:
                    builder.Append("ALTER TABLE ").Append(parentQualifiedName).Append(" ADD CONSTRAINT ").Append(SchemaScriptName.Quote(constraint.Name)).Append(' ')
                        .Append(constraint.Kind == SchemaConstraintKind.PrimaryKey ? "PRIMARY KEY " : "UNIQUE ")
                        .Append(constraint.IsClustered ? "CLUSTERED" : "NONCLUSTERED")
                        .Append(" (").Append(string.Join(", ", constraint.Columns.Select(c => SchemaScriptName.Quote(c) + " ASC"))).Append(")");
                    break;
                case SchemaConstraintKind.ForeignKey:
                    builder.Append("ALTER TABLE ").Append(parentQualifiedName).Append(constraint.IsNotTrusted ? " WITH NOCHECK" : " WITH CHECK")
                        .Append(" ADD CONSTRAINT ").Append(SchemaScriptName.Quote(constraint.Name))
                        .Append(" FOREIGN KEY (").Append(string.Join(", ", constraint.Columns.Select(SchemaScriptName.Quote))).Append(")")
                        .Append(" REFERENCES ").Append(SchemaScriptName.Quote(constraint.ReferencedSchema, constraint.ReferencedTable))
                        .Append(" (").Append(string.Join(", ", constraint.ReferencedColumns.Select(SchemaScriptName.Quote))).Append(")");

                    if (!string.IsNullOrWhiteSpace(constraint.DeleteAction) && !string.Equals(constraint.DeleteAction, "NO_ACTION", StringComparison.OrdinalIgnoreCase))
                    {
                        builder.Append(" ON DELETE ").Append(constraint.DeleteAction.Replace("_", " "));
                    }

                    if (!string.IsNullOrWhiteSpace(constraint.UpdateAction) && !string.Equals(constraint.UpdateAction, "NO_ACTION", StringComparison.OrdinalIgnoreCase))
                    {
                        builder.Append(" ON UPDATE ").Append(constraint.UpdateAction.Replace("_", " "));
                    }
                    break;
                default:
                    builder.Append("ALTER TABLE ").Append(parentQualifiedName).Append(constraint.IsNotTrusted ? " WITH NOCHECK" : " WITH CHECK")
                        .Append(" ADD CONSTRAINT ").Append(SchemaScriptName.Quote(constraint.Name))
                        .Append(" CHECK ").Append((constraint.Definition ?? string.Empty).Trim());
                    break;
            }

            return builder.ToString();
        }

        private static string CreateIndexStatement(string parentQualifiedName, SchemaIndex index)
        {
            var builder = new StringBuilder();
            builder.Append("CREATE ").Append(index.IsUnique ? "UNIQUE " : string.Empty)
                .Append(index.IsClustered ? "CLUSTERED " : "NONCLUSTERED ")
                .Append("INDEX ").Append(SchemaScriptName.Quote(index.Name))
                .Append(" ON ").Append(parentQualifiedName)
                .Append(" (").Append(index.ColumnSignature).Append(")");

            if (index.IncludedColumns.Count > 0)
            {
                builder.Append(" INCLUDE (").Append(string.Join(", ", index.IncludedColumns.Select(SchemaScriptName.Quote))).Append(")");
            }

            if (!string.IsNullOrWhiteSpace(index.FilterDefinition))
            {
                builder.Append(" WHERE ").Append(index.FilterDefinition.Trim());
            }

            return builder.ToString();
        }

        /// <summary>Turns "CREATE PROCEDURE ..." into "CREATE OR ALTER PROCEDURE ...".</summary>
        private static string ToCreateOrAlter(string definition)
        {
            string text = (definition ?? string.Empty).Trim();
            var createMatch = CreateKeyword.Match(text);
            var alterMatch = AlterKeyword.Match(text);

            if (createMatch.Success && (!alterMatch.Success || createMatch.Index < alterMatch.Index))
            {
                return text.Substring(0, createMatch.Index) + "CREATE OR ALTER" + text.Substring(createMatch.Index + createMatch.Length);
            }

            if (alterMatch.Success)
            {
                return text.Substring(0, alterMatch.Index) + "CREATE OR ALTER" + text.Substring(alterMatch.Index + alterMatch.Length);
            }

            return text;
        }
    }
}
