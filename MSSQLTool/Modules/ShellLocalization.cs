using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.CommandBars;
using Microsoft.VisualStudio.Shell;

namespace MSSQLTool
{
    /// <summary>
    /// The command table (VSCT) stores Chinese captions, so switching the extension language did not
    /// change menu or toolbar labels.  This walks the shell CommandBars after startup and after a
    /// language change and rewrites only the captions/tooltips that belong to this extension.
    /// </summary>
    internal static class ShellLocalization
    {
        private const string Chinese = "zh-CN";
        private const string English = "en-US";
        private const string TraditionalChinese = "zh-TW";
        private const string Japanese = "ja-JP";
        private const string Korean = "ko-KR";
        private const string German = "de-DE";
        private const string French = "fr-FR";
        private const string Spanish = "es-ES";

        // Source keys are the Chinese texts in MSSQLToolPackage.vsct.
        private static readonly Dictionary<string, Dictionary<string, string>> Texts =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
            {
                ["工具"] = Row("Tools", "工具", "ツール", "도구", "Extras", "Outils", "Herramientas"),
                ["格式化查询"] = Row("Format Query", "格式化查詢", "クエリの書式設定", "쿼리 서식", "Abfrage formatieren", "Formater la requête", "Formatear consulta"),
                ["格式化 SQL 查询（按住 Shift 可打开高级选项）"] = Row(
                    "Format the SQL query (hold Shift for advanced options)",
                    "格式化 SQL 查詢（按住 Shift 可開啟進階選項）",
                    "SQL クエリを書式設定します（Shift で詳細オプション）",
                    "SQL 쿼리 서식을 지정합니다(Shift로 고급 옵션).",
                    "SQL-Abfrage formatieren (Shift für erweiterte Optionen)",
                    "Formater la requête SQL (Maj pour les options avancées)",
                    "Formatear la consulta SQL (Mayús para opciones avanzadas)"),
                ["结果集面板"] = Row("Result panel", "結果集面板", "結果セットパネル", "결과 집합 패널", "Ergebnisbereich", "Panneau de résultats", "Panel de resultados"),
                ["显示结果集面板（选中单元格即可看到数量、合计、平均、最小、最大、去重）"] = Row(
                    "Show the result panel (select cells to see count, sum, average, min, max and distinct)",
                    "顯示結果集面板（選取儲存格即可看到數量、合計、平均、最小、最大、去除重複）",
                    "結果セットパネルを表示します（セルを選択すると件数、合計、平均、最小、最大、重複を確認できます）",
                    "결과 집합 패널을 표시합니다(셀을 선택하면 개수, 합계, 평균, 최소, 최대, 고유값을 볼 수 있습니다).",
                    "Ergebnisbereich anzeigen (Zellen auswählen für Anzahl, Summe, Mittelwert, Min, Max und Eindeutige)",
                    "Afficher le panneau de résultats (sélectionnez des cellules pour le nombre, la somme, la moyenne, le min, le max et les valeurs distinctes)",
                    "Mostrar el panel de resultados (seleccione celdas para ver recuento, suma, media, mín., máx. y distintos)"),
                ["将结果网格导出为临时表"] = Row("Export grid to temp table", "將結果格線匯出為暫存表", "結果グリッドを一時テーブルにエクスポート", "결과 표를 임시 테이블로 내보내기", "Grid in temporäre Tabelle exportieren", "Exporter la grille vers une table temporaire", "Exportar la cuadrícula a una tabla temporal"),
                ["在新窗口生成对象定义脚本"] = Row("Script object definition in a new window", "在新視窗產生物件定義指令碼", "新しいウィンドウでオブジェクト定義をスクリプト化", "새 창에서 개체 정의 스크립트 생성", "Objektdefinition in neuem Fenster skripten", "Générer le script de définition dans une nouvelle fenêtre", "Generar script de definición en una ventana nueva"),
                ["快速搜索"] = Row("Quick Search", "快速搜尋", "クイック検索", "빠른 검색", "Schnellsuche", "Recherche rapide", "Búsqueda rápida"),
                ["代码片段管理器"] = Row("Snippet Manager", "程式碼片段管理員", "スニペット マネージャー", "코드 조각 관리자", "Snippet-Manager", "Gestionnaire d'extraits", "Administrador de fragmentos"),
                ["选择当前语句"] = Row("Select Current Statement", "選取目前陳述式", "現在のステートメントを選択", "현재 문 선택", "Aktuelle Anweisung auswählen", "Sélectionner l'instruction actuelle", "Seleccionar instrucción actual"),
                ["选择光标所在的 SQL 语句（Shift+F5）"] = Row("Select the SQL statement at the caret (Shift+F5)", "選取游標所在的 SQL 陳述式（Shift+F5）", "カーソル位置の SQL ステートメントを選択します（Shift+F5）", "커서 위치의 SQL 문을 선택합니다(Shift+F5).", "SQL-Anweisung an der Einfügemarke auswählen (Umschalt+F5)", "Sélectionner l'instruction SQL au curseur (Maj+F5)", "Seleccionar la instrucción SQL en el cursor (Mayús+F5)"),
                ["切换块注释"] = Row("Toggle Block Comment", "切換區塊註解", "ブロック コメントの切り替え", "블록 주석 전환", "Blockkommentar umschalten", "Basculer le commentaire de bloc", "Alternar comentario de bloque"),
                ["使用 /* */ 包裹所选 SQL，或移除光标处的块注释"] = Row("Wrap the selected SQL in /* */ or remove the block comment at the caret", "使用 /* */ 包住選取的 SQL，或移除游標處的區塊註解", "選択した SQL を /* */ で囲むか、カーソル位置のブロック コメントを削除します", "선택한 SQL을 /* */로 감싸거나 커서 위치의 블록 주석을 제거합니다.", "Ausgewähltes SQL in /* */ einschließen oder den Blockkommentar an der Einfügemarke entfernen", "Entourer le SQL sélectionné de /* */ ou supprimer le commentaire de bloc au curseur", "Rodear el SQL seleccionado con /* */ o quitar el comentario de bloque en el cursor"),
                ["查询历史"] = Row("Query History", "查詢歷程", "クエリ履歴", "쿼리 기록", "Abfrageverlauf", "Historique des requêtes", "Historial de consultas"),
                ["统计信息摘要"] = Row("Statistics Summary", "統計資訊摘要", "統計情報の概要", "통계 정보 요약", "Statistikzusammenfassung", "Résumé des statistiques", "Resumen de estadísticas"),
                ["打开最近捕获的 SET STATISTICS IO/TIME 输出摘要。"] = Row("Open the summary of the most recently captured SET STATISTICS IO/TIME output.", "開啟最近擷取的 SET STATISTICS IO/TIME 輸出摘要。", "最近取得した SET STATISTICS IO/TIME 出力の概要を開きます。", "가장 최근에 캡처한 SET STATISTICS IO/TIME 출력 요약을 엽니다.", "Zusammenfassung der zuletzt erfassten SET STATISTICS IO/TIME-Ausgabe öffnen.", "Ouvrir le résumé de la dernière sortie SET STATISTICS IO/TIME capturée.", "Abrir el resumen de la última salida de SET STATISTICS IO/TIME capturada."),
                ["服务器健康面板"] = Row("Server Health Dashboard", "伺服器健康面板", "サーバー正常性ダッシュボード", "서버 상태 대시보드", "Serverintegrität", "Tableau de bord d'intégrité du serveur", "Panel de estado del servidor"),
                ["SQL Server 版本信息"] = Row("SQL Server Builds", "SQL Server 版本資訊", "SQL Server ビルド情報", "SQL Server 빌드 정보", "SQL Server-Builds", "Versions de SQL Server", "Versiones de SQL Server"),
                ["数据传输"] = Row("Data Transfer", "資料傳輸", "データ転送", "데이터 전송", "Datenübertragung", "Transfert de données", "Transferencia de datos"),
                ["同步到 GitHub"] = Row("Sync to GitHub", "同步到 GitHub", "GitHub に同期", "GitHub에 동기화", "Mit GitHub synchronisieren", "Synchroniser avec GitHub", "Sincronizar con GitHub"),
                ["模板语句"] = Row("Query Templates", "範本陳述式", "クエリ テンプレート", "쿼리 템플릿", "Abfragevorlagen", "Modèles de requête", "Plantillas de consulta"),
                ["浏览模板语句库：竖向分类树即模板文件夹层级，可新建/重命名/删除分类与语句，双击或回车插入编辑器（Ctrl+Alt+T）"] = Row(
                    "Browse the template library: the tree mirrors the template folders; create/rename/delete categories and statements; double-click or press Enter to insert (Ctrl+Alt+T)",
                    "瀏覽範本陳述式庫：樹狀分類即範本資料夾階層，可新建/重新命名/刪除分類與陳述式，按兩下或按 Enter 插入編輯器（Ctrl+Alt+T）",
                    "テンプレート ライブラリを参照します。ツリーはテンプレート フォルダーに対応し、カテゴリとステートメントの作成/名前変更/削除、ダブルクリックまたは Enter で挿入できます（Ctrl+Alt+T）",
                    "템플릿 라이브러리를 찾아봅니다. 트리는 템플릿 폴더를 반영하며 범주/문을 만들거나 이름을 바꾸거나 삭제하고 두 번 클릭하거나 Enter를 눌러 삽입합니다(Ctrl+Alt+T).",
                    "Vorlagenbibliothek durchsuchen: Der Baum entspricht den Vorlagenordnern; Kategorien und Anweisungen erstellen/umbenennen/löschen; Doppelklick oder Eingabe zum Einfügen (Strg+Alt+T)",
                    "Parcourir la bibliothèque de modèles : l'arborescence reflète les dossiers ; créer/renommer/supprimer catégories et instructions ; double-clic ou Entrée pour insérer (Ctrl+Alt+T)",
                    "Examinar la biblioteca de plantillas: el árbol refleja las carpetas; crear/cambiar nombre/eliminar categorías e instrucciones; doble clic o Entrar para insertar (Ctrl+Alt+T)"),
                ["刷新补全缓存"] = Row("Refresh Completion Cache", "重新整理補全快取", "入力候補キャッシュを更新", "완성 캐시 새로 고침", "Vervollständigungscache aktualisieren", "Actualiser le cache de complétion", "Actualizar caché de finalización"),
                ["丢弃 SQL 补全元数据缓存并重新加载（适用于其他人在服务器上修改了表结构的情况）"] = Row(
                    "Discard and reload the SQL completion metadata cache (for example after someone changed a table on the server)",
                    "捨棄並重新載入 SQL 補全中繼資料快取（例如其他人在伺服器上修改了資料表結構）",
                    "SQL 入力候補メタデータ キャッシュを破棄して再読み込みします（サーバー上のテーブルが変更された場合など）",
                    "SQL 완성 메타데이터 캐시를 삭제하고 다시 로드합니다(다른 사람이 서버에서 테이블 구조를 변경한 경우 등).",
                    "SQL-Vervollständigungsmetadaten-Cache verwerfen und neu laden (z. B. nachdem jemand eine Tabelle auf dem Server geändert hat)",
                    "Supprimer et recharger le cache des métadonnées de complétion SQL (par exemple après une modification de table sur le serveur)",
                    "Descartar y recargar la caché de metadatos de finalización SQL (por ejemplo, tras cambiar una tabla en el servidor)"),
                ["结构对比"] = Row("Schema Compare", "結構對比", "スキーマ比較", "스키마 비교", "Schemavergleich", "Comparaison de schémas", "Comparar esquemas"),
                ["比较两个数据库的结构差异，并生成同步脚本"] = Row("Compare two databases and generate a synchronization script", "比較兩個資料庫的結構差異，並產生同步指令碼", "2 つのデータベースの構造を比較し、同期スクリプトを生成します", "두 데이터베이스의 구조를 비교하고 동기화 스크립트를 생성합니다.", "Zwei Datenbanken vergleichen und ein Synchronisierungsskript generieren", "Comparer deux bases de données et générer un script de synchronisation", "Comparar dos bases de datos y generar un script de sincronización"),
                ["设置"] = Row("Settings", "設定", "設定", "설정", "Einstellungen", "Paramètres", "Configuración"),
                ["关于"] = Row("About", "關於", "バージョン情報", "정보", "Info", "À propos", "Acerca de"),
            };

        private static Dictionary<string, string> Row(
            string english, string traditional, string japanese, string korean, string german, string french, string spanish)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [English] = english,
                [TraditionalChinese] = traditional,
                [Japanese] = japanese,
                [Korean] = korean,
                [German] = german,
                [French] = french,
                [Spanish] = spanish
            };
        }

        public static void Apply()
        {
            if (!ThreadHelper.CheckAccess()) return;

            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                if (dte == null) return;

                var commandBars = dte.CommandBars as CommandBars;
                if (commandBars == null) return;

                foreach (CommandBar bar in commandBars)
                    ApplyBar(bar);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not localize the shell command bars.");
            }
        }

        private static void ApplyBar(CommandBar bar)
        {
            if (bar == null) return;

            try
            {
                foreach (CommandBarControl control in bar.Controls)
                    ApplyControl(control);
            }
            catch (Exception)
            {
            }
        }

        private static void ApplyControl(CommandBarControl control)
        {
            if (control == null) return;

            try
            {
                string caption = control.Caption;
                if (TryFindSource(caption, out string source, out string text))
                    control.Caption = text;

                string tooltip = control.TooltipText;
                if (TryFindSource(tooltip, out _, out string localizedTooltip))
                    control.TooltipText = localizedTooltip;
            }
            catch (Exception)
            {
            }

            if (control is CommandBarPopup popup)
            {
                try { ApplyBar(popup.CommandBar); }
                catch (Exception) { }
            }
        }

        private static bool TryFindSource(string value, out string source, out string localized)
        {
            source = null;
            localized = null;
            if (string.IsNullOrEmpty(value)) return false;

            foreach (var pair in Texts)
            {
                if (string.Equals(pair.Key, value, StringComparison.Ordinal)
                    || pair.Value.Values.Any(t => string.Equals(t, value, StringComparison.Ordinal)))
                {
                    source = pair.Key;
                    localized = LocalizationManager.CurrentLanguage == Chinese
                        ? pair.Key
                        : pair.Value.TryGetValue(LocalizationManager.CurrentLanguage, out string text) ? text : pair.Value[English];
                    return true;
                }
            }

            return false;
        }
    }
}
