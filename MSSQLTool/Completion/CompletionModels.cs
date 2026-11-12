using System;
using System.Collections.Generic;
using System.Linq;
using MSSQLTool.Model;

namespace MSSQLTool.Completion
{
    internal enum CompletionItemKind { Keyword, Server, Database, Schema, Table, View, Column, Parameter, Procedure, Function, Synonym, Sequence, Type, Snippet, Join }

    internal sealed class CompletionItem
    {
        private string displayText;
        private CompletionItemKind kind;
        private string key;
        private string scopeKey;
        private string usageKey;
        private string matchText;
        private string bareMatchText;
        private uint matchMask;
        private bool matchMaskReady;

        public string DisplayText
        {
            get { return displayText; }
            set { displayText = value; key = null; usageKey = null; matchText = null; bareMatchText = null; matchMaskReady = false; }
        }

        public string InsertText
        {
            get
            {
                if (insertText == null && insertTextProvider != null)
                {
                    insertText = insertTextProvider();
                    insertTextProvider = null;
                }
                if (insertText == null && ObjectSource != null)
                    insertText = SqlCompletionAnalyzer.ObjectInsertText(ObjectSource, OmitSchema, AutoAlias ? AliasContext : null);
                return insertText;
            }
            set { insertText = value; insertTextProvider = null; }
        }

        private string insertText;
        private Func<string> insertTextProvider;

        /// <summary>Metadata row this candidate was built from, when the text can be derived lazily.</summary>
        internal DatabaseObjectMetadata ObjectSource { get; set; }
        internal bool OmitSchema { get; set; }
        internal bool AutoAlias { get; set; }
        internal CompletionContext AliasContext { get; set; }

        /// <summary>
        /// Defers building the inserted text (schema quoting, auto alias) until a row is actually
        /// committed.  A 20k-table source list only reads 200 descriptions and one insert text.
        /// </summary>
        internal Func<string> InsertTextProvider
        {
            get { return insertTextProvider; }
            set { insertTextProvider = value; insertText = null; }
        }

        public CompletionItemKind Kind
        {
            get { return kind; }
            set { kind = value; key = null; usageKey = null; }
        }

        /// <summary>
        /// Identity used by the popup's refresh diff.  Built once per text change instead of once
        /// per comparison, which used to allocate a string for every row on every keystroke.
        /// </summary>
        internal string Key
        {
            get
            {
                if (key == null) key = kind + "|" + displayText;
                return key;
            }
        }
        public bool IsSystem { get; set; }
        public int Score { get; set; }
        public string ScopeKey
        {
            get { return scopeKey; }
            set { scopeKey = value; usageKey = null; }
        }

        /// <summary>MRU lookup key, built once per candidate list instead of once per keystroke.</summary>
        internal string UsageKey
        {
            get
            {
                if (usageKey == null) usageKey = (scopeKey ?? string.Empty) + "|" + kind + "|" + displayText;
                return usageKey;
            }
        }

        /// <summary>Display text without the leading '@' used by the matcher, computed once.</summary>
        internal string MatchText
        {
            get
            {
                if (matchText == null) matchText = (displayText ?? string.Empty).TrimStart('@');
                return matchText;
            }
        }

        /// <summary>The part after the last dot, computed once instead of allocating per keystroke.</summary>
        internal string BareMatchText
        {
            get
            {
                if (bareMatchText == null)
                {
                    string text = MatchText;
                    int dot = text.LastIndexOf('.');
                    bareMatchText = dot < 0 ? text : text.Substring(dot + 1);
                }
                return bareMatchText;
            }
        }

        /// <summary>
        /// Bitmask of the ASCII letters in the display text.  A typed prefix whose letters are
        /// not all present can never match by prefix, initials, substring or in-order channels;
        /// rejecting it with one bit test keeps a 20k-object database cheap.
        /// </summary>
        internal uint MatchMask
        {
            get
            {
                if (!matchMaskReady)
                {
                    string text = MatchText;
                    uint mask = 0;
                    for (int i = 0; i < text.Length; i++)
                    {
                        char c = text[i];
                        if (c >= 'a' && c <= 'z') mask |= 1u << (c - 'a');
                        else if (c >= 'A' && c <= 'Z') mask |= 1u << (c - 'A');
                    }
                    matchMask = mask;
                    matchMaskReady = true;
                }
                return matchMask;
            }
        }

        // Same idea for the one-line description: a select list carries ~200 candidates and their
        // texts are only read for the row that is actually shown, so the keyword and function
        // descriptions are supplied as a provider and built on first read.
        private string description;
        private Func<string> descriptionProvider;

        public string Description
        {
            get
            {
                if (description == null && descriptionProvider != null)
                {
                    description = descriptionProvider();
                    descriptionProvider = null;
                }
                if (description == null && ObjectSource != null) description = ObjectSource.DescriptionSummary;
                return description;
            }
            set
            {
                description = value;
                descriptionProvider = null;
            }
        }

        internal Func<string> DescriptionProvider
        {
            get { return descriptionProvider; }
            set { descriptionProvider = value; description = null; }
        }

        /// <summary>True while the description has not been built yet (used by the perf guard).</summary>
        internal bool DescriptionIsDeferred => description == null && (descriptionProvider != null || ObjectSource != null);

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
                if (detailText == null && ObjectSource != null) detailText = SqlCompletionAnalyzer.ObjectDetail(ObjectSource);
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
            DisplayText = DisplayText, InsertText = InsertText,
            Kind = Kind, IsSystem = IsSystem, Score = Score, ScopeKey = ScopeKey,
            ObjectSource = ObjectSource, OmitSchema = OmitSchema, AutoAlias = AutoAlias, AliasContext = AliasContext,
            description = description, descriptionProvider = descriptionProvider,
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

        private string displayQualifiedName;
        internal string DisplayQualifiedName => displayQualifiedName ?? (displayQualifiedName =
            (IsExternal && !string.IsNullOrWhiteSpace(Server) ? Server + "." : "")
            + (IsExternal && !string.IsNullOrWhiteSpace(Database) ? Database + "." : "")
            + (string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name));

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

    /// <summary>
    /// Shares the few strings that repeat once per column (data type names, collations).
    /// A 225k-column catalog otherwise keeps 225k separate copies of "int"/"nvarchar",
    /// which is pure Gen2 pressure; sharing them changes no behaviour.
    /// </summary>
    internal static class MetadataStringPool
    {
        private const int Capacity = 1024;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Pool =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        internal static string Share(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            // Type names are short; anything longer is not a shared vocabulary word.
            if (value.Length > 64) return value;
            if (Pool.TryGetValue(value, out string existing)) return existing;
            if (Pool.Count >= Capacity) return value;
            return Pool.GetOrAdd(value, value);
        }
    }

    internal sealed class ColumnMetadata
    {
        private string dataType;
        public string Name { get; set; }
        public string DataType { get => dataType; set => dataType = MetadataStringPool.Share(value); }
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
        private string dataType;
        public string ObjectName { get; set; }
        public string Name { get; set; }
        public string DataType { get => dataType; set => dataType = MetadataStringPool.Share(value); }
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

        // Completion used to rescan every routine parameter once per parameter it visited, which
        // is quadratic on a snapshot holding tens of thousands of parameters.  The bucket key is
        // the last name part; callers still run the full name comparison on the (small) bucket,
        // so matches are identical to a linear scan.
        private Dictionary<string, List<RoutineParameterMetadata>> parameterIndex;
        private int parameterIndexCount = -1;
        private readonly object parameterIndexLock = new object();
        private static readonly List<RoutineParameterMetadata> NoParameters = new List<RoutineParameterMetadata>();

        internal List<RoutineParameterMetadata> ParametersFor(string normalizedRoutine)
        {
            string key = LastNamePart(normalizedRoutine ?? string.Empty);
            Dictionary<string, List<RoutineParameterMetadata>> index = ParameterIndex();
            return index.TryGetValue(key, out List<RoutineParameterMetadata> bucket) ? bucket : NoParameters;
        }

        private Dictionary<string, List<RoutineParameterMetadata>> ParameterIndex()
        {
            lock (parameterIndexLock)
            {
                if (parameterIndex != null && parameterIndexCount == Parameters.Count) return parameterIndex;

                var built = new Dictionary<string, List<RoutineParameterMetadata>>(StringComparer.OrdinalIgnoreCase);
                foreach (RoutineParameterMetadata parameter in Parameters)
                {
                    string key = LastNamePart(DatabaseIdentifier.NormalizeSqlServer(parameter.ObjectName));
                    if (!built.TryGetValue(key, out List<RoutineParameterMetadata> bucket))
                        built[key] = bucket = new List<RoutineParameterMetadata>();
                    bucket.Add(parameter);
                }

                parameterIndex = built;
                parameterIndexCount = Parameters.Count;
                return built;
            }
        }

        private static string LastNamePart(string value)
        {
            int dot = value.LastIndexOf('.');
            return dot >= 0 ? value.Substring(dot + 1) : value;
        }
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
        private readonly List<DatabaseObjectMetadata> dataSources = new List<DatabaseObjectMetadata>();
        private readonly List<DatabaseObjectMetadata> procedures = new List<DatabaseObjectMetadata>();

        public IReadOnlyList<DatabaseObjectMetadata> DataSources => dataSources;
        public IReadOnlyList<DatabaseObjectMetadata> Procedures => procedures;

        public MetadataObjectIndex(MetadataSnapshot snapshot)
        {
            this.snapshot = snapshot;
            foreach (DatabaseObjectMetadata item in snapshot.Objects)
            {
                Add(bySimpleName, DatabaseIdentifier.NormalizeSqlServer(item.Name ?? string.Empty), item);
                Add(byQualifiedName, DatabaseIdentifier.NormalizeSqlServer(item.QualifiedName ?? string.Empty), item);
                if (SqlCompletionAnalyzer.IsDataSource(item)) dataSources.Add(item);
                else if (item.Kind == CompletionItemKind.Procedure) procedures.Add(item);
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
        AlterTableAction, AlterTableColumn, IndexColumns,
        // "FROM dbo.Orders |" — the source itself is written, what follows is a hint, a join or an alias.
        TableSourceTail,
        // "INNER JOIN dbo.Orders o |" — the join target is written: ON, a table hint or the next join.
        JoinSourceTail,
        // "FROM dbo.Orders o WITH (|" — the hint name is the body of the parenthesis.
        TableHint,
        // "... OPTION (|" — the query hint is the body of the parenthesis.
        QueryHint
    }

    internal sealed class CompletionContext
    {
        public CompletionContextKind Kind { get; set; }
        public SqlPredicateState PredicateState { get; set; }
        public int PredicateDepth { get; set; }
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

        // Absolute caret offset into DocumentText, kept so the completion pipeline can ask the
        // shared document model which aliases are visible instead of guessing from text.
        public int CaretOffset { get; set; } = -1;
        public string DocumentText { get; set; }

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
        // "… JOIN dbo.Orders o |" — the only token that can follow is ON.  Set from the
        // masked statement so the popup can rank ON above the column list.
        public bool AwaitingJoinPredicate { get; set; }
        // "FROM dbo.Orders |": the written source object and whether an alias already follows it.
        public string TableSourceName { get; set; }
        public bool TableAliasWritten { get; set; }
        // True when the caret position resolver answered instead of the text-shaped guesses; the
        // corpus test uses it to keep a second classifier from creeping back.
        public bool PositionResolved { get; set; }

        /// <summary>
        /// Set when the typed word is exactly a snippet prefix.  The snippet is then added on top of
        /// whatever the position allows instead of switching the whole list to "general", which used
        /// to offer columns and functions at a table source.
        /// </summary>
        public string ExactSnippetPrefix { get; set; }

        /// <summary>
        /// Whether snippets were offered when this context was analyzed.  The editor serves a
        /// keystroke that only extends the word from the cached candidate list; that list was built
        /// before the last letter existed, so a snippet prefix completed by that letter is missing
        /// from it.  This flag lets the cache decide without re-reading the setting.
        /// </summary>
        public bool SnippetsEnabled { get; set; }

        /// <summary>
        /// The clause that owns the predicate: WHERE, HAVING or ON.  Only an ON predicate can be
        /// followed by another JOIN.
        /// </summary>
        public string PredicateOwner { get; set; }

        /// <summary>The position the resolver answered, kept so a downstream layer can be checked
        /// against it instead of silently replacing it.</summary>
        public SqlCaretKind ResolvedKind { get; set; }

        /// <summary>The clause that owns the caret (SELECT, FROM, JOIN, ON, WHERE, GROUP BY, ...).</summary>
        public string EnclosingClause { get; set; }

        /// <summary>SELECT / INSERT / UPDATE / DELETE / MERGE / DDL / EXEC, from the tree.</summary>
        public string StatementKind { get; set; }
        // True when the parse tree is what placed the caret; false means the token scan did.  The
        // position has one entry point either way.
        public bool PositionDecidedByTree { get; set; }
        public bool HasOmittedSchemaQualifier { get; set; }
    }

    internal sealed class DeclaredVariableInfo
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }
}
