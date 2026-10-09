// Schema Compare - builds the SSMS connection descriptors used to read both sides.
// The credentials (integrated security, SQL login, Entra token or SqlCredential) are
// owned by the SSMS connection, so a side is always derived from it: only the server
// name and the database are overridden.

using System;
using Microsoft.Data.SqlClient;

namespace MSSQLTool.SchemaCompare
{
    internal static class SchemaCompareConnectionFactory
    {
        /// <summary>
        /// Clones an SSMS connection for another server/database. Authentication is carried
        /// over untouched (including the token/credential that is not part of the string).
        /// </summary>
        public static ScriptFactoryAccess.ConnectionInfo Create(ScriptFactoryAccess.ConnectionInfo baseConnection, string serverName, string databaseName)
        {
            if (baseConnection == null)
            {
                return null;
            }

            var builder = new SqlConnectionStringBuilder(baseConnection.FullConnectionString ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(serverName))
            {
                builder.DataSource = serverName;
            }

            if (!string.IsNullOrWhiteSpace(databaseName))
            {
                builder.InitialCatalog = databaseName;
            }

            return new ScriptFactoryAccess.ConnectionInfo
            {
                FullConnectionString = builder.ConnectionString,
                ServerName = builder.DataSource,
                Database = builder.InitialCatalog,
                ActiveConnectionInfo = baseConnection.ActiveConnectionInfo,
                AccessToken = baseConnection.AccessToken,
                AccessTokenCallback = baseConnection.AccessTokenCallback,
                Credential = baseConnection.Credential
            };
        }

        /// <summary>Connection string of the supplied side, optionally pointed at another database.</summary>
        public static string GetConnectionString(ScriptFactoryAccess.ConnectionInfo baseConnection, string databaseName)
        {
            if (baseConnection == null)
            {
                return null;
            }

            var builder = new SqlConnectionStringBuilder(baseConnection.FullConnectionString ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(databaseName))
            {
                builder.InitialCatalog = databaseName;
            }

            return builder.ConnectionString;
        }
    }
}
