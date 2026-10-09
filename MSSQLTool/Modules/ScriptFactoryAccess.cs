using Microsoft.SqlServer.Management.Smo;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.SqlServer.Management.Common;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Text.RegularExpressions;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System.Reflection;

namespace MSSQLTool
{
    public static class ScriptFactoryAccess
    {
        private static ConnectionInfo lastKnownEditorConnection;

        public class ConnectionInfo
        {          
            public string FullConnectionString { get; set; }
            public string Database { get; set; }            
            public string ServerName { get; set; }            
            public UIConnectionInfo ActiveConnectionInfo { get; set; }
            internal string AccessToken { get; set; }
            internal Func<SqlAuthenticationParameters, System.Threading.CancellationToken, Task<SqlAuthenticationToken>> AccessTokenCallback { get; set; }
            internal SqlCredential Credential { get; set; }

            public string DisplayName
            {
                get
                {
                    return $"[{ServerName}] \\ [{Database}]";
                }
            }

            public override string ToString() => DisplayName;

            internal SqlConnection CreateSqlConnection(string database = null, bool? trustServerCertificate = null)
            {
                var builder = new SqlConnectionStringBuilder(FullConnectionString ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(database))
                    builder.InitialCatalog = database;
                if (trustServerCertificate.HasValue)
                    builder.TrustServerCertificate = trustServerCertificate.Value;

                // SSMS can authenticate the editor with an access token/callback or a
                // SqlCredential. These values are not exposed in ConnectionString, so
                // carry them across to the background metadata connection explicitly.
                bool hasTokenCallback = AccessTokenCallback != null;
                bool hasAccessToken = !hasTokenCallback && !string.IsNullOrWhiteSpace(AccessToken);
                bool hasCredential = !hasTokenCallback && !hasAccessToken && Credential != null;
                if (hasTokenCallback || hasAccessToken || hasCredential)
                {
                    builder.Remove("Authentication");
                    builder.Remove("Integrated Security");
                    builder.Remove("User ID");
                    builder.Remove("Password");
                }

                var connection = new SqlConnection(builder.ConnectionString);
                if (hasTokenCallback)
                    connection.AccessTokenCallback = AccessTokenCallback;
                else if (hasAccessToken)
                    connection.AccessToken = AccessToken;
                else if (hasCredential)
                    connection.Credential = Credential;

                return connection;
            }
        }

        private static INodeInformation GetSelectedNode(IObjectExplorerService _objectExplorerService)
        {
            INodeInformation[] nodes;
            int nodeCount;
            _objectExplorerService.GetSelectedNodes(out nodeCount, out nodes);

            return (nodeCount > 0 ? nodes[0] : null);
        }

        public static ConnectionInfo GetCurrentConnectionInfoFromObjectExplorer(bool inMaster = false)
        {
            var oeService = (IObjectExplorerService)ServiceCache.ServiceProvider.GetService(typeof(IObjectExplorerService));
            if (oeService == null)
                return null;

            // Get the currently selected nodes in Object Explorer.
            var selectedNode = GetSelectedNode(oeService);

            if (selectedNode == null)
                return null;

            string databaseName = "master";
            if (!inMaster)
            {
                Match match = Regex.Match(selectedNode.Context, @"Database\[@Name='(.*?)'\]");
                if (match.Success)
                {
                    databaseName = match.Groups[1].Value;
                }
            }

            return BuildConnectionInfo(selectedNode, databaseName);
        }

        /// <summary>A server that is currently open in Object Explorer.</summary>
        public sealed class ObjectExplorerServer
        {
            public string ServerName { get; set; }
            public string DisplayName { get; set; }
            public string LoginName { get; set; }
            public bool IsConnected { get; set; }

            public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? ServerName : DisplayName;
        }

        /// <summary>
        /// Every server connected in Object Explorer, so the user can pick the search target instead
        /// of having to select the right node in the tree first.
        /// </summary>
        public static List<ObjectExplorerServer> GetObjectExplorerServers()
        {
            var servers = new List<ObjectExplorerServer>();
            try
            {
                var navigation = ServiceCache.ServiceProvider?.GetService(typeof(IObjectExplorerNavigationService))
                    as IObjectExplorerNavigationService;
                if (navigation == null)
                {
                    return servers;
                }

                IReadOnlyList<OEServerInfo> connected = navigation.GetConnectedServers();
                if (connected == null)
                {
                    return servers;
                }

                foreach (OEServerInfo server in connected)
                {
                    if (server == null || string.IsNullOrWhiteSpace(server.ServerName)) continue;

                    servers.Add(new ObjectExplorerServer
                    {
                        ServerName = server.ServerName,
                        DisplayName = string.IsNullOrWhiteSpace(server.DisplayName) ? server.ServerName : server.DisplayName,
                        LoginName = server.LoginName,
                        IsConnected = server.IsConnected
                    });
                }

                servers.Sort((left, right) => string.Compare(left.ServerName, right.ServerName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Object Explorer", "Could not list the connected servers", ex);
            }

            return servers;
        }

        /// <summary>
        /// Connection (including the credentials Object Explorer holds) for a server it has open.
        /// </summary>
        public static ConnectionInfo GetConnectionInfoFromObjectExplorerServer(string serverName, string database = null)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return null;

            try
            {
                var oeService = (IObjectExplorerService)ServiceCache.ServiceProvider.GetService(typeof(IObjectExplorerService));
                if (oeService == null) return null;

                INodeInformation node = null;
                try
                {
                    node = oeService.FindNode($"Server[@Name='{serverName}']");
                }
                catch (Exception ex)
                {
                    FeatureDiagnostics.Report("Object Explorer", "Looking up a connected server failed", ex);
                }

                if (node == null)
                {
                    // Fall back to the selection when it happens to be the same server.
                    INodeInformation selected = GetSelectedNode(oeService);
                    if (selected != null && string.Equals(NodeServerName(selected), serverName, StringComparison.OrdinalIgnoreCase))
                        node = selected;
                }

                if (node == null) return null;

                string databaseName = string.IsNullOrWhiteSpace(database) ? "master" : database;
                return BuildConnectionInfo(node, databaseName);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Object Explorer", "Could not build a connection for a connected server", ex);
                return null;
            }
        }

        /// <summary>The server name an Object Explorer node belongs to, from its context path.</summary>
        private static string NodeServerName(INodeInformation node)
        {
            try
            {
                Match match = Regex.Match(node?.Context ?? string.Empty, @"Server\[@Name='(.*?)'\]");
                if (match.Success) return match.Groups[1].Value;
            }
            catch (Exception)
            {
            }

            try
            {
                return node?.Connection?.ServerName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Turns an Object Explorer node into the connection string used to query it.</summary>
        private static ConnectionInfo BuildConnectionInfo(INodeInformation node, string databaseName)
        {
            var objectExplorerConnection = node.Connection;
            string userName = objectExplorerConnection.UserName;
            string password = objectExplorerConnection.Password;
            string auth = GetAuthenticationMode(objectExplorerConnection);

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = objectExplorerConnection.ServerName,
                InitialCatalog = databaseName,
                ApplicationName = "MSSQL Tool"
            };

            ApplyAuthentication(builder, userName, password, auth);

            if (objectExplorerConnection is SqlConnectionInfo sqlConnectionInfo)
            {
                builder.Encrypt = sqlConnectionInfo.EncryptConnection;
                builder.TrustServerCertificate = sqlConnectionInfo.TrustServerCertificate;
            }

            ConnectionInfo ci = new ConnectionInfo();
            ci.FullConnectionString = builder.ToString();
            ci.Database = databaseName;
            ci.ServerName = builder.DataSource;

            return ci;
        }

        public static ConnectionInfo GetCurrentConnectionInfo(bool inMaster = false)
        {
            // Completion calls this several times per keystroke (request plus
            // posted callbacks) and each call walks COM objects and reflection.
            // A short-lived memo keeps those calls consistent and cheap without
            // masking real connection changes for longer than a keystroke burst.
            if (!inMaster)
            {
                ConnectionInfo memo = memoizedConnection;
                if (memo != null && (DateTime.UtcNow - memoizedConnectionUtc).TotalMilliseconds < 250)
                    return memo;
            }

            var scriptFactory = ServiceCache.ScriptFactory;
            if (scriptFactory == null) return null;

            var connInfo = scriptFactory.CurrentlyActiveWndConnectionInfo;
            if (connInfo == null) return null;

            UIConnectionInfo connection = connInfo.UIConnectionInfo;
            if (connection == null) return null;

            SqlConnection liveConnection;
            TryGetActiveEditorConnection(out liveConnection);
            string liveServer = TryGet(() => liveConnection?.DataSource);
            string liveDatabase = TryGet(() => liveConnection?.Database);

            string databaseName = inMaster ? "master" : liveDatabase;
            if (string.IsNullOrWhiteSpace(databaseName))
                databaseName = GetAdvancedOption(connection, "DATABASE");
            if (string.IsNullOrWhiteSpace(databaseName))
                databaseName = "master";

            SqlConnectionStringBuilder builder = TryCreateConnectionStringBuilder(liveConnection);
            if (builder == null)
            {
                builder = new SqlConnectionStringBuilder();
                string auth = GetAuthenticationMode(connection);
                ApplyAuthentication(builder, connection.UserName, connection.Password, auth);

                if (IsTrue(GetAdvancedOption(connection, "ENCRYPT_CONNECTION")))
                    builder.Encrypt = true;

                if (IsTrue(GetAdvancedOption(connection, "TRUST_SERVER_CERTIFICATE")))
                    builder.TrustServerCertificate = true;
            }
            else if (!builder.IntegratedSecurity && !string.IsNullOrWhiteSpace(connection.Password))
            {
                // An open SqlConnection hides its password unless Persist Security
                // Info is enabled. UIConnectionInfo still owns the credential SSMS
                // used, so restore it without discarding the live transport options.
                if (!string.IsNullOrWhiteSpace(connection.UserName))
                    builder.UserID = connection.UserName;
                builder.Password = connection.Password;
            }

            builder.DataSource = string.IsNullOrWhiteSpace(liveServer) ? connection.ServerName : liveServer;
            builder.InitialCatalog = databaseName;
            builder.ApplicationName = "MSSQL Tool";

            var ci = new ConnectionInfo
            {
                FullConnectionString = builder.ToString(),
                Database = databaseName,
                ServerName = builder.DataSource,
                ActiveConnectionInfo = connection,
                AccessToken = TryGet(() => liveConnection?.AccessToken),
                AccessTokenCallback = TryGet(() => liveConnection?.AccessTokenCallback),
                Credential = TryGet(() => liveConnection?.Credential)
            };

            lastKnownEditorConnection = ci;
            if (!inMaster)
            {
                memoizedConnection = ci;
                memoizedConnectionUtc = DateTime.UtcNow;
            }
            return ci;
        }

        private static volatile ConnectionInfo memoizedConnection;
        private static DateTime memoizedConnectionUtc;

        public static ConnectionInfo GetCurrentOrLastConnectionInfo(bool inMaster = false)
        {
            ConnectionInfo current = GetCurrentConnectionInfo(inMaster);
            if (current != null)
                return current;

            ConnectionInfo last = lastKnownEditorConnection;
            if (last == null || !inMaster || string.Equals(last.Database, "master", StringComparison.OrdinalIgnoreCase))
                return last;

            var builder = new SqlConnectionStringBuilder(last.FullConnectionString) { InitialCatalog = "master" };
            return new ConnectionInfo
            {
                FullConnectionString = builder.ConnectionString,
                Database = "master",
                ServerName = last.ServerName,
                ActiveConnectionInfo = last.ActiveConnectionInfo,
                AccessToken = last.AccessToken,
                AccessTokenCallback = last.AccessTokenCallback,
                Credential = last.Credential
            };
        }

        private static bool TryGetActiveEditorConnection(out SqlConnection connection)
        {
            connection = null;
            try
            {
                object factory = ServiceCache.ScriptFactory;
                if (factory == null) return false;
                MethodInfo method = factory.GetType().GetMethod("GetCurrentlyActiveFrameDocView", BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null) return false;
                object docView = method.Invoke(factory, new object[] { ServiceCache.VSMonitorSelection, false, null });
                object liveConnection = GridAccess.GetNonPublicField(docView, "m_connection");
                connection = liveConnection as SqlConnection;
                if (connection == null)
                {
                    object results = GridAccess.GetNonPublicField(docView, "m_sqlResultsControl");
                    object execution = GridAccess.GetNonPublicField(results, "m_sqlExec");
                    liveConnection = GridAccess.GetNonPublicField(execution, "m_conn");
                    connection = liveConnection as SqlConnection;
                }
                return connection != null;
            }
            catch
            {
                return false;
            }
        }

        private static SqlConnectionStringBuilder TryCreateConnectionStringBuilder(SqlConnection connection)
        {
            string value = TryGet(() => connection?.ConnectionString);
            if (string.IsNullOrWhiteSpace(value))
                return null;

            try
            {
                return new SqlConnectionStringBuilder(value);
            }
            catch
            {
                return null;
            }
        }

        private static T TryGet<T>(Func<T> getter)
        {
            try
            {
                return getter == null ? default(T) : getter();
            }
            catch
            {
                return default(T);
            }
        }

        private static string GetAdvancedOption(UIConnectionInfo connection, string key)
        {
            if (connection?.AdvancedOptions == null || string.IsNullOrWhiteSpace(key))
                return null;

            return connection.AdvancedOptions.Get(key);
        }

        private static string GetAuthenticationMode(UIConnectionInfo connection)
        {
            if (connection == null)
                return string.Empty;

            var values = new List<string>();

            if (!string.IsNullOrWhiteSpace(connection.OtherParams))
                values.Add(connection.OtherParams);

            if (connection.AdvancedOptions != null)
            {
                foreach (string key in connection.AdvancedOptions.AllKeys)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                        values.Add(key);

                    string value = connection.AdvancedOptions.Get(key);
                    if (!string.IsNullOrWhiteSpace(value))
                        values.Add(value);
                }
            }

            return string.Join(";", values);
        }

        private static string GetAuthenticationMode(object connection)
        {
            if (connection == null)
                return string.Empty;

            var values = new List<string>();

            AddPropertyValue(connection, "Authentication", values);
            AddPropertyValue(connection, "AuthenticationMethod", values);
            AddPropertyValue(connection, "AuthenticationType", values);
            AddPropertyValue(connection, "ConnectionString", values);

            return string.Join(";", values);
        }

        private static void AddPropertyValue(object instance, string propertyName, List<string> values)
        {
            var property = instance.GetType().GetProperty(propertyName);
            if (property == null)
                return;

            object value = null;
            try
            {
                value = property.GetValue(instance, null);
            }
            catch
            {
                return;
            }

            if (value == null)
                return;

            string text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                values.Add(text);
        }

        private static void ApplyAuthentication(SqlConnectionStringBuilder builder, string userName, string password, string auth)
        {
            SqlAuthenticationMethod? authenticationMethod = GetSqlAuthenticationMethod(auth, userName, password);

            if (authenticationMethod.HasValue)
            {
                builder.Authentication = authenticationMethod.Value;

                if (!string.IsNullOrWhiteSpace(userName))
                    builder.UserID = userName;

                if (ShouldSendPassword(authenticationMethod.Value) && !string.IsNullOrWhiteSpace(password))
                    builder.Password = password;

                // Do not set IntegratedSecurity=True for Microsoft Entra.
                // Active Directory / Microsoft Entra authentication is mutually exclusive with SSPI/Kerberos.
            }
            else if (!string.IsNullOrWhiteSpace(password))
            {
                builder.IntegratedSecurity = false;
                builder.UserID = userName;
                builder.Password = password;
            }
            else
            {
                builder.IntegratedSecurity = true;
            }
        }

        private static SqlAuthenticationMethod? GetSqlAuthenticationMethod(string auth, string userName, string password)
        {
            if (ContainsAny(auth, "Active Directory Password", "ActiveDirectoryPassword", "Microsoft Entra Password", "Azure Active Directory Password"))
                return SqlAuthenticationMethod.ActiveDirectoryPassword;

            if (ContainsAny(auth, "Active Directory Integrated", "ActiveDirectoryIntegrated", "Microsoft Entra Integrated", "Azure Active Directory Integrated"))
                return SqlAuthenticationMethod.ActiveDirectoryIntegrated;

            if (ContainsAny(auth, "Active Directory Default", "ActiveDirectoryDefault", "Microsoft Entra Default", "Azure Active Directory Default"))
                return SqlAuthenticationMethod.ActiveDirectoryDefault;

            if (ShouldUseMicrosoftEntraInteractive(userName, password, auth))
                return SqlAuthenticationMethod.ActiveDirectoryInteractive;

            return null;
        }

        private static bool ShouldSendPassword(SqlAuthenticationMethod authenticationMethod)
        {
            return authenticationMethod == SqlAuthenticationMethod.ActiveDirectoryPassword ||
                   authenticationMethod == SqlAuthenticationMethod.SqlPassword;
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            if (string.IsNullOrWhiteSpace(value) || tokens == null)
                return false;

            foreach (string token in tokens)
            {
                if (!string.IsNullOrWhiteSpace(token) &&
                    value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ShouldUseMicrosoftEntraInteractive(string userName, string password, string auth)
        {
            if (IsMicrosoftEntraMfa(auth))
                return true;

            bool hasUserName = !string.IsNullOrWhiteSpace(userName);
            bool hasPassword = !string.IsNullOrWhiteSpace(password);

            // Microsoft Entra MFA commonly has a UPN username and no password.
            // This prevents accidental fallback to Integrated Security=True / SSPI.
            if (hasUserName && !hasPassword && LooksLikeUpn(userName))
                return true;

            return false;
        }

        private static bool IsMicrosoftEntraMfa(string auth)
        {
            if (string.IsNullOrWhiteSpace(auth))
                return false;

            return auth.IndexOf("Active Directory Interactive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   auth.IndexOf("ActiveDirectoryInteractive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   auth.IndexOf("Microsoft Entra MFA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   auth.IndexOf("Microsoft Entra", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   auth.IndexOf("Azure Active Directory - Universal with MFA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   auth.IndexOf("Universal with MFA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   auth.IndexOf("MFA", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeUpn(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
                return false;

            return userName.Contains("@") && !userName.Contains("\\");
        }

        private static bool IsTrue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetActiveQueryWindowText()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                DTE application = ServiceCache.ExtensibilityModel?.Application;
                if (application == null || application.ActiveDocument == null)
                {
                    return string.Empty;
                }

                TextDocument doc = application.ActiveDocument.Object("TextDocument") as TextDocument;
                if (doc == null)
                {
                    return string.Empty;
                }

                EditPoint startPoint = doc.StartPoint.CreateEditPoint();
                return startPoint.GetText(doc.EndPoint);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string GetXmlFromUIConnectionInfo(UIConnectionInfo connectionInfo)
        {
            if (connectionInfo == null)
            {
                throw new ArgumentNullException(nameof(connectionInfo), "ConnectionInfo cannot be null.");
            }

            StringBuilder sb = new StringBuilder();

            using (XmlWriter writer = XmlWriter.Create(sb))
            {
                connectionInfo.SaveToStream(writer, saveName: true);
            }

            return sb.ToString();
        }

        public static UIConnectionInfo CreateConnectionInfoFromXml(string xmlString)
        {
            if (string.IsNullOrEmpty(xmlString))
            {
                throw new ArgumentNullException(nameof(xmlString), "The XML string cannot be null or empty.");
            }

            UIConnectionInfo connectionInfo = null;

            using (var xmlReader = XmlReader.Create(new StringReader(xmlString)))
            {
                xmlReader.MoveToContent();
                connectionInfo = UIConnectionInfo.LoadFromStream(xmlReader);
            }

            return connectionInfo;
        }

    }
}
