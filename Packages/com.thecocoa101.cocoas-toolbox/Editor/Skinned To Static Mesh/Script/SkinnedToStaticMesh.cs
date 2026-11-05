#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Skinned To Static Mesh — Cocoa's Toolbox
/// ボーン回転・移動・スケールや BlendShape で形を作った状態の SkinnedMeshRenderer を、
/// 見た目上の位置・形状を完全に維持したままボーンなしの StaticMesh（MeshFilter + MeshRenderer）へ変換する。
///
/// [主な機能]
/// ・現在のボーン変形＋BlendShape変形をワールド座標経由で正確にベイク（非一様スケール・負スケール対応）
/// ・「複製して生成（非破壊・元を非表示）」と「その場で変換（直接置換）」の切り替え
/// ・指定ボーン（Head / Chest 等）の子へ見た目位置を維持したまま配置する機能
/// ・複数の SkinnedMeshRenderer を 1 つの StaticMesh に結合（同一マテリアルのサブメッシュ自動統合）
/// ・全8チャンネルUV（2D/3D/4D次元維持）・頂点カラー・Renderer設定（Material / ProbeAnchor / Shadow / Layer）引き継ぎ
/// ・EditorWindow および Hierarchy 右クリックメニューの両方に対応
/// </summary>
public class SkinnedToStaticMesh : EditorWindow
{
    // ─────────────────────────────────────────────────────────
    //  定数
    // ─────────────────────────────────────────────────────────

    private const string DEFAULT_FALLBACK_DIR = "Assets/BakedStaticMeshes";
    private const string STATIC_SUFFIX_MARKER = "_Static-";

    // ─────────────────────────────────────────────────────────
    //  データ構造
    // ─────────────────────────────────────────────────────────

    private enum ConvertMode
    {
        Duplicate = 0,
        InPlace = 1
    }

    [Serializable]
    private class BakeResultRecord
    {
        public GameObject OutputObject;
        public string OutputName;
        public string MeshAssetPath;
        public int VertexCount;
        public int TriangleCount;
        public int SubMeshCount;
        public int RemovedBoneCount;
        public int ActiveBlendShapeCount;
    }

    private class PosedMeshData
    {
        public SkinnedMeshRenderer SourceSMR;
        public Mesh SourceMesh;
        public Vector3[] WorldVertices;
        public Vector3[] WorldNormals;   // null if source has no normals
        public Vector4[] WorldTangents;  // null if source has no tangents
        public Color32[] Colors32;       // null if source has no vertex colors
        public UVChannelData[] UVChannels = new UVChannelData[8];
        public List<SubMeshTriangles> SubMeshes = new List<SubMeshTriangles>();
        public float SrcDetSign = 1f;
        public int OriginalBoneCount;
        public int ActiveBlendShapeCount;
    }

    private class UVChannelData
    {
        public int Dimension; // 0 = none, 2 = Vector2, 3 = Vector3, 4 = Vector4
        public List<Vector2> UV2;
        public List<Vector3> UV3;
        public List<Vector4> UV4;
    }

    private class SubMeshTriangles
    {
        public Material Material;
        public MeshTopology Topology;
        public int[] Indices;
    }

    // ─────────────────────────────────────────────────────────
    //  シリアライズ状態（Domain Reload 後も維持）
    // ─────────────────────────────────────────────────────────

    [SerializeField] private bool _useRootMode = true;
    [SerializeField] private GameObject _rootObject;
    [SerializeField] private List<SkinnedMeshRenderer> _smrList = new List<SkinnedMeshRenderer>();
    [SerializeField] private List<bool> _smrEnabled = new List<bool>();

    [SerializeField] private ConvertMode _convertMode = ConvertMode.Duplicate;
    [SerializeField] private bool _hideSourceOnDuplicate = true;
    [SerializeField] private bool _combineMeshes = false;
    [SerializeField] private string _combinedObjectName = "";
    [SerializeField] private Transform _targetParent;

    [SerializeField] private string _fileSavePath = "";

    [SerializeField] private Vector2 _scrollMain;
    [SerializeField] private Vector2 _scrollSMR;
    [SerializeField] private bool _foldResults = true;
    [SerializeField] private List<BakeResultRecord> _results = new List<BakeResultRecord>();

    [SerializeField] private string _statusMessage = "";
    [SerializeField] private MessageType _statusType = MessageType.Info;

    // ─────────────────────────────────────────────────────────
    //  GUIStyle キャッシュ
    // ─────────────────────────────────────────────────────────

    private GUIStyle _executeButtonStyle;
    private GUIStyle _badgeStyle;
    private GUIStyle _rowButtonStyle;
    private GUIStyle _wrapMiniStyle;

    private GUIStyle ExecuteButtonStyle => _executeButtonStyle ?? (_executeButtonStyle = new GUIStyle(GUI.skin.button)
    {
        fontSize = 13,
        fontStyle = FontStyle.Bold
    });

    private GUIStyle BadgeStyle => _badgeStyle ?? (_badgeStyle = new GUIStyle(EditorStyles.miniLabel)
    {
        alignment = TextAnchor.MiddleRight
    });

    private GUIStyle RowButtonStyle => _rowButtonStyle ?? (_rowButtonStyle = new GUIStyle(EditorStyles.label)
    {
        clipping = TextClipping.Clip,
        wordWrap = false
    });

    private GUIStyle WrapMiniStyle => _wrapMiniStyle ?? (_wrapMiniStyle = new GUIStyle(EditorStyles.miniLabel)
    {
        wordWrap = true
    });

    // ─────────────────────────────────────────────────────────
    //  メニュー登録（Tools メニュー / Hierarchy 右クリック / Inspector コンテキスト）
    // ─────────────────────────────────────────────────────────

    [MenuItem("Tools/Cocoa's Toolbox/Skinned To Static Mesh (Experimental)")]
    public static void Open()
    {
        var win = GetWindow<SkinnedToStaticMesh>(false, "Skinned To Static (Experimental)", true);
        win.minSize = new Vector2(420f, 480f);
        win.Show();
    }

    [MenuItem("GameObject/Cocoa's Toolbox/Skinned To Static Mesh (Experimental)", false, 20)]
    private static void OpenFromHierarchyContext(MenuCommand command)
    {
        var selectedGOs = Selection.gameObjects;
        if (command.context != null && selectedGOs.Length > 1 && command.context != selectedGOs[0])
            return;

        var targets = GetTargetGameObjectsFromContext(command);
        var win = GetWindow<SkinnedToStaticMesh>(false, "Skinned To Static (Experimental)", true);
        win.minSize = new Vector2(420f, 480f);
        win.Show();
        win.Focus();

        if (targets.Count == 1)
        {
            win.SetRootTarget(targets[0]);
        }
        else if (targets.Count > 1)
        {
            win.SetIndividualTargetsFromGameObjects(targets);
        }
    }

    [MenuItem("GameObject/Cocoa's Toolbox/Skinned To Static Mesh（クイック複製変換） (Experimental)", false, 21)]
    private static void QuickBakeFromHierarchyContext(MenuCommand command)
    {
        var selectedGOs = Selection.gameObjects;
        if (command.context != null && selectedGOs.Length > 1 && command.context != selectedGOs[0])
            return;

        var targets = GetTargetGameObjectsFromContext(command);
        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "Skinned To Static Mesh (Experimental)",
                "変換対象の GameObject が選択されていません。",
                "OK");
            return;
        }

        // Scene 上のオブジェクトのみ対象
        var sceneTargets = targets.Where(go => go != null && go.scene.IsValid()).ToList();
        if (sceneTargets.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "Skinned To Static Mesh (Experimental)",
                "Project 内の Prefab アセットではなく、Hierarchy（Scene）上のオブジェクトを選択してください。",
                "OK");
            return;
        }

        var smrs = new List<SkinnedMeshRenderer>();
        foreach (var go in sceneTargets)
        {
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr != null && smr.sharedMesh != null && !smrs.Contains(smr))
                {
                    if (IsActiveRelativeToRoot(smr.transform, go.transform) && smr.enabled)
                        smrs.Add(smr);
                }
            }
        }

        // 相対アクティブなものが0件なら全SMRを対象にする
        if (smrs.Count == 0)
        {
            foreach (var go in sceneTargets)
            {
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr != null && smr.sharedMesh != null && !smrs.Contains(smr))
                        smrs.Add(smr);
                }
            }
        }

        if (smrs.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "Skinned To Static Mesh (Experimental)",
                "選択されたオブジェクト（またはその子階層）に有効な SkinnedMeshRenderer が見つかりませんでした。",
                "OK");
            return;
        }

        GameObject singleRoot = sceneTargets.Count == 1 ? sceneTargets[0] : null;
        var records = ExecuteBakeInternal(
            smrs,
            singleRoot,
            ConvertMode.Duplicate,
            hideSourceOnDuplicate: true,
            combineMeshes: false,
            combinedObjectName: "",
            targetParent: null,
            customSaveDir: "",
            out List<string> errors);

        if (records.Count > 0)
        {
            var createdObjs = records
                .Where(r => r.OutputObject != null)
                .Select(r => (UnityEngine.Object)r.OutputObject)
                .Distinct()
                .ToArray();

            if (createdObjs.Length > 0)
            {
                Selection.objects = createdObjs;
                EditorGUIUtility.PingObject(createdObjs[0]);
            }

            Debug.Log($"[SkinnedToStaticMesh] クイック変換完了: {records.Count} 件の StaticMesh を生成しました。");
        }

        if (errors.Count > 0)
        {
            Debug.LogWarning("[SkinnedToStaticMesh] 一部エラー:\n" + string.Join("\n", errors));
        }
    }

    [MenuItem("CONTEXT/SkinnedMeshRenderer/Skinned To Static Mesh (Experimental)")]
    private static void OpenFromSMRContext(MenuCommand command)
    {
        var smr = command.context as SkinnedMeshRenderer;
        var win = GetWindow<SkinnedToStaticMesh>(false, "Skinned To Static (Experimental)", true);
        win.minSize = new Vector2(420f, 480f);
        win.Show();
        win.Focus();

        if (smr != null)
        {
            win.SetSingleSMRTarget(smr);
        }
    }

    private static List<GameObject> GetTargetGameObjectsFromContext(MenuCommand command)
    {
        var result = new List<GameObject>();
        if (Selection.gameObjects != null && Selection.gameObjects.Length > 0)
        {
            result.AddRange(Selection.gameObjects.Where(g => g != null));
        }
        else if (command.context is GameObject ctxGo && ctxGo != null)
        {
            result.Add(ctxGo);
        }
        return result;
    }

    private void OnEnable()
    {
        minSize = new Vector2(420f, 480f);
        SyncEnabledListLength();
    }

    // ─────────────────────────────────────────────────────────
    //  外部からのターゲットセット用メソッド
    // ─────────────────────────────────────────────────────────

    private void SetRootTarget(GameObject root)
    {
        _useRootMode = true;
        _rootObject = root;
        CollectSMRsFromRoot();
        Repaint();
    }

    private void SetIndividualTargetsFromGameObjects(List<GameObject> gos)
    {
        _useRootMode = false;
        _smrList.Clear();
        _smrEnabled.Clear();

        foreach (var go in gos)
        {
            if (go == null) continue;
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr != null && !_smrList.Contains(smr))
                {
                    _smrList.Add(smr);
                    _smrEnabled.Add(true);
                }
            }
        }

        SetStatus($"{_smrList.Count} 件の SkinnedMeshRenderer をセットしました。", MessageType.Info);
    }

    private void SetSingleSMRTarget(SkinnedMeshRenderer smr)
    {
        _useRootMode = false;
        _smrList.Clear();
        _smrEnabled.Clear();
        if (smr != null)
        {
            _smrList.Add(smr);
            _smrEnabled.Add(true);
            SetStatus($"「{smr.name}」をセットしました。", MessageType.Info);
        }
        Repaint();
    }

    // ─────────────────────────────────────────────────────────
    //  OnGUI
    // ─────────────────────────────────────────────────────────

    private void OnGUI()
    {
        _scrollMain = EditorGUILayout.BeginScrollView(_scrollMain);

        DrawHeader();
        EditorGUILayout.Space(6f);

        DrawTargetSection();
        EditorGUILayout.Space(8f);

        DrawOptionsSection();
        EditorGUILayout.Space(8f);

        DrawSaveSection();
        EditorGUILayout.Space(10f);

        bool triggerExecute = DrawExecuteButtonSection();

        EditorGUILayout.Space(6f);
        DrawResultsSection();

        EditorGUILayout.EndScrollView();

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox(_statusMessage, _statusType);
            EditorGUILayout.Space(4f);
        }

        if (triggerExecute)
        {
            ExecuteBakeFromUI();
            GUIUtility.ExitGUI();
        }
    }

    // ─────────────────────────────────────────────────────────
    //  UI セクション描画
    // ─────────────────────────────────────────────────────────

    private void DrawHeader()
    {
        EditorGUILayout.LabelField("Skinned To Static Mesh (Experimental)", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Cocoa's Toolbox", EditorStyles.miniLabel);
        var rect = GUILayoutUtility.GetRect(0f, 1f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, EditorStyles.label.normal.textColor * 0.3f);
    }

    private void DrawTargetSection()
    {
        EditorGUILayout.LabelField("① 対象メッシュ (SkinnedMeshRenderer)", EditorStyles.boldLabel);

        using (new EditorGUI.IndentLevelScope())
        {
            int mode = _useRootMode ? 0 : 1;
            int nextMode = GUILayout.Toolbar(mode, new[] { "GameObject から自動収集", "SMR を個別に選択" });
            if (nextMode != mode)
            {
                _useRootMode = (nextMode == 0);
                if (_useRootMode && _rootObject != null)
                    CollectSMRsFromRoot();
            }

            EditorGUILayout.Space(3f);

            if (_useRootMode)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    _rootObject = (GameObject)EditorGUILayout.ObjectField(
                        "Root GameObject", _rootObject, typeof(GameObject), true);
                    if (EditorGUI.EndChangeCheck())
                    {
                        CollectSMRsFromRoot();
                    }

                    using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
                    {
                        if (GUILayout.Button("選択中", EditorStyles.miniButton, GUILayout.Width(52f)))
                        {
                            _rootObject = Selection.activeGameObject;
                            CollectSMRsFromRoot();
                        }
                    }
                }

                if (_rootObject == null)
                {
                    EditorGUILayout.HelpBox(
                        "ボーンを曲げる・BlendShapeを調整するなどして形を作った GameObject をセットしてください。\n" +
                        "配下の SkinnedMeshRenderer を自動収集します。",
                        MessageType.None);
                }
                else if (!_rootObject.scene.IsValid())
                {
                    EditorGUILayout.HelpBox(
                        "Project 内の Prefab アセットが指定されています。\n" +
                        "ボーン変形を正しく焼き込むため、Scene（Hierarchy）上に配置したオブジェクトを指定してください。",
                        MessageType.Warning);
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("+ 枠を追加", EditorStyles.miniButton, GUILayout.Width(80f)))
                    {
                        _smrList.Add(null);
                        _smrEnabled.Add(true);
                    }

                    if (GUILayout.Button("選択中を追加", EditorStyles.miniButton, GUILayout.Width(92f)))
                    {
                        AddSelectedSMRs();
                    }

                    GUILayout.FlexibleSpace();

                    if (_smrList.Count > 0 && GUILayout.Button("クリア", EditorStyles.miniButton, GUILayout.Width(52f)))
                    {
                        _smrList.Clear();
                        _smrEnabled.Clear();
                    }
                }

                if (_smrList.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "「+ 枠を追加」または Hierarchy でオブジェクトを選択して「選択中を追加」を押してください。",
                        MessageType.None);
                }
            }

            DrawSMRList();
        }
    }

    private void DrawSMRList()
    {
        SyncEnabledListLength();
        if (_smrList.Count == 0) return;

        EditorGUILayout.Space(3f);

        using (new EditorGUILayout.HorizontalScope())
        {
            int activeCount = GetActiveSMRs().Count;
            EditorGUILayout.LabelField(
                $"有効: {activeCount} / {_smrList.Count} SMR",
                EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            if (_smrList.Count > 1)
            {
                if (GUILayout.Button("全 ON", EditorStyles.miniButtonLeft, GUILayout.Width(48f)))
                    SetAllSMREnabled(true);
                if (GUILayout.Button("全 OFF", EditorStyles.miniButtonRight, GUILayout.Width(52f)))
                    SetAllSMREnabled(false);
            }

            if (_useRootMode && GUILayout.Button("再収集", EditorStyles.miniButton, GUILayout.Width(52f)))
            {
                CollectSMRsFromRoot();
            }
        }

        float maxListHeight = Mathf.Min(_smrList.Count * 22f + 8f, 150f);
        using (var scroll = new EditorGUILayout.ScrollViewScope(_scrollSMR, "box", GUILayout.Height(maxListHeight)))
        {
            _scrollSMR = scroll.scrollPosition;

            int prevIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            for (int i = 0; i < _smrList.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _smrEnabled[i] = EditorGUILayout.Toggle(_smrEnabled[i], GUILayout.Width(16f));

                    if (_useRootMode)
                    {
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUILayout.ObjectField(
                                _smrList[i], typeof(SkinnedMeshRenderer), true,
                                GUILayout.MinWidth(80f), GUILayout.ExpandWidth(true));
                        }
                    }
                    else
                    {
                        _smrList[i] = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                            _smrList[i], typeof(SkinnedMeshRenderer), true,
                            GUILayout.MinWidth(80f), GUILayout.ExpandWidth(true));
                    }

                    string summary = BuildSMRRowBadge(_smrList[i]);
                    if (!string.IsNullOrEmpty(summary))
                    {
                        GUILayout.Label(summary, BadgeStyle, GUILayout.Width(148f));
                    }

                    if (!_useRootMode)
                    {
                        if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22f)))
                        {
                            _smrList.RemoveAt(i);
                            _smrEnabled.RemoveAt(i);
                            EditorGUI.indentLevel = prevIndent;
                            break;
                        }
                    }
                }
            }

            EditorGUI.indentLevel = prevIndent;
        }
    }

    private string BuildSMRRowBadge(SkinnedMeshRenderer smr)
    {
        if (smr == null) return "未設定";
        if (!smr.gameObject.scene.IsValid()) return "[Asset不可]";
        if (smr.sharedMesh == null) return "[Meshなし]";

        int boneCount = CountValidBones(smr);
        int activeBS = CountActiveBlendShapes(smr);
        string stateTag = (!smr.gameObject.activeInHierarchy || !smr.enabled) ? "[非表示] " : "";
        return $"{stateTag}Bone:{boneCount} / BS:{activeBS}";
    }

    private void DrawOptionsSection()
    {
        EditorGUILayout.LabelField("② 変換・配置オプション", EditorStyles.boldLabel);

        using (new EditorGUI.IndentLevelScope())
        {
            // 1. 変換モード
            EditorGUILayout.LabelField("元オブジェクトの扱い", EditorStyles.miniBoldLabel);
            _convertMode = (ConvertMode)GUILayout.Toolbar(
                (int)_convertMode,
                new[] { "複製して生成（非破壊）", "その場で変換（直接置換）" });

            if (_convertMode == ConvertMode.Duplicate)
            {
                _hideSourceOnDuplicate = EditorGUILayout.ToggleLeft(
                    "変換後に元のオブジェクトを非表示にする",
                    _hideSourceOnDuplicate);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "対象の SkinnedMeshRenderer を直接 MeshFilter + MeshRenderer に差し替えます（Ctrl+Z で Undo 可能）。",
                    MessageType.None);
            }

            EditorGUILayout.Space(4f);

            // 2. メッシュ結合オプション
            _combineMeshes = EditorGUILayout.ToggleLeft(
                "有効な複数の SkinnedMesh を 1 つの StaticMesh に結合する（同一マテリアル統合）",
                _combineMeshes);

            if (_combineMeshes)
            {
                string defaultName = SuggestCombinedBaseName() + "_Static";
                string nextName = EditorGUILayout.TextField("結合オブジェクト名", _combinedObjectName);
                _combinedObjectName = nextName;
                if (string.IsNullOrWhiteSpace(_combinedObjectName))
                {
                    EditorGUILayout.LabelField($"※ 空欄時は自動で「{defaultName}」になります", EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.Space(4f);

            // 3. 追従ボーン / 親 Transform 指定
            using (new EditorGUILayout.HorizontalScope())
            {
                _targetParent = (Transform)EditorGUILayout.ObjectField(
                    "配置先（親ボーン等）", _targetParent, typeof(Transform), true);

                if (_targetParent != null && GUILayout.Button("クリア", EditorStyles.miniButton, GUILayout.Width(50f)))
                {
                    _targetParent = null;
                }
            }

            if (_targetParent == null)
            {
                EditorGUILayout.LabelField(
                    "未指定：現在の階層位置にそのまま配置します（Head や Chest ボーンを入れるとその子に追従配置できます）",
                    WrapMiniStyle);
            }
            else if (!_targetParent.gameObject.scene.IsValid())
            {
                EditorGUILayout.HelpBox(
                    "配置先には Scene（Hierarchy）上のボーン・Transform を指定してください。",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField(
                    $"✓ シーン上の見た目位置・角度・スケールを完全に維持したまま「{_targetParent.name}」の子へ配置します。",
                    WrapMiniStyle);
            }
        }
    }

    private void DrawSaveSection()
    {
        EditorGUILayout.LabelField("③ .mesh アセット保存先", EditorStyles.boldLabel);

        using (new EditorGUI.IndentLevelScope())
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string displayPath = string.IsNullOrEmpty(_fileSavePath)
                    ? "（元ファイルと同じフォルダ）"
                    : _fileSavePath;

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("保存先フォルダ", displayPath);
                }

                if (GUILayout.Button("…", GUILayout.Width(28f)))
                {
                    string initial = string.IsNullOrEmpty(_fileSavePath) ? Application.dataPath : _fileSavePath;
                    string abs = EditorUtility.OpenFolderPanel("保存先フォルダを選択", initial, "");
                    if (!string.IsNullOrEmpty(abs))
                    {
                        string dataPath = Application.dataPath.Replace('\\', '/');
                        string normAbs = abs.Replace('\\', '/');
                        if (normAbs.Equals(dataPath, StringComparison.OrdinalIgnoreCase))
                        {
                            _fileSavePath = "Assets";
                        }
                        else if (normAbs.StartsWith(dataPath + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            _fileSavePath = "Assets/" + normAbs.Substring(dataPath.Length + 1);
                        }
                        else
                        {
                            SetStatus("保存先は現在のプロジェクトの Assets フォルダ内を指定してください。", MessageType.Warning);
                        }
                        GUIUtility.ExitGUI();
                    }
                }

                if (!string.IsNullOrEmpty(_fileSavePath) && GUILayout.Button("戻す", EditorStyles.miniButton, GUILayout.Width(42f)))
                {
                    _fileSavePath = "";
                }
            }

            EditorGUILayout.LabelField(
                "ボーンウェイト・バインドポーズを除去し、現在の変形形状を焼き込んだ .mesh アセット（例: 元名_Static-000.mesh）を保存します。",
                WrapMiniStyle);
        }
    }

    private bool DrawExecuteButtonSection()
    {
        var actives = GetActiveSMRs();
        bool hasInvalidSceneObj = actives.Any(s => !s.gameObject.scene.IsValid());
        bool hasInvalidParent = _targetParent != null && !_targetParent.gameObject.scene.IsValid();
        bool canExecute = actives.Count > 0 && !hasInvalidSceneObj && !hasInvalidParent && !EditorApplication.isCompiling;

        bool clicked = false;
        using (new EditorGUI.DisabledScope(!canExecute))
        {
            string btnLabel = _combineMeshes && actives.Count > 1
                ? $"✦  {actives.Count} 件を結合して StaticMesh 化  ✦"
                : "✦  StaticMesh 化を実行  ✦";

            if (GUILayout.Button(btnLabel, ExecuteButtonStyle, GUILayout.Height(34f)))
            {
                clicked = true;
            }
        }

        return clicked;
    }

    private void DrawResultsSection()
    {
        if (_results == null || _results.Count == 0) return;

        _foldResults = EditorGUILayout.Foldout(_foldResults, $"変換結果 ({_results.Count} 件)", true);
        if (!_foldResults) return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            foreach (var rec in _results)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Texture goIcon = EditorGUIUtility.ObjectContent(rec.OutputObject, typeof(GameObject)).image;
                    string title = rec.OutputObject != null ? rec.OutputObject.name : rec.OutputName;
                    string tooltip =
                        $"Mesh: {rec.MeshAssetPath}\n" +
                        $"頂点数: {rec.VertexCount:N0} / 三角面: {rec.TriangleCount:N0} / SubMesh: {rec.SubMeshCount}\n" +
                        $"解除したボーン数: {rec.RemovedBoneCount} / 焼き込みBlendShape数: {rec.ActiveBlendShapeCount}";

                    if (GUILayout.Button(new GUIContent(" " + title, goIcon, tooltip),
                        RowButtonStyle, GUILayout.MinWidth(60f), GUILayout.ExpandWidth(true), GUILayout.Height(20f)))
                    {
                        if (rec.OutputObject != null)
                        {
                            EditorGUIUtility.PingObject(rec.OutputObject);
                            Selection.activeGameObject = rec.OutputObject;
                        }
                    }

                    GUILayout.Label(
                        $"{rec.TriangleCount:N0} tris / Bone {rec.RemovedBoneCount}→0",
                        BadgeStyle, GUILayout.Width(138f));

                    if (!string.IsNullOrEmpty(rec.MeshAssetPath) &&
                        GUILayout.Button(".mesh", EditorStyles.miniButton, GUILayout.Width(46f)))
                    {
                        var meshAsset = AssetDatabase.LoadAssetAtPath<Mesh>(rec.MeshAssetPath);
                        if (meshAsset != null)
                        {
                            EditorGUIUtility.PingObject(meshAsset);
                            Selection.activeObject = meshAsset;
                        }
                    }
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  UI アクション・収集ヘルパー
    // ─────────────────────────────────────────────────────────

    private void CollectSMRsFromRoot()
    {
        _smrList.Clear();
        _smrEnabled.Clear();

        if (_rootObject == null)
        {
            Repaint();
            return;
        }

        var found = _rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        bool anyRelActive = found.Any(s => s != null && s.sharedMesh != null && IsActiveRelativeToRoot(s.transform, _rootObject.transform) && s.enabled);

        foreach (var smr in found)
        {
            if (smr == null) continue;
            _smrList.Add(smr);
            bool defaultOn = smr.sharedMesh != null && (!anyRelActive || (IsActiveRelativeToRoot(smr.transform, _rootObject.transform) && smr.enabled));
            _smrEnabled.Add(defaultOn);
        }

        SetStatus($"{_smrList.Count} 件の SkinnedMeshRenderer を検出しました。", MessageType.Info);
    }

    private void AddSelectedSMRs()
    {
        int added = 0;
        foreach (var go in Selection.gameObjects)
        {
            if (go == null) continue;
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr != null && !_smrList.Contains(smr))
                {
                    _smrList.Add(smr);
                    _smrEnabled.Add(smr.sharedMesh != null);
                    added++;
                }
            }
        }

        if (added > 0)
            SetStatus($"{added} 件の SkinnedMeshRenderer を追加しました。", MessageType.Info);
        else
            SetStatus("選択中のオブジェクトから新規追加できる SkinnedMeshRenderer はありませんでした。", MessageType.Info);
    }

    private static bool IsActiveRelativeToRoot(Transform target, Transform root)
    {
        Transform cur = target;
        while (cur != null)
        {
            if (!cur.gameObject.activeSelf)
            {
                // root 自体が非アクティブな場合のみ許容（配下で個別にOFFにされているものは除外）
                if (cur == root) return true;
                return false;
            }
            if (cur == root) break;
            cur = cur.parent;
        }
        return true;
    }

    private void SyncEnabledListLength()
    {
        while (_smrEnabled.Count < _smrList.Count) _smrEnabled.Add(true);
        while (_smrEnabled.Count > _smrList.Count) _smrEnabled.RemoveAt(_smrEnabled.Count - 1);
    }

    private void SetAllSMREnabled(bool value)
    {
        SyncEnabledListLength();
        for (int i = 0; i < _smrEnabled.Count; i++)
            _smrEnabled[i] = value;
    }

    private List<SkinnedMeshRenderer> GetActiveSMRs()
    {
        SyncEnabledListLength();
        var list = new List<SkinnedMeshRenderer>();
        for (int i = 0; i < _smrList.Count; i++)
        {
            var smr = _smrList[i];
            if (smr != null && smr.sharedMesh != null && _smrEnabled[i])
                list.Add(smr);
        }
        return list;
    }

    private string SuggestCombinedBaseName()
    {
        if (_useRootMode && _rootObject != null)
            return StripStaticSuffix(_rootObject.name);

        var actives = GetActiveSMRs();
        if (actives.Count > 0)
            return StripStaticSuffix(actives[0].name);

        return "CombinedMesh";
    }

    // ─────────────────────────────────────────────────────────
    //  メイン変換処理
    // ─────────────────────────────────────────────────────────

    private void ExecuteBakeFromUI()
    {
        var actives = GetActiveSMRs();
        if (actives.Count == 0)
        {
            SetStatus("有効な SkinnedMeshRenderer が選択されていません。", MessageType.Warning);
            return;
        }

        // 「その場で変換」の場合、Prefab インスタンス内の構造変更が必要になるため事前チェック
        if (_convertMode == ConvertMode.InPlace)
        {
            if (!EnsurePrefabInstancesUnpackedForInPlace(actives, _targetParent))
            {
                SetStatus("変換をキャンセルしました（Prefab を維持したい場合は「複製して生成」をご利用ください）。", MessageType.Info);
                return;
            }
        }

        GameObject rootContext = (_useRootMode && _rootObject != null) ? _rootObject : null;
        _results = ExecuteBakeInternal(
            actives,
            rootContext,
            _convertMode,
            _hideSourceOnDuplicate,
            _combineMeshes,
            _combinedObjectName,
            _targetParent,
            _fileSavePath,
            out List<string> errors);

        _foldResults = _results.Count > 0;

        // 「その場で変換」後は元の SMR が消えるためリストを更新
        if (_convertMode == ConvertMode.InPlace)
        {
            if (_useRootMode && _rootObject != null)
                CollectSMRsFromRoot();
            else
                _smrList.RemoveAll(s => s == null);
            SyncEnabledListLength();
        }

        if (_results.Count > 0 && errors.Count == 0)
        {
            int totalTris = _results.Sum(r => r.TriangleCount);
            SetStatus(
                $"{_results.Count} 件の StaticMesh 化が完了しました！（計 {totalTris:N0} tris / ボーン依存なし）",
                MessageType.Info);
        }
        else if (_results.Count > 0 && errors.Count > 0)
        {
            SetStatus(
                $"{_results.Count} 件成功 / {errors.Count} 件エラー:\n" + string.Join("\n", errors),
                MessageType.Warning);
        }
        else
        {
            SetStatus(
                "StaticMesh 化に失敗しました:\n" + string.Join("\n", errors),
                MessageType.Error);
        }
    }

    /// <summary>
    /// 「その場で変換」時に Prefab インスタンスのコンポーネント削除エラーを防ぐため、必要に応じて Unpack を行う。
    /// </summary>
    private static bool EnsurePrefabInstancesUnpackedForInPlace(List<SkinnedMeshRenderer> actives, Transform targetParent)
    {
        var rootsToUnpack = new HashSet<GameObject>();
        foreach (var smr in actives)
        {
            if (smr == null) continue;
            bool smrFromPrefab = PrefabUtility.IsPartOfPrefabInstance(smr.gameObject) && !PrefabUtility.IsAddedComponentOverride(smr);
            bool needReparentOut = targetParent != null && smr.transform.parent != targetParent && PrefabUtility.IsPartOfPrefabInstance(smr.gameObject);

            if (smrFromPrefab || needReparentOut)
            {
                var nearest = PrefabUtility.GetNearestPrefabInstanceRoot(smr.gameObject);
                if (nearest != null)
                    rootsToUnpack.Add(nearest);
            }
        }

        if (rootsToUnpack.Count == 0)
            return true;

        string names = string.Join(", ", rootsToUnpack.Select(r => $"「{r.name}」"));
        bool ok = EditorUtility.DisplayDialog(
            "Prefab の Unpack 確認",
            $"「その場で変換」を行うには、Prefab インスタンス {names} を Unpack する必要があります。\n\n" +
            "Unpack して変換を続行しますか？\n" +
            "（※「複製して生成」モードなら Unpack 不要でそのまま変換できます）",
            "Unpack して変換",
            "キャンセル");

        if (!ok)
            return false;

        Undo.SetCurrentGroupName("Unpack For Skinned To Static Mesh");
        foreach (var smr in actives)
        {
            if (smr == null) continue;
            while (PrefabUtility.IsPartOfPrefabInstance(smr.gameObject))
            {
                var nearest = PrefabUtility.GetNearestPrefabInstanceRoot(smr.gameObject);
                if (nearest == null) break;
                PrefabUtility.UnpackPrefabInstance(nearest, PrefabUnpackMode.OutermostRoot, InteractionMode.UserAction);
            }
        }

        return true;
    }

    private static List<BakeResultRecord> ExecuteBakeInternal(
        List<SkinnedMeshRenderer> actives,
        GameObject rootContext,
        ConvertMode convertMode,
        bool hideSourceOnDuplicate,
        bool combineMeshes,
        string combinedObjectName,
        Transform targetParent,
        string customSaveDir,
        out List<string> errors)
    {
        errors = new List<string>();
        var results = new List<BakeResultRecord>();

        if (actives == null || actives.Count == 0)
        {
            errors.Add("変換対象の SkinnedMeshRenderer がありません。");
            return results;
        }

        // もし targetParent が「その場で変換」で消える他オブジェクトだった場合の保護
        if (combineMeshes && convertMode == ConvertMode.InPlace && targetParent != null)
        {
            for (int i = 1; i < actives.Count; i++)
            {
                if (actives[i] != null && targetParent == actives[i].transform)
                {
                    errors.Add("結合で削除対象となるサブメッシュ自身を配置先に指定することはできません。");
                    return results;
                }
            }
        }

        Undo.SetCurrentGroupName("Skinned To Static Mesh");
        int undoGroup = Undo.GetCurrentGroup();

        try
        {
            // Step 1: まず元のボーン・階層を変形させる前に、全対象SMRの現在ポーズをワールド空間でスナップショット取得
            var posedList = new List<PosedMeshData>();
            foreach (var smr in actives)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                try
                {
                    var posed = CapturePosedMeshInWorldSpace(smr);
                    if (posed != null)
                        posedList.Add(posed);
                }
                catch (Exception ex)
                {
                    errors.Add($"{smr.name}: メッシュ変形取得エラー ({ex.Message})");
                    Debug.LogException(ex);
                }
            }

            if (posedList.Count == 0)
            {
                if (errors.Count == 0)
                    errors.Add("有効な頂点データを持つメッシュが見つかりませんでした。");
                return results;
            }

            // Step 2: 結合モード or 個別モードで StaticMesh アセット保存＆GameObject構築
            if (combineMeshes)
            {
                var rec = BuildCombinedStaticMesh(
                    posedList,
                    rootContext,
                    convertMode,
                    hideSourceOnDuplicate,
                    combinedObjectName,
                    targetParent,
                    customSaveDir,
                    errors);

                if (rec != null)
                    results.Add(rec);
            }
            else
            {
                BuildIndividualStaticMeshes(
                    posedList,
                    rootContext,
                    convertMode,
                    hideSourceOnDuplicate,
                    targetParent,
                    customSaveDir,
                    results,
                    errors);
            }

            AssetDatabase.SaveAssets();
            SceneView.RepaintAll();
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }

        return results;
    }

    // ─────────────────────────────────────────────────────────
    //  個別変換の構築
    // ─────────────────────────────────────────────────────────

    private static void BuildIndividualStaticMeshes(
        List<PosedMeshData> posedList,
        GameObject rootContext,
        ConvertMode convertMode,
        bool hideSourceOnDuplicate,
        Transform targetParent,
        string customSaveDir,
        List<BakeResultRecord> results,
        List<string> errors)
    {
        // 複製モードで複数SMRがあり、かつ rootContext がある場合（そして rootContext 自体が単一SMR自身ではない場合）は
        // 親グループ GameObject でまとめると Hierarchy が散らからない
        Transform groupContainer = null;
        if (convertMode == ConvertMode.Duplicate && posedList.Count > 1 && rootContext != null)
        {
            string groupName = StripStaticSuffix(rootContext.name) + "_Static";
            var groupGo = new GameObject(groupName);
            Undo.RegisterCreatedObjectUndo(groupGo, "Create Static Mesh Group");
            groupGo.layer = rootContext.layer;

            if (targetParent != null)
            {
                groupGo.transform.SetParent(targetParent, false);
                groupGo.transform.localPosition = Vector3.zero;
                groupGo.transform.localRotation = Quaternion.identity;
                groupGo.transform.localScale = Vector3.one;
            }
            else
            {
                Transform refParent = rootContext.transform.parent;
                groupGo.transform.SetParent(refParent, false);
                groupGo.transform.SetSiblingIndex(rootContext.transform.GetSiblingIndex() + 1);
                groupGo.transform.position = rootContext.transform.position;
                groupGo.transform.rotation = rootContext.transform.rotation;
                groupGo.transform.localScale = Vector3.one;
            }

            groupContainer = groupGo.transform;
        }

        var createdOutputs = new List<Transform>();

        foreach (var posed in posedList)
        {
            var smr = posed.SourceSMR;
            if (smr == null) continue;

            try
            {
                if (convertMode == ConvertMode.Duplicate)
                {
                    string objName = StripStaticSuffix(smr.gameObject.name) + "_Static";
                    var outGo = new GameObject(objName);
                    Undo.RegisterCreatedObjectUndo(outGo, "Create Static Mesh: " + objName);
                    CopyGameObjectMetadata(smr.gameObject, outGo);

                    if (groupContainer != null)
                    {
                        outGo.transform.SetParent(groupContainer, false);
                        outGo.transform.localPosition = Vector3.zero;
                        outGo.transform.localRotation = Quaternion.identity;
                        outGo.transform.localScale = Vector3.one;
                    }
                    else if (targetParent != null)
                    {
                        outGo.transform.SetParent(targetParent, false);
                        outGo.transform.localPosition = Vector3.zero;
                        outGo.transform.localRotation = Quaternion.identity;
                        outGo.transform.localScale = Vector3.one;
                    }
                    else
                    {
                        // rootContext が単一オブジェクト（プレハブルートの子に1つだけSMRがある場合など）は
                        // rootContext と同じ階層に置かないと、rootContext 非表示時に一緒に消えてしまうため配慮
                        Transform anchorTransform = (rootContext != null && CanDeactivateWholeRoot(rootContext, posedList))
                            ? rootContext.transform
                            : smr.transform;

                        outGo.transform.SetParent(anchorTransform.parent, false);
                        outGo.transform.SetSiblingIndex(anchorTransform.GetSiblingIndex() + 1);
                        outGo.transform.position = smr.transform.position;
                        outGo.transform.rotation = smr.transform.rotation;
                        outGo.transform.localScale = Vector3.one;
                    }

                    Mesh staticMesh = BuildMeshInTargetSpace(new List<PosedMeshData> { posed }, outGo.transform, mergeByMaterial: false, out Material[] outMaterials, out int triCount);
                    string savedPath = SaveMeshAsset(staticMesh, posed.SourceMesh, smr.name, customSaveDir);
                    Mesh loadedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(savedPath);

                    var mf = outGo.AddComponent<MeshFilter>();
                    mf.sharedMesh = loadedMesh;

                    var mr = outGo.AddComponent<MeshRenderer>();
                    CopyRendererSettings(smr, mr, outMaterials);

                    EditorUtility.SetDirty(outGo);
                    createdOutputs.Add(outGo.transform);

                    results.Add(new BakeResultRecord
                    {
                        OutputObject = outGo,
                        OutputName = outGo.name,
                        MeshAssetPath = savedPath,
                        VertexCount = loadedMesh.vertexCount,
                        TriangleCount = triCount,
                        SubMeshCount = loadedMesh.subMeshCount,
                        RemovedBoneCount = posed.OriginalBoneCount,
                        ActiveBlendShapeCount = posed.ActiveBlendShapeCount
                    });
                }
                else
                {
                    // その場で変換（InPlace）
                    GameObject go = smr.gameObject;
                    if (targetParent != null && go.transform.parent != targetParent)
                    {
                        Undo.SetTransformParent(go.transform, targetParent, true, "Reparent Static Mesh");
                    }

                    Mesh staticMesh = BuildMeshInTargetSpace(new List<PosedMeshData> { posed }, go.transform, mergeByMaterial: false, out Material[] outMaterials, out int triCount);
                    string savedPath = SaveMeshAsset(staticMesh, posed.SourceMesh, smr.name, customSaveDir);
                    Mesh loadedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(savedPath);

                    ReplaceSMRWithStaticComponents(smr, loadedMesh, outMaterials);

                    results.Add(new BakeResultRecord
                    {
                        OutputObject = go,
                        OutputName = go.name,
                        MeshAssetPath = savedPath,
                        VertexCount = loadedMesh.vertexCount,
                        TriangleCount = triCount,
                        SubMeshCount = loadedMesh.subMeshCount,
                        RemovedBoneCount = posed.OriginalBoneCount,
                        ActiveBlendShapeCount = posed.ActiveBlendShapeCount
                    });
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{smr.name}: {ex.Message}");
                Debug.LogException(ex);
            }
        }

        if (convertMode == ConvertMode.Duplicate && hideSourceOnDuplicate && createdOutputs.Count > 0)
        {
            HideOriginalSourcesSafely(posedList, rootContext, createdOutputs);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  結合変換の構築
    // ─────────────────────────────────────────────────────────

    private static BakeResultRecord BuildCombinedStaticMesh(
        List<PosedMeshData> posedList,
        GameObject rootContext,
        ConvertMode convertMode,
        bool hideSourceOnDuplicate,
        string combinedObjectName,
        Transform targetParent,
        string customSaveDir,
        List<string> errors)
    {
        var primarySMR = posedList[0].SourceSMR;
        string defaultBaseName = rootContext != null
            ? StripStaticSuffix(rootContext.name)
            : StripStaticSuffix(primarySMR.name);
        string finalObjName = string.IsNullOrWhiteSpace(combinedObjectName)
            ? defaultBaseName + "_Static"
            : combinedObjectName.Trim();

        try
        {
            if (convertMode == ConvertMode.Duplicate)
            {
                var outGo = new GameObject(finalObjName);
                Undo.RegisterCreatedObjectUndo(outGo, "Create Combined Static Mesh: " + finalObjName);
                CopyGameObjectMetadata(primarySMR.gameObject, outGo);

                if (targetParent != null)
                {
                    outGo.transform.SetParent(targetParent, false);
                    outGo.transform.localPosition = Vector3.zero;
                    outGo.transform.localRotation = Quaternion.identity;
                    outGo.transform.localScale = Vector3.one;
                }
                else
                {
                    Transform anchor = (rootContext != null && CanDeactivateWholeRoot(rootContext, posedList))
                        ? rootContext.transform
                        : primarySMR.transform;

                    outGo.transform.SetParent(anchor.parent, false);
                    outGo.transform.SetSiblingIndex(anchor.GetSiblingIndex() + 1);
                    outGo.transform.position = anchor.position;
                    outGo.transform.rotation = anchor.rotation;
                    outGo.transform.localScale = Vector3.one;
                }

                Mesh combinedMesh = BuildMeshInTargetSpace(posedList, outGo.transform, mergeByMaterial: true, out Material[] outMaterials, out int triCount);
                string savedPath = SaveMeshAsset(combinedMesh, posedList[0].SourceMesh, StripStaticSuffix(finalObjName), customSaveDir);
                Mesh loadedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(savedPath);

                var mf = outGo.AddComponent<MeshFilter>();
                mf.sharedMesh = loadedMesh;

                var mr = outGo.AddComponent<MeshRenderer>();
                CopyRendererSettings(primarySMR, mr, outMaterials);

                EditorUtility.SetDirty(outGo);

                if (hideSourceOnDuplicate)
                {
                    HideOriginalSourcesSafely(posedList, rootContext, new List<Transform> { outGo.transform });
                }

                return new BakeResultRecord
                {
                    OutputObject = outGo,
                    OutputName = outGo.name,
                    MeshAssetPath = savedPath,
                    VertexCount = loadedMesh.vertexCount,
                    TriangleCount = triCount,
                    SubMeshCount = loadedMesh.subMeshCount,
                    RemovedBoneCount = CountDistinctBones(posedList),
                    ActiveBlendShapeCount = posedList.Sum(p => p.ActiveBlendShapeCount)
                };
            }
            else
            {
                // その場で結合変換：先頭 SMR の GameObject をベースにし、残りの SMR を統合・整理
                GameObject baseGo = primarySMR.gameObject;
                if (!string.IsNullOrWhiteSpace(combinedObjectName))
                {
                    Undo.RecordObject(baseGo, "Rename Combined Static Mesh");
                    baseGo.name = finalObjName;
                }

                if (targetParent != null && baseGo.transform.parent != targetParent)
                {
                    Undo.SetTransformParent(baseGo.transform, targetParent, true, "Reparent Combined Static Mesh");
                }

                Mesh combinedMesh = BuildMeshInTargetSpace(posedList, baseGo.transform, mergeByMaterial: true, out Material[] outMaterials, out int triCount);
                string savedPath = SaveMeshAsset(combinedMesh, posedList[0].SourceMesh, StripStaticSuffix(finalObjName), customSaveDir);
                Mesh loadedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(savedPath);

                ReplaceSMRWithStaticComponents(primarySMR, loadedMesh, outMaterials);

                // 2番目以降の SMR コンポーネント（および空になったオブジェクト）を削除
                for (int i = 1; i < posedList.Count; i++)
                {
                    var otherSmr = posedList[i].SourceSMR;
                    if (otherSmr == null || otherSmr == primarySMR) continue;
                    GameObject otherGo = otherSmr.gameObject;
                    Undo.DestroyObjectImmediate(otherSmr);

                    // 他のコンポーネント（Transform以外）も子オブジェクトも無ければ GameObject 自体も整理
                    if (otherGo != baseGo &&
                        otherGo.transform.childCount == 0 &&
                        otherGo.GetComponents<Component>().Length <= 1)
                    {
                        Undo.DestroyObjectImmediate(otherGo);
                    }
                }

                return new BakeResultRecord
                {
                    OutputObject = baseGo,
                    OutputName = baseGo.name,
                    MeshAssetPath = savedPath,
                    VertexCount = loadedMesh.vertexCount,
                    TriangleCount = triCount,
                    SubMeshCount = loadedMesh.subMeshCount,
                    RemovedBoneCount = CountDistinctBones(posedList),
                    ActiveBlendShapeCount = posedList.Sum(p => p.ActiveBlendShapeCount)
                };
            }
        }
        catch (Exception ex)
        {
            errors.Add($"結合エラー: {ex.Message}");
            Debug.LogException(ex);
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────
    //  コアジオメトリ処理①: 現在のポーズをワールド空間で取得
    // ─────────────────────────────────────────────────────────

    private static PosedMeshData CapturePosedMeshInWorldSpace(SkinnedMeshRenderer smr)
    {
        Mesh srcMesh = smr.sharedMesh;
        int vc = srcMesh.vertexCount;
        if (vc == 0) return null;

        var data = new PosedMeshData
        {
            SourceSMR = smr,
            SourceMesh = srcMesh,
            OriginalBoneCount = CountValidBones(smr),
            ActiveBlendShapeCount = CountActiveBlendShapes(smr)
        };

        bool hasSkinning = HasEffectiveSkinning(smr, srcMesh);

        if (hasSkinning)
        {
            // BakeMesh(mesh, useScale: false) は
            // 現在のボーン変形（移動・回転・非一様スケール）＋全BlendShape変形を適用した頂点を、
            // Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one) 基準の空間で出力する。
            // スケール成分を含まない剛体変換なので、親やSMRの非一様スケールで法線・頂点が歪まない。
            var baked = new Mesh();
            try
            {
                smr.BakeMesh(baked, false);

                Matrix4x4 rigidToWorld = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                Quaternion rot = smr.transform.rotation;

                var bVerts = baked.vertices;
                var wVerts = new Vector3[vc];
                for (int i = 0; i < vc; i++)
                    wVerts[i] = rigidToWorld.MultiplyPoint3x4(bVerts[i]);
                data.WorldVertices = wVerts;

                var bNorms = baked.normals;
                if (bNorms != null && bNorms.Length == vc)
                {
                    var wNorms = new Vector3[vc];
                    for (int i = 0; i < vc; i++)
                    {
                        Vector3 n = rot * bNorms[i];
                        wNorms[i] = n.sqrMagnitude > 1e-20f ? n.normalized : Vector3.up;
                    }
                    data.WorldNormals = wNorms;
                }

                var bTans = baked.tangents;
                if (bTans != null && bTans.Length == vc)
                {
                    var wTans = new Vector4[vc];
                    for (int i = 0; i < vc; i++)
                    {
                        Vector4 t = bTans[i];
                        Vector3 td = rot * new Vector3(t.x, t.y, t.z);
                        td = td.sqrMagnitude > 1e-20f ? td.normalized : Vector3.right;
                        wTans[i] = new Vector4(td.x, td.y, td.z, t.w);
                    }
                    data.WorldTangents = wTans;
                }

                // 負スケール（左右反転コピーした髪飾り等）でラスタライザのカリング向きが反転していた場合も引き継ぐ
                data.SrcDetSign = smr.transform.localToWorldMatrix.determinant < 0f ? -1f : 1f;
            }
            finally
            {
                DestroyImmediate(baked);
            }
        }
        else
        {
            // ボーンを持たない BlendShape 専用の SkinnedMeshRenderer の場合：
            // Unity は smr.transform.localToWorldMatrix（lossyScale 含む）で描画するため、
            // BlendShape デルタを適用した上で localToWorldMatrix によりワールド空間へ変換する。
            EvaluateBlendShapesOnlyInWorldSpace(smr, srcMesh, data);
        }

        // 頂点カラー & 全8チャンネルUV（次元保持）を元Meshから取得
        var cols = srcMesh.colors32;
        if (cols != null && cols.Length == vc)
            data.Colors32 = cols;

        for (int ch = 0; ch < 8; ch++)
            data.UVChannels[ch] = ReadUVChannel(srcMesh, ch);

        // サブメッシュとマテリアルの対応を取得（複数マテリアル重ね掛け表現にも対応）
        ExtractSubMeshes(smr, srcMesh, data.SubMeshes);

        return data;
    }

    private static bool HasEffectiveSkinning(SkinnedMeshRenderer smr, Mesh srcMesh)
    {
        var bindposes = srcMesh.bindposes;
        if (bindposes == null || bindposes.Length == 0) return false;

        var bones = smr.bones;
        if (bones == null || bones.Length == 0) return false;

        bool anyBone = false;
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] != null) { anyBone = true; break; }
        }
        if (!anyBone) return false;

        return srcMesh.HasVertexAttribute(VertexAttribute.BlendWeight) ||
               (srcMesh.boneWeights != null && srcMesh.boneWeights.Length > 0);
    }

    private static void EvaluateBlendShapesOnlyInWorldSpace(SkinnedMeshRenderer smr, Mesh srcMesh, PosedMeshData data)
    {
        int vc = srcMesh.vertexCount;
        var verts = srcMesh.vertices;
        var norms = srcMesh.normals;
        var tans = srcMesh.tangents;

        bool hasNorms = norms != null && norms.Length == vc;
        bool hasTans = tans != null && tans.Length == vc;

        var tanDirs = hasTans ? new Vector3[vc] : null;
        if (hasTans)
        {
            for (int i = 0; i < vc; i++)
                tanDirs[i] = new Vector3(tans[i].x, tans[i].y, tans[i].z);
        }

        int bsCount = srcMesh.blendShapeCount;
        if (bsCount > 0)
        {
            var dV = new Vector3[vc];
            var dN = new Vector3[vc];
            var dT = new Vector3[vc];
            var dVPrev = new Vector3[vc];
            var dNPrev = new Vector3[vc];
            var dTPrev = new Vector3[vc];

            for (int bi = 0; bi < bsCount; bi++)
            {
                float w = smr.GetBlendShapeWeight(bi);
                if (Mathf.Approximately(w, 0f)) continue;

                int fc = srcMesh.GetBlendShapeFrameCount(bi);
                if (fc <= 0) continue;

                if (fc == 1)
                {
                    float fw = srcMesh.GetBlendShapeFrameWeight(bi, 0);
                    if (Mathf.Approximately(fw, 0f)) continue;
                    float factor = w / fw;
                    srcMesh.GetBlendShapeFrameVertices(bi, 0, dV, dN, dT);
                    ApplyBlendDelta(verts, norms, tanDirs, dV, dN, dT, factor, hasNorms, hasTans);
                }
                else
                {
                    // マルチフレーム BlendShape の補間
                    float firstW = srcMesh.GetBlendShapeFrameWeight(bi, 0);
                    float lastW = srcMesh.GetBlendShapeFrameWeight(bi, fc - 1);

                    if (w <= firstW)
                    {
                        float factor = !Mathf.Approximately(firstW, 0f) ? (w / firstW) : 0f;
                        srcMesh.GetBlendShapeFrameVertices(bi, 0, dV, dN, dT);
                        ApplyBlendDelta(verts, norms, tanDirs, dV, dN, dT, factor, hasNorms, hasTans);
                    }
                    else if (w >= lastW)
                    {
                        float factor = !Mathf.Approximately(lastW, 0f) ? (w / lastW) : 1f;
                        srcMesh.GetBlendShapeFrameVertices(bi, fc - 1, dV, dN, dT);
                        ApplyBlendDelta(verts, norms, tanDirs, dV, dN, dT, factor, hasNorms, hasTans);
                    }
                    else
                    {
                        for (int fi = 0; fi < fc - 1; fi++)
                        {
                            float w0 = srcMesh.GetBlendShapeFrameWeight(bi, fi);
                            float w1 = srcMesh.GetBlendShapeFrameWeight(bi, fi + 1);
                            if (w >= w0 && w <= w1)
                            {
                                float t = Mathf.Approximately(w1, w0) ? 0f : (w - w0) / (w1 - w0);
                                srcMesh.GetBlendShapeFrameVertices(bi, fi, dVPrev, dNPrev, dTPrev);
                                srcMesh.GetBlendShapeFrameVertices(bi, fi + 1, dV, dN, dT);
                                for (int v = 0; v < vc; v++)
                                {
                                    verts[v] += Vector3.Lerp(dVPrev[v], dV[v], t);
                                    if (hasNorms) norms[v] += Vector3.Lerp(dNPrev[v], dN[v], t);
                                    if (hasTans) tanDirs[v] += Vector3.Lerp(dTPrev[v], dT[v], t);
                                }
                                break;
                            }
                        }
                    }
                }
            }
        }

        Matrix4x4 l2w = smr.transform.localToWorldMatrix;
        Matrix4x4 normalL2W = smr.transform.worldToLocalMatrix.transpose;

        var wVerts = new Vector3[vc];
        for (int i = 0; i < vc; i++)
            wVerts[i] = l2w.MultiplyPoint3x4(verts[i]);
        data.WorldVertices = wVerts;

        if (hasNorms)
        {
            var wNorms = new Vector3[vc];
            for (int i = 0; i < vc; i++)
            {
                Vector3 n = normalL2W.MultiplyVector(norms[i]);
                wNorms[i] = n.sqrMagnitude > 1e-20f ? n.normalized : Vector3.up;
            }
            data.WorldNormals = wNorms;
        }

        if (hasTans)
        {
            var wTans = new Vector4[vc];
            for (int i = 0; i < vc; i++)
            {
                Vector3 td = l2w.MultiplyVector(tanDirs[i]);
                td = td.sqrMagnitude > 1e-20f ? td.normalized : Vector3.right;
                wTans[i] = new Vector4(td.x, td.y, td.z, tans[i].w);
            }
            data.WorldTangents = wTans;
        }

        data.SrcDetSign = l2w.determinant < 0f ? -1f : 1f;
    }

    private static void ApplyBlendDelta(
        Vector3[] verts, Vector3[] norms, Vector3[] tanDirs,
        Vector3[] dV, Vector3[] dN, Vector3[] dT,
        float factor, bool hasNorms, bool hasTans)
    {
        int vc = verts.Length;
        for (int v = 0; v < vc; v++)
        {
            verts[v] += dV[v] * factor;
            if (hasNorms) norms[v] += dN[v] * factor;
            if (hasTans) tanDirs[v] += dT[v] * factor;
        }
    }

    private static UVChannelData ReadUVChannel(Mesh mesh, int channel)
    {
        VertexAttribute attr = VertexAttribute.TexCoord0 + channel;
        if (!mesh.HasVertexAttribute(attr))
            return new UVChannelData { Dimension = 0 };

        int dim = mesh.GetVertexAttributeDimension(attr);
        if (dim <= 2)
        {
            var list = new List<Vector2>(mesh.vertexCount);
            mesh.GetUVs(channel, list);
            if (list.Count == 0) return new UVChannelData { Dimension = 0 };
            return new UVChannelData { Dimension = 2, UV2 = list };
        }
        if (dim == 3)
        {
            var list = new List<Vector3>(mesh.vertexCount);
            mesh.GetUVs(channel, list);
            if (list.Count == 0) return new UVChannelData { Dimension = 0 };
            return new UVChannelData { Dimension = 3, UV3 = list };
        }
        else
        {
            var list = new List<Vector4>(mesh.vertexCount);
            mesh.GetUVs(channel, list);
            if (list.Count == 0) return new UVChannelData { Dimension = 0 };
            return new UVChannelData { Dimension = 4, UV4 = list };
        }
    }

    private static void ExtractSubMeshes(SkinnedMeshRenderer smr, Mesh srcMesh, List<SubMeshTriangles> outSubMeshes)
    {
        int subCount = srcMesh.subMeshCount;
        if (subCount <= 0) return;

        var mats = smr.sharedMaterials ?? Array.Empty<Material>();

        for (int s = 0; s < subCount; s++)
        {
            MeshTopology topo = srcMesh.GetTopology(s);
            int[] indices = topo == MeshTopology.Triangles ? srcMesh.GetTriangles(s) : srcMesh.GetIndices(s);
            Material mat = s < mats.Length ? mats[s] : null;
            outSubMeshes.Add(new SubMeshTriangles
            {
                Material = mat,
                Topology = topo,
                Indices = indices
            });
        }

        // VRChat でよくある「1 SubMesh に対して複数 Material スロットを割り当ててオーバーレイ描画する」表現への対応
        if (mats.Length > subCount && subCount > 0)
        {
            int lastSub = subCount - 1;
            MeshTopology lastTopo = srcMesh.GetTopology(lastSub);
            int[] lastIndices = lastTopo == MeshTopology.Triangles ? srcMesh.GetTriangles(lastSub) : srcMesh.GetIndices(lastSub);

            for (int m = subCount; m < mats.Length; m++)
            {
                if (mats[m] == null) continue;
                var copyIdx = new int[lastIndices.Length];
                Array.Copy(lastIndices, copyIdx, lastIndices.Length);
                outSubMeshes.Add(new SubMeshTriangles
                {
                    Material = mats[m],
                    Topology = lastTopo,
                    Indices = copyIdx
                });
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  コアジオメトリ処理②: 配置先 Transform 空間で StaticMesh 構築
    // ─────────────────────────────────────────────────────────

    private static Mesh BuildMeshInTargetSpace(
        List<PosedMeshData> parts,
        Transform dstTransform,
        bool mergeByMaterial,
        out Material[] outMaterials,
        out int totalTriangleCount)
    {
        Matrix4x4 dstLocalToWorld = dstTransform.localToWorldMatrix;
        Matrix4x4 dstWorldToLocal = dstTransform.worldToLocalMatrix;
        Matrix4x4 normalWorldToLocal = dstLocalToWorld.transpose;
        float dstDetSign = dstLocalToWorld.determinant < 0f ? -1f : 1f;

        int totalVerts = parts.Sum(p => p.WorldVertices.Length);
        bool anyNormals = parts.Any(p => p.WorldNormals != null);
        bool anyTangents = parts.Any(p => p.WorldTangents != null);
        bool anyColors = parts.Any(p => p.Colors32 != null);

        var finalVerts = new Vector3[totalVerts];
        var finalNorms = anyNormals ? new Vector3[totalVerts] : null;
        var finalTans = anyTangents ? new Vector4[totalVerts] : null;
        var finalColors = anyColors ? new Color32[totalVerts] : null;

        int[] maxUVDim = new int[8];
        for (int ch = 0; ch < 8; ch++)
        {
            foreach (var p in parts)
            {
                if (p.UVChannels[ch] != null && p.UVChannels[ch].Dimension > maxUVDim[ch])
                    maxUVDim[ch] = p.UVChannels[ch].Dimension;
            }
        }

        // サブメッシュ収集用
        var materialOrder = new List<Material>();
        var mergedBuckets = new Dictionary<MaterialKey, List<int>>();
        var unmergedSubMeshes = new List<SubMeshTriangles>();

        int vertexOffset = 0;
        foreach (var part in parts)
        {
            int vc = part.WorldVertices.Length;
            bool flipWinding = (part.SrcDetSign * dstDetSign) < 0f;
            float tanWSign = flipWinding ? -1f : 1f;

            for (int i = 0; i < vc; i++)
            {
                int di = vertexOffset + i;
                finalVerts[di] = dstWorldToLocal.MultiplyPoint3x4(part.WorldVertices[i]);

                if (anyNormals)
                {
                    if (part.WorldNormals != null)
                    {
                        Vector3 nl = normalWorldToLocal.MultiplyVector(part.WorldNormals[i]);
                        finalNorms[di] = nl.sqrMagnitude > 1e-20f ? nl.normalized : Vector3.up;
                    }
                    else
                    {
                        finalNorms[di] = Vector3.up;
                    }
                }

                if (anyTangents)
                {
                    if (part.WorldTangents != null)
                    {
                        Vector4 tw = part.WorldTangents[i];
                        Vector3 tl = dstWorldToLocal.MultiplyVector(new Vector3(tw.x, tw.y, tw.z));
                        if (finalNorms != null)
                        {
                            // 非一様スケール下でも法線と直交する接線基底を維持（Gram-Schmidt直交化）
                            Vector3 n = finalNorms[di];
                            Vector3 ortho = tl - n * Vector3.Dot(n, tl);
                            if (ortho.sqrMagnitude > 1e-12f)
                                tl = ortho;
                        }
                        tl = tl.sqrMagnitude > 1e-20f ? tl.normalized : Vector3.right;
                        finalTans[di] = new Vector4(tl.x, tl.y, tl.z, tw.w * tanWSign);
                    }
                    else
                    {
                        finalTans[di] = new Vector4(1f, 0f, 0f, 1f);
                    }
                }

                if (anyColors)
                {
                    // 頂点カラーを持たないメッシュと結合する場合、白(255,255,255,255)で埋めることで
                    // lilToon等の頂点カラーアウトライン幅が0（消失）になるのを防ぐ
                    finalColors[di] = part.Colors32 != null
                        ? part.Colors32[i]
                        : new Color32(255, 255, 255, 255);
                }
            }

            foreach (var sub in part.SubMeshes)
            {
                if (sub.Indices == null || sub.Indices.Length == 0) continue;

                int[] offsetIndices = new int[sub.Indices.Length];
                if (flipWinding && sub.Topology == MeshTopology.Triangles)
                {
                    for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
                    {
                        offsetIndices[t] = sub.Indices[t] + vertexOffset;
                        offsetIndices[t + 1] = sub.Indices[t + 2] + vertexOffset;
                        offsetIndices[t + 2] = sub.Indices[t + 1] + vertexOffset;
                    }
                }
                else
                {
                    for (int idx = 0; idx < sub.Indices.Length; idx++)
                        offsetIndices[idx] = sub.Indices[idx] + vertexOffset;
                }

                if (mergeByMaterial && sub.Topology == MeshTopology.Triangles)
                {
                    var key = new MaterialKey(sub.Material);
                    if (!mergedBuckets.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<int>(offsetIndices.Length);
                        mergedBuckets[key] = bucket;
                        materialOrder.Add(sub.Material);
                    }
                    bucket.AddRange(offsetIndices);
                }
                else
                {
                    unmergedSubMeshes.Add(new SubMeshTriangles
                    {
                        Material = sub.Material,
                        Topology = sub.Topology,
                        Indices = offsetIndices
                    });
                }
            }

            vertexOffset += vc;
        }

        // StaticMesh 生成（boneWeights / bindposes / blendShapes は一切持たない純粋な StaticMesh）
        var mesh = new Mesh
        {
            indexFormat = totalVerts >= 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
        };

        mesh.vertices = finalVerts;
        if (anyNormals) mesh.normals = finalNorms;
        if (anyTangents) mesh.tangents = finalTans;
        if (anyColors) mesh.colors32 = finalColors;

        // 全8チャンネルUV書き込み
        for (int ch = 0; ch < 8; ch++)
        {
            int dim = maxUVDim[ch];
            if (dim == 0) continue;
            WriteCombinedUVChannel(mesh, ch, dim, parts, totalVerts);
        }

        // サブメッシュ書き込み
        var matList = new List<Material>();
        totalTriangleCount = 0;

        if (mergeByMaterial)
        {
            int subTotal = materialOrder.Count + unmergedSubMeshes.Count;
            mesh.subMeshCount = Mathf.Max(1, subTotal);
            int sIdx = 0;

            foreach (var mat in materialOrder)
            {
                var tris = mergedBuckets[new MaterialKey(mat)];
                mesh.SetTriangles(tris, sIdx++);
                matList.Add(mat);
                totalTriangleCount += tris.Count / 3;
            }

            foreach (var extra in unmergedSubMeshes)
            {
                if (extra.Topology == MeshTopology.Triangles)
                {
                    mesh.SetTriangles(extra.Indices, sIdx++);
                    totalTriangleCount += extra.Indices.Length / 3;
                }
                else
                {
                    mesh.SetIndices(extra.Indices, extra.Topology, sIdx++);
                }
                matList.Add(extra.Material);
            }
        }
        else
        {
            mesh.subMeshCount = Mathf.Max(1, unmergedSubMeshes.Count);
            for (int s = 0; s < unmergedSubMeshes.Count; s++)
            {
                var sub = unmergedSubMeshes[s];
                if (sub.Topology == MeshTopology.Triangles)
                {
                    mesh.SetTriangles(sub.Indices, s);
                    totalTriangleCount += sub.Indices.Length / 3;
                }
                else
                {
                    mesh.SetIndices(sub.Indices, sub.Topology, s);
                }
                matList.Add(sub.Material);
            }
        }

        mesh.RecalculateBounds();
        outMaterials = matList.ToArray();
        return mesh;
    }

    private readonly struct MaterialKey : IEquatable<MaterialKey>
    {
        private readonly int _instanceId;
        public MaterialKey(Material mat) => _instanceId = ReferenceEquals(mat, null) ? 0 : mat.GetInstanceID();
        public bool Equals(MaterialKey other) => _instanceId == other._instanceId;
        public override bool Equals(object obj) => obj is MaterialKey other && Equals(other);
        public override int GetHashCode() => _instanceId;
    }

    private static void WriteCombinedUVChannel(Mesh dst, int channel, int targetDim, List<PosedMeshData> parts, int totalVerts)
    {
        if (targetDim == 2)
        {
            var uvs = new List<Vector2>(totalVerts);
            foreach (var p in parts)
            {
                int vc = p.WorldVertices.Length;
                var chData = p.UVChannels[channel];
                if (chData != null && chData.Dimension == 2 && chData.UV2 != null && chData.UV2.Count == vc)
                {
                    uvs.AddRange(chData.UV2);
                }
                else
                {
                    for (int i = 0; i < vc; i++) uvs.Add(Vector2.zero);
                }
            }
            dst.SetUVs(channel, uvs);
        }
        else if (targetDim == 3)
        {
            var uvs = new List<Vector3>(totalVerts);
            foreach (var p in parts)
            {
                int vc = p.WorldVertices.Length;
                var chData = p.UVChannels[channel];
                if (chData != null && chData.Dimension == 3 && chData.UV3 != null && chData.UV3.Count == vc)
                {
                    uvs.AddRange(chData.UV3);
                }
                else if (chData != null && chData.Dimension == 2 && chData.UV2 != null && chData.UV2.Count == vc)
                {
                    for (int i = 0; i < vc; i++) uvs.Add(chData.UV2[i]);
                }
                else
                {
                    for (int i = 0; i < vc; i++) uvs.Add(Vector3.zero);
                }
            }
            dst.SetUVs(channel, uvs);
        }
        else
        {
            var uvs = new List<Vector4>(totalVerts);
            foreach (var p in parts)
            {
                int vc = p.WorldVertices.Length;
                var chData = p.UVChannels[channel];
                if (chData != null && chData.Dimension == 4 && chData.UV4 != null && chData.UV4.Count == vc)
                {
                    uvs.AddRange(chData.UV4);
                }
                else if (chData != null && chData.Dimension == 3 && chData.UV3 != null && chData.UV3.Count == vc)
                {
                    for (int i = 0; i < vc; i++)
                    {
                        Vector3 v = chData.UV3[i];
                        uvs.Add(new Vector4(v.x, v.y, v.z, 0f));
                    }
                }
                else if (chData != null && chData.Dimension == 2 && chData.UV2 != null && chData.UV2.Count == vc)
                {
                    for (int i = 0; i < vc; i++)
                    {
                        Vector2 v = chData.UV2[i];
                        uvs.Add(new Vector4(v.x, v.y, 0f, 0f));
                    }
                }
                else
                {
                    for (int i = 0; i < vc; i++) uvs.Add(Vector4.zero);
                }
            }
            dst.SetUVs(channel, uvs);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  コンポーネント・階層・非表示処理ヘルパー
    // ─────────────────────────────────────────────────────────

    private static void ReplaceSMRWithStaticComponents(SkinnedMeshRenderer smr, Mesh staticMeshAsset, Material[] materials)
    {
        GameObject go = smr.gameObject;

        // SkinnedMeshRenderer 破棄前に Renderer 設定を退避
        var shadowMode = smr.shadowCastingMode;
        bool receiveShadows = smr.receiveShadows;
        var lightProbes = smr.lightProbeUsage;
        var reflProbes = smr.reflectionProbeUsage;
        Transform probeAnchor = smr.probeAnchor;
        GameObject lppv = smr.lightProbeProxyVolumeOverride;
        var motionVec = smr.motionVectorGenerationMode;
        bool dynamicOcc = smr.allowOcclusionWhenDynamic;
        uint renderLayerMask = smr.renderingLayerMask;
        int priority = smr.rendererPriority;
        int sortingLayerID = smr.sortingLayerID;
        int sortingOrder = smr.sortingOrder;
        bool wasEnabled = smr.enabled;

        // 1. 先に SkinnedMeshRenderer を削除（1つの GameObject に複数の Renderer は共存不可のため、Undo順序も逆順で安全になる）
        Undo.DestroyObjectImmediate(smr);

        // 2. MeshFilter を追加・設定
        var mf = go.GetComponent<MeshFilter>();
        if (mf != null)
            Undo.RecordObject(mf, "Assign Static Mesh");
        else
            mf = Undo.AddComponent<MeshFilter>(go);
        mf.sharedMesh = staticMeshAsset;

        // 3. MeshRenderer を追加・設定復元
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
            Undo.RecordObject(mr, "Configure Static MeshRenderer");
        else
            mr = Undo.AddComponent<MeshRenderer>(go);

        mr.sharedMaterials = materials;
        mr.shadowCastingMode = shadowMode;
        mr.receiveShadows = receiveShadows;
        mr.lightProbeUsage = lightProbes;
        mr.reflectionProbeUsage = reflProbes;
        mr.probeAnchor = probeAnchor;
        mr.lightProbeProxyVolumeOverride = lppv;
        mr.motionVectorGenerationMode = motionVec;
        mr.allowOcclusionWhenDynamic = dynamicOcc;
        mr.renderingLayerMask = renderLayerMask;
        mr.rendererPriority = priority;
        mr.sortingLayerID = sortingLayerID;
        mr.sortingOrder = sortingOrder;
        mr.enabled = wasEnabled;

        EditorUtility.SetDirty(go);
    }

    private static void CopyRendererSettings(SkinnedMeshRenderer src, MeshRenderer dst, Material[] materials)
    {
        dst.sharedMaterials = materials;
        dst.shadowCastingMode = src.shadowCastingMode;
        dst.receiveShadows = src.receiveShadows;
        dst.lightProbeUsage = src.lightProbeUsage;
        dst.reflectionProbeUsage = src.reflectionProbeUsage;
        dst.probeAnchor = src.probeAnchor;
        dst.lightProbeProxyVolumeOverride = src.lightProbeProxyVolumeOverride;
        dst.motionVectorGenerationMode = src.motionVectorGenerationMode;
        dst.allowOcclusionWhenDynamic = src.allowOcclusionWhenDynamic;
        dst.renderingLayerMask = src.renderingLayerMask;
        dst.rendererPriority = src.rendererPriority;
        dst.sortingLayerID = src.sortingLayerID;
        dst.sortingOrder = src.sortingOrder;
    }

    private static void CopyGameObjectMetadata(GameObject src, GameObject dst)
    {
        dst.layer = src.layer;
        try
        {
            if (!string.IsNullOrEmpty(src.tag) && src.tag != "Untagged")
                dst.tag = src.tag;
        }
        catch
        {
            // 未登録タグの場合は無視
        }
        GameObjectUtility.SetStaticEditorFlags(dst, GameObjectUtility.GetStaticEditorFlags(src));
    }

    /// <summary>
    /// rootContext 配下の全ての有効な Renderer が今回変換された（＝素体の一部だけ抜粋したのではなく、髪飾りPrefab丸ごと等）か判定する。
    /// </summary>
    private static bool CanDeactivateWholeRoot(GameObject rootContext, List<PosedMeshData> converted)
    {
        if (rootContext == null) return false;

        // VRCAvatarDescriptor 等を持つアバタールート自身は絶対に非表示化しない
        foreach (var comp in rootContext.GetComponents<Component>())
        {
            if (comp == null) continue;
            string tn = comp.GetType().Name;
            if (tn.IndexOf("AvatarDescriptor", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        }

        var convertedSMRs = new HashSet<SkinnedMeshRenderer>(converted.Select(c => c.SourceSMR));
        foreach (var r in rootContext.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            if (!IsActiveRelativeToRoot(r.transform, rootContext.transform) || !r.enabled) continue;

            if (r is SkinnedMeshRenderer smr && convertedSMRs.Contains(smr))
                continue;

            // 変換対象外のアクティブな Renderer が含まれている場合はルート丸ごと非表示にしない
            return false;
        }

        return true;
    }

    private static void HideOriginalSourcesSafely(
        List<PosedMeshData> converted,
        GameObject rootContext,
        List<Transform> createdOutputs)
    {
        bool anyOutputInsideRoot = rootContext != null &&
            createdOutputs.Any(o => o != null && o.IsChildOf(rootContext.transform));

        if (rootContext != null && !anyOutputInsideRoot && CanDeactivateWholeRoot(rootContext, converted))
        {
            Undo.RecordObject(rootContext, "Hide Original Skinned Object");
            rootContext.SetActive(false);
            EditorUtility.SetDirty(rootContext);
            return;
        }

        // アバター内の一部のアクセサリーSMRだけを変換した場合などは、各SMRごとに安全に非表示化
        var convertedSet = new HashSet<SkinnedMeshRenderer>(converted.Select(c => c.SourceSMR));
        foreach (var item in converted)
        {
            var smr = item.SourceSMR;
            if (smr == null) continue;

            GameObject go = smr.gameObject;
            bool containsOutput = createdOutputs.Any(o => o != null && o.IsChildOf(go.transform));
            bool hasOtherRenderer = go.GetComponentsInChildren<Renderer>(false)
                .Any(r => r != null && r != smr && (!(r is SkinnedMeshRenderer s) || !convertedSet.Contains(s)));

            if (!containsOutput && !hasOtherRenderer && go.transform.childCount == 0)
            {
                Undo.RecordObject(go, "Hide Original Skinned Mesh");
                go.SetActive(false);
                EditorUtility.SetDirty(go);
            }
            else
            {
                Undo.RecordObject(smr, "Disable Original SkinnedMeshRenderer");
                smr.enabled = false;
                EditorUtility.SetDirty(smr);
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  .mesh アセット保存ユーティリティ
    // ─────────────────────────────────────────────────────────

    private static string SaveMeshAsset(Mesh mesh, Mesh referenceSourceMesh, string fallbackBaseName, string customSaveDir)
    {
        string srcPath = referenceSourceMesh != null ? AssetDatabase.GetAssetPath(referenceSourceMesh) : "";
        string saveDir = ResolveSaveDir(srcPath, customSaveDir);
        EnsureFolder(saveDir);

        string rawBase = !string.IsNullOrWhiteSpace(fallbackBaseName)
            ? fallbackBaseName
            : (referenceSourceMesh != null ? referenceSourceMesh.name : "Mesh");
        string cleanBase = SanitizeFileName(StripStaticSuffix(rawBase));
        if (string.IsNullOrEmpty(cleanBase)) cleanBase = "Mesh";

        int nextNum = ResolveNextStaticNumber(saveDir, cleanBase, ".mesh");
        string fileName = $"{cleanBase}{STATIC_SUFFIX_MARKER}{nextNum:D3}.mesh";
        string assetPath = AssetDatabase.GenerateUniqueAssetPath(saveDir + "/" + fileName);

        mesh.name = Path.GetFileNameWithoutExtension(assetPath);
        AssetDatabase.CreateAsset(mesh, assetPath);

        return assetPath;
    }

    private static string ResolveSaveDir(string srcAssetPath, string customSaveDir)
    {
        if (!string.IsNullOrWhiteSpace(customSaveDir) && customSaveDir.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            return customSaveDir.Replace('\\', '/').TrimEnd('/');

        if (!string.IsNullOrEmpty(srcAssetPath) && srcAssetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            string dir = Path.GetDirectoryName(srcAssetPath);
            if (!string.IsNullOrEmpty(dir))
                return dir.Replace('\\', '/');
        }

        return DEFAULT_FALLBACK_DIR;
    }

    private static void EnsureFolder(string folderPath)
    {
        folderPath = folderPath.Replace('\\', '/').TrimEnd('/');
        if (AssetDatabase.IsValidFolder(folderPath)) return;

        string[] parts = folderPath.Split('/');
        if (parts.Length == 0) return;

        string current = parts[0]; // "Assets"
        for (int i = 1; i < parts.Length; i++)
        {
            if (string.IsNullOrEmpty(parts[i])) continue;
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    private static int ResolveNextStaticNumber(string dir, string baseName, string ext)
    {
        int maxNum = -1;
        string prefix = baseName + STATIC_SUFFIX_MARKER;

        if (!AssetDatabase.IsValidFolder(dir)) return 0;

        string[] guids = AssetDatabase.FindAssets("", new[] { dir });
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string fileName = Path.GetFileName(path);

            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;

            string numPart = fileName.Substring(
                prefix.Length,
                fileName.Length - prefix.Length - ext.Length);

            if (int.TryParse(numPart, out int num) && num > maxNum)
                maxNum = num;
        }

        return maxNum + 1;
    }

    private static string StripStaticSuffix(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";

        while (true)
        {
            int idx = name.LastIndexOf(STATIC_SUFFIX_MARKER, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string rest = name.Substring(idx + STATIC_SUFFIX_MARKER.Length);
                if (Regex.IsMatch(rest, @"^\d+$"))
                {
                    name = name.Substring(0, idx);
                    continue;
                }
            }

            if (name.EndsWith("_Static", StringComparison.OrdinalIgnoreCase) && name.Length > "_Static".Length)
            {
                name = name.Substring(0, name.Length - "_Static".Length);
                continue;
            }

            break;
        }

        return name;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    // ─────────────────────────────────────────────────────────
    //  カウント・ステータスユーティリティ
    // ─────────────────────────────────────────────────────────

    private static int CountValidBones(SkinnedMeshRenderer smr)
    {
        if (smr == null || smr.bones == null) return 0;
        int c = 0;
        var seen = new HashSet<Transform>();
        foreach (var b in smr.bones)
        {
            if (b != null && seen.Add(b)) c++;
        }
        return c;
    }

    private static int CountDistinctBones(List<PosedMeshData> list)
    {
        var seen = new HashSet<Transform>();
        foreach (var p in list)
        {
            if (p.SourceSMR == null || p.SourceSMR.bones == null) continue;
            foreach (var b in p.SourceSMR.bones)
            {
                if (b != null) seen.Add(b);
            }
        }
        return seen.Count;
    }

    private static int CountActiveBlendShapes(SkinnedMeshRenderer smr)
    {
        if (smr == null || smr.sharedMesh == null) return 0;
        int count = smr.sharedMesh.blendShapeCount;
        int active = 0;
        for (int i = 0; i < count; i++)
        {
            if (!Mathf.Approximately(smr.GetBlendShapeWeight(i), 0f))
                active++;
        }
        return active;
    }

    private void SetStatus(string msg, MessageType type)
    {
        _statusMessage = msg;
        _statusType = type;
        Repaint();
    }
}
#endif
