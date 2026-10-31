#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// Missing Asset Importer — Cocoa's Toolbox
/// Migrate先のプロジェクトにて、元プロジェクトから Import してきた Prefab で
/// Missing になっている不足アセットを元プロジェクトから検索し、必要なファイルだけを追加する。
/// </summary>
public class MissingAssetImporter : EditorWindow
{
    // ─────────────────────────────────────────────────────────
    //  定数・正規表現
    // ─────────────────────────────────────────────────────────

    private const string PREF_KEY_SOURCE_PATH = "CocoasToolbox.MissingAssetImporter.SourceProjectPath";
    private const string SESSION_PENDING_REIMPORT = "CocoasToolbox.MissingAssetImporter.PendingReimport";

    private static readonly Regex GuidRegex = new Regex(
        @"[\""']?guid[\""']?\s*:\s*[\""']?([0-9a-fA-F]{32})[\""']?",
        RegexOptions.Compiled);

    private static readonly Regex MetaOwnGuidRegex = new Regex(
        @"\bguid:\s*([0-9a-fA-F]{32})\b",
        RegexOptions.Compiled);

    private static readonly Regex MissingPrefabNameGuidRegex = new Regex(
        @"Missing Prefab with guid:\s*([0-9a-fA-F]{32})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShaderIncludeRegex = new Regex(
        @"^\s*#include\s+\""([^\""]+)\""",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly HashSet<string> BinaryMediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".bmp", ".exr", ".hdr", ".dds", ".webp", ".gif", ".ico",
        ".fbx", ".obj", ".dae", ".3ds", ".dxf", ".blend", ".max", ".ma", ".mb", ".gltf", ".glb",
        ".wav", ".mp3", ".ogg", ".aiff", ".aif", ".flac", ".xm", ".mod", ".it", ".s3m",
        ".mp4", ".mov", ".avi", ".webm", ".asf", ".mpg", ".mpeg",
        ".ttf", ".otf", ".ttc", ".dfont",
        ".dll", ".so", ".dylib", ".exe", ".a", ".lib", ".aar", ".jar", ".zip"
    };

    private static readonly HashSet<string> ShaderCodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".shader", ".cginc", ".hlsl", ".glslinc", ".compute", ".raytrace"
    };

    private static readonly HashSet<string> ScriptExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".dll", ".asmdef", ".asmref"
    };

    private static readonly HashSet<string> JsonGuidExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".shadergraph", ".shadersubgraph", ".vfx"
    };

    // ─────────────────────────────────────────────────────────
    //  データ構造
    // ─────────────────────────────────────────────────────────

    private enum DestinationMode
    {
        KeepOriginalStructure = 0,
        CustomFolder = 1
    }

    [Serializable]
    private class ImportedRecord
    {
        public string Guid;
        public string DestAssetPath;
        public string SourceRelPath;
        public bool IsDirect;
        public bool RenamedForConflict;
    }

    [Serializable]
    private class PackageWarningRecord
    {
        public string PackageName;
        public string DisplayName;
        public string Version;
        public int ReferenceCount;
        public List<string> SampleAssets = new List<string>();
        [NonSerialized] public HashSet<string> MatchedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    [Serializable]
    private class UnresolvedRecord
    {
        public string Guid;
        public string ReferrerInfo;
    }

    [Serializable]
    private class PackageManifestStub
    {
        public string name;
        public string displayName;
        public string version;
    }

    private class SourceAssetEntry
    {
        public string Guid;
        public string RelPath;      // "Assets/..."
        public string FullPath;
        public string MetaFullPath;
        public bool IsDirectory;
    }

    private class SourcePackageEntry
    {
        public string Guid;
        public string PackageName;
        public string DisplayName;
        public string Version;
        public string AssetRelPath;
    }

    private class SourceProjectIndex
    {
        public string ProjectRoot;
        public string AssetsRoot;
        public Dictionary<string, SourceAssetEntry> AssetsByGuid = new Dictionary<string, SourceAssetEntry>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> GuidByAssetFullPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SourcePackageEntry> PackagesByGuid = new Dictionary<string, SourcePackageEntry>(StringComparer.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────
    //  シリアライズ状態（Domain Reload 後も維持）
    // ─────────────────────────────────────────────────────────

    [SerializeField] private string _sourceProjectInput = "";
    [SerializeField] private GameObject _targetPrefab = null;

    [SerializeField] private bool _foldOptions = false;
    [SerializeField] private bool _scanExistingDependencies = true;
    [SerializeField] private bool _includeTransitiveDeps = true;
    [SerializeField] private DestinationMode _destMode = DestinationMode.KeepOriginalStructure;
    [SerializeField] private string _customDestFolder = "Assets/ImportedMissing";

    [SerializeField] private Vector2 _scrollMain;
    [SerializeField] private bool _foldImported = true;
    [SerializeField] private bool _foldPackages = true;
    [SerializeField] private bool _foldUnresolved = false;

    [SerializeField] private List<ImportedRecord> _importedRecords = new List<ImportedRecord>();
    [SerializeField] private List<PackageWarningRecord> _packageWarnings = new List<PackageWarningRecord>();
    [SerializeField] private List<UnresolvedRecord> _unresolvedRecords = new List<UnresolvedRecord>();

    [SerializeField] private string _statusMessage = "";
    [SerializeField] private MessageType _statusType = MessageType.Info;

    // 元プロジェクトのインデックスキャッシュ（メモリ常駐）
    private static SourceProjectIndex _cachedIndex;

    // GUIStyle キャッシュ
    private GUIStyle _getButtonStyle;
    private GUIStyle _badgeStyle;
    private GUIStyle _richMiniStyle;
    private GUIStyle _assetRowStyle;

    private GUIStyle GetButtonStyle => _getButtonStyle ?? (_getButtonStyle = new GUIStyle(GUI.skin.button)
    {
        fontSize = 13,
        fontStyle = FontStyle.Bold
    });

    private GUIStyle BadgeStyle => _badgeStyle ?? (_badgeStyle = new GUIStyle(EditorStyles.miniLabel)
    {
        alignment = TextAnchor.MiddleRight
    });

    private GUIStyle RichMiniStyle => _richMiniStyle ?? (_richMiniStyle = new GUIStyle(EditorStyles.miniLabel)
    {
        wordWrap = true
    });

    private GUIStyle AssetRowStyle => _assetRowStyle ?? (_assetRowStyle = new GUIStyle(EditorStyles.label)
    {
        clipping = TextClipping.Clip,
        wordWrap = false
    });

    // ─────────────────────────────────────────────────────────
    //  メニュー登録・初期化
    // ─────────────────────────────────────────────────────────

    [MenuItem("Tools/Cocoa's Toolbox/Missing Asset Importer")]
    public static void Open()
    {
        var win = GetWindow<MissingAssetImporter>(false, "Missing Asset Importer", true);
        win.minSize = new Vector2(420f, 440f);
        win.Show();
    }

    private void OnEnable()
    {
        minSize = new Vector2(420f, 440f);
        if (string.IsNullOrEmpty(_sourceProjectInput))
        {
            _sourceProjectInput = EditorPrefs.GetString(PREF_KEY_SOURCE_PATH, "");
        }
    }

    /// <summary>
    /// C# スクリプト (.cs / .dll) が追加されてコンパイル＆Domain Reloadが走った後、
    /// 対象 Prefab 等を再インポートして Missing (Mono Script) を確実に復帰させる。
    /// </summary>
    [InitializeOnLoadMethod]
    private static void RegisterPostCompileReimport()
    {
        EditorApplication.delayCall += RunPendingReimportsIfReady;
    }

    private static void RunPendingReimportsIfReady()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += RunPendingReimportsIfReady;
            return;
        }

        string raw = SessionState.GetString(SESSION_PENDING_REIMPORT, "");
        if (string.IsNullOrEmpty(raw)) return;

        SessionState.EraseString(SESSION_PENDING_REIMPORT);

        var paths = raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0) return;

        ReimportAssetPathsOrdered(paths);
        InternalEditorUtility.RepaintAllViews();
    }

    // ─────────────────────────────────────────────────────────
    //  OnGUI
    // ─────────────────────────────────────────────────────────

    private void OnGUI()
    {
        _scrollMain = EditorGUILayout.BeginScrollView(_scrollMain);

        DrawHeader();
        EditorGUILayout.Space(6f);

        DrawSourceProjectSection();
        EditorGUILayout.Space(8f);

        DrawTargetPrefabSection();
        EditorGUILayout.Space(6f);

        DrawOptionsSection();
        EditorGUILayout.Space(10f);

        bool triggerGet = DrawGetButtonSection();

        EditorGUILayout.Space(6f);
        DrawResultsSection();

        EditorGUILayout.EndScrollView();

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox(_statusMessage, _statusType);
            EditorGUILayout.Space(4f);
        }

        if (triggerGet)
        {
            ExecuteGetMissingAssets();
            GUIUtility.ExitGUI();
        }
    }

    // ─────────────────────────────────────────────────────────
    //  UI セクション描画
    // ─────────────────────────────────────────────────────────

    private void DrawHeader()
    {
        EditorGUILayout.LabelField("Missing Asset Importer", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Cocoa's Toolbox", EditorStyles.miniLabel);
        var rect = GUILayoutUtility.GetRect(0f, 1f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, EditorStyles.label.normal.textColor * 0.3f);
    }

    private void DrawSourceProjectSection()
    {
        EditorGUILayout.LabelField("① 元プロジェクトフォルダ", EditorStyles.boldLabel);

        using (new EditorGUI.IndentLevelScope())
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                string nextInput = EditorGUILayout.TextField("プロジェクトパス", _sourceProjectInput);
                if (EditorGUI.EndChangeCheck())
                {
                    SetSourceProjectPath(nextInput);
                }

                if (GUILayout.Button("…", GUILayout.Width(30f)))
                {
                    string initialDir = "";
                    if (!string.IsNullOrEmpty(_sourceProjectInput) && Directory.Exists(CleanPathInput(_sourceProjectInput)))
                    {
                        initialDir = CleanPathInput(_sourceProjectInput);
                    }
                    else
                    {
                        var currentRoot = Directory.GetParent(Application.dataPath);
                        if (currentRoot?.Parent != null)
                            initialDir = currentRoot.Parent.FullName;
                    }

                    string selected = EditorUtility.OpenFolderPanel("元プロジェクトのフォルダを選択", initialDir, "");
                    if (!string.IsNullOrEmpty(selected))
                    {
                        SetSourceProjectPath(selected);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(_sourceProjectInput))
            {
                EditorGUILayout.HelpBox(
                    "参照元となる元プロジェクトのルートフォルダ（または Assets フォルダ）を選択してください。",
                    MessageType.None);
            }
            else if (!TryValidateSourceProject(_sourceProjectInput, out string resolvedRoot, out _, out string validationError))
            {
                EditorGUILayout.HelpBox(validationError, MessageType.Warning);
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string projectName = Path.GetFileName(resolvedRoot.TrimEnd('/', '\\'));
                    bool isCached = _cachedIndex != null && PathsEqual(_cachedIndex.ProjectRoot, resolvedRoot);
                    string cacheNote = isCached
                        ? $"✓ 認識済み: {projectName} （インデックス済み: {_cachedIndex.AssetsByGuid.Count} 件）"
                        : $"✓ 認識済み: {projectName}";

                    EditorGUILayout.LabelField(cacheNote, EditorStyles.miniLabel);

                    if (isCached && GUILayout.Button("再読込", EditorStyles.miniButton, GUILayout.Width(56f)))
                    {
                        _cachedIndex = null;
                        SetStatus("元プロジェクトのインデックスキャッシュをクリアしました。次回 GET 時に再走査します。", MessageType.Info);
                    }
                }
            }
        }
    }

    private void DrawTargetPrefabSection()
    {
        EditorGUILayout.LabelField("② Missing になっている Prefab", EditorStyles.boldLabel);

        using (new EditorGUI.IndentLevelScope())
        {
            _targetPrefab = (GameObject)EditorGUILayout.ObjectField(
                "Target Prefab", _targetPrefab, typeof(GameObject), true);

            if (_targetPrefab == null)
            {
                EditorGUILayout.HelpBox(
                    "Project 内の Prefab アセット（または Scene 上のオブジェクト）をセットして\n" +
                    "「GET」ボタンを押すと、Missing になっている不足アセットだけを追加します。",
                    MessageType.None);
            }
        }
    }

    private void DrawOptionsSection()
    {
        _foldOptions = EditorGUILayout.Foldout(_foldOptions, "詳細設定", true);
        if (!_foldOptions) return;

        using (new EditorGUI.IndentLevelScope())
        {
            _scanExistingDependencies = EditorGUILayout.ToggleLeft(
                "参照先アセット（Material / Controller / Menu 等）内の Missing も検査する",
                _scanExistingDependencies);

            _includeTransitiveDeps = EditorGUILayout.ToggleLeft(
                "追加されるアセットがさらに参照する不足ファイルも連鎖的に取得する",
                _includeTransitiveDeps);

            EditorGUILayout.Space(2f);

            string[] modeLabels =
            {
                "元プロジェクトのフォルダ構成を維持 (Assets/...)",
                "指定フォルダ配下に保存"
            };
            _destMode = (DestinationMode)EditorGUILayout.Popup("保存先", (int)_destMode, modeLabels);

            if (_destMode == DestinationMode.CustomFolder)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _customDestFolder = EditorGUILayout.TextField("保存先フォルダ", _customDestFolder);
                    if (GUILayout.Button("…", GUILayout.Width(30f)))
                    {
                        string abs = EditorUtility.OpenFolderPanel("保存先フォルダを選択", Application.dataPath, "");
                        if (!string.IsNullOrEmpty(abs))
                        {
                            string dataPath = Application.dataPath.Replace('\\', '/');
                            string normAbs = abs.Replace('\\', '/');
                            if (normAbs.Equals(dataPath, StringComparison.OrdinalIgnoreCase))
                            {
                                _customDestFolder = "Assets";
                            }
                            else if (normAbs.StartsWith(dataPath + "/", StringComparison.OrdinalIgnoreCase))
                            {
                                _customDestFolder = "Assets/" + normAbs.Substring(dataPath.Length + 1);
                            }
                            else
                            {
                                SetStatus("保存先は現在のプロジェクトの Assets フォルダ内を指定してください。", MessageType.Warning);
                            }
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
        }
    }

    private bool DrawGetButtonSection()
    {
        bool validSource = TryValidateSourceProject(_sourceProjectInput, out _, out _, out _);
        bool canGet = validSource && _targetPrefab != null && !EditorApplication.isCompiling;

        bool clicked = false;
        using (new EditorGUI.DisabledScope(!canGet))
        {
            if (GUILayout.Button("✦  GET  ✦", GetButtonStyle, GUILayout.Height(34f)))
            {
                clicked = true;
            }
        }

        if (EditorApplication.isCompiling)
        {
            EditorGUILayout.HelpBox("スクリプトのコンパイル中です。完了後に自動で Prefab を更新します…", MessageType.Info);
        }

        return clicked;
    }

    private void DrawResultsSection()
    {
        // 1. 追加されたアセット一覧
        if (_importedRecords != null && _importedRecords.Count > 0)
        {
            _foldImported = EditorGUILayout.Foldout(
                _foldImported, $"追加されたアセット ({_importedRecords.Count} 件)", true);

            if (_foldImported)
            {
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    foreach (var rec in _importedRecords)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            Texture icon = AssetDatabase.GetCachedIcon(rec.DestAssetPath);
                            var content = new GUIContent(" " + rec.DestAssetPath, icon, $"GUID: {rec.Guid}\n元パス: {rec.SourceRelPath}");

                            if (GUILayout.Button(content, AssetRowStyle, GUILayout.MinWidth(40f), GUILayout.ExpandWidth(true), GUILayout.Height(18f)))
                            {
                                var obj = AssetDatabase.LoadMainAssetAtPath(rec.DestAssetPath);
                                if (obj != null)
                                {
                                    EditorGUIUtility.PingObject(obj);
                                    Selection.activeObject = obj;
                                }
                            }

                            string tag = rec.IsDirect ? "[直接]" : "[依存]";
                            if (rec.RenamedForConflict) tag += " [別名保存]";
                            GUILayout.Label(tag, BadgeStyle, GUILayout.Width(rec.RenamedForConflict ? 96f : 44f));
                        }
                    }
                }
            }
        }

        // 2. パッケージ由来の不足参照（VCC / ALCOM 導入案内）
        if (_packageWarnings != null && _packageWarnings.Count > 0)
        {
            EditorGUILayout.Space(4f);
            _foldPackages = EditorGUILayout.Foldout(
                _foldPackages, $"不足パッケージの検出 ({_packageWarnings.Count} パッケージ)", true);

            if (_foldPackages)
            {
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField(
                        "以下の Missing 参照は元プロジェクトの Packages 内のアセットです。\n" +
                        "重複エラー防止のため Assets へはコピーしていません。VCC / ALCOM 等からパッケージを導入してください。",
                        RichMiniStyle);
                    EditorGUILayout.Space(2f);

                    foreach (var pkg in _packageWarnings)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            string title = string.IsNullOrEmpty(pkg.DisplayName)
                                ? pkg.PackageName
                                : $"{pkg.DisplayName} ({pkg.PackageName})";
                            if (!string.IsNullOrEmpty(pkg.Version))
                                title += $" v{pkg.Version}";

                            EditorGUILayout.LabelField($"• {title}  [{pkg.ReferenceCount} 件]", EditorStyles.miniBoldLabel, GUILayout.MinWidth(40f), GUILayout.ExpandWidth(true));

                            if (GUILayout.Button("IDコピー", EditorStyles.miniButton, GUILayout.Width(60f)))
                            {
                                EditorGUIUtility.systemCopyBuffer = pkg.PackageName;
                                SetStatus($"パッケージID '{pkg.PackageName}' をクリップボードにコピーしました。", MessageType.Info);
                            }
                        }
                    }
                }
            }
        }

        // 3. 元プロジェクト内でも見つからなかった参照
        if (_unresolvedRecords != null && _unresolvedRecords.Count > 0)
        {
            EditorGUILayout.Space(4f);
            _foldUnresolved = EditorGUILayout.Foldout(
                _foldUnresolved, $"元プロジェクト内でも見つからない参照 ({_unresolvedRecords.Count} 件)", true);

            if (_foldUnresolved)
            {
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    foreach (var un in _unresolvedRecords)
                    {
                        EditorGUILayout.LabelField($"• {un.ReferrerInfo}  (GUID: {un.Guid})", RichMiniStyle);
                    }
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  メイン実行処理 — GET
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Target Prefab と元プロジェクトを照合し、Missing になっている不足アセットだけを追加する。
    /// </summary>
    public void ExecuteGetMissingAssets()
    {
        _importedRecords.Clear();
        _packageWarnings.Clear();
        _unresolvedRecords.Clear();

        if (!TryValidateSourceProject(_sourceProjectInput, out string srcProjectRoot, out string srcAssetsRoot, out string err))
        {
            SetStatus(err, MessageType.Warning);
            return;
        }

        if (_targetPrefab == null)
        {
            SetStatus("Target Prefab がセットされていません。", MessageType.Warning);
            return;
        }

        if (_destMode == DestinationMode.CustomFolder && !IsValidCustomDestFolder(_customDestFolder))
        {
            SetStatus("保存先フォルダは 'Assets' から始まる有効なパスを指定してください。", MessageType.Warning);
            return;
        }

        try
        {
            // 1. 元プロジェクトの GUID インデックス取得（未構築なら構築）
            SourceProjectIndex index = GetOrBuildSourceIndex(srcProjectRoot, srcAssetsRoot);

            var allImportedByGuid = new Dictionary<string, ImportedRecord>(StringComparer.OrdinalIgnoreCase);
            var allPackageHits = new Dictionary<string, PackageWarningRecord>(StringComparer.OrdinalIgnoreCase);
            var allUnresolved = new Dictionary<string, UnresolvedRecord>(StringComparer.OrdinalIgnoreCase);
            var allAffectedCurrentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyScriptImported = false;

            // バイナリアセット経由の依存も取りこぼさないよう最大3パスまで確認（通常のYAMLは1パスで完結）
            int maxPasses = _includeTransitiveDeps ? 3 : 1;
            for (int pass = 0; pass < maxPasses; pass++)
            {
                EditorUtility.DisplayProgressBar(
                    "Missing Asset Importer",
                    "Missing 参照を解析しています...",
                    0.25f + pass * 0.2f);

                // 2. 現在プロジェクトの Target Prefab から Missing GUID を収集
                CollectMissingFromTarget(
                    _targetPrefab,
                    _scanExistingDependencies,
                    index,
                    out Dictionary<string, HashSet<string>> missingGuidReferrers,
                    out Dictionary<string, string> missingGuidContext,
                    out HashSet<string> targetAssetPaths);

                foreach (var tp in targetAssetPaths)
                    allAffectedCurrentPaths.Add(tp);

                // 3. 元プロジェクト内で解決 ＆ 連鎖依存アセットを収集
                ResolveInSourceProject(
                    missingGuidReferrers,
                    missingGuidContext,
                    index,
                    _includeTransitiveDeps,
                    out List<(SourceAssetEntry entry, bool isDirect)> toImportThisPass,
                    allPackageHits,
                    allUnresolved);

                // 既に今セッションでインポート済みのものを除外
                toImportThisPass.RemoveAll(item => allImportedByGuid.ContainsKey(item.entry.Guid));

                if (toImportThisPass.Count == 0)
                    break;

                // 参照元となっていた現在プロジェクトのアセットを再インポート対象に記録
                foreach (var kv in missingGuidReferrers)
                {
                    if (index.AssetsByGuid.ContainsKey(kv.Key))
                    {
                        foreach (var refPath in kv.Value)
                            allAffectedCurrentPaths.Add(refPath);
                    }
                }

                // 4. ファイルと .meta をコピーして AssetDatabase.Refresh
                CopyAssetsFromSource(
                    toImportThisPass,
                    index,
                    allImportedByGuid,
                    allAffectedCurrentPaths,
                    ref anyScriptImported);

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                if (anyScriptImported)
                {
                    // スクリプト追加時はコンパイル完了を待つ必要があるためループ終了
                    break;
                }
            }

            EditorUtility.DisplayProgressBar("Missing Asset Importer", "Prefab の参照を更新しています...", 0.9f);

            // 5. 対象 Prefab および参照元アセットを再インポートして Missing 表示を即時解消
            if (allImportedByGuid.Count > 0)
            {
                if (anyScriptImported || EditorApplication.isCompiling)
                {
                    SessionState.SetString(
                        SESSION_PENDING_REIMPORT,
                        string.Join("\n", allAffectedCurrentPaths));
                }
                else
                {
                    ReimportAssetPathsOrdered(allAffectedCurrentPaths);
                    InternalEditorUtility.RepaintAllViews();
                }
            }

            // 6. UI 表示用にリスト整形
            _importedRecords = allImportedByGuid.Values
                .OrderByDescending(r => r.IsDirect)
                .ThenBy(r => r.DestAssetPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _packageWarnings = allPackageHits.Values
                .OrderByDescending(p => p.ReferenceCount)
                .ThenBy(p => p.PackageName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _unresolvedRecords = allUnresolved.Values
                .OrderBy(u => u.ReferrerInfo, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _foldImported = _importedRecords.Count > 0;
            _foldPackages = _packageWarnings.Count > 0;

            // 7. ステータスメッセージ生成
            BuildCompletionStatus(anyScriptImported);
        }
        catch (Exception ex)
        {
            SetStatus($"エラーが発生しました: {ex.Message}", MessageType.Error);
            Debug.LogException(ex);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private void BuildCompletionStatus(bool anyScriptImported)
    {
        int importedCount = _importedRecords.Count;
        int pkgCount = _packageWarnings.Count;
        int unresolvedCount = _unresolvedRecords.Count;

        if (importedCount == 0 && pkgCount == 0 && unresolvedCount == 0)
        {
            SetStatus("Missing になっているアセットはありませんでした。", MessageType.Info);
            return;
        }

        var parts = new List<string>();
        if (importedCount > 0)
            parts.Add($"{importedCount} 件の不足アセットを追加しました");
        else
            parts.Add("Assets から追加可能な不足ファイルはありませんでした");

        if (anyScriptImported)
            parts.Add("スクリプトのコンパイル完了後に自動で Prefab を再更新します");
        if (pkgCount > 0)
            parts.Add($"不足パッケージ {pkgCount} 件あり");
        if (unresolvedCount > 0)
            parts.Add($"元プロジェクト不在 {unresolvedCount} 件");

        MessageType msgType = (pkgCount > 0 || unresolvedCount > 0) && importedCount == 0
            ? MessageType.Warning
            : MessageType.Info;

        string summary = string.Join(" / ", parts);
        SetStatus(summary, msgType);
        Debug.Log($"[MissingAssetImporter] {summary}");
    }

    // ─────────────────────────────────────────────────────────
    //  Step 1: 元プロジェクトの検証 ＆ GUID インデックス構築
    // ─────────────────────────────────────────────────────────

    private void SetSourceProjectPath(string rawPath)
    {
        string cleaned = CleanPathInput(rawPath);
        if (TryNormalizeSourceProjectRoot(cleaned, out string normalizedRoot))
        {
            cleaned = normalizedRoot;
        }

        _sourceProjectInput = cleaned;
        EditorPrefs.SetString(PREF_KEY_SOURCE_PATH, _sourceProjectInput);
    }

    private static string CleanPathInput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return raw.Trim().Trim('"').Trim();
    }

    private static bool TryNormalizeSourceProjectRoot(string path, out string projectRoot)
    {
        projectRoot = null;
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            string full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) return false;

            // ルート直下に Assets がある場合
            string subAssets = Path.Combine(full, "Assets");
            if (Directory.Exists(subAssets))
            {
                projectRoot = full;
                return true;
            }

            // ユーザーが誤って Assets フォルダ自体を選択した場合
            string dirName = Path.GetFileName(full.TrimEnd('/', '\\'));
            if (string.Equals(dirName, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                DirectoryInfo parent = Directory.GetParent(full.TrimEnd('/', '\\'));
                if (parent != null && Directory.Exists(parent.FullName))
                {
                    projectRoot = parent.FullName;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryValidateSourceProject(
        string rawInput,
        out string projectRoot,
        out string assetsRoot,
        out string errorMessage)
    {
        projectRoot = null;
        assetsRoot = null;
        errorMessage = null;

        string cleaned = CleanPathInput(rawInput);
        if (string.IsNullOrEmpty(cleaned))
        {
            errorMessage = "元プロジェクトのフォルダパスを指定してください。";
            return false;
        }

        if (!Directory.Exists(cleaned))
        {
            errorMessage = "指定されたフォルダが存在しません。";
            return false;
        }

        if (!TryNormalizeSourceProjectRoot(cleaned, out projectRoot))
        {
            errorMessage = "指定されたフォルダ内に Assets フォルダが見つかりません。Unity プロジェクトのフォルダを指定してください。";
            return false;
        }

        assetsRoot = Path.Combine(projectRoot, "Assets");

        string currentProjectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
        if (PathsEqual(projectRoot, currentProjectRoot))
        {
            errorMessage = "現在のプロジェクト（Migrate先）と同じフォルダが指定されています。元プロジェクトを指定してください。";
            return false;
        }

        return true;
    }

    private static SourceProjectIndex GetOrBuildSourceIndex(string projectRoot, string assetsRoot)
    {
        if (_cachedIndex != null && PathsEqual(_cachedIndex.ProjectRoot, projectRoot))
        {
            return _cachedIndex;
        }

        EditorUtility.DisplayProgressBar(
            "Missing Asset Importer",
            "元プロジェクトの .meta インデックスを構築しています...",
            0.1f);

        var index = new SourceProjectIndex
        {
            ProjectRoot = projectRoot,
            AssetsRoot = assetsRoot
        };

        // 1. Assets 配下の全 .meta を並列読み取り
        var assetMetaBag = new ConcurrentBag<SourceAssetEntry>();
        string[] assetMetaFiles = Directory.GetFiles(assetsRoot, "*.meta", SearchOption.AllDirectories);

        Parallel.ForEach(assetMetaFiles, metaPath =>
        {
            string targetFullPath = metaPath.Substring(0, metaPath.Length - 5);
            bool isFile = File.Exists(targetFullPath);
            bool isDir = !isFile && Directory.Exists(targetFullPath);
            if (!isFile && !isDir) return; // 孤立した .meta は無視

            if (!TryReadOwnGuidFromMeta(metaPath, out string guid)) return;

            string relInsideAssets = GetRelativePathNormalized(assetsRoot, targetFullPath);
            string relPath = "Assets/" + relInsideAssets;

            assetMetaBag.Add(new SourceAssetEntry
            {
                Guid = guid,
                RelPath = relPath,
                FullPath = targetFullPath,
                MetaFullPath = metaPath,
                IsDirectory = isDir
            });
        });

        foreach (var entry in assetMetaBag)
        {
            index.AssetsByGuid[entry.Guid] = entry;
            index.GuidByAssetFullPath[NormalizeFullPathKey(entry.FullPath)] = entry.Guid;
        }

        // 2. Packages および Library/PackageCache 配下の .meta を読み取り（パッケージ不足通知用）
        IndexPackagesDirectory(Path.Combine(projectRoot, "Packages"), index.PackagesByGuid);
        IndexPackagesDirectory(Path.Combine(projectRoot, "Library", "PackageCache"), index.PackagesByGuid);

        _cachedIndex = index;
        return index;
    }

    private static void IndexPackagesDirectory(
        string packagesContainerDir,
        Dictionary<string, SourcePackageEntry> dict)
    {
        if (!Directory.Exists(packagesContainerDir)) return;

        string[] pkgDirs;
        try
        {
            pkgDirs = Directory.GetDirectories(packagesContainerDir);
        }
        catch
        {
            return;
        }

        var bag = new ConcurrentBag<SourcePackageEntry>();

        Parallel.ForEach(pkgDirs, pkgDir =>
        {
            string manifestPath = Path.Combine(pkgDir, "package.json");
            if (!File.Exists(manifestPath)) return;

            string pkgName = Path.GetFileName(pkgDir);
            string displayName = pkgName;
            string version = "";

            try
            {
                string json = ReadAllTextShared(manifestPath);
                var stub = JsonUtility.FromJson<PackageManifestStub>(json);
                if (stub != null)
                {
                    if (!string.IsNullOrEmpty(stub.name)) pkgName = stub.name;
                    if (!string.IsNullOrEmpty(stub.displayName)) displayName = stub.displayName;
                    if (!string.IsNullOrEmpty(stub.version)) version = stub.version;
                }
            }
            catch
            {
                // package.json パース失敗時はフォルダ名をフォールバック使用
            }

            string[] metas;
            try
            {
                metas = Directory.GetFiles(pkgDir, "*.meta", SearchOption.AllDirectories);
            }
            catch
            {
                return;
            }

            foreach (var metaPath in metas)
            {
                string targetPath = metaPath.Substring(0, metaPath.Length - 5);
                if (!File.Exists(targetPath) && !Directory.Exists(targetPath)) continue;
                if (!TryReadOwnGuidFromMeta(metaPath, out string guid)) continue;

                string relInPkg = GetRelativePathNormalized(pkgDir, targetPath);
                bag.Add(new SourcePackageEntry
                {
                    Guid = guid,
                    PackageName = pkgName,
                    DisplayName = displayName,
                    Version = version,
                    AssetRelPath = $"Packages/{pkgName}/{relInPkg}"
                });
            }
        });

        foreach (var item in bag)
        {
            if (!dict.ContainsKey(item.Guid))
                dict[item.Guid] = item;
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Step 2: Target Prefab から Missing GUID を収集
    // ─────────────────────────────────────────────────────────

    private static void CollectMissingFromTarget(
        GameObject targetGo,
        bool scanExistingDeps,
        SourceProjectIndex sourceIndex,
        out Dictionary<string, HashSet<string>> missingGuidReferrers,
        out Dictionary<string, string> missingGuidContext,
        out HashSet<string> rootTargetAssetPaths)
    {
        missingGuidReferrers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        missingGuidContext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        rootTargetAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var assetQueue = new Queue<string>();
        var visitedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (EditorUtility.IsPersistent(targetGo))
        {
            // Project 内の Prefab アセットが指定された場合
            string prefabPath = AssetDatabase.GetAssetPath(targetGo);
            if (!string.IsNullOrEmpty(prefabPath))
            {
                rootTargetAssetPaths.Add(prefabPath);
                if (visitedAssets.Add(prefabPath))
                    assetQueue.Enqueue(prefabPath);

                // ルート Prefab は SerializedObject でも走査して分かりやすいコンポーネント名を記録
                CollectSerializedObjectReferencesFromHierarchy(
                    targetGo,
                    prefabPath,
                    scanExistingDeps,
                    assetQueue,
                    visitedAssets,
                    missingGuidReferrers,
                    missingGuidContext);
            }
        }
        else
        {
            // Scene 上の GameObject / Prefab インスタンスが指定された場合
            string nearestPrefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(targetGo);
            if (!string.IsNullOrEmpty(nearestPrefabPath) && nearestPrefabPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                rootTargetAssetPaths.Add(nearestPrefabPath);
                if (visitedAssets.Add(nearestPrefabPath))
                    assetQueue.Enqueue(nearestPrefabPath);
            }

            // 子階層に含まれる別 Nested Prefab のソースアセットもキューへ追加
            foreach (Transform t in targetGo.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                string subPrefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject);
                if (!string.IsNullOrEmpty(subPrefabPath) && subPrefabPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    rootTargetAssetPaths.Add(subPrefabPath);
                    if (visitedAssets.Add(subPrefabPath))
                        assetQueue.Enqueue(subPrefabPath);
                }
            }

            // Scene 上のオーバーライドや Unpack 済みオブジェクトを SerializedObject で走査
            CollectSerializedObjectReferencesFromHierarchy(
                targetGo,
                nearestPrefabPath ?? targetGo.name,
                scanExistingDeps,
                assetQueue,
                visitedAssets,
                missingGuidReferrers,
                missingGuidContext);
        }

        // Assets 内の対象アセット（および既存依存アセット）を BFS 走査
        while (assetQueue.Count > 0)
        {
            string currentAssetPath = assetQueue.Dequeue();
            ScanCurrentProjectAssetFile(
                currentAssetPath,
                scanExistingDeps,
                sourceIndex,
                assetQueue,
                visitedAssets,
                missingGuidReferrers,
                missingGuidContext);
        }
    }

    private static void CollectSerializedObjectReferencesFromHierarchy(
        GameObject rootGo,
        string ownerAssetOrName,
        bool scanExistingDeps,
        Queue<string> assetQueue,
        HashSet<string> visitedAssets,
        Dictionary<string, HashSet<string>> missingGuidReferrers,
        Dictionary<string, string> missingGuidContext)
    {
        foreach (Transform t in rootGo.GetComponentsInChildren<Transform>(true))
        {
            if (t == null) continue;

            // Missing Nested Prefab プレースホルダー名から GUID 検出
            Match mPrefab = MissingPrefabNameGuidRegex.Match(t.name);
            if (mPrefab.Success)
            {
                string mpGuid = mPrefab.Groups[1].Value.ToLowerInvariant();
                if (!ExistsInCurrentProject(mpGuid, out _))
                {
                    RecordMissingGuid(
                        mpGuid,
                        ownerAssetOrName,
                        $"{t.name}",
                        missingGuidReferrers,
                        missingGuidContext);
                }
            }

            Component[] comps = t.GetComponents<Component>();
            foreach (Component comp in comps)
            {
                if (comp == null || comp is Transform) continue;

                InspectUnityObjectViaSerializedObject(
                    comp,
                    ownerAssetOrName,
                    $"{t.name} ({comp.GetType().Name})",
                    scanExistingDeps,
                    assetQueue,
                    visitedAssets,
                    missingGuidReferrers,
                    missingGuidContext);
            }
        }
    }

    private static void InspectUnityObjectViaSerializedObject(
        UnityEngine.Object obj,
        string referrerAssetPath,
        string contextPrefix,
        bool scanExistingDeps,
        Queue<string> assetQueue,
        HashSet<string> visitedAssets,
        Dictionary<string, HashSet<string>> missingGuidReferrers,
        Dictionary<string, string> missingGuidContext)
    {
        if (obj == null) return;

        SerializedObject so;
        try
        {
            so = new SerializedObject(obj);
        }
        catch
        {
            return;
        }

        SerializedProperty prop = so.GetIterator();
        bool enterChildren = true;

        while (prop.Next(enterChildren))
        {
            enterChildren = true;

            if (prop.propertyType == SerializedPropertyType.String)
            {
                enterChildren = false;
                continue;
            }

            if (prop.isArray &&
                prop.propertyType != SerializedPropertyType.Generic &&
                prop.propertyType != SerializedPropertyType.ObjectReference &&
                prop.propertyType != SerializedPropertyType.ManagedReference)
            {
                enterChildren = false;
                continue;
            }

            if (prop.isArray)
            {
                string elemType = prop.arrayElementType;
                if (elemType == "byte" || elemType == "UInt8" || elemType == "int" ||
                    elemType == "float" || elemType == "double" || elemType == "char")
                {
                    enterChildren = false;
                    continue;
                }
            }

            if (prop.propertyType == SerializedPropertyType.ObjectReference)
            {
                enterChildren = false;
                UnityEngine.Object refVal = prop.objectReferenceValue;
                int instId = prop.objectReferenceInstanceIDValue;

                if (refVal == null && instId != 0)
                {
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(instId, out string guid, out long _))
                    {
                        guid = guid.ToLowerInvariant();
                        if (!IsBuiltInOrEmptyGuid(guid) && !ExistsInCurrentProject(guid, out _))
                        {
                            RecordMissingGuid(
                                guid,
                                referrerAssetPath,
                                $"{contextPrefix}.{prop.propertyPath}",
                                missingGuidReferrers,
                                missingGuidContext);
                        }
                    }
                }
                else if (refVal != null && scanExistingDeps)
                {
                    string depPath = AssetDatabase.GetAssetPath(refVal);
                    if (!string.IsNullOrEmpty(depPath) &&
                        depPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                        visitedAssets.Add(depPath))
                    {
                        assetQueue.Enqueue(depPath);
                    }
                }
            }
        }
    }

    private static void ScanCurrentProjectAssetFile(
        string assetPath,
        bool scanExistingDeps,
        SourceProjectIndex sourceIndex,
        Queue<string> assetQueue,
        HashSet<string> visitedAssets,
        Dictionary<string, HashSet<string>> missingGuidReferrers,
        Dictionary<string, string> missingGuidContext)
    {
        if (string.IsNullOrEmpty(assetPath)) return;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(assetPath);
        }
        catch
        {
            return;
        }

        if (!File.Exists(fullPath)) return;

        string selfGuid = AssetDatabase.AssetPathToGUID(assetPath);
        string ext = Path.GetExtension(fullPath);

        // 1. .meta 内の参照 GUID を走査（FBX の externalObjects マテリアル割り当て等）
        string metaPath = fullPath + ".meta";
        if (File.Exists(metaPath))
        {
            ScanTextFileForGuids(
                metaPath,
                selfGuid,
                assetPath,
                $"{Path.GetFileName(assetPath)} (.meta)",
                scanExistingDeps,
                assetQueue,
                visitedAssets,
                missingGuidReferrers,
                missingGuidContext);
        }

        // 2. Shader / HLSL / CGINC の #include 不足ファイルを検査
        if (ShaderCodeExtensions.Contains(ext))
        {
            ScanShaderIncludesInCurrentProject(
                assetPath,
                fullPath,
                sourceIndex,
                scanExistingDeps,
                assetQueue,
                visitedAssets,
                missingGuidReferrers,
                missingGuidContext);
            return;
        }

        // 3. 画像・音声・FBX・DLL等のバイナリメディアは本体をテキスト走査しない
        if (BinaryMediaExtensions.Contains(ext) || string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 4. YAML または JSON (ShaderGraph 等) の場合はテキストから全 GUID を高速抽出
        if (IsYamlOrJsonGuidAsset(fullPath, ext))
        {
            ScanTextFileForGuids(
                fullPath,
                selfGuid,
                assetPath,
                Path.GetFileName(assetPath),
                scanExistingDeps,
                assetQueue,
                visitedAssets,
                missingGuidReferrers,
                missingGuidContext);
        }
        else
        {
            // 5. バイナリシリアライズされた Unity アセット（旧形式 .mat / .asset / .anim 等）へのフォールバック
            UnityEngine.Object[] allSubAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            foreach (var sub in allSubAssets)
            {
                if (sub == null || sub is Transform || sub is GameObject) continue;

                InspectUnityObjectViaSerializedObject(
                    sub,
                    assetPath,
                    $"{Path.GetFileName(assetPath)} ({sub.GetType().Name})",
                    scanExistingDeps,
                    assetQueue,
                    visitedAssets,
                    missingGuidReferrers,
                    missingGuidContext);

                if (sub is AnimationClip clip)
                {
                    InspectAnimationClipObjectCurves(
                        clip,
                        assetPath,
                        scanExistingDeps,
                        assetQueue,
                        visitedAssets,
                        missingGuidReferrers,
                        missingGuidContext);
                }
            }
        }
    }

    private static void InspectAnimationClipObjectCurves(
        AnimationClip clip,
        string assetPath,
        bool scanExistingDeps,
        Queue<string> assetQueue,
        HashSet<string> visitedAssets,
        Dictionary<string, HashSet<string>> missingGuidReferrers,
        Dictionary<string, string> missingGuidContext)
    {
        EditorCurveBinding[] bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
        foreach (var b in bindings)
        {
            ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
            if (keys == null) continue;
            foreach (var k in keys)
            {
                if (k.value != null && scanExistingDeps)
                {
                    string dep = AssetDatabase.GetAssetPath(k.value);
                    if (!string.IsNullOrEmpty(dep) &&
                        dep.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                        visitedAssets.Add(dep))
                    {
                        assetQueue.Enqueue(dep);
                    }
                }
            }
        }
    }

    private static void ScanTextFileForGuids(
        string filePath,
        string selfGuidToSkip,
        string referrerAssetPath,
        string contextLabel,
        bool scanExistingDeps,
        Queue<string> assetQueue,
        HashSet<string> visitedAssets,
        Dictionary<string, HashSet<string>> missingGuidReferrers,
        Dictionary<string, string> missingGuidContext)
    {
        string text;
        try
        {
            text = ReadAllTextShared(filePath);
        }
        catch
        {
            return;
        }

        foreach (Match match in GuidRegex.Matches(text))
        {
            string guid = match.Groups[1].Value.ToLowerInvariant();
            if (IsBuiltInOrEmptyGuid(guid)) continue;
            if (!string.IsNullOrEmpty(selfGuidToSkip) && string.Equals(guid, selfGuidToSkip, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ExistsInCurrentProject(guid, out string existingPath))
            {
                if (scanExistingDeps &&
                    !string.IsNullOrEmpty(existingPath) &&
                    existingPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                    visitedAssets.Add(existingPath))
                {
                    assetQueue.Enqueue(existingPath);
                }
            }
            else
            {
                RecordMissingGuid(guid, referrerAssetPath, contextLabel, missingGuidReferrers, missingGuidContext);
            }
        }
    }

    private static void ScanShaderIncludesInCurrentProject(
        string shaderAssetPath,
        string shaderFullPath,
        SourceProjectIndex sourceIndex,
        bool scanExistingDeps,
        Queue<string> assetQueue,
        HashSet<string> visitedAssets,
        Dictionary<string, HashSet<string>> missingGuidReferrers,
        Dictionary<string, string> missingGuidContext)
    {
        string code;
        try
        {
            code = ReadAllTextShared(shaderFullPath);
        }
        catch
        {
            return;
        }

        string currentProjectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
        string shaderDir = Path.GetDirectoryName(shaderFullPath) ?? "";

        foreach (Match m in ShaderIncludeRegex.Matches(code))
        {
            string inc = m.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(inc) || inc.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                continue;

            // 現在プロジェクト内に既に存在するか確認
            string candidateFull = inc.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(currentProjectRoot, inc)
                : Path.Combine(shaderDir, inc);

            try
            {
                candidateFull = Path.GetFullPath(candidateFull);
            }
            catch
            {
                continue;
            }

            if (File.Exists(candidateFull))
            {
                if (scanExistingDeps)
                {
                    string dataFull = Path.GetFullPath(Application.dataPath);
                    if (candidateFull.StartsWith(dataFull, StringComparison.OrdinalIgnoreCase))
                    {
                        string relAsset = "Assets/" + GetRelativePathNormalized(dataFull, candidateFull);
                        if (visitedAssets.Add(relAsset))
                            assetQueue.Enqueue(relAsset);
                    }
                }
            }
            else
            {
                // 元プロジェクト内の対応パスに #include 先が存在するか確認
                string srcShaderEquivalent = shaderAssetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(sourceIndex.ProjectRoot, shaderAssetPath)
                    : null;

                string srcCandidate = null;
                if (inc.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    srcCandidate = Path.Combine(sourceIndex.ProjectRoot, inc);
                }
                else if (!string.IsNullOrEmpty(srcShaderEquivalent))
                {
                    string srcDir = Path.GetDirectoryName(srcShaderEquivalent) ?? "";
                    srcCandidate = Path.Combine(srcDir, inc);
                }

                if (!string.IsNullOrEmpty(srcCandidate))
                {
                    try
                    {
                        string normSrc = NormalizeFullPathKey(srcCandidate);
                        if (sourceIndex.GuidByAssetFullPath.TryGetValue(normSrc, out string incGuid) &&
                            !ExistsInCurrentProject(incGuid, out _))
                        {
                            RecordMissingGuid(
                                incGuid,
                                shaderAssetPath,
                                $"{Path.GetFileName(shaderAssetPath)} (#include \"{inc}\")",
                                missingGuidReferrers,
                                missingGuidContext);
                        }
                    }
                    catch
                    {
                        // 無効なパスはスキップ
                    }
                }
            }
        }
    }

    private static void RecordMissingGuid(
        string guid,
        string referrerAssetPath,
        string contextText,
        Dictionary<string, HashSet<string>> missingGuidReferrers,
        Dictionary<string, string> missingGuidContext)
    {
        if (!missingGuidReferrers.TryGetValue(guid, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            missingGuidReferrers[guid] = set;
        }
        if (!string.IsNullOrEmpty(referrerAssetPath) && referrerAssetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            set.Add(referrerAssetPath);
        }

        // より詳細なコンテキスト（コンポーネント・プロパティ名付き）を優先保持
        if (!missingGuidContext.TryGetValue(guid, out string existing) ||
            (existing != null && !existing.Contains("(") && contextText.Contains("(")))
        {
            missingGuidContext[guid] = contextText;
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Step 3: 元プロジェクト内での解決 ＆ 連鎖依存解決
    // ─────────────────────────────────────────────────────────

    private static void ResolveInSourceProject(
        Dictionary<string, HashSet<string>> directMissingReferrers,
        Dictionary<string, string> directMissingContext,
        SourceProjectIndex sourceIndex,
        bool includeTransitive,
        out List<(SourceAssetEntry entry, bool isDirect)> resolvedAssets,
        Dictionary<string, PackageWarningRecord> packageHits,
        Dictionary<string, UnresolvedRecord> unresolvedHits)
    {
        resolvedAssets = new List<(SourceAssetEntry entry, bool isDirect)>();
        var visitedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string guid, bool isDirect, string referrerContext)>();

        foreach (var guid in directMissingReferrers.Keys)
        {
            string ctx = directMissingContext.TryGetValue(guid, out string c) ? c : "Target";
            if (visitedGuids.Add(guid))
            {
                queue.Enqueue((guid, true, ctx));
            }
        }

        while (queue.Count > 0)
        {
            var (guid, isDirect, referrerContext) = queue.Dequeue();

            if (IsBuiltInOrEmptyGuid(guid) || ExistsInCurrentProject(guid, out _))
                continue;

            if (sourceIndex.AssetsByGuid.TryGetValue(guid, out SourceAssetEntry assetEntry))
            {
                resolvedAssets.Add((assetEntry, isDirect));

                if (includeTransitive && !assetEntry.IsDirectory)
                {
                    EnqueueSourceAssetDependencies(assetEntry, sourceIndex, visitedGuids, queue);
                }
            }
            else if (sourceIndex.PackagesByGuid.TryGetValue(guid, out SourcePackageEntry pkgEntry))
            {
                if (!packageHits.TryGetValue(pkgEntry.PackageName, out var warn))
                {
                    warn = new PackageWarningRecord
                    {
                        PackageName = pkgEntry.PackageName,
                        DisplayName = pkgEntry.DisplayName,
                        Version = pkgEntry.Version,
                        ReferenceCount = 0
                    };
                    packageHits[pkgEntry.PackageName] = warn;
                }
                if (warn.MatchedGuids.Add(guid))
                {
                    warn.ReferenceCount = warn.MatchedGuids.Count;
                }
                if (warn.SampleAssets.Count < 5 && !warn.SampleAssets.Contains(pkgEntry.AssetRelPath))
                {
                    warn.SampleAssets.Add(pkgEntry.AssetRelPath);
                }
            }
            else if (isDirect)
            {
                if (!unresolvedHits.ContainsKey(guid))
                {
                    unresolvedHits[guid] = new UnresolvedRecord
                    {
                        Guid = guid,
                        ReferrerInfo = referrerContext
                    };
                }
            }
        }
    }

    private static void EnqueueSourceAssetDependencies(
        SourceAssetEntry srcEntry,
        SourceProjectIndex sourceIndex,
        HashSet<string> visitedGuids,
        Queue<(string guid, bool isDirect, string referrerContext)> queue)
    {
        string ext = Path.GetExtension(srcEntry.FullPath);
        string srcName = Path.GetFileName(srcEntry.FullPath);

        // 1. .meta 内の参照 GUID を抽出
        if (File.Exists(srcEntry.MetaFullPath))
        {
            ExtractGuidsFromSourceTextFile(srcEntry.MetaFullPath, srcEntry.Guid, srcName, visitedGuids, queue);
        }

        // 2. Shader / HLSL / CGINC の #include 依存を抽出
        if (ShaderCodeExtensions.Contains(ext))
        {
            try
            {
                string code = ReadAllTextShared(srcEntry.FullPath);
                string shaderDir = Path.GetDirectoryName(srcEntry.FullPath) ?? "";
                foreach (Match m in ShaderIncludeRegex.Matches(code))
                {
                    string inc = m.Groups[1].Value.Trim();
                    if (string.IsNullOrEmpty(inc) || inc.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string candidate = inc.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(sourceIndex.ProjectRoot, inc)
                        : Path.Combine(shaderDir, inc);

                    string normKey = NormalizeFullPathKey(candidate);
                    if (sourceIndex.GuidByAssetFullPath.TryGetValue(normKey, out string incGuid) &&
                        !ExistsInCurrentProject(incGuid, out _) &&
                        visitedGuids.Add(incGuid))
                    {
                        queue.Enqueue((incGuid, false, srcName));
                    }
                }
            }
            catch
            {
                // 読み取り失敗時はスキップ
            }
            return;
        }

        // 3. YAML / JSON アセット本体の参照 GUID を抽出
        if (!BinaryMediaExtensions.Contains(ext) &&
            !string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase) &&
            IsYamlOrJsonGuidAsset(srcEntry.FullPath, ext))
        {
            ExtractGuidsFromSourceTextFile(srcEntry.FullPath, srcEntry.Guid, srcName, visitedGuids, queue);
        }
    }

    private static void ExtractGuidsFromSourceTextFile(
        string filePath,
        string selfGuid,
        string referrerName,
        HashSet<string> visitedGuids,
        Queue<(string guid, bool isDirect, string referrerContext)> queue)
    {
        string text;
        try
        {
            text = ReadAllTextShared(filePath);
        }
        catch
        {
            return;
        }

        foreach (Match m in GuidRegex.Matches(text))
        {
            string depGuid = m.Groups[1].Value.ToLowerInvariant();
            if (IsBuiltInOrEmptyGuid(depGuid)) continue;
            if (string.Equals(depGuid, selfGuid, StringComparison.OrdinalIgnoreCase)) continue;
            if (ExistsInCurrentProject(depGuid, out _)) continue;

            if (visitedGuids.Add(depGuid))
            {
                queue.Enqueue((depGuid, false, referrerName));
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Step 4: 安全なアセット＆.meta コピー処理
    // ─────────────────────────────────────────────────────────

    private void CopyAssetsFromSource(
        List<(SourceAssetEntry entry, bool isDirect)> itemsToImport,
        SourceProjectIndex sourceIndex,
        Dictionary<string, ImportedRecord> allImportedByGuid,
        HashSet<string> allAffectedCurrentPaths,
        ref bool anyScriptImported)
    {
        string currentProjectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
        int total = itemsToImport.Count;

        AssetDatabase.StartAssetEditing();
        try
        {
            for (int i = 0; i < total; i++)
            {
                var (srcEntry, isDirect) = itemsToImport[i];

                EditorUtility.DisplayProgressBar(
                    "Missing Asset Importer",
                    $"コピー中 ({i + 1}/{total}): {Path.GetFileName(srcEntry.RelPath)}",
                    0.45f + 0.4f * ((float)(i + 1) / total));

                string desiredAssetPath = ComputeDesiredDestPath(srcEntry.RelPath);
                string finalAssetPath = ResolveConflictSafePath(desiredAssetPath, srcEntry.Guid, currentProjectRoot, out bool renamed);
                string finalFullPath = Path.Combine(currentProjectRoot, finalAssetPath.Replace('/', Path.DirectorySeparatorChar));

                if (srcEntry.IsDirectory)
                {
                    EnsureParentDirectoriesWithSafeMetas(finalAssetPath, currentProjectRoot, sourceIndex);
                    if (!Directory.Exists(finalFullPath))
                    {
                        Directory.CreateDirectory(finalFullPath);
                        if (File.Exists(srcEntry.MetaFullPath) && !File.Exists(finalFullPath + ".meta"))
                        {
                            CopyFileShared(srcEntry.MetaFullPath, finalFullPath + ".meta");
                        }
                    }
                }
                else
                {
                    string parentAssetDir = Path.GetDirectoryName(finalAssetPath)?.Replace('\\', '/') ?? "Assets";
                    EnsureParentDirectoriesWithSafeMetas(parentAssetDir, currentProjectRoot, sourceIndex);

                    CopyFileShared(srcEntry.FullPath, finalFullPath);
                    if (File.Exists(srcEntry.MetaFullPath))
                    {
                        CopyFileShared(srcEntry.MetaFullPath, finalFullPath + ".meta");
                    }

                    string ext = Path.GetExtension(finalFullPath);
                    if (ScriptExtensions.Contains(ext))
                    {
                        anyScriptImported = true;
                    }
                    if (string.Equals(ext, ".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        allAffectedCurrentPaths.Add(finalAssetPath);
                    }
                }

                allImportedByGuid[srcEntry.Guid] = new ImportedRecord
                {
                    Guid = srcEntry.Guid,
                    DestAssetPath = finalAssetPath,
                    SourceRelPath = srcEntry.RelPath,
                    IsDirect = isDirect,
                    RenamedForConflict = renamed
                };
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }
    }

    private string ComputeDesiredDestPath(string srcRelPath)
    {
        if (_destMode == DestinationMode.KeepOriginalStructure)
        {
            return srcRelPath.Replace('\\', '/');
        }

        string baseFolder = _customDestFolder.Replace('\\', '/').TrimEnd('/');
        string inner = srcRelPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            ? srcRelPath.Substring("Assets/".Length)
            : srcRelPath;

        return $"{baseFolder}/{inner}";
    }

    /// <summary>
    /// 保存先に既に別 GUID の同名ファイルが存在する場合、既存ファイルを破壊しないよう連番パスを生成する。
    /// </summary>
    private static string ResolveConflictSafePath(
        string desiredAssetPath,
        string incomingGuid,
        string currentProjectRoot,
        out bool renamed)
    {
        renamed = false;
        string fullDesired = Path.Combine(currentProjectRoot, desiredAssetPath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(fullDesired) && !Directory.Exists(fullDesired))
        {
            return desiredAssetPath;
        }

        string existingGuid = AssetDatabase.AssetPathToGUID(desiredAssetPath);
        if (string.IsNullOrEmpty(existingGuid) && File.Exists(fullDesired + ".meta"))
        {
            TryReadOwnGuidFromMeta(fullDesired + ".meta", out existingGuid);
        }

        if (string.Equals(existingGuid, incomingGuid, StringComparison.OrdinalIgnoreCase))
        {
            return desiredAssetPath;
        }

        // 別アセットが同名で存在するため、ユニークな別名にして既存アセットの上書きを防止
        renamed = true;
        string dir = Path.GetDirectoryName(desiredAssetPath)?.Replace('\\', '/') ?? "Assets";
        string baseName = Path.GetFileNameWithoutExtension(desiredAssetPath);
        string ext = Path.GetExtension(desiredAssetPath);

        int counter = 1;
        while (true)
        {
            string candidate = $"{dir}/{baseName} ({counter}){ext}";
            string candidateFull = Path.Combine(currentProjectRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(candidateFull) && !Directory.Exists(candidateFull) && !File.Exists(candidateFull + ".meta"))
            {
                return candidate;
            }
            counter++;
        }
    }

    /// <summary>
    /// 親フォルダを順次作成する。新規作成されるフォルダに限り、元プロジェクトのフォルダ GUID が
    /// 競合しなければフォルダの .meta も引き継ぐ（既存フォルダの .meta は絶対に上書きしない）。
    /// </summary>
    private void EnsureParentDirectoriesWithSafeMetas(
        string assetFolderRelPath,
        string currentProjectRoot,
        SourceProjectIndex sourceIndex)
    {
        if (string.IsNullOrEmpty(assetFolderRelPath)) return;

        string[] parts = assetFolderRelPath.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return; // "Assets" のみ

        string accumRel = parts[0]; // "Assets"
        for (int i = 1; i < parts.Length; i++)
        {
            accumRel += "/" + parts[i];
            string fullDir = Path.Combine(currentProjectRoot, accumRel.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(fullDir))
                continue;

            Directory.CreateDirectory(fullDir);

            if (_destMode == DestinationMode.KeepOriginalStructure)
            {
                string srcMetaCandidate = Path.Combine(
                    sourceIndex.ProjectRoot,
                    accumRel.Replace('/', Path.DirectorySeparatorChar)) + ".meta";

                if (File.Exists(srcMetaCandidate) &&
                    TryReadOwnGuidFromMeta(srcMetaCandidate, out string folderGuid) &&
                    !ExistsInCurrentProject(folderGuid, out _) &&
                    !File.Exists(fullDir + ".meta"))
                {
                    CopyFileShared(srcMetaCandidate, fullDir + ".meta");
                }
            }
        }
    }

    private static void ReimportAssetPathsOrdered(IEnumerable<string> paths)
    {
        // 非 Prefab（Material, Controller, FBX 等）を先に、Prefab を後に再インポートして依存解決を確実にする
        var validPaths = paths
            .Where(p => !string.IsNullOrEmpty(p) && p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string p in validPaths)
        {
            string full = Path.GetFullPath(p);
            if (File.Exists(full))
            {
                AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  ユーティリティ
    // ─────────────────────────────────────────────────────────

    private static bool IsBuiltInOrEmptyGuid(string guid)
    {
        if (string.IsNullOrEmpty(guid) || guid.Length != 32) return true;
        // 00000000000000000000000000000000 および Unity 組み込みリソース (0000000000000000e000000000000000 等)
        if (guid.StartsWith("0000000000000000", StringComparison.Ordinal) &&
            guid.EndsWith("000000000000000", StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private static bool ExistsInCurrentProject(string guid, out string existingAssetPath)
    {
        existingAssetPath = null;
        if (IsBuiltInOrEmptyGuid(guid)) return true;

        string path = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(path)) return false;

        if (path.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Library/", StringComparison.OrdinalIgnoreCase))
        {
            existingAssetPath = path;
            return true;
        }

        try
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full) || Directory.Exists(full))
            {
                existingAssetPath = path;
                return true;
            }
        }
        catch
        {
            // 無効なパスは下にフォールバック
        }

        if (path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
        {
            var pkgInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            if (pkgInfo != null && !string.IsNullOrEmpty(pkgInfo.resolvedPath))
            {
                string prefix = "Packages/" + pkgInfo.name;
                string sub = path.Length > prefix.Length ? path.Substring(prefix.Length).TrimStart('/') : "";
                string resolvedTarget = string.IsNullOrEmpty(sub)
                    ? pkgInfo.resolvedPath
                    : Path.Combine(pkgInfo.resolvedPath, sub);

                if (File.Exists(resolvedTarget) || Directory.Exists(resolvedTarget))
                {
                    existingAssetPath = path;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadOwnGuidFromMeta(string metaPath, out string guid)
    {
        guid = null;
        try
        {
            byte[] buffer = new byte[512];
            int read;
            using (var fs = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                read = fs.Read(buffer, 0, buffer.Length);
            }
            if (read <= 0) return false;

            string head = Encoding.UTF8.GetString(buffer, 0, read);
            Match m = MetaOwnGuidRegex.Match(head);
            if (m.Success)
            {
                guid = m.Groups[1].Value.ToLowerInvariant();
                return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static bool IsYamlOrJsonGuidAsset(string fullPath, string ext)
    {
        if (JsonGuidExtensions.Contains(ext)) return true;

        try
        {
            byte[] buf = new byte[32];
            int read;
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                read = fs.Read(buf, 0, buf.Length);
            }
            if (read < 5) return false;

            int offset = 0;
            // UTF-8 BOM スキップ
            if (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF)
                offset = 3;

            if (read - offset >= 5 &&
                buf[offset] == (byte)'%' &&
                buf[offset + 1] == (byte)'Y' &&
                buf[offset + 2] == (byte)'A' &&
                buf[offset + 3] == (byte)'M' &&
                buf[offset + 4] == (byte)'L')
            {
                return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static string ReadAllTextShared(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8, true))
        {
            return sr.ReadToEnd();
        }
    }

    private static void CopyFileShared(string srcPath, string dstPath)
    {
        string dstDir = Path.GetDirectoryName(dstPath);
        if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir))
        {
            Directory.CreateDirectory(dstDir);
        }

        // 既存ファイルが ReadOnly 属性だった場合でも安全に書き込めるようにする
        if (File.Exists(dstPath))
        {
            FileAttributes attrs = File.GetAttributes(dstPath);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(dstPath, attrs & ~FileAttributes.ReadOnly);
            }
        }

        using (var inStream = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var outStream = new FileStream(dstPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            inStream.CopyTo(outStream);
        }
    }

    private static string GetRelativePathNormalized(string basePath, string fullPath)
    {
        string normBase = Path.GetFullPath(basePath).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        string normFull = Path.GetFullPath(fullPath);

        if (normFull.StartsWith(normBase, StringComparison.OrdinalIgnoreCase))
        {
            return normFull.Substring(normBase.Length).Replace('\\', '/');
        }

        Uri baseUri = new Uri(normBase);
        Uri fullUri = new Uri(normFull);
        return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString()).Replace('\\', '/');
    }

    private static string NormalizeFullPathKey(string path)
    {
        return Path.GetFullPath(path).TrimEnd('/', '\\').Replace('\\', '/');
    }

    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(NormalizeFullPathKey(a), NormalizeFullPathKey(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidCustomDestFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        string norm = folder.Replace('\\', '/').Trim('/');
        return string.Equals(norm, "Assets", StringComparison.OrdinalIgnoreCase) ||
               norm.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string msg, MessageType type)
    {
        _statusMessage = msg;
        _statusType = type;
        Repaint();
    }
}
#endif
