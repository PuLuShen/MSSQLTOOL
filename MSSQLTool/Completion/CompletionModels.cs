using System;
using System.Collections.Generic;
using System.Linq;

namespace MSSQLTool.Completion
{
    internal enum CompletionItemKind { Keyword, Server, Database, Schema, Table, View, Column, Parameter, Procedure, Function, Synonym, Sequence, Type, Snippet, Join }

    internal sealed class CompletionItem
    {
        public string DisplayText { get; set; }
        public string InsertText { get; set; }
        public string Description { get; set; }
        public CompletionItemKind Kind { get; set; }
        public bool IsSystem { get; set; }
        public int Score { get; set; }
        public string ScopeKey { get; set; }

        // Detail strings can be large (full column lists, module definitions).
        // They are built on first read only, so candidate construction per
        // keystroke never pays for details the user never opens.
        private string detailText;
        private Func<string> detailTextProvider;

        public string DetailText
        {
            get
            {
                if (detailText == null && detailTextProvider != null) detailText = detailTextProvider();
                return detailText;
            }
            set
            {
                detailText = value;
                detailTextProvider = null;
            }
        }

        internal Func<string> DetailTextProvider
        {
            get { return detailTextProvider; }
            set { detailTextProvider = value; detailText = null; }
        }

        internal CompletionItem Clone() => new CompletionItem
        {
            DisplayText = DisplayText, InsertText = InsertText, Description = Description,
            Kind = Kind, IsSystem = IsSystem, Score = Score,
            detailText = detailText, detailTextProvider = detailTextProvider
        };

        public override string ToString() => string.IsNullOrWhiteSpace(Description)
            ? DisplayText
            : DisplayText + "    " + Description;
    }

    internal sealed class DatabaseObjectMetadata
    {
        public string Server { get; set; }
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Name { get; set; }
        public CompletionItemKind Kind { get; set; }
        public bool IsSystem { get; set; }
        public bool IsTableValuedFunction { get; set; }
        public bool IsExternal { get; set; }
        public List<ColumnMetadata> Columns { get; } = new List<ColumnMetadata>();
        // Indexes and constraints arrive with the background enrichment pass,
        // after the snapshot is already published to readers; the fully built
        // lists are swapped in atomically instead of being appended to live.
        public List<IndexMetadata> Indexes { get; private set; } = new List<IndexMetadata>();
        public List<CheckConstraintMetadata> CheckConstraints { get; private set; } = new List<CheckConstraintMetadata>();
        internal void SetIndexes(List<IndexMetadata> value) => Indexes = value ?? new List<IndexMetadata>();
        internal void SetCheckConstraints(List<CheckConstraintMetadata> value) => CheckConstraints = value ?? new List<CheckConstraintMetadata>();
        public List<string> ProjectionSources { get; } = new List<string>();

        // Identifier parts never change after load, so the joins performed for
        // every candidate item are computed once instead of per keystroke.
        private string qualifiedName;
        public string QualifiedName => qualifiedName ?? (qualifiedName = string.Join(".", new[] { Server, Database, Schema, Name }.Where(p => !string.IsNullOrWhiteSpace(p))));

        private string descriptionSummary;
        internal string DescriptionSummary => descriptionSummary ?? (descriptionSummary = Kind + (string.IsNullOrWhiteSpace(Database) ? "" : " in " + Database));

        private string detail;
        private int detailColumnCount = -1;

        /// <summary>Detail string, rebuilt if the column list changed since the last build (e.g. synonym columns resolved late).</summary>
        internal string CachedDetail
        {
            get
            {
                if (detail == null || detailColumnCount != Columns.Count)
                {
                    detail = BuildDetail();
                    detailColumnCount = Columns.Count;
                }
                return detail;
            }
        }

        /// <summary>
        /// Drops the cached detail so tooltips rebuilt after the background
        /// enrichment pass pick up the late-arriving Definition/Description.
        /// </summary>
        internal void ResetDetailCache()
        {
            detail = null;
            detailColumnCount = -1;
        }

        public string SynonymBaseObjectName { get; set; }
        public string Definition { get; set; }
        public string Description { get; set; }
        public long? EstimatedRowCount { get; set; }

        private string BuildDetail()
        {
            var lines = new List<string> { Kind + "  " + QualifiedName };
            if (!string.IsNullOrWhiteSpace(Description)) lines.Add(Description);
            if (EstimatedRowCount.HasValue) lines.Add("Estimated rows: " + EstimatedRowCount.Value.ToString("N0"));
            if (Indexes.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Indexes:");
                foreach (IndexMetadata index in Indexes.Take(30))
                {
                    string flags = index.IsPrimaryKey ? "PK" : index.IsUnique ? "UNIQUE" : index.TypeDescription;
                    string columns = string.Join(", ", index.KeyColumns);
                    string included = index.IncludedColumns.Count == 0 ? string.Empty : " INCLUDE (" + string.Join(", ", index.IncludedColumns) + ")";
                    lines.Add("  " + flags + " " + index.Name + " (" + columns + ")" + included);
                }
            }
            if (CheckConstraints.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Check constraints:");
                lines.AddRange(CheckConstraints.Take(30).Select(c => "  " + c.Name + " " + c.Definition));
            }
            if (Columns.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Columns:");
                lines.AddRange(Columns.OrderBy(c => c.Ordinal).Take(200).Select(ColumnDetail));
                if (Columns.Count > 200) lines.Add("... " + (Columns.Count - 200) + " more columns");
            }
            if (!string.IsNullOrWhiteSpace(Definition))
            {
                lines.Add(string.Empty);
                lines.Add(Definition.Length > 6000 ? Definition.Substring(0, 6000) + Environment.NewLine + "..." : Definition);
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static string ColumnDetail(ColumnMetadata column)
        {
            string flags = (column.IsPrimaryKey ? "PK " : string.Empty)
                + (column.IsForeignKey ? "FK " : string.Empty)
                + (column.IsUnique && !column.IsPrimaryKey ? "UQ " : string.Empty);
            string detailText = (string.IsNullOrWhiteSpace(flags) ? "   " : flags.PadRight(3))
                + column.Name + "  " + column.TypeDisplay + (column.IsNullable ? " NULL" : " NOT NULL")
                + (column.IsIdentity ? "  IDENTITY" : string.Empty) + (column.IsComputed ? "  COMPUTED" : string.Empty)
                + (string.IsNullOrWhiteSpace(column.DefaultDefinition) ? string.Empty : "  DEFAULT " + column.DefaultDefinition);
            if (!string.IsNullOrWhiteSpace(column.Description)) detailText += "  -- " + column.Description;
            return detailText;
        }
    }

    internal sealed class ColumnMetadata
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
        public bool IsComputed { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsForeignKey { get; set; }
        public bool IsUnique { get; set; }
        public int Ordinal { get; set; }
        public short MaxLength { get; set; }
        public byte Precision { get; set; }
        public byte Scale { get; set; }
        public string DefaultDefinition { get; set; }
        public string Description { get; set; }
        public string TypeDisplay => typeDisplay ?? (typeDisplay = SqlTypeDisplay.Format(DataType, MaxLength, Precision, Scale));
        private string typeDisplay;
    }

    internal sealed class IndexMetadata
    {
        public string Name { get; set; }
        public string TypeDescription { get; set; }
        public bool IsUnique { get; set; }
        public bool IsPrimaryKey { get; set; }
        public List<string> KeyColumns { get; } = new List<string>();
        public List<string> IncludedColumns { get; } = new List<string>();
    }

    internal sealed class CheckConstraintMetadata
    {
        public string Name { get; set; }
        public string Definition { get; set; }
    }

    internal sealed class RoutineParameterMetadata
    {
        public string ObjectName { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool IsOutput { get; set; }
        public bool HasDefaultValue { get; set; }
        public short MaxLength { get; set; }
        public byte Precision { get; set; }
        public byte Scale { get; set; }
        public int Ordinal { get; set; }
        public string TypeDisplay
            => SqlTypeDisplay.Format(DataType, MaxLength, Precision, Scale);
    }

    internal static class SqlTypeDisplay
    {
        public static string Format(string dataType, short maxLength, byte precision, byte scale)
        {
            string type = dataType ?? string.Empty;
            if ((type.IndexOf("char", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("binary", StringComparison.OrdinalIgnoreCase) >= 0) && maxLength != 0)
            {
                int length = maxLength;
                if (length > 0 && (string.Equals(type, "nvarchar", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "nchar", StringComparison.OrdinalIgnoreCase))) length /= 2;
                return type + "(" + (length < 0 ? "max" : length.ToString()) + ")";
            }
            if ((string.Equals(type, "decimal", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "numeric", StringComparison.OrdinalIgnoreCase)) && precision > 0)
                return type + "(" + precision + "," + scale + ")";
            return type;
        }
    }

    internal sealed class ForeignKeyMetadata
    {
        public string ParentObject { get; set; }
        public string ReferencedObject { get; set; }
        public List<string> ParentColumns { get; } = new List<string>();
        public List<string> ReferencedColumns { get; } = new List<string>();
    }

    internal sealed class MetadataSnapshot
    {
        public static readonly MetadataSnapshot Empty = new MetadataSnapshot();
        public DateTime LoadedUtc { get; set; }
        public string Scope { get; set; }
        public string ErrorMessage { get; set; }
        public bool IsPartial { get; set; }
        public int CompatibilityLevel { get; set; } = 170;
        public List<string> Schemas { get; } = new List<string>();
        public List<string> Databases { get; } = new List<string>();
        public List<string> LinkedServers { get; } = new List<string>();
        public Dictionary<string, List<string>> LinkedServerDatabases { get; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public List<DatabaseObjectMetadata> Objects { get; } = new List<DatabaseObjectMetadata>();
        public List<RoutineParameterMetadata> Parameters { get; } = new List<RoutineParameterMetadata>();
        // Foreign keys are loaded by the background enrichment pass; the whole
        // list is swapped in atomically so concurrent readers never enumerate
        // a list that is still being built.
        public List<ForeignKeyMetadata> ForeignKeys { get; private set; } = new List<ForeignKeyMetadata>();
        internal void SetForeignKeys(List<ForeignKeyMetadata> value)
        {
            ForeignKeys = value ?? new List<ForeignKeyMetadata>();
            // The lazily built object index pre-buckets foreign keys, so it
            // must be rebuilt to pick up the enriched list.
            index = null;
        }
        public string StatusText => !string.IsNullOrWhiteSpace(ErrorMessage)
            ? (IsPartial
                ? LocalizationManager.T("metadata partially loaded")
                : LocalizationManager.T("metadata unavailable"))
            : LocalizationManager.Format("metadata {0} objects / {1} columns", Objects.Count, Objects.Sum(o => o.Columns.Count));

        // Built lazily on first completion request after load; the object and
        // foreign-key lists are stable afterwards, so one index serves every
        // keystroke instead of O(objects) scans per lookup.
        private MetadataObjectIndex index;
        private readonly object indexGate = new object();

        internal MetadataObjectIndex GetIndex()
        {
            MetadataObjectIndex value = index;
            if (value != null) return value;
            lock (indexGate)
            {
                if (index == null) index = new MetadataObjectIndex(this);
                return index;
            }
        }
    }

    /// <summary>
    /// Hash indexes over a loaded snapshot. Candidates are pre-bucketed by
    /// simple and qualified name; exact <c>ObjectMatches</c> filtering still
    /// happens at the call site. Buckets holding more than one object fall
    /// back to the original linear scan so list-order selection stays stable.
    /// </summary>
    internal sealed class MetadataObjectIndex
    {
        private readonly MetadataSnapshot snapshot;
        private readonly Dictionary<string, List<DatabaseObjectMetadata>> bySimpleName = new Dictionary<string, List<DatabaseObjectMetadata>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<DatabaseObjectMetadata>> byQualifiedName = new Dictionary<string, List<DatabaseObjectMetadata>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ForeignKeyMetadata>> foreignKeysBySimpleName = new Dictionary<string, List<ForeignKeyMetadata>>(StringComparer.OrdinalIgnoreCase);

        public MetadataObjectIndex(MetadataSnapshot snapshot)
        {
            this.snapshot = snapshot;
            foreach (DatabaseObjectMetadata item in snapshot.Objects)
            {
                Add(bySimpleName, DatabaseIdentifier.NormalizeSqlServer(item.Name ?? string.Empty), item);
                Add(byQualifiedName, DatabaseIdentifier.NormalizeSqlServer(item.QualifiedName ?? string.Empty), item);
            }
            foreach (ForeignKeyMetadata foreignKey in snapshot.ForeignKeys)
            {
                AddForeignKey(foreignKey, foreignKey.ParentObject);
                AddForeignKey(foreignKey, foreignKey.ReferencedObject);
            }
        }

        private static void Add<T>(Dictionary<string, List<T>> map, string key, T item)
        {
            if (string.IsNullOrEmpty(key)) return;
            List<T> bucket;
            if (!map.TryGetValue(key, out bucket)) map[key] = bucket = new List<T>();
            bucket.Add(item);
        }

        private void AddForeignKey(ForeignKeyMetadata foreignKey, string qualifiedName)
        {
            string normalized = DatabaseIdentifier.NormalizeSqlServer(qualifiedName ?? string.Empty);
            int dot = normalized.LastIndexOf('.');
            string simple = dot >= 0 ? normalized.Substring(dot + 1) : normalized;
            if (simple.Length == 0) return;
            List<ForeignKeyMetadata> bucket;
            if (!foreignKeysBySimpleName.TryGetValue(simple, out bucket)) foreignKeysBySimpleName[simple] = bucket = new List<ForeignKeyMetadata>();
            if (!bucket.Contains(foreignKey)) bucket.Add(foreignKey);
        }

        /// <summary>Resolves a target the way <c>FirstOrDefault(ObjectMatches)</c> did.</summary>
        public DatabaseObjectMetadata Find(string cleanedTarget, Func<DatabaseObjectMetadata, string, bool> matches)
        {
            List<DatabaseObjectMetadata> candidates = GetCandidates(cleanedTarget);
            if (candidates == null) return null;
            if (candidates.Count == 1)
                return matches(candidates[0], cleanedTarget) ? candidates[0] : null;
            // Several objects share this name (e.g. same table in two merged
            // databases); keep the original list-order first-match behavior.
            foreach (DatabaseObjectMetadata item in snapshot.Objects)
                if (matches(item, cleanedTarget)) return item;
            return null;
        }

        public bool Any(string cleanedTarget, Func<DatabaseObjectMetadata, string, bool> matches, Func<DatabaseObjectMetadata, bool> predicate)
        {
            List<DatabaseObjectMetadata> candidates = GetCandidates(cleanedTarget);
            if (candidates == null) return false;
            if (candidates.Count == 1) return predicate(candidates[0]) && matches(candidates[0], cleanedTarget);
            foreach (DatabaseObjectMetadata item in snapshot.Objects)
                if (predicate(item) && matches(item, cleanedTarget)) return true;
            return false;
        }

        public List<ForeignKeyMetadata> GetForeignKeysBySimpleName(string cleanedSimpleName)
            => cleanedSimpleName != null && foreignKeysBySimpleName.TryGetValue(cleanedSimpleName, out List<ForeignKeyMetadata> bucket) ? bucket : null;

        private List<DatabaseObjectMetadata> GetCandidates(string cleanedTarget)
        {
            if (string.IsNullOrEmpty(cleanedTarget)) return null;
            bool hasDot = cleanedTarget.IndexOf('.') >= 0;
            if (!hasDot)
            {
                List<DatabaseObjectMetadata> bucket;
                return bySimpleName.TryGetValue(cleanedTarget, out bucket) ? bucket : null;
            }
            List<DatabaseObjectMetadata> qualified;
            byQualifiedName.TryGetValue(cleanedTarget, out qualified);
            int lastDot = cleanedTarget.LastIndexOf('.');
            List<DatabaseObjectMetadata> simple;
            bySimpleName.TryGetValue(cleanedTarget.Substring(lastDot + 1), out simple);
            if (qualified == null) return simple;
            if (simple == null) return qualified;
            var merged = new List<DatabaseObjectMetadata>(qualified);
            foreach (DatabaseObjectMetadata item in simple)
                if (!merged.Contains(item)) merged.Add(item);
            return merged;
        }
    }

    internal enum CompletionContextKind
    {
        General, DataSource, Member, Join, InsertBody, InsertColumns, UpdateSet,
        ExecuteObject, ExecuteArguments, ExecuteArgumentValue, FunctionArguments, Predicate, GroupBy, OrderBy, MergeSource,
        SelectList, WindowPartitionBy, WindowOrderBy, Output, Pivot, CreateTableDefinition, ConstraintColumns,
        AlterTableAction, AlterTableColumn, IndexColumns
    }

    internal sealed class CompletionContext
    {
        public CompletionContextKind Kind { get; set; }
        public string Prefix { get; set; }
        public string Qualifier { get; set; }
        public int ReplacementStartColumn { get; set; }

        // Caret position the request was made at.  Together with
        // ReplacementStartColumn this identifies the completion session: a request
        // raised somewhere else starts a new popup session even when the previous
        // popup happens to still be visible.
        public int CaretLine { get; set; }
        public int CaretColumn { get; set; }
        public Dictionary<string, string> Aliases { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<DatabaseObjectMetadata> LocalObjects { get; } = new List<DatabaseObjectMetadata>();
        public string TargetObject { get; set; }
        public string CurrentStatement { get; set; }
        public string CurrentBatch { get; set; }

        // Masked/derived forms of the statement are reused across the analyze
        // and item-building phases instead of being recomputed per call.
        public string MaskedStatement { get; set; }
        public List<DeclaredVariableInfo> DeclaredVariables { get; set; }

        public HashSet<string> UsedParameters { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReferencedColumns { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> GroupByColumns { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<string> SelectAliases { get; } = new List<string>();
        public string ActiveColumn { get; set; }
        public string ActiveParameter { get; set; }
        public bool IsExplicitRequest { get; set; }
        public string MetadataScope { get; set; }
        public bool Suppress { get; set; }
        public List<SqlEditorDiagnostic> Diagnostics { get; } = new List<SqlEditorDiagnostic>();
        public bool HasParseErrors { get; set; }
        public int ArgumentIndex { get; set; }
        public bool IsJoinSource { get; set; }
        public bool IsDataSourceMember { get; set; }
        public bool HasOmittedSchemaQualifier { get; set; }
    }

    internal sealed class DeclaredVariableInfo
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }
}
