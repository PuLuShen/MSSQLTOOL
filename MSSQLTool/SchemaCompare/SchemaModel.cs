// Schema Compare - serializable schema model (DTOs).
// The model is deliberately free of any UI or SSMS dependency so that the
// reader, comparer and script writer can be unit tested in isolation.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MSSQLTool.SchemaCompare
{
    /// <summary>Kind of the compared object. Triggers are reported as routines.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SchemaObjectType
    {
        Table,
        Column,
        Index,
        Constraint,
        View,
        Routine
    }

    /// <summary>How a source object relates to the target.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SchemaDifferenceStatus
    {
        /// <summary>The object exists in the source (baseline) but not in the target. A CREATE is needed.</summary>
        MissingInTarget,

        /// <summary>The object exists in the target but not in the source. A DROP would be needed.</summary>
        MissingInSource,

        /// <summary>The object exists on both sides but the definition differs.</summary>
        Different
    }

    /// <summary>
    /// Information: nothing to execute (whitespace/comment only or cosmetic notice).
    /// Warning: an executable statement exists but it is risky.
    /// Error: the difference cannot be scripted automatically.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SchemaDifferenceSeverity
    {
        Information,
        Warning,
        Error
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum SchemaRoutineKind
    {
        Procedure,
        ScalarFunction,
        InlineTableValuedFunction,
        TableValuedFunction,
        Aggregate,
        Trigger
    }

    /// <summary>
    /// Constraint kinds. Defaults are surfaced through <see cref="SchemaColumn.DefaultDefinition"/>
    /// (and <see cref="SchemaColumn.DefaultConstraintName"/>) instead of a separate constraint entry,
    /// so a renamed default constraint does not show up as a create + drop pair.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SchemaConstraintKind
    {
        PrimaryKey,
        Unique,
        ForeignKey,
        Check,
        Default
    }

    /// <summary>A single attribute change of a compared object.</summary>
    public sealed class PropertyDifference
    {
        public string Property { get; set; }
        public string SourceValue { get; set; }
        public string TargetValue { get; set; }

        /// <summary>True when the two values only differ by whitespace or comments.</summary>
        public bool IsWhitespaceOnly { get; set; }

        public override string ToString()
        {
            return (Property ?? string.Empty) + ": " + (SourceValue ?? string.Empty) + " -> " + (TargetValue ?? string.Empty);
        }
    }

    /// <summary>One column of a table or view.</summary>
    public sealed class SchemaColumn
    {
        public string Schema { get; set; }
        public string Table { get; set; }
        public string Name { get; set; }

        /// <summary>Type name as returned by sys.types (an alias/user defined type keeps its own name).</summary>
        public string DataType { get; set; }

        /// <summary>Base system type name (sys.columns.system_type_id). Used to format the length/precision.</summary>
        public string SystemTypeName { get; set; }

        /// <summary>Raw sys.columns.max_length (bytes). -1 means MAX.</summary>
        public short MaxLength { get; set; }

        public byte Precision { get; set; }
        public byte Scale { get; set; }
        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
        public bool IsComputed { get; set; }
        public bool IsPersisted { get; set; }
        public string DefaultDefinition { get; set; }

        /// <summary>Name of the default constraint bound to the column (needed to drop or replace it).</summary>
        public string DefaultConstraintName { get; set; }

        public string Collation { get; set; }

        /// <summary>1 based column_id (column order inside the table).</summary>
        public int Ordinal { get; set; }

        public string ComputedDefinition { get; set; }
        public decimal? IdentitySeed { get; set; }
        public decimal? IdentityIncrement { get; set; }

        /// <summary>Type with explicit length/precision, e.g. decimal(18,2), nvarchar(50), varbinary(max).</summary>
        [JsonIgnore]
        public string TypeDisplay
        {
            get
            {
                string baseName = string.IsNullOrEmpty(SystemTypeName) ? (DataType ?? string.Empty) : SystemTypeName;
                string declared = DataType ?? string.Empty;
                if (string.IsNullOrEmpty(declared))
                {
                    return string.Empty;
                }

                switch (baseName.ToLowerInvariant())
                {
                    case "char":
                    case "varchar":
                    case "binary":
                    case "varbinary":
                        return MaxLength == -1 ? declared + "(max)" : declared + "(" + MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                    case "nchar":
                    case "nvarchar":
                        return MaxLength == -1
                            ? declared + "(max)"
                            : declared + "(" + (MaxLength / 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                    case "decimal":
                    case "numeric":
                        return declared + "(" + Precision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                               Scale.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                    case "datetime2":
                    case "datetimeoffset":
                    case "time":
                        return declared + "(" + Scale.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                    case "float":
                        return Precision == 53
                            ? declared
                            : declared + "(" + Precision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                    default:
                        return declared;
                }
            }
        }

        /// <summary>Key inside the owning table. Case insensitive.</summary>
        [JsonIgnore]
        public string Key
        {
            get { return Name ?? string.Empty; }
        }

        public override string ToString()
        {
            return (Name ?? string.Empty) + " " + TypeDisplay;
        }
    }

    /// <summary>One key column of an index.</summary>
    public sealed class SchemaIndexColumn
    {
        public string Name { get; set; }
        public bool IsDescending { get; set; }

        /// <summary>Kept for completeness; <see cref="SchemaIndex.Columns"/> only carries key columns.</summary>
        public bool IsIncluded { get; set; }

        public int Ordinal { get; set; }

        public override string ToString()
        {
            return (Name ?? string.Empty) + (IsDescending ? " DESC" : " ASC");
        }
    }

    public sealed class SchemaIndex
    {
        public string Name { get; set; }

        /// <summary>CLUSTERED / NONCLUSTERED / XML / SPATIAL / JSON.</summary>
        public string Type { get; set; }

        public bool IsUnique { get; set; }
        public bool IsClustered { get; set; }

        /// <summary>True when the index implements a PRIMARY KEY / UNIQUE constraint.</summary>
        public bool IsPrimaryKey { get; set; }

        public bool IsUniqueConstraint { get; set; }

        public bool IsDisabled { get; set; }
        public string FilterDefinition { get; set; }

        /// <summary>Ordered key columns.</summary>
        public List<SchemaIndexColumn> Columns { get; set; } = new List<SchemaIndexColumn>();

        /// <summary>Ordered INCLUDE columns.</summary>
        public List<string> IncludedColumns { get; set; } = new List<string>();

        /// <summary>Signature of the key columns, e.g. "[A] ASC, [B] DESC".</summary>
        [JsonIgnore]
        public string ColumnSignature
        {
            get { return string.Join(", ", Columns.Select(c => "[" + (c.Name ?? string.Empty) + "]" + (c.IsDescending ? " DESC" : " ASC"))); }
        }

        [JsonIgnore]
        public bool BacksConstraint
        {
            get { return IsPrimaryKey || IsUniqueConstraint; }
        }
    }

    public sealed class SchemaConstraint
    {
        public string Name { get; set; }
        public SchemaConstraintKind Kind { get; set; }

        /// <summary>Columns of the owning (parent) table.</summary>
        public List<string> Columns { get; set; } = new List<string>();

        public string ReferencedSchema { get; set; }
        public string ReferencedTable { get; set; }
        public List<string> ReferencedColumns { get; set; } = new List<string>();

        /// <summary>NO_ACTION / CASCADE / SET_NULL / SET_DEFAULT.</summary>
        public string DeleteAction { get; set; }

        public string UpdateAction { get; set; }

        /// <summary>CHECK expression (as stored in sys.check_constraints.definition).</summary>
        public string Definition { get; set; }

        public bool IsNotTrusted { get; set; }
        public bool IsDisabled { get; set; }
        public bool IsClustered { get; set; }
        public bool IsSystemNamed { get; set; }

        [JsonIgnore]
        public string ColumnSignature
        {
            get { return string.Join(", ", Columns.Select(c => "[" + (c ?? string.Empty) + "]")); }
        }
    }

    public sealed class SchemaTable
    {
        public string Schema { get; set; }
        public string Name { get; set; }
        public List<SchemaColumn> Columns { get; set; } = new List<SchemaColumn>();
        public List<SchemaIndex> Indexes { get; set; } = new List<SchemaIndex>();
        public List<SchemaConstraint> Constraints { get; set; } = new List<SchemaConstraint>();

        /// <summary>Approximate row count (sys.partitions). Null when not collected.</summary>
        public long? RowCount { get; set; }

        [JsonIgnore]
        public string Key
        {
            get { return (Schema ?? string.Empty) + "." + (Name ?? string.Empty); }
        }

        [JsonIgnore]
        public string QualifiedName
        {
            get { return "[" + (Schema ?? string.Empty) + "].[" + (Name ?? string.Empty) + "]"; }
        }
    }

    public sealed class SchemaRoutine
    {
        public string Schema { get; set; }
        public string Name { get; set; }
        public SchemaRoutineKind Kind { get; set; }

        /// <summary>Original definition (sys.sql_modules.definition). Null for encrypted or CLR modules.</summary>
        public string Definition { get; set; }

        /// <summary>Whitespace and comment free definition, used for comparison.</summary>
        public string NormalizedDefinition { get; set; }

        public bool IsEncrypted { get; set; }
        public bool IsSchemaBound { get; set; }

        /// <summary>Defaults to true: a hand built snapshot behaves like a normal SSMS module.</summary>
        public bool UsesAnsiNulls { get; set; } = true;

        public bool UsesQuotedIdentifier { get; set; } = true;

        /// <summary>Parent table for triggers, e.g. the table a DML trigger is bound to.</summary>
        public string ParentSchema { get; set; }

        public string ParentName { get; set; }

        public bool IsDisabled { get; set; }

        [JsonIgnore]
        public string Key
        {
            get
            {
                if (Kind == SchemaRoutineKind.Trigger)
                {
                    return (ParentSchema ?? string.Empty) + "." + (ParentName ?? string.Empty) + "." + (Name ?? string.Empty);
                }

                return (Schema ?? string.Empty) + "." + (Name ?? string.Empty);
            }
        }
    }

    public sealed class SchemaView
    {
        public string Schema { get; set; }
        public string Name { get; set; }
        public string Definition { get; set; }
        public string NormalizedDefinition { get; set; }
        public bool IsEncrypted { get; set; }
        public bool IsSchemaBound { get; set; }
        public bool UsesAnsiNulls { get; set; } = true;
        public bool UsesQuotedIdentifier { get; set; } = true;

        [JsonIgnore]
        public string Key
        {
            get { return (Schema ?? string.Empty) + "." + (Name ?? string.Empty); }
        }
    }

    /// <summary>A full picture of one database captured at a point in time.</summary>
    public sealed class SchemaSnapshot
    {
        public string ServerName { get; set; }
        public string DatabaseName { get; set; }
        public DateTime CapturedUtc { get; set; }
        public string DatabaseCollation { get; set; }
        public string ProductVersion { get; set; }
        public List<SchemaTable> Tables { get; set; } = new List<SchemaTable>();
        public List<SchemaRoutine> Routines { get; set; } = new List<SchemaRoutine>();
        public List<SchemaView> Views { get; set; } = new List<SchemaView>();

        /// <summary>Non fatal problems collected while reading (missing permissions and the like).</summary>
        public List<string> Notes { get; set; } = new List<string>();

        [JsonIgnore]
        public int ObjectCount
        {
            get { return (Tables?.Count ?? 0) + (Views?.Count ?? 0) + (Routines?.Count ?? 0); }
        }

        [JsonIgnore]
        public int ColumnCount
        {
            get { return Tables == null ? 0 : Tables.Sum(t => t.Columns.Count); }
        }

        [JsonIgnore]
        public string DisplayName
        {
            get { return "[" + (ServerName ?? string.Empty) + "] \\ [" + (DatabaseName ?? string.Empty) + "]"; }
        }

        private static readonly JsonSerializerSettings JsonSettings = CreateJsonSettings();

        private static JsonSerializerSettings CreateJsonSettings()
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DateFormatHandling = DateFormatHandling.IsoDateFormat
            };
            settings.Converters.Add(new StringEnumConverter());
            return settings;
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, JsonSettings);
        }

        public static SchemaSnapshot FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonConvert.DeserializeObject<SchemaSnapshot>(json, JsonSettings);
        }

        public override string ToString()
        {
            return DisplayName + " (" + ObjectCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " objects)";
        }
    }

    /// <summary>Which parts of the schema a reader or a comparison should touch.</summary>
    public sealed class SchemaReadOptions
    {
        public bool ReadTables { get; set; } = true;
        public bool ReadColumns { get; set; } = true;
        public bool ReadIndexes { get; set; } = true;
        public bool ReadConstraints { get; set; } = true;
        public bool ReadViews { get; set; } = true;
        public bool ReadRoutines { get; set; } = true;
        public bool ReadTriggers { get; set; } = true;

        /// <summary>Row counts come from a DMV and need extra permissions, so they are opt in.</summary>
        public bool ReadRowCounts { get; set; }

        public bool IncludeSystemObjects { get; set; }

        public int CommandTimeoutSeconds { get; set; } = 120;
    }

    /// <summary>Options that decide which differences are reported.</summary>
    public sealed class SchemaCompareOptions
    {
        public bool CompareTables { get; set; } = true;
        public bool CompareColumns { get; set; } = true;
        public bool CompareIndexes { get; set; } = true;
        public bool CompareConstraints { get; set; } = true;
        public bool CompareViews { get; set; } = true;
        public bool CompareRoutines { get; set; } = true;
        public bool CompareTriggers { get; set; } = true;

        /// <summary>Drops whitespace/comment only definition differences from the result.</summary>
        public bool IgnoreWhitespaceDifferences { get; set; }

        /// <summary>Emits an information entry when the column order of a table differs.</summary>
        public bool ReportColumnOrder { get; set; } = true;
    }

    /// <summary>Options for the sync script generator.</summary>
    public sealed class SchemaScriptOptions
    {
        /// <summary>Emits DROP statements for objects that only exist in the target.</summary>
        public bool IncludeDrops { get; set; } = true;

        /// <summary>Emits ALTER COLUMN statements (potentially destructive, isolated in their own section).</summary>
        public bool IncludeAlterColumn { get; set; } = true;

        public bool IncludeIndexes { get; set; } = true;
        public bool IncludeConstraints { get; set; } = true;
        public bool IncludeViews { get; set; } = true;
        public bool IncludeRoutines { get; set; } = true;

        /// <summary>Emits the informational column order comments (defaults off, they are not statements).</summary>
        public bool IncludeInformationalComments { get; set; }

        /// <summary>The generated script never wraps the statements in a transaction by default.</summary>
        public bool IncludeTransaction { get; set; }
    }
}
