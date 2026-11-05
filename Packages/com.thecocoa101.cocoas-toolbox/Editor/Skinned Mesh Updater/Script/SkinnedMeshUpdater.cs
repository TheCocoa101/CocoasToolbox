#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Skinned Mesh Updater (Experimental) — Cocoa's Toolbox
/// Blender等の外部DCCツールで編集して再エクスポートしたメッシュを、
/// 既存の SkinnedMeshRenderer の Mesh 欄にそのまま置き換えて正しく表示・変形できるように
/// Unity側のみで座標系・スケール・原点オフセット・ボーン順・バインドポーズを一括補正するツール。
///
/// [主な機能]
/// ・共通ボーン位置に基づく自動アライメント（Procrustes解析：回転・スケール・位置ズレの自動算出、造形変更の影響を受けない）
/// ・ボーンなしメッシュ向けのバウンディングボックス基準自動フィット＆手動微調整（±90°回転 / ×0.01・×100プリセット等）
/// ・Blender特有のボーンローカル軸変化による破綻を防ぐ、ボーン順リマップ＆元バインドポーズ引き継ぎ（親ボーンへのウェイト集約フォールバック対応）
/// ・ウェイト消失メッシュへの元メッシュからの最近傍ボーンウェイト自動転写オプション
/// ・全8チャンネルUV（2D/3D/4D）・頂点カラー・サブメッシュ・BlendShape（マルチフレーム対応）維持
/// ・シーン上の SkinnedMeshRenderer でのリアルタイムプレビュー（変更前／変更後のワンクリック比較対応）
/// ・「.mesh形式でそのメッシュだけ新規作成」と「.fbx形式で上書き保存（GUID・SubAsset ID維持＋自動バックアップ復元付き）」の2モード対応
/// </summary>
public class SkinnedMeshUpdater : EditorWindow
{
    // ─────────────────────────────────────────────────────────
    //  定数・列挙型
    // ─────────────────────────────────────────────────────────

    private const string WINDOW_TITLE = "Skinned Mesh Updater (Experimental)";
    private const string HEADER_TITLE = "Skinned Mesh Updater (Experimental)";
    private const string DEFAULT_SAVE_DIR = "Assets/UpdatedSkinnedMeshes";
    private const string UPDATED_SUFFIX_MARKER = "_Updated-";
    private const string TEMP_BACKUP_DIR = "Temp/SkinnedMeshUpdaterBackups";

    private enum SaveMode
    {
        CreateNewMesh = 0,
        OverwriteFbx = 1
    }

    private struct BoneMappingSummary
    {
        public int SourceBoneCount;
        public int TargetBoneCount;
        public int DirectMatchCount;
        public int ParentFallbackCount;
        public int UnmatchedCount;
        public int SourceWeightedVertexCount;
        public bool UsedTransferFromReference;
    }

    private class FbxMeshPayload
    {
        public string MeshName;
        public Mesh MeshData;
        public Transform[] Bones;
        public Transform RootBone;
        public string[] MaterialNames;
    }

    // ─────────────────────────────────────────────────────────
    //  シリアライズ状態
    // ─────────────────────────────────────────────────────────

    [SerializeField] private SkinnedMeshRenderer _targetSmr;
    [SerializeField] private UnityEngine.Object _sourceObject; // GameObject (FBX) or Mesh
    [SerializeField] private int _selectedSubMeshAssetIndex = 0;
    [SerializeField] private Mesh _manualReferenceMesh;
    [SerializeField] private bool _foldAdvancedRef = false;

    // 補正パラメータ
    [SerializeField] private Vector3 _rotationEuler = Vector3.zero;
    [SerializeField] private bool _uniformScale = true;
    [SerializeField] private float _scaleFactor = 1f;
    [SerializeField] private Vector3 _scaleXYZ = Vector3.one;
    [SerializeField] private Vector3 _positionOffset = Vector3.zero;
    [SerializeField] private bool _mirrorX = false;

    // ボーン補正オプション
    [SerializeField] private bool _fixBonesAndBindposes = true;
    [SerializeField] private bool _transferWeightsWhenMissing = true;
    [SerializeField] private bool _recalculateNormals = false;
    [SerializeField] private bool _recalculateTangents = false;

    // プレビュー状態
    [SerializeField] private bool _autoUpdatePreview = true;
    private bool _isPreviewActive = false;
    private bool _previewShowOriginal = false;
    private SkinnedMeshRenderer _previewSmr;
    private Mesh _originalSmrMeshBeforePreview;
    private Bounds _originalSmrLocalBoundsBeforePreview;
    private Mesh _previewTempMesh;

    // 保存設定
    [SerializeField] private SaveMode _saveMode = SaveMode.CreateNewMesh;
    [SerializeField] private string _meshSaveFolder = "";
    [SerializeField] private bool _assignToSmrAfterSave = true;

    // FBXバックアップ復元用
    [SerializeField] private string _lastBackupTempPath = "";
    [SerializeField] private string _lastOverwrittenFbxAssetPath = "";

    // UIスクロール・ステータス
    [SerializeField] private Vector2 _scrollMain;
    [SerializeField] private string _statusMessage = "";
    [SerializeField] private MessageType _statusType = MessageType.Info;

    // 解決済みキャッシュ（非シリアライズ）
    private List<Mesh> _availableSourceMeshes = new List<Mesh>();
    private string[] _availableSourceMeshLabels = Array.Empty<string>();
    private BoneMappingSummary _lastBoneSummary;
    private bool _hasBoneSummary = false;

    // ─────────────────────────────────────────────────────────
    //  GUIStyle キャッシュ
    // ─────────────────────────────────────────────────────────

    private GUIStyle _executeButtonStyle;
    private GUIStyle _previewActiveButtonStyle;
    private GUIStyle _previewIdleButtonStyle;
    private GUIStyle _wrapMiniStyle;

    private GUIStyle ExecuteButtonStyle => _executeButtonStyle ?? (_executeButtonStyle = new GUIStyle(GUI.skin.button)
    {
        fontSize = 13,
        fontStyle = FontStyle.Bold
    });

    private GUIStyle PreviewActiveButtonStyle => _previewActiveButtonStyle ?? (_previewActiveButtonStyle = new GUIStyle(GUI.skin.button)
    {
        fontSize = 12,
        fontStyle = FontStyle.Bold
    });

    private GUIStyle PreviewIdleButtonStyle => _previewIdleButtonStyle ?? (_previewIdleButtonStyle = new GUIStyle(GUI.skin.button)
    {
        fontSize = 12
    });

    private GUIStyle WrapMiniStyle => _wrapMiniStyle ?? (_wrapMiniStyle = new GUIStyle(EditorStyles.miniLabel)
    {
        wordWrap = true
    });

    // ─────────────────────────────────────────────────────────
    //  メニュー登録
    // ─────────────────────────────────────────────────────────

    [MenuItem("Tools/Cocoa's Toolbox/Skinned Mesh Updater (Experimental)")]
    public static void Open()
    {
        var win = GetWindow<SkinnedMeshUpdater>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 560f);
        win.Show();
    }

    [MenuItem("GameObject/Cocoa's Toolbox/Skinned Mesh Updater (Experimental)", false, 22)]
    private static void OpenFromHierarchyContext(MenuCommand command)
    {
        GameObject go = command.context as GameObject ?? Selection.activeGameObject;
        SkinnedMeshRenderer smr = null;
        if (go != null)
        {
            smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr == null)
                smr = go.GetComponentInChildren<SkinnedMeshRenderer>(true);
        }

        var win = GetWindow<SkinnedMeshUpdater>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 560f);
        win.Show();
        win.Focus();

        if (smr != null)
            win.SetTargetSmr(smr);
    }

    [MenuItem("CONTEXT/SkinnedMeshRenderer/Skinned Mesh Updater (Experimental)")]
    private static void OpenFromSmrContext(MenuCommand command)
    {
        var smr = command.context as SkinnedMeshRenderer;
        var win = GetWindow<SkinnedMeshUpdater>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 560f);
        win.Show();
        win.Focus();

        if (smr != null)
            win.SetTargetSmr(smr);
    }

    // ─────────────────────────────────────────────────────────
    //  ライフサイクル・プレビュー保護
    // ─────────────────────────────────────────────────────────

    private void OnEnable()
    {
        minSize = new Vector2(440f, 560f);
        AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        RefreshAvailableSourceMeshes(autoSelectBestMatch: false);
    }

    private void OnDisable()
    {
        AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        StopPreview();
    }

    private void OnDestroy()
    {
        StopPreview();
    }

    private void OnBeforeAssemblyReload()
    {
        StopPreview();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
            StopPreview();
    }

    public void SetTargetSmr(SkinnedMeshRenderer smr)
    {
        if (_targetSmr != smr)
        {
            StopPreview();
            _targetSmr = smr;
            RefreshAvailableSourceMeshes(autoSelectBestMatch: true);
            if (GetSelectedSourceMesh() != null)
                RunAutoAlignBones(silentIfAlreadyAligned: true);
        }
        Repaint();
    }

    // ─────────────────────────────────────────────────────────
    //  OnGUI
    // ─────────────────────────────────────────────────────────

    private void OnGUI()
    {
        // プレビュー中に対象SMRが削除された場合のクリーンアップ
        if (_isPreviewActive && _previewSmr == null)
        {
            StopPreview();
        }

        _scrollMain = EditorGUILayout.BeginScrollView(_scrollMain);

        DrawHeader();
        EditorGUILayout.Space(6f);

        DrawTargetAndSourceSection();
        EditorGUILayout.Space(8f);

        bool paramsChanged = DrawAlignmentSection();
        EditorGUILayout.Space(8f);

        DrawPreviewSection(paramsChanged);
        EditorGUILayout.Space(8f);

        bool triggerSave = DrawSaveSection();

        EditorGUILayout.EndScrollView();

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox(_statusMessage, _statusType);
            EditorGUILayout.Space(4f);
        }

        if (triggerSave)
        {
            ExecuteSave();
            GUIUtility.ExitGUI();
        }
    }

    private void DrawHeader()
    {
        EditorGUILayout.LabelField(HEADER_TITLE, EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Cocoa's Toolbox", EditorStyles.miniLabel);
        var rect = GUILayoutUtility.GetRect(0f, 1f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, EditorStyles.label.normal.textColor * 0.3f);
    }

    // ─────────────────────────────────────────────────────────
    //  ① 対象 SkinnedMeshRenderer ＆ 新しいメッシュ選択
    // ─────────────────────────────────────────────────────────

    private void DrawTargetAndSourceSection()
    {
        EditorGUILayout.LabelField("① 更新対象と新しいメッシュ", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // Target SMR
            EditorGUI.BeginChangeCheck();
            var nextSmr = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                new GUIContent("対象の SkinnedMeshRenderer", "Meshを更新したいシーン上のSkinnedMeshRendererを指定します。"),
                _targetSmr, typeof(SkinnedMeshRenderer), true);
            if (EditorGUI.EndChangeCheck())
            {
                StopPreview();
                _targetSmr = nextSmr;
                RefreshAvailableSourceMeshes(autoSelectBestMatch: true);
                if (_targetSmr != null && GetSelectedSourceMesh() != null)
                {
                    RunAutoAlignBones(silentIfAlreadyAligned: false);
                }
            }

            if (_targetSmr != null && !IsSceneObject(_targetSmr.gameObject))
            {
                EditorGUILayout.HelpBox(
                    "Project内のPrefab/FBXアセットではなく、Hierarchy（Scene）上の SkinnedMeshRenderer を指定してください。",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(2f);

            // Source FBX / Mesh
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var nextSrc = EditorGUILayout.ObjectField(
                    new GUIContent("新しい FBX / Mesh", "Blender等からエクスポートしたFBXアセット、またはその中のMeshアセットを指定します。"),
                    _sourceObject, typeof(UnityEngine.Object), false);
                if (EditorGUI.EndChangeCheck())
                {
                    AcceptSourceObject(nextSrc, runAutoAlign: true);
                }

                Mesh currentSmrOriginalMesh = GetCurrentTargetUnpreviewedMesh();
                using (new EditorGUI.DisabledScope(currentSmrOriginalMesh == null))
                {
                    if (GUILayout.Button(
                        new GUIContent("現在のMeshをセット", "すでにSkinnedMeshRendererのMesh欄を置き換え済みの場合や、元FBXを直接上書きした場合に現在のMeshをそのまま補正対象としてセットします。"),
                        EditorStyles.miniButton, GUILayout.Width(114f)))
                    {
                        AcceptSourceObject(currentSmrOriginalMesh, runAutoAlign: true);
                    }
                }
            }

            // 複数Meshを含むFBXの場合はドロップダウン表示
            if (_availableSourceMeshes.Count > 1)
            {
                int clamped = Mathf.Clamp(_selectedSubMeshAssetIndex, 0, _availableSourceMeshes.Count - 1);
                EditorGUI.BeginChangeCheck();
                int nextIdx = EditorGUILayout.Popup("FBX内の対象メッシュ", clamped, _availableSourceMeshLabels);
                if (EditorGUI.EndChangeCheck() && nextIdx != clamped)
                {
                    _selectedSubMeshAssetIndex = nextIdx;
                    UpdateBoneSummaryPreview();
                    if (_targetSmr != null)
                        RunAutoAlignBones(silentIfAlreadyAligned: false);
                    if (_isPreviewActive && _autoUpdatePreview)
                        StartOrUpdatePreview();
                }
            }

            // 参照元メッシュ解決状況と詳細オプション
            Mesh selectedSourceMesh = GetSelectedSourceMesh();
            Mesh resolvedRefMesh = ResolveReferenceMesh(selectedSourceMesh, out string refSourceLabel);

            _foldAdvancedRef = EditorGUILayout.Foldout(_foldAdvancedRef, $"基準データ情報（{refSourceLabel}）", true);
            if (_foldAdvancedRef)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUI.BeginChangeCheck();
                    _manualReferenceMesh = (Mesh)EditorGUILayout.ObjectField(
                        new GUIContent("元の参照Mesh（任意）", "すでにSMRのMesh欄を上書きした後でも、置き換え前の元Meshを指定するとポーズ中アバターでも正確にバインドポーズ・位置を引き継げます（未指定時はPrefab元または現在のボーン階層から自動取得）。"),
                        _manualReferenceMesh, typeof(Mesh), false);
                    if (EditorGUI.EndChangeCheck())
                    {
                        UpdateBoneSummaryPreview();
                        if (_isPreviewActive && _autoUpdatePreview)
                            StartOrUpdatePreview();
                    }
                }
            }

            // 診断サマリー表示
            if (selectedSourceMesh != null)
            {
                if (!_hasBoneSummary)
                    UpdateBoneSummaryPreview();

                EditorGUILayout.Space(2f);
                DrawSourceDiagnosticSummary(selectedSourceMesh, resolvedRefMesh);
            }
        }
    }

    private void DrawSourceDiagnosticSummary(Mesh srcMesh, Mesh refMesh)
    {
        string srcPath = AssetDatabase.GetAssetPath(srcMesh);
        int triCount = CountMeshTriangles(srcMesh);
        var sb = new StringBuilder();
        sb.Append($"選択Mesh: {srcMesh.name}  ({srcMesh.vertexCount:N0} 頂点 / {triCount:N0} tris / SubMesh {srcMesh.subMeshCount} / BlendShape {srcMesh.blendShapeCount})");

        if (_targetSmr != null)
        {
            int dstBoneCount = _targetSmr.bones != null ? _targetSmr.bones.Length : 0;
            if (dstBoneCount == 0)
            {
                sb.Append("\n対象SMRの bones 配列が空です（静的形状のみ補正されます）。");
            }
            else if (_lastBoneSummary.SourceWeightedVertexCount == 0)
            {
                if (refMesh != null && HasAnyBoneWeights(refMesh))
                    sb.Append($"\nボーンウェイト: 新Meshにウェイトがありません → 参照Mesh（{refMesh.name}）から自動転写可能");
                else
                    sb.Append("\nボーンウェイト: 新Meshにウェイトが含まれていません。");
            }
            else
            {
                sb.Append($"\nボーン対応: 直接一致 {_lastBoneSummary.DirectMatchCount}/{_lastBoneSummary.SourceBoneCount} (対象SMR側 {dstBoneCount} 本)");
                if (_lastBoneSummary.ParentFallbackCount > 0)
                    sb.Append($" / 親ボーン集約 {_lastBoneSummary.ParentFallbackCount} 本");
                if (_lastBoneSummary.UnmatchedCount > 0)
                    sb.Append($" / 未一致 {_lastBoneSummary.UnmatchedCount} 本");
            }
        }

        if (!string.IsNullOrEmpty(srcPath))
            sb.Append($"\nアセットパス: {srcPath}");

        EditorGUILayout.LabelField(sb.ToString(), WrapMiniStyle);
    }

    // ─────────────────────────────────────────────────────────
    //  ② 座標系・サイズ・ボーン補正セクション
    // ─────────────────────────────────────────────────────────

    private bool DrawAlignmentSection()
    {
        bool changed = false;
        Mesh srcMesh = GetSelectedSourceMesh();
        bool ready = _targetSmr != null && srcMesh != null;

        EditorGUILayout.LabelField("② 座標系・サイズ・ボーン補正", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // 自動補正ボタン列
            using (new EditorGUI.DisabledScope(!ready))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(
                        new GUIContent("⚡ 自動位置合わせ (ボーン基準)", "共通ボーンのヘッド座標を比較して、回転・スケール・位置オフセットを自動算出します。頂点編集（スカルプトやポリゴン増減）の影響を受けません。"),
                        GUILayout.Height(24f)))
                    {
                        RunAutoAlignBones(silentIfAlreadyAligned: false);
                        changed = true;
                    }

                    if (GUILayout.Button(
                        new GUIContent("📦 Bounds基準で合わせる", "参照メッシュ（またはSMRの範囲）のバウンディングボックスサイズ・向き・中心にフィットさせます。ボーンがないメッシュにも有効です。"),
                        GUILayout.Height(24f)))
                    {
                        RunAutoAlignBounds();
                        changed = true;
                    }

                    if (GUILayout.Button(new GUIContent("リセット", "回転・スケール・位置オフセット・ミラーを初期値に戻します。"), GUILayout.Width(62f), GUILayout.Height(24f)))
                    {
                        ResetTransformParams();
                        SetStatus("補正パラメータをリセットしました。", MessageType.Info);
                        changed = true;
                    }
                }
            }

            EditorGUILayout.Space(4f);

            // 回転
            EditorGUI.BeginChangeCheck();
            _rotationEuler = EditorGUILayout.Vector3Field("回転補正 (Euler °)", _rotationEuler);
            if (EditorGUI.EndChangeCheck())
                changed = true;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("回転プリセット加算:", EditorStyles.miniLabel, GUILayout.Width(108f));
                if (GUILayout.Button("X -90°", EditorStyles.miniButtonLeft)) { ApplyAdditiveRotation(new Vector3(-90f, 0f, 0f)); changed = true; }
                if (GUILayout.Button("X +90°", EditorStyles.miniButtonMid)) { ApplyAdditiveRotation(new Vector3(90f, 0f, 0f)); changed = true; }
                if (GUILayout.Button("Y 180°", EditorStyles.miniButtonMid)) { ApplyAdditiveRotation(new Vector3(0f, 180f, 0f)); changed = true; }
                if (GUILayout.Button("Z 180°", EditorStyles.miniButtonMid)) { ApplyAdditiveRotation(new Vector3(0f, 0f, 180f)); changed = true; }
                if (GUILayout.Button("0°", EditorStyles.miniButtonRight, GUILayout.Width(34f))) { _rotationEuler = Vector3.zero; changed = true; }
            }

            EditorGUILayout.Space(4f);

            // スケール
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                if (_uniformScale)
                {
                    _scaleFactor = EditorGUILayout.FloatField("スケール倍率", _scaleFactor);
                    if (Mathf.Abs(_scaleFactor) < 1e-7f) _scaleFactor = 1e-4f;
                }
                else
                {
                    _scaleXYZ = EditorGUILayout.Vector3Field("スケール (XYZ)", _scaleXYZ);
                }

                bool nextUniform = GUILayout.Toggle(_uniformScale, new GUIContent("均一", "XYZ均一スケールと軸別スケールを切り替えます。"), EditorStyles.miniButton, GUILayout.Width(42f));
                if (nextUniform != _uniformScale)
                {
                    if (nextUniform)
                        _scaleFactor = _scaleXYZ.x;
                    else
                        _scaleXYZ = new Vector3(_scaleFactor, _scaleFactor, _scaleFactor);
                    _uniformScale = nextUniform;
                }

                if (EditorGUI.EndChangeCheck())
                    changed = true;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("スケール定数:", EditorStyles.miniLabel, GUILayout.Width(108f));
                if (GUILayout.Button("×0.01", EditorStyles.miniButtonLeft)) { SetUniformScaleValue(0.01f); changed = true; }
                if (GUILayout.Button("×0.1", EditorStyles.miniButtonMid)) { SetUniformScaleValue(0.1f); changed = true; }
                if (GUILayout.Button("×1", EditorStyles.miniButtonMid)) { SetUniformScaleValue(1f); changed = true; }
                if (GUILayout.Button("×10", EditorStyles.miniButtonMid)) { SetUniformScaleValue(10f); changed = true; }
                if (GUILayout.Button("×100", EditorStyles.miniButtonRight)) { SetUniformScaleValue(100f); changed = true; }
            }

            EditorGUILayout.Space(4f);

            // 位置オフセット
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _positionOffset = EditorGUILayout.Vector3Field("位置オフセット", _positionOffset);
                if (EditorGUI.EndChangeCheck())
                    changed = true;

                using (new EditorGUI.DisabledScope(!ready))
                {
                    if (GUILayout.Button(new GUIContent("中心合わせ", "現在の回転・スケールのまま、バウンディングボックス中心が元の参照メッシュ中心と一致するよう位置オフセットを計算します。"), EditorStyles.miniButton, GUILayout.Width(68f)))
                    {
                        AlignCenterOnly();
                        changed = true;
                    }
                }
            }

            // ミラー＆ボーン設定
            EditorGUILayout.Space(3f);

            EditorGUI.BeginChangeCheck();
            _mirrorX = EditorGUILayout.ToggleLeft(
                new GUIContent("X軸ミラー反転（面の裏返り・法線・Tangent符号も自動補正）", "左右反転している場合のみONにしてください。負スケールによるポリゴン裏返りを防ぎます。"),
                _mirrorX);

            _fixBonesAndBindposes = EditorGUILayout.ToggleLeft(
                new GUIContent("ボーン順リマップ＆元バインドポーズ引き継ぎ（推奨）", "対象SkinnedMeshRendererのbones配列順に合わせてウェイト番号を並べ替え、Blenderのボーン軸変更によるねじれを防ぎます。"),
                _fixBonesAndBindposes);

            if (srcMesh != null && _lastBoneSummary.SourceWeightedVertexCount == 0)
            {
                _transferWeightsWhenMissing = EditorGUILayout.ToggleLeft(
                    new GUIContent("新メッシュにウェイトがない場合、元の参照メッシュから最近傍ウェイトを自動転写する", "Blenderからメッシュ単体でエクスポートしてArmatureウェイトが消えている場合に、元のメッシュからボーンウェイトを自動コピーします。"),
                    _transferWeightsWhenMissing);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _recalculateNormals = EditorGUILayout.ToggleLeft("法線を再計算", _recalculateNormals, GUILayout.Width(130f));
                _recalculateTangents = EditorGUILayout.ToggleLeft("Tangentを再計算", _recalculateTangents);
            }

            if (EditorGUI.EndChangeCheck())
                changed = true;
        }

        if (changed)
            UpdateBoneSummaryPreview();

        return changed;
    }

    // ─────────────────────────────────────────────────────────
    //  ③ プレビューセクション
    // ─────────────────────────────────────────────────────────

    private void DrawPreviewSection(bool paramsChanged)
    {
        Mesh srcMesh = GetSelectedSourceMesh();
        bool canPreview = _targetSmr != null && IsSceneObject(_targetSmr.gameObject) && srcMesh != null;

        if (paramsChanged && _isPreviewActive && _autoUpdatePreview && canPreview)
        {
            _previewShowOriginal = false;
            StartOrUpdatePreview();
        }

        EditorGUILayout.LabelField("③ シーンプレビュー", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            using (new EditorGUI.DisabledScope(!canPreview))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Color prevBg = GUI.backgroundColor;
                    if (_isPreviewActive)
                        GUI.backgroundColor = new Color(0.55f, 0.95f, 0.65f, 1f);

                    string btnLabel = _isPreviewActive
                        ? "👁 プレビュー中（クリックで終了・元に戻す）"
                        : "👁 シーン上でプレビュー開始";

                    if (GUILayout.Button(btnLabel, _isPreviewActive ? PreviewActiveButtonStyle : PreviewIdleButtonStyle, GUILayout.Height(28f)))
                    {
                        if (_isPreviewActive)
                        {
                            StopPreview();
                            SetStatus("プレビューを終了し、元のメッシュ表示に戻しました。", MessageType.Info);
                        }
                        else
                        {
                            _previewShowOriginal = false;
                            StartOrUpdatePreview();
                        }
                    }

                    GUI.backgroundColor = prevBg;

                    if (_isPreviewActive)
                    {
                        Color cmpBg = GUI.backgroundColor;
                        if (_previewShowOriginal)
                            GUI.backgroundColor = new Color(1f, 0.85f, 0.45f, 1f);

                        string cmpLabel = _previewShowOriginal ? "現在: 変更前を表示中" : "変更前と比較";
                        if (GUILayout.Button(new GUIContent(cmpLabel, "クリックするたびに補正後プレビューと変更前の元のMesh表示を切り替えて比較できます。"), GUILayout.Width(130f), GUILayout.Height(28f)))
                        {
                            _previewShowOriginal = !_previewShowOriginal;
                            ApplyPreviewVisibilityState();
                        }

                        GUI.backgroundColor = cmpBg;
                    }
                }
            }

            _autoUpdatePreview = EditorGUILayout.ToggleLeft(
                "プレビュー中にパラメータを変更したらリアルタイム更新する",
                _autoUpdatePreview);

            if (_isPreviewActive)
            {
                EditorGUILayout.LabelField(
                    "※プレビュー用の一時メッシュを表示中です。ウィンドウを閉じるか「プレビュー中」ボタンでいつでも元に戻ります。",
                    WrapMiniStyle);
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  ④ 保存モードと実行セクション
    // ─────────────────────────────────────────────────────────

    private bool DrawSaveSection()
    {
        bool triggerSave = false;
        Mesh srcMesh = GetSelectedSourceMesh();
        string srcAssetPath = srcMesh != null ? AssetDatabase.GetAssetPath(srcMesh) : "";
        bool isSourceFbxInAssets = IsEditableFbxAssetPath(srcAssetPath);

        EditorGUILayout.LabelField("④ 保存方法と実行", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            int modeIdx = (int)_saveMode;
            int nextModeIdx = GUILayout.Toolbar(modeIdx, new[]
            {
                ".mesh 形式で新規作成",
                ".fbx 形式で上書き保存"
            }, GUILayout.Height(24f));
            if (nextModeIdx != modeIdx)
                _saveMode = (SaveMode)nextModeIdx;

            EditorGUILayout.Space(4f);

            if (_saveMode == SaveMode.CreateNewMesh)
            {
                string autoFolder = DetermineDefaultMeshSaveFolder(srcAssetPath);
                string displayFolder = string.IsNullOrEmpty(_meshSaveFolder) ? autoFolder : _meshSaveFolder;

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    string edited = EditorGUILayout.TextField("保存先フォルダ", displayFolder);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _meshSaveFolder = edited == autoFolder ? "" : edited;
                    }

                    if (GUILayout.Button("参照", GUILayout.Width(44f)))
                    {
                        string absInit = Directory.Exists(displayFolder) ? displayFolder : "Assets";
                        string picked = EditorUtility.OpenFolderPanel("保存先フォルダを選択", absInit, "");
                        if (!string.IsNullOrEmpty(picked))
                        {
                            string rel = ToAssetsRelativePath(picked);
                            if (!string.IsNullOrEmpty(rel))
                                _meshSaveFolder = rel;
                            else
                                SetStatus("プロジェクトの Assets フォルダ配下を選択してください。", MessageType.Warning);
                        }
                    }

                    if (!string.IsNullOrEmpty(_meshSaveFolder) && GUILayout.Button("自動", GUILayout.Width(42f)))
                    {
                        _meshSaveFolder = "";
                    }
                }

                if (srcMesh != null)
                {
                    string baseName = StripUpdatedSuffix(srcMesh.name);
                    EditorGUILayout.LabelField($"生成ファイル例: {displayFolder}/{baseName}{UPDATED_SUFFIX_MARKER}000.mesh", WrapMiniStyle);
                }

                _assignToSmrAfterSave = EditorGUILayout.ToggleLeft(
                    "作成後に SkinnedMeshRenderer の Mesh 欄へ自動適用する",
                    _assignToSmrAfterSave);
            }
            else
            {
                if (srcMesh == null)
                {
                    EditorGUILayout.HelpBox("新しい FBX アセット（またはFBX内のMesh）を①に指定してください。", MessageType.Info);
                }
                else if (!isSourceFbxInAssets)
                {
                    EditorGUILayout.HelpBox(
                        $"選択中のメッシュ（{(string.IsNullOrEmpty(srcAssetPath) ? "メモリ上のMesh" : srcAssetPath)}）は Assets 配下の .fbx ファイルではないため、FBX上書きはできません。\n" +
                        "「.mesh 形式で新規作成」を選ぶか、Assets フォルダ内の .fbx を指定してください。",
                        MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.LabelField($"上書き対象FBX: {srcAssetPath}", WrapMiniStyle);
                    EditorGUILayout.LabelField(
                        "※ .fbx.meta（GUID・SubAsset ID）と同FBX内の他メッシュを維持してBinary FBX 7400形式で上書きします。\n" +
                        "※ 上書き前の元ファイルは Temp 内に自動バックアップされます。",
                        WrapMiniStyle);
                }

                _assignToSmrAfterSave = EditorGUILayout.ToggleLeft(
                    "上書き再インポート後に SkinnedMeshRenderer の Mesh 欄へ自動適用する",
                    _assignToSmrAfterSave);

                if (CanRestoreLastFbxBackup())
                {
                    EditorGUILayout.Space(2f);
                    if (GUILayout.Button(
                        new GUIContent($"↩ 直前に上書きしたFBXをバックアップから復元 ({Path.GetFileName(_lastOverwrittenFbxAssetPath)})", _lastOverwrittenFbxAssetPath),
                        GUILayout.Height(22f)))
                    {
                        RestoreLastFbxBackup();
                    }
                }
            }

            EditorGUILayout.Space(6f);

            bool canExecute = _targetSmr != null
                              && IsSceneObject(_targetSmr.gameObject)
                              && srcMesh != null
                              && (_saveMode == SaveMode.CreateNewMesh || isSourceFbxInAssets);

            using (new EditorGUI.DisabledScope(!canExecute))
            {
                string execLabel = _saveMode == SaveMode.CreateNewMesh
                    ? "新しい .mesh を作成して更新"
                    : "FBX を上書き保存して更新";

                if (GUILayout.Button(execLabel, ExecuteButtonStyle, GUILayout.Height(36f)))
                {
                    triggerSave = true;
                }
            }
        }

        return triggerSave;
    }

    // ─────────────────────────────────────────────────────────
    //  ターゲット＆ソース解決ヘルパー
    // ─────────────────────────────────────────────────────────

    private static bool IsSceneObject(GameObject go)
    {
        return go != null && go.scene.IsValid() && !EditorUtility.IsPersistent(go);
    }

    private Mesh GetCurrentTargetUnpreviewedMesh()
    {
        if (_targetSmr == null) return null;
        if (_isPreviewActive && _previewSmr == _targetSmr)
            return _originalSmrMeshBeforePreview;
        return _targetSmr.sharedMesh;
    }

    private void AcceptSourceObject(UnityEngine.Object obj, bool runAutoAlign)
    {
        if (obj == null)
        {
            _sourceObject = null;
            _availableSourceMeshes.Clear();
            _availableSourceMeshLabels = Array.Empty<string>();
            _hasBoneSummary = false;
            if (_isPreviewActive) StopPreview();
            return;
        }

        if (obj is SkinnedMeshRenderer smrObj && smrObj.sharedMesh != null)
            obj = smrObj.sharedMesh;
        else if (obj is MeshFilter mfObj && mfObj.sharedMesh != null)
            obj = mfObj.sharedMesh;

        if (!(obj is Mesh) && !(obj is GameObject))
        {
            SetStatus("FBXモデル（GameObject）または Mesh アセットを指定してください。", MessageType.Warning);
            return;
        }

        _sourceObject = obj;
        RefreshAvailableSourceMeshes(autoSelectBestMatch: true);

        if (obj is Mesh directMesh)
        {
            int idx = _availableSourceMeshes.IndexOf(directMesh);
            if (idx >= 0) _selectedSubMeshAssetIndex = idx;
        }

        UpdateBoneSummaryPreview();

        if (runAutoAlign && _targetSmr != null && GetSelectedSourceMesh() != null)
        {
            RunAutoAlignBones(silentIfAlreadyAligned: false);
            if (_isPreviewActive && _autoUpdatePreview)
                StartOrUpdatePreview();
        }
    }

    private void RefreshAvailableSourceMeshes(bool autoSelectBestMatch)
    {
        _availableSourceMeshes.Clear();
        _availableSourceMeshLabels = Array.Empty<string>();

        if (_sourceObject == null)
            return;

        if (_sourceObject is Mesh m)
        {
            string assetPath = AssetDatabase.GetAssetPath(m);
            if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                var allMeshes = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Mesh>()
                    .Where(x => x != null && !x.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.name, StringComparer.Ordinal)
                    .ToList();
                if (allMeshes.Count > 0)
                {
                    _availableSourceMeshes.AddRange(allMeshes);
                    int exactIdx = _availableSourceMeshes.IndexOf(m);
                    if (exactIdx >= 0)
                        _selectedSubMeshAssetIndex = exactIdx;
                }
                else
                {
                    _availableSourceMeshes.Add(m);
                    _selectedSubMeshAssetIndex = 0;
                }
            }
            else
            {
                _availableSourceMeshes.Add(m);
                _selectedSubMeshAssetIndex = 0;
            }
        }
        else if (_sourceObject is GameObject go)
        {
            string assetPath = AssetDatabase.GetAssetPath(go);
            if (!string.IsNullOrEmpty(assetPath))
            {
                // FBXルート階層から取得できるMeshを優先し、SubAssetも網羅
                var seen = new HashSet<Mesh>();
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr != null && smr.sharedMesh != null && seen.Add(smr.sharedMesh))
                        _availableSourceMeshes.Add(smr.sharedMesh);
                }
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf != null && mf.sharedMesh != null && seen.Add(mf.sharedMesh))
                        _availableSourceMeshes.Add(mf.sharedMesh);
                }
                foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Mesh>())
                {
                    if (sub != null && !sub.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase) && seen.Add(sub))
                        _availableSourceMeshes.Add(sub);
                }
            }
            else
            {
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr != null && smr.sharedMesh != null && !_availableSourceMeshes.Contains(smr.sharedMesh))
                        _availableSourceMeshes.Add(smr.sharedMesh);
                }
            }

            if (autoSelectBestMatch && _availableSourceMeshes.Count > 1)
            {
                _selectedSubMeshAssetIndex = FindBestMatchingMeshIndex(_availableSourceMeshes);
            }
        }

        _selectedSubMeshAssetIndex = Mathf.Clamp(_selectedSubMeshAssetIndex, 0, Mathf.Max(0, _availableSourceMeshes.Count - 1));
        _availableSourceMeshLabels = _availableSourceMeshes
            .Select((mesh, idx) => $"{idx}: {mesh.name} ({mesh.vertexCount:N0} verts)")
            .ToArray();
    }

    private int FindBestMatchingMeshIndex(List<Mesh> candidates)
    {
        Mesh currentTargetMesh = GetCurrentTargetUnpreviewedMesh();
        string preferredName = currentTargetMesh != null ? StripUpdatedSuffix(currentTargetMesh.name) : (_targetSmr != null ? _targetSmr.name : "");
        if (string.IsNullOrEmpty(preferredName))
            return 0;

        for (int i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(StripUpdatedSuffix(candidates[i].name), preferredName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].name.IndexOf(preferredName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                preferredName.IndexOf(candidates[i].name, StringComparison.OrdinalIgnoreCase) >= 0)
                return i;
        }
        return 0;
    }

    private Mesh GetSelectedSourceMesh()
    {
        if (_sourceObject == null) return null;
        if (_availableSourceMeshes.Count == 0)
            RefreshAvailableSourceMeshes(autoSelectBestMatch: false);
        if (_availableSourceMeshes.Count == 0) return null;
        int idx = Mathf.Clamp(_selectedSubMeshAssetIndex, 0, _availableSourceMeshes.Count - 1);
        return _availableSourceMeshes[idx];
    }

    /// <summary>
    /// 比較・バインドポーズ引き継ぎの基準となる元Meshを解決する。
    /// 1. 手動指定があればそれを使用
    /// 2. 対象SMRの現在のMeshが新Meshと異なる場合はそれを使用
    /// 3. 既にSMRのMeshを新Meshへ差し替え済みの場合は、Prefab元の対応SMRのMeshを自動検出
    /// 4. それもない場合（元FBXを直接上書き等）は null を返し、Scene上の targetSmr.bones の現在階層から直接計算する
    /// </summary>
    private Mesh ResolveReferenceMesh(Mesh sourceMesh, out string sourceLabel)
    {
        if (_manualReferenceMesh != null)
        {
            sourceLabel = $"手動指定Mesh: {_manualReferenceMesh.name}";
            return _manualReferenceMesh;
        }

        Mesh currentSmrMesh = GetCurrentTargetUnpreviewedMesh();
        if (currentSmrMesh != null && currentSmrMesh != sourceMesh)
        {
            sourceLabel = $"変更前のSMR Mesh: {currentSmrMesh.name}";
            return currentSmrMesh;
        }

        if (_targetSmr != null)
        {
            var prefabSourceSmr = PrefabUtility.GetCorrespondingObjectFromSource(_targetSmr);
            if (prefabSourceSmr != null && prefabSourceSmr.sharedMesh != null && prefabSourceSmr.sharedMesh != sourceMesh)
            {
                sourceLabel = $"Prefab元のSMR Mesh: {prefabSourceSmr.sharedMesh.name}";
                return prefabSourceSmr.sharedMesh;
            }
        }

        if (_targetSmr != null && _targetSmr.bones != null && _targetSmr.bones.Length > 0)
        {
            sourceLabel = "Scene上のボーン階層（現在の姿勢基準）";
        }
        else
        {
            sourceLabel = "対象SMR未設定";
        }
        return null;
    }

    /// <summary>
    /// 新しいMeshに対応するソース側のボーン配列（Transform[]）とSMRを取得する。
    /// </summary>
    private static bool TryResolveSourceBones(Mesh sourceMesh, UnityEngine.Object sourceObjContext, out Transform[] sourceBones, out SkinnedMeshRenderer sourceSmr)
    {
        sourceBones = null;
        sourceSmr = null;
        if (sourceMesh == null) return false;

        // 1. sourceObjContext が GameObject の場合、その階層から探す
        if (sourceObjContext is GameObject ctxGo)
        {
            foreach (var smr in ctxGo.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr != null && smr.sharedMesh == sourceMesh && smr.bones != null && smr.bones.Length > 0)
                {
                    sourceSmr = smr;
                    sourceBones = smr.bones;
                    return true;
                }
            }
        }

        // 2. sourceMesh のアセットパスから FBX ルート GameObject をロードして探す
        string assetPath = AssetDatabase.GetAssetPath(sourceMesh);
        if (!string.IsNullOrEmpty(assetPath))
        {
            var modelRoot = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (modelRoot != null)
            {
                foreach (var smr in modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr != null && smr.sharedMesh == sourceMesh && smr.bones != null && smr.bones.Length > 0)
                    {
                        sourceSmr = smr;
                        sourceBones = smr.bones;
                        return true;
                    }
                }

                // 名前一致フォールバック
                foreach (var smr in modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr != null && smr.sharedMesh != null && smr.sharedMesh.name == sourceMesh.name && smr.bones != null && smr.bones.Length > 0)
                    {
                        sourceSmr = smr;
                        sourceBones = smr.bones;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 対象SMR側の基準バインドポーズ配列（targetSmr.bones と同じ長さ）を構築する。
    /// </summary>
    private Matrix4x4[] BuildTargetReferenceBindposes(Mesh referenceMesh)
    {
        if (_targetSmr == null || _targetSmr.bones == null)
            return Array.Empty<Matrix4x4>();

        Transform[] dstBones = _targetSmr.bones;
        int n = dstBones.Length;
        var bindposes = new Matrix4x4[n];
        Matrix4x4[] refBps = referenceMesh != null ? referenceMesh.bindposes : null;
        Matrix4x4 smrLocalToWorld = _targetSmr.transform.localToWorldMatrix;

        for (int i = 0; i < n; i++)
        {
            if (refBps != null && i < refBps.Length && IsValidBindposeMatrix(refBps[i]))
            {
                bindposes[i] = refBps[i];
            }
            else if (dstBones[i] != null)
            {
                bindposes[i] = dstBones[i].worldToLocalMatrix * smrLocalToWorld;
            }
            else
            {
                bindposes[i] = Matrix4x4.identity;
            }
        }

        return bindposes;
    }

    private static bool IsValidBindposeMatrix(Matrix4x4 m)
    {
        float det = m.determinant;
        return !float.IsNaN(det) && !float.IsInfinity(det) && Mathf.Abs(det) > 1e-12f;
    }

    // ─────────────────────────────────────────────────────────
    //  自動アライメント（Procrustes ボーン位置解析 ＆ Bounds フィット）
    // ─────────────────────────────────────────────────────────

    private void ResetTransformParams()
    {
        _rotationEuler = Vector3.zero;
        _uniformScale = true;
        _scaleFactor = 1f;
        _scaleXYZ = Vector3.one;
        _positionOffset = Vector3.zero;
        _mirrorX = false;
    }

    private void SetUniformScaleValue(float val)
    {
        _uniformScale = true;
        _scaleFactor = val;
        _scaleXYZ = new Vector3(val, val, val);
    }

    private void ApplyAdditiveRotation(Vector3 addEuler)
    {
        Quaternion q = Quaternion.Euler(addEuler) * Quaternion.Euler(_rotationEuler);
        _rotationEuler = NormalizeEulerSigned(q.eulerAngles);
        _rotationEuler = SnapEulerIfClose(q, _rotationEuler);
    }

    private void AlignCenterOnly()
    {
        Mesh srcMesh = GetSelectedSourceMesh();
        if (_targetSmr == null || srcMesh == null) return;

        Mesh refMesh = ResolveReferenceMesh(srcMesh, out _);
        Bounds targetBounds = GetTargetReferenceBounds(refMesh);

        // オフセット0での変換後Bounds中心を求めて差分をオフセットに設定
        Matrix4x4 linearOnly = BuildTransformMatrix(_rotationEuler, GetCurrentScaleVector(), Vector3.zero, _mirrorX);
        Bounds transformedSrcBounds = TransformMeshBoundsExact(srcMesh, linearOnly);
        _positionOffset = targetBounds.center - transformedSrcBounds.center;
        _positionOffset = SnapVectorNearZero(_positionOffset, 1e-5f);
        SetStatus("バウンディングボックス中心に合わせて位置オフセットを調整しました。", MessageType.Info);
    }

    private Bounds GetTargetReferenceBounds(Mesh refMesh)
    {
        if (refMesh != null && refMesh.vertexCount > 0)
            return refMesh.bounds;

        if (_targetSmr != null)
            return _targetSmr.localBounds;

        return new Bounds(Vector3.zero, Vector3.one);
    }

    /// <summary>
    /// 共通ボーンのバインド空間ヘッド座標から最適相似変換（回転・均一スケール・並進・必要に応じたミラー）を算出する。
    /// 非共線ボーンが3点未満の場合は Bounds 併用ソルバーへ自動フォールバックする。
    /// </summary>
    private void RunAutoAlignBones(bool silentIfAlreadyAligned)
    {
        Mesh srcMesh = GetSelectedSourceMesh();
        if (_targetSmr == null || srcMesh == null)
            return;

        Mesh refMesh = ResolveReferenceMesh(srcMesh, out _);
        Matrix4x4[] dstBindposes = BuildTargetReferenceBindposes(refMesh);
        Transform[] dstBones = _targetSmr.bones;

        var srcPoints = new List<Vector3>();
        var dstPoints = new List<Vector3>();

        if (dstBones != null && dstBones.Length > 0 && TryResolveSourceBones(srcMesh, _sourceObject, out Transform[] srcBones, out SkinnedMeshRenderer srcSmr))
        {
            int[] boneMap = BuildBoneIndexMap(srcBones, dstBones, out _, out _, out _);
            Matrix4x4[] srcBindposes = srcMesh.bindposes;

            for (int j = 0; j < srcBones.Length; j++)
            {
                int dstIdx = boneMap[j];
                if (dstIdx < 0 || dstIdx >= dstBindposes.Length || srcBones[j] == null || dstBones[dstIdx] == null)
                    continue;

                Vector3 pSrc;
                if (srcBindposes != null && j < srcBindposes.Length && IsValidBindposeMatrix(srcBindposes[j]))
                {
                    Matrix4x4 invSrc = srcBindposes[j].inverse;
                    pSrc = new Vector3(invSrc.m03, invSrc.m13, invSrc.m23);
                }
                else if (srcSmr != null)
                {
                    pSrc = srcSmr.transform.InverseTransformPoint(srcBones[j].position);
                }
                else
                {
                    continue;
                }

                Matrix4x4 invDst = dstBindposes[dstIdx].inverse;
                Vector3 pDst = new Vector3(invDst.m03, invDst.m13, invDst.m23);

                srcPoints.Add(pSrc);
                dstPoints.Add(pDst);
            }
        }

        // 非共線なボーン点群が十分あるか判定
        if (HasSufficient3DSpread(srcPoints) && HasSufficient3DSpread(dstPoints))
        {
            SolveSimilarityProcrustes(srcPoints, dstPoints, mirrorX: false, out Quaternion rotNoMirror, out float scaleNoMirror, out Vector3 transNoMirror, out float rmsNoMirror);
            SolveSimilarityProcrustes(srcPoints, dstPoints, mirrorX: true, out Quaternion rotMirror, out float scaleMirror, out Vector3 transMirror, out float rmsMirror);

            // 平面ボーン配置での誤反転を防ぐため、非同一平面かつミラー誤差が明確に小さい場合のみミラーを採用
            bool useMirror = HasNonCoplanarSpread(srcPoints)
                             && rmsNoMirror > 1e-3f
                             && rmsMirror < rmsNoMirror * 0.2f;

            Quaternion bestQ = useMirror ? rotMirror : rotNoMirror;
            float bestScale = useMirror ? scaleMirror : scaleNoMirror;
            Vector3 bestTrans = useMirror ? transMirror : transNoMirror;
            float bestRms = useMirror ? rmsMirror : rmsNoMirror;

            bestScale = SnapScaleIfClose(bestScale);
            Vector3 euler = NormalizeEulerSigned(bestQ.eulerAngles);
            euler = SnapEulerIfClose(bestQ, euler);

            // スナップ後に重心関係から並進を再計算して微小ズレを除去
            Quaternion finalQ = Quaternion.Euler(euler);
            Vector3 cSrc = ComputeCentroid(srcPoints);
            Vector3 cDst = ComputeCentroid(dstPoints);
            if (useMirror) cSrc.x = -cSrc.x;
            bestTrans = cDst - bestScale * (finalQ * cSrc);
            bestTrans = SnapVectorNearZero(bestTrans, 1e-5f);

            _mirrorX = useMirror;
            SetUniformScaleValue(bestScale);
            _rotationEuler = euler;
            _positionOffset = bestTrans;

            if (!silentIfAlreadyAligned || !IsIdentityTransform())
            {
                SetStatus(
                    $"共通ボーン {srcPoints.Count} 点から自動補正しました（回転: ({_rotationEuler.x:0.##}°, {_rotationEuler.y:0.##}°, {_rotationEuler.z:0.##}°) / スケール: ×{_scaleFactor:G5} / 誤差RMS: {bestRms:0.0000}）。",
                    MessageType.Info);
            }
            return;
        }

        // ボーン数が少ない（1〜2本・アクセサリ等）またはボーンなしの場合は Bounds ＆ 形状サンプル併用フィット
        if (refMesh != null && refMesh.vertexCount > 0)
        {
            Vector3? anchorSrc = srcPoints.Count > 0 ? srcPoints[0] : (Vector3?)null;
            Vector3? anchorDst = dstPoints.Count > 0 ? dstPoints[0] : (Vector3?)null;
            RunAutoAlignBoundsInternal(srcMesh, refMesh, refMesh.bounds, anchorSrc, anchorDst);
            SetStatus(
                srcPoints.Count > 0
                    ? $"ボーン数が少ないため（{srcPoints.Count}本）、ボーン位置とメッシュ形状から自動補正しました。"
                    : "共通ボーンが見つからないため、参照メッシュ形状・バウンディングボックスから自動補正しました。",
                MessageType.Info);
        }
        else if (!silentIfAlreadyAligned)
        {
            SetStatus("比較用の共通ボーンまたは参照メッシュが不足しているため、手動プリセット（X -90° / ×100 等）で調整してください。", MessageType.Info);
        }
    }

    private void RunAutoAlignBounds()
    {
        Mesh srcMesh = GetSelectedSourceMesh();
        if (_targetSmr == null || srcMesh == null)
            return;

        Mesh refMesh = ResolveReferenceMesh(srcMesh, out _);
        Bounds targetBounds = GetTargetReferenceBounds(refMesh);
        if (srcMesh.bounds.size.sqrMagnitude < 1e-12f || targetBounds.size.sqrMagnitude < 1e-12f)
        {
            SetStatus("メッシュのサイズが 0 のため Bounds 計算ができません。", MessageType.Warning);
            return;
        }

        RunAutoAlignBoundsInternal(srcMesh, refMesh, targetBounds, null, null);
        SetStatus(
            $"バウンディングボックス・形状基準で自動補正しました（回転: ({_rotationEuler.x:0.##}°, {_rotationEuler.y:0.##}°, {_rotationEuler.z:0.##}°) / スケール: ×{_scaleFactor:G5}）。",
            MessageType.Info);
    }

    private void RunAutoAlignBoundsInternal(Mesh srcMesh, Mesh refMesh, Bounds targetBounds, Vector3? anchorSrc, Vector3? anchorDst)
    {
        Bounds srcBounds = srcMesh.bounds;
        float srcDiag = srcBounds.size.magnitude;
        float dstDiag = targetBounds.size.magnitude;
        float diagScale = srcDiag > 1e-8f ? SnapScaleIfClose(dstDiag / srcDiag) : 1f;

        Vector3[] srcSample = SubsampleVertices(srcMesh, 96);
        Vector3[] refSample = (refMesh != null && refMesh.vertexCount > 0) ? SubsampleVertices(refMesh, 384) : null;

        // 一部造形追加（耳や装飾追加）でBounds比が微妙に変わったケースでも標準単位スケール（×100等）を検出できるよう候補に含める
        var scaleCandidates = new List<float> { diagScale };
        if (refSample != null)
        {
            float[] stdScales = { 0.001f, 0.01f, 0.1f, 1f, 10f, 100f, 1000f };
            foreach (float sc in stdScales)
            {
                if (sc >= diagScale * 0.45f && sc <= diagScale * 2.2f && Mathf.Abs(sc - diagScale) / sc > 1e-4f)
                    scaleCandidates.Add(sc);
            }
        }

        Vector3[] candidateEulers = Generate24AxisRotations();
        Quaternion bestRot = Quaternion.identity;
        Vector3 bestEuler = Vector3.zero;
        float bestScale = diagScale;
        Vector3 bestTrans = Vector3.zero;
        float bestScore = float.MaxValue;

        var sqDists = srcSample != null ? new float[srcSample.Length] : null;
        var nearestOffsets = srcSample != null ? new Vector3[srcSample.Length] : null;
        var orderIndices = srcSample != null ? new int[srcSample.Length] : null;

        foreach (float candScale in scaleCandidates)
        {
            for (int i = 0; i < candidateEulers.Length; i++)
            {
                Vector3 e = candidateEulers[i];
                Quaternion q = Quaternion.Euler(e);
                Vector3 rotatedSize = AbsRotateExtents(q, srcBounds.size * candScale);
                float sizeDiff = (rotatedSize - targetBounds.size).magnitude / Mathf.Max(dstDiag, 1e-4f);

                Vector3 candTrans = (anchorSrc.HasValue && anchorDst.HasValue)
                    ? (anchorDst.Value - candScale * (q * anchorSrc.Value))
                    : (targetBounds.center - candScale * (q * srcBounds.center));

                // 単調増加の微小プライオリティ（完全対称な立方体等では 無回転 > X-90° > X+90° > Y180° の順に優先）
                float priorPenalty = i * 0.0004f;
                float score;

                if (refSample != null && srcSample != null && srcSample.Length > 0)
                {
                    int trimCount = Mathf.Max(1, Mathf.RoundToInt(srcSample.Length * 0.75f));

                    for (int s = 0; s < srcSample.Length; s++)
                    {
                        Vector3 tp = candScale * (q * srcSample[s]) + candTrans;
                        Vector3 nearest = FindNearestInSample(refSample, tp, out float d2);
                        sqDists[s] = d2;
                        nearestOffsets[s] = nearest - tp;
                        orderIndices[s] = s;
                    }
                    Array.Sort(sqDists, orderIndices);

                    float baseSumSq = 0f;
                    Vector3 shift = Vector3.zero;
                    for (int k = 0; k < trimCount; k++)
                    {
                        baseSumSq += sqDists[k];
                        shift += nearestOffsets[orderIndices[k]];
                    }
                    float bestCandSumSq = baseSumSq;

                    // 装飾追加等でBounds中心自体が大きくズレている場合のみ並進ICPシフトを採用
                    if (!anchorSrc.HasValue)
                    {
                        Vector3 icpTrans = candTrans + shift / trimCount;
                        for (int s = 0; s < srcSample.Length; s++)
                        {
                            Vector3 tp = candScale * (q * srcSample[s]) + icpTrans;
                            FindNearestInSample(refSample, tp, out float d2);
                            sqDists[s] = d2;
                        }
                        Array.Sort(sqDists);
                        float icpSumSq = 0f;
                        for (int k = 0; k < trimCount; k++)
                            icpSumSq += sqDists[k];

                        if (icpSumSq < baseSumSq * 0.35f)
                        {
                            candTrans = icpTrans;
                            bestCandSumSq = icpSumSq;
                        }
                    }

                    float trimmedRms = Mathf.Sqrt(bestCandSumSq / trimCount) / Mathf.Max(dstDiag, 1e-4f);
                    score = trimmedRms + sizeDiff * 0.15f + priorPenalty;
                }
                else
                {
                    score = sizeDiff + priorPenalty;
                    if (anchorSrc.HasValue && anchorDst.HasValue)
                    {
                        Vector3 predictedCenter = anchorDst.Value + candScale * (q * (srcBounds.center - anchorSrc.Value));
                        float centerErr = Vector3.Distance(predictedCenter, targetBounds.center) / Mathf.Max(dstDiag, 1e-4f);
                        score += centerErr * 0.5f;
                    }
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestRot = q;
                    bestEuler = e;
                    bestScale = candScale;
                    bestTrans = candTrans;
                }
            }
        }

        _mirrorX = false;
        SetUniformScaleValue(bestScale);
        _rotationEuler = bestEuler;

        // 参照頂点群がない通常Bounds時は厳密変換後Bounds中心で最終補正
        if (refSample == null && !anchorSrc.HasValue)
        {
            Matrix4x4 linear = BuildTransformMatrix(_rotationEuler, GetCurrentScaleVector(), Vector3.zero, false);
            Bounds transBounds = TransformMeshBoundsExact(srcMesh, linear);
            bestTrans = targetBounds.center - transBounds.center;
        }
        _positionOffset = SnapVectorNearZero(bestTrans, 1e-5f);
    }

    private static Vector3[] SubsampleVertices(Mesh mesh, int maxSamples)
    {
        if (mesh == null) return null;
        Vector3[] verts = mesh.vertices;
        if (verts == null || verts.Length == 0) return null;
        if (verts.Length <= maxSamples) return verts;

        var sample = new Vector3[maxSamples];
        double step = (double)(verts.Length - 1) / (maxSamples - 1);
        for (int i = 0; i < maxSamples; i++)
            sample[i] = verts[(int)Math.Round(i * step)];
        return sample;
    }

    private static Vector3 FindNearestInSample(Vector3[] sample, Vector3 p, out float bestDistSq)
    {
        bestDistSq = float.MaxValue;
        Vector3 best = Vector3.zero;
        for (int i = 0; i < sample.Length; i++)
        {
            Vector3 diff = sample[i] - p;
            float d2 = diff.x * diff.x + diff.y * diff.y + diff.z * diff.z;
            if (d2 < bestDistSq)
            {
                bestDistSq = d2;
                best = sample[i];
            }
        }
        return best;
    }

    private static Vector3[] Generate24AxisRotations()
    {
        // 代表的なDCC座標変換を先頭に配置しつつ全24通りの右手回転を網羅
        var list = new List<Vector3>
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(-90f, 0f, 0f),
            new Vector3(90f, 0f, 0f),
            new Vector3(0f, 180f, 0f),
            new Vector3(-90f, 180f, 0f),
            new Vector3(90f, 180f, 0f)
        };

        float[] angles = { 0f, 90f, 180f, -90f };
        foreach (float ax in angles)
        {
            foreach (float ay in angles)
            {
                foreach (float az in angles)
                {
                    Vector3 candidate = new Vector3(ax, ay, az);
                    Quaternion cq = Quaternion.Euler(candidate);
                    bool exists = false;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (Quaternion.Angle(cq, Quaternion.Euler(list[i])) < 1f)
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                        list.Add(candidate);
                }
            }
        }
        return list.ToArray();
    }

    private static Vector3 AbsRotateExtents(Quaternion q, Vector3 size)
    {
        Matrix4x4 m = Matrix4x4.Rotate(q);
        return new Vector3(
            Mathf.Abs(m.m00) * size.x + Mathf.Abs(m.m01) * size.y + Mathf.Abs(m.m02) * size.z,
            Mathf.Abs(m.m10) * size.x + Mathf.Abs(m.m11) * size.y + Mathf.Abs(m.m12) * size.z,
            Mathf.Abs(m.m20) * size.x + Mathf.Abs(m.m21) * size.y + Mathf.Abs(m.m22) * size.z);
    }

    /// <summary>
    /// Horn (1987) の単位四元数法による3D相似変換 Procrustes ソルバー: dst ≈ scale * R * (mirror(src)) + trans
    /// </summary>
    private static void SolveSimilarityProcrustes(
        List<Vector3> srcPoints,
        List<Vector3> dstPoints,
        bool mirrorX,
        out Quaternion rotation,
        out float scale,
        out Vector3 translation,
        out float rmsError)
    {
        int n = srcPoints.Count;
        Vector3 cSrc = Vector3.zero;
        Vector3 cDst = Vector3.zero;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = srcPoints[i];
            if (mirrorX) p.x = -p.x;
            cSrc += p;
            cDst += dstPoints[i];
        }
        cSrc /= n;
        cDst /= n;

        double sxx = 0, sxy = 0, sxz = 0;
        double syx = 0, syy = 0, syz = 0;
        double szx = 0, szy = 0, szz = 0;
        double sumSrcSq = 0;

        for (int i = 0; i < n; i++)
        {
            Vector3 p = srcPoints[i];
            if (mirrorX) p.x = -p.x;
            Vector3 x = p - cSrc;
            Vector3 y = dstPoints[i] - cDst;

            sxx += (double)x.x * y.x; sxy += (double)x.x * y.y; sxz += (double)x.x * y.z;
            syx += (double)x.y * y.x; syy += (double)x.y * y.y; syz += (double)x.y * y.z;
            szx += (double)x.z * y.x; szy += (double)x.z * y.y; szz += (double)x.z * y.z;
            sumSrcSq += (double)x.x * x.x + (double)x.y * x.y + (double)x.z * x.z;
        }

        double[,] N = new double[4, 4];
        N[0, 0] = sxx + syy + szz;
        N[0, 1] = N[1, 0] = syz - szy;
        N[0, 2] = N[2, 0] = szx - sxz;
        N[0, 3] = N[3, 0] = sxy - syx;

        N[1, 1] = sxx - syy - szz;
        N[1, 2] = N[2, 1] = sxy + syx;
        N[1, 3] = N[3, 1] = szx + sxz;

        N[2, 2] = -sxx + syy - szz;
        N[2, 3] = N[3, 2] = syz + szy;

        N[3, 3] = -sxx - syy + szz;

        double[] maxEigVec = MaxEigenvectorSymmetric4x4(N);
        // maxEigVec = (w, x, y, z)
        rotation = new Quaternion((float)maxEigVec[1], (float)maxEigVec[2], (float)maxEigVec[3], (float)maxEigVec[0]).normalized;

        double dotSum = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = srcPoints[i];
            if (mirrorX) p.x = -p.x;
            Vector3 rx = rotation * (p - cSrc);
            Vector3 y = dstPoints[i] - cDst;
            dotSum += Vector3.Dot(rx, y);
        }

        scale = sumSrcSq > 1e-14 ? (float)(dotSum / sumSrcSq) : 1f;
        if (scale <= 1e-6f) scale = 1f;

        translation = cDst - scale * (rotation * cSrc);

        double errSq = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = srcPoints[i];
            if (mirrorX) p.x = -p.x;
            Vector3 pred = scale * (rotation * p) + translation;
            errSq += (pred - dstPoints[i]).sqrMagnitude;
        }
        rmsError = Mathf.Sqrt((float)(errSq / n));
    }

    /// <summary>
    /// 4x4 実対称行列の最大固有値に対応する正規化固有ベクトルをヤコビ回転法で求める。
    /// </summary>
    private static double[] MaxEigenvectorSymmetric4x4(double[,] A)
    {
        double[,] V = new double[4, 4];
        for (int i = 0; i < 4; i++) V[i, i] = 1.0;

        for (int sweep = 0; sweep < 20; sweep++)
        {
            double maxOff = 0.0;
            for (int p = 0; p < 3; p++)
                for (int q = p + 1; q < 4; q++)
                    maxOff = Math.Max(maxOff, Math.Abs(A[p, q]));

            if (maxOff < 1e-15)
                break;

            for (int p = 0; p < 3; p++)
            {
                for (int q = p + 1; q < 4; q++)
                {
                    double apq = A[p, q];
                    if (Math.Abs(apq) < 1e-15) continue;

                    double tau = (A[q, q] - A[p, p]) / (2.0 * apq);
                    double t = tau >= 0.0
                        ? 1.0 / (tau + Math.Sqrt(1.0 + tau * tau))
                        : -1.0 / (-tau + Math.Sqrt(1.0 + tau * tau));
                    double c = 1.0 / Math.Sqrt(1.0 + t * t);
                    double s = t * c;

                    double app = A[p, p];
                    double aqq = A[q, q];
                    A[p, p] = app - t * apq;
                    A[q, q] = aqq + t * apq;
                    A[p, q] = A[q, p] = 0.0;

                    for (int r = 0; r < 4; r++)
                    {
                        if (r == p || r == q) continue;
                        double arp = A[r, p];
                        double arq = A[r, q];
                        A[r, p] = A[p, r] = c * arp - s * arq;
                        A[r, q] = A[q, r] = s * arp + c * arq;
                    }

                    for (int r = 0; r < 4; r++)
                    {
                        double vrp = V[r, p];
                        double vrq = V[r, q];
                        V[r, p] = c * vrp - s * vrq;
                        V[r, q] = s * vrp + c * vrq;
                    }
                }
            }
        }

        int bestCol = 0;
        double bestVal = A[0, 0];
        for (int i = 1; i < 4; i++)
        {
            if (A[i, i] > bestVal)
            {
                bestVal = A[i, i];
                bestCol = i;
            }
        }

        double[] vec = { V[0, bestCol], V[1, bestCol], V[2, bestCol], V[3, bestCol] };
        if (vec[0] < 0)
        {
            for (int i = 0; i < 4; i++) vec[i] = -vec[i];
        }
        return vec;
    }

    private static bool HasSufficient3DSpread(List<Vector3> pts)
    {
        if (pts == null || pts.Count < 3) return false;
        Vector3 p0 = pts[0];
        float maxDistSq = 0f;
        int farIdx = -1;
        for (int i = 1; i < pts.Count; i++)
        {
            float d2 = (pts[i] - p0).sqrMagnitude;
            if (d2 > maxDistSq) { maxDistSq = d2; farIdx = i; }
        }
        if (farIdx < 0 || maxDistSq < 1e-8f) return false;

        Vector3 dir = (pts[farIdx] - p0).normalized;
        float maxLineDistSq = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            Vector3 v = pts[i] - p0;
            Vector3 perp = v - Vector3.Dot(v, dir) * dir;
            if (perp.sqrMagnitude > maxLineDistSq)
                maxLineDistSq = perp.sqrMagnitude;
        }

        return maxLineDistSq > maxDistSq * 1e-4f && maxLineDistSq > 1e-8f;
    }

    private static bool HasNonCoplanarSpread(List<Vector3> pts)
    {
        if (pts == null || pts.Count < 4) return false;
        Vector3 p0 = pts[0];
        int i1 = -1, i2 = -1;
        float maxD2 = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            float d2 = (pts[i] - p0).sqrMagnitude;
            if (d2 > maxD2) { maxD2 = d2; i1 = i; }
        }
        if (i1 < 0 || maxD2 < 1e-8f) return false;

        Vector3 u = (pts[i1] - p0).normalized;
        float maxPerp2 = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            Vector3 v = pts[i] - p0;
            Vector3 perp = v - Vector3.Dot(v, u) * u;
            if (perp.sqrMagnitude > maxPerp2) { maxPerp2 = perp.sqrMagnitude; i2 = i; }
        }
        if (i2 < 0 || maxPerp2 < 1e-8f) return false;

        Vector3 planeNormal = Vector3.Cross(u, pts[i2] - p0).normalized;
        for (int i = 1; i < pts.Count; i++)
        {
            float dist = Mathf.Abs(Vector3.Dot(pts[i] - p0, planeNormal));
            if (dist * dist > maxD2 * 1e-4f)
                return true;
        }
        return false;
    }

    private static Vector3 ComputeCentroid(List<Vector3> pts)
    {
        if (pts == null || pts.Count == 0) return Vector3.zero;
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < pts.Count; i++) sum += pts[i];
        return sum / pts.Count;
    }

    private static Vector3 NormalizeEulerSigned(Vector3 e)
    {
        return new Vector3(WrapSigned180(e.x), WrapSigned180(e.y), WrapSigned180(e.z));
    }

    private static float WrapSigned180(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg <= -180f) deg += 360f;
        if (Mathf.Abs(deg) < 1e-4f) deg = 0f;
        return deg;
    }

    private static Vector3 SnapEulerIfClose(Quaternion originalQ, Vector3 euler)
    {
        Vector3 snapped = new Vector3(
            SnapAngleTo90Multiple(euler.x),
            SnapAngleTo90Multiple(euler.y),
            SnapAngleTo90Multiple(euler.z));

        if (Quaternion.Angle(originalQ, Quaternion.Euler(snapped)) < 0.03f)
            return snapped;
        return euler;
    }

    private static float SnapAngleTo90Multiple(float deg)
    {
        float nearest = Mathf.Round(deg / 90f) * 90f;
        if (Mathf.Abs(deg - nearest) < 0.03f)
            return WrapSigned180(nearest);
        return deg;
    }

    private static float SnapScaleIfClose(float s)
    {
        float[] commonScales = { 0.001f, 0.01f, 0.1f, 1f, 10f, 100f, 1000f };
        foreach (float c in commonScales)
        {
            if (Mathf.Abs(s - c) / c < 5e-4f)
                return c;
        }
        return s;
    }

    private static Vector3 SnapVectorNearZero(Vector3 v, float eps)
    {
        return new Vector3(
            Mathf.Abs(v.x) < eps ? 0f : v.x,
            Mathf.Abs(v.y) < eps ? 0f : v.y,
            Mathf.Abs(v.z) < eps ? 0f : v.z);
    }

    private Vector3 GetCurrentScaleVector()
    {
        return _uniformScale ? new Vector3(_scaleFactor, _scaleFactor, _scaleFactor) : _scaleXYZ;
    }

    private bool IsIdentityTransform()
    {
        Vector3 s = GetCurrentScaleVector();
        return !_mirrorX
               && _rotationEuler.sqrMagnitude < 1e-4f
               && (s - Vector3.one).sqrMagnitude < 1e-6f
               && _positionOffset.sqrMagnitude < 1e-6f;
    }

    private static Matrix4x4 BuildTransformMatrix(Vector3 eulerDeg, Vector3 scale, Vector3 offset, bool mirrorX)
    {
        Matrix4x4 f = mirrorX ? Matrix4x4.Scale(new Vector3(-1f, 1f, 1f)) : Matrix4x4.identity;
        Matrix4x4 s = Matrix4x4.Scale(scale);
        Matrix4x4 r = Matrix4x4.Rotate(Quaternion.Euler(eulerDeg));
        Matrix4x4 t = Matrix4x4.Translate(offset);
        return t * r * s * f;
    }

    private static Bounds TransformMeshBoundsExact(Mesh mesh, Matrix4x4 mat)
    {
        Vector3[] verts = mesh.vertices;
        if (verts == null || verts.Length == 0)
            return new Bounds(mat.MultiplyPoint3x4(Vector3.zero), Vector3.zero);

        Vector3 first = mat.MultiplyPoint3x4(verts[0]);
        Vector3 min = first;
        Vector3 max = first;
        for (int i = 1; i < verts.Length; i++)
        {
            Vector3 p = mat.MultiplyPoint3x4(verts[i]);
            if (p.x < min.x) min.x = p.x;
            if (p.y < min.y) min.y = p.y;
            if (p.z < min.z) min.z = p.z;
            if (p.x > max.x) max.x = p.x;
            if (p.y > max.y) max.y = p.y;
            if (p.z > max.z) max.z = p.z;
        }
        Bounds b = new Bounds();
        b.SetMinMax(min, max);
        return b;
    }

    // ─────────────────────────────────────────────────────────
    //  ボーン対応付け＆メッシュ再構築エンジン
    // ─────────────────────────────────────────────────────────

    private void UpdateBoneSummaryPreview()
    {
        _hasBoneSummary = false;
        Mesh srcMesh = GetSelectedSourceMesh();
        if (srcMesh == null) return;

        var summary = new BoneMappingSummary
        {
            SourceWeightedVertexCount = CountWeightedVertices(srcMesh),
            TargetBoneCount = _targetSmr != null && _targetSmr.bones != null ? _targetSmr.bones.Length : 0
        };

        if (_targetSmr != null && _targetSmr.bones != null && _targetSmr.bones.Length > 0 &&
            TryResolveSourceBones(srcMesh, _sourceObject, out Transform[] srcBones, out _))
        {
            summary.SourceBoneCount = srcBones.Length;
            BuildBoneIndexMap(srcBones, _targetSmr.bones, out summary.DirectMatchCount, out summary.ParentFallbackCount, out summary.UnmatchedCount);
        }

        _lastBoneSummary = summary;
        _hasBoneSummary = true;
    }

    /// <summary>
    /// srcBones[j] -> dstBones[i] のインデックス対応表を作成する。
    /// 直接一致しないボーンは srcBone.parent を遡って最も近い親ボーンへ自動集約する。
    /// </summary>
    private static int[] BuildBoneIndexMap(
        Transform[] srcBones,
        Transform[] dstBones,
        out int directCount,
        out int parentFallbackCount,
        out int unmatchedCount)
    {
        directCount = 0;
        parentFallbackCount = 0;
        unmatchedCount = 0;

        int srcLen = srcBones != null ? srcBones.Length : 0;
        int[] map = new int[srcLen];
        for (int j = 0; j < srcLen; j++) map[j] = -1;
        if (srcLen == 0 || dstBones == null || dstBones.Length == 0)
            return map;

        // 1. 完全名一致および親付き名一致の辞書構築
        var exactMap = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var normMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < dstBones.Length; i++)
        {
            Transform db = dstBones[i];
            if (db == null) continue;

            if (!exactMap.TryGetValue(db.name, out var list))
            {
                list = new List<int>();
                exactMap[db.name] = list;
            }
            list.Add(i);

            string norm = NormalizeBoneName(db.name);
            if (!normMap.TryGetValue(norm, out var nList))
            {
                nList = new List<int>();
                normMap[norm] = nList;
            }
            nList.Add(i);
        }

        int TryFindTargetIndex(Transform sb)
        {
            if (sb == null) return -1;

            if (exactMap.TryGetValue(sb.name, out var candidates))
            {
                if (candidates.Count == 1)
                    return candidates[0];

                string parentName = sb.parent != null ? sb.parent.name : "";
                foreach (int c in candidates)
                {
                    string dstParent = dstBones[c].parent != null ? dstBones[c].parent.name : "";
                    if (string.Equals(parentName, dstParent, StringComparison.Ordinal))
                        return c;
                }
                return candidates[0];
            }

            string norm = NormalizeBoneName(sb.name);
            if (normMap.TryGetValue(norm, out var normCandidates) && normCandidates.Count == 1)
            {
                return normCandidates[0];
            }

            return -1;
        }

        for (int j = 0; j < srcLen; j++)
        {
            Transform sb = srcBones[j];
            if (sb == null)
            {
                unmatchedCount++;
                continue;
            }

            int direct = TryFindTargetIndex(sb);
            if (direct >= 0)
            {
                map[j] = direct;
                directCount++;
                continue;
            }

            // 親階層を遡ってフォールバック検索
            Transform cur = sb.parent;
            int ancestorIdx = -1;
            while (cur != null)
            {
                ancestorIdx = TryFindTargetIndex(cur);
                if (ancestorIdx >= 0) break;
                cur = cur.parent;
            }

            if (ancestorIdx >= 0)
            {
                map[j] = ancestorIdx;
                parentFallbackCount++;
            }
            else
            {
                unmatchedCount++;
            }
        }

        return map;
    }

    private static string NormalizeBoneName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string s = name.Trim();
        int pipeIdx = s.LastIndexOf('|');
        if (pipeIdx >= 0 && pipeIdx + 1 < s.Length)
            s = s.Substring(pipeIdx + 1);
        // Blender の重複サフィックス (.001 等) を除去
        s = Regex.Replace(s, @"\.\d{3}$", "");
        return s;
    }

    /// <summary>
    /// 現在のパラメータ設定に基づいて補正済み Mesh を生成する。
    /// </summary>
    private Mesh BuildAlignedMesh(Mesh sourceMesh, string outputName, out BoneMappingSummary summary)
    {
        summary = default;
        if (sourceMesh == null) return null;

        Mesh refMesh = ResolveReferenceMesh(sourceMesh, out _);
        Matrix4x4 cMat = BuildTransformMatrix(_rotationEuler, GetCurrentScaleVector(), _positionOffset, _mirrorX);

        // 線形部分 L と 法線変換用逆転置行列 Nmat
        Matrix4x4 linearMat = cMat;
        linearMat.m03 = linearMat.m13 = linearMat.m23 = 0f;
        float det = linearMat.determinant;
        bool flipWinding = det < 0f;
        float absDetCbrt = Mathf.Pow(Mathf.Max(Mathf.Abs(det), 1e-18f), 1f / 3f);

        Matrix4x4 normalMat = Mathf.Abs(det) > 1e-18f ? linearMat.inverse.transpose : Matrix4x4.identity;
        // BlendShape法線・接線デルタ用（スケール倍率を除去して単位長スケールを保持）
        Matrix4x4 unitNormalDeltaMat = ScaleMatrix3x3(normalMat, absDetCbrt);
        Matrix4x4 unitTangentDeltaMat = ScaleMatrix3x3(linearMat, 1f / absDetCbrt);

        int vertexCount = sourceMesh.vertexCount;
        var outMesh = new Mesh
        {
            name = outputName,
            indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
        };

        // 1. 頂点座標
        Vector3[] srcVerts = sourceMesh.vertices;
        Vector3[] dstVerts = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            dstVerts[i] = cMat.MultiplyPoint3x4(srcVerts[i]);
        outMesh.vertices = dstVerts;

        // 2. 法線
        Vector3[] srcNormals = sourceMesh.normals;
        Vector3[] dstNormals = null;
        if (!_recalculateNormals && srcNormals != null && srcNormals.Length == vertexCount)
        {
            dstNormals = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 n = normalMat.MultiplyVector(srcNormals[i]);
                float magSq = n.sqrMagnitude;
                dstNormals[i] = magSq > 1e-16f ? (n / Mathf.Sqrt(magSq)) : Vector3.zero;
            }
            outMesh.normals = dstNormals;
        }

        // 3. 頂点カラー & 全8チャンネルUV（2D/3D/4D維持）
        Color32[] colors = sourceMesh.colors32;
        if (colors != null && colors.Length == vertexCount)
            outMesh.colors32 = colors;

        CopyAllUVChannels(sourceMesh, outMesh);

        // 4. サブメッシュ＆インデックス（ミラー反転時は面の巻き順を反転）
        int subMeshCount = Mathf.Max(1, sourceMesh.subMeshCount);
        outMesh.subMeshCount = subMeshCount;
        for (int s = 0; s < sourceMesh.subMeshCount; s++)
        {
            MeshTopology topo = sourceMesh.GetTopology(s);
            int[] indices = sourceMesh.GetIndices(s, true);
            if (flipWinding)
                FlipTopologyWindingInPlace(indices, topo);
            outMesh.SetIndices(indices, topo, s, true, 0);
        }

        if (_recalculateNormals || dstNormals == null)
        {
            outMesh.RecalculateNormals();
            dstNormals = outMesh.normals;
        }

        // 5. Tangent（ミラー時は w の符号も反転）
        Vector4[] srcTangents = sourceMesh.tangents;
        if (!_recalculateTangents && srcTangents != null && srcTangents.Length == vertexCount)
        {
            float wSign = flipWinding ? -1f : 1f;
            Vector4[] dstTangents = new Vector4[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                Vector4 st = srcTangents[i];
                Vector3 tDir = linearMat.MultiplyVector(new Vector3(st.x, st.y, st.z));
                if (dstNormals != null && i < dstNormals.Length)
                {
                    Vector3 n = dstNormals[i];
                    tDir -= Vector3.Dot(tDir, n) * n;
                }
                float magSq = tDir.sqrMagnitude;
                if (magSq > 1e-16f) tDir /= Mathf.Sqrt(magSq);
                dstTangents[i] = new Vector4(tDir.x, tDir.y, tDir.z, st.w * wSign);
            }
            outMesh.tangents = dstTangents;
        }
        else if (_recalculateTangents)
        {
            outMesh.RecalculateTangents();
        }

        // 6. BlendShape（マルチフレーム対応）
        int bsCount = sourceMesh.blendShapeCount;
        if (bsCount > 0)
        {
            var dv = new Vector3[vertexCount];
            var dn = new Vector3[vertexCount];
            var dt = new Vector3[vertexCount];
            var outDv = new Vector3[vertexCount];
            var outDn = new Vector3[vertexCount];
            var outDt = new Vector3[vertexCount];

            for (int b = 0; b < bsCount; b++)
            {
                string shapeName = sourceMesh.GetBlendShapeName(b);
                int frameCount = sourceMesh.GetBlendShapeFrameCount(b);
                for (int f = 0; f < frameCount; f++)
                {
                    float weight = sourceMesh.GetBlendShapeFrameWeight(b, f);
                    sourceMesh.GetBlendShapeFrameVertices(b, f, dv, dn, dt);

                    for (int v = 0; v < vertexCount; v++)
                    {
                        outDv[v] = linearMat.MultiplyVector(dv[v]);
                        outDn[v] = unitNormalDeltaMat.MultiplyVector(dn[v]);
                        outDt[v] = unitTangentDeltaMat.MultiplyVector(dt[v]);
                    }
                    outMesh.AddBlendShapeFrame(shapeName, weight, outDv, outDn, outDt);
                }
            }
        }

        // 7. ボーンウェイト＆バインドポーズ
        ApplyBonesAndBindposes(sourceMesh, refMesh, dstVerts, outMesh, out summary);

        outMesh.RecalculateBounds();
        return outMesh;
    }

    private void ApplyBonesAndBindposes(
        Mesh sourceMesh,
        Mesh referenceMesh,
        Vector3[] alignedVertices,
        Mesh outMesh,
        out BoneMappingSummary summary)
    {
        summary = new BoneMappingSummary
        {
            SourceWeightedVertexCount = CountWeightedVertices(sourceMesh),
            TargetBoneCount = _targetSmr != null && _targetSmr.bones != null ? _targetSmr.bones.Length : 0
        };

        if (!_fixBonesAndBindposes || _targetSmr == null || _targetSmr.bones == null || _targetSmr.bones.Length == 0)
        {
            // ボーン補正OFF時は元メッシュのウェイトとバインドポーズをそのままコピー
            CopyRawBoneWeights(sourceMesh, outMesh);
            if (sourceMesh.bindposes != null && sourceMesh.bindposes.Length > 0)
                outMesh.bindposes = sourceMesh.bindposes;
            return;
        }

        Transform[] dstBones = _targetSmr.bones;
        int dstBoneCount = dstBones.Length;
        Matrix4x4[] targetBindposes = BuildTargetReferenceBindposes(referenceMesh);

        // 新メッシュにウェイトが全くない場合、オプションONかつ参照メッシュにウェイトがあれば最近傍転写
        if (summary.SourceWeightedVertexCount == 0)
        {
            if (_transferWeightsWhenMissing && referenceMesh != null && HasAnyBoneWeights(referenceMesh))
            {
                TransferNearestBoneWeights(referenceMesh, alignedVertices, dstBoneCount, outMesh);
                outMesh.bindposes = targetBindposes;
                summary.UsedTransferFromReference = true;
                summary.DirectMatchCount = dstBoneCount;
                return;
            }

            // 参照もない場合は rootBone または 0番ボーンへ 100% ウェイトを付与してスキニング消失を防止
            int fallbackBone = FindRootBoneIndexInArray(dstBones, _targetSmr.rootBone);
            AssignUniformSingleBoneWeight(outMesh, alignedVertices.Length, fallbackBone);
            outMesh.bindposes = targetBindposes;
            return;
        }

        // ソースボーン解決とインデックスマッピング作成
        int[] boneMap;
        if (TryResolveSourceBones(sourceMesh, _sourceObject, out Transform[] srcBones, out _))
        {
            summary.SourceBoneCount = srcBones.Length;
            boneMap = BuildBoneIndexMap(srcBones, dstBones, out summary.DirectMatchCount, out summary.ParentFallbackCount, out summary.UnmatchedCount);
        }
        else
        {
            // 単独 .mesh 等でボーン名が取得できない場合はバインドポーズ位置またはインデックス順で対応付け
            Matrix4x4[] srcBps = sourceMesh.bindposes;
            int srcCount = srcBps != null ? srcBps.Length : dstBoneCount;
            summary.SourceBoneCount = srcCount;
            boneMap = BuildPositionBasedBoneMapFallback(srcBps, targetBindposes, dstBoneCount, out summary.DirectMatchCount);
        }

        int defaultFallbackIdx = FindRootBoneIndexInArray(dstBones, _targetSmr.rootBone);
        RemapMeshBoneWeights(sourceMesh, outMesh, boneMap, dstBoneCount, defaultFallbackIdx);
        outMesh.bindposes = targetBindposes;
    }

    private int[] BuildPositionBasedBoneMapFallback(Matrix4x4[] srcBindposes, Matrix4x4[] dstBindposes, int dstCount, out int matched)
    {
        matched = 0;
        int srcLen = srcBindposes != null ? srcBindposes.Length : 0;
        int[] map = new int[srcLen];
        if (srcLen == 0) return map;

        if (srcLen == dstCount)
        {
            for (int i = 0; i < srcLen; i++) map[i] = i;
            matched = srcLen;
            return map;
        }

        Matrix4x4 cMat = BuildTransformMatrix(_rotationEuler, GetCurrentScaleVector(), _positionOffset, _mirrorX);
        Vector3[] dstHeads = new Vector3[dstCount];
        for (int i = 0; i < dstCount; i++)
        {
            Matrix4x4 inv = dstBindposes[i].inverse;
            dstHeads[i] = new Vector3(inv.m03, inv.m13, inv.m23);
        }

        for (int j = 0; j < srcLen; j++)
        {
            Matrix4x4 invS = srcBindposes[j].inverse;
            Vector3 p = cMat.MultiplyPoint3x4(new Vector3(invS.m03, invS.m13, invS.m23));
            int best = 0;
            float bestD2 = float.MaxValue;
            for (int i = 0; i < dstCount; i++)
            {
                float d2 = (dstHeads[i] - p).sqrMagnitude;
                if (d2 < bestD2) { bestD2 = d2; best = i; }
            }
            map[j] = best;
            matched++;
        }
        return map;
    }

    private static int FindRootBoneIndexInArray(Transform[] bones, Transform rootBone)
    {
        if (bones == null || bones.Length == 0) return 0;
        if (rootBone != null)
        {
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] == rootBone) return i;
        }
        for (int i = 0; i < bones.Length; i++)
            if (bones[i] != null) return i;
        return 0;
    }

    private static void RemapMeshBoneWeights(Mesh srcMesh, Mesh dstMesh, int[] boneMap, int dstBoneCount, int fallbackBoneIndex)
    {
        NativeArray<byte> srcCounts = srcMesh.GetBonesPerVertex();
        NativeArray<BoneWeight1> srcWeights = srcMesh.GetAllBoneWeights();

        int vCount = srcMesh.vertexCount;
        var outCountsList = new byte[vCount];
        var outWeightsList = new List<BoneWeight1>(srcWeights.Length);
        var tempPerVertex = new List<BoneWeight1>(8);

        int cursor = 0;
        for (int v = 0; v < vCount; v++)
        {
            int count = v < srcCounts.Length ? srcCounts[v] : 0;
            tempPerVertex.Clear();

            for (int k = 0; k < count && cursor < srcWeights.Length; k++, cursor++)
            {
                BoneWeight1 bw = srcWeights[cursor];
                if (bw.weight <= 0f) continue;

                int mapped = (bw.boneIndex >= 0 && bw.boneIndex < boneMap.Length) ? boneMap[bw.boneIndex] : -1;
                if (mapped < 0 || mapped >= dstBoneCount)
                    mapped = fallbackBoneIndex;

                // 同一ターゲットボーンへ集約されたウェイトは合算
                bool merged = false;
                for (int m = 0; m < tempPerVertex.Count; m++)
                {
                    if (tempPerVertex[m].boneIndex == mapped)
                    {
                        var existing = tempPerVertex[m];
                        existing.weight += bw.weight;
                        tempPerVertex[m] = existing;
                        merged = true;
                        break;
                    }
                }

                if (!merged)
                {
                    tempPerVertex.Add(new BoneWeight1 { boneIndex = mapped, weight = bw.weight });
                }
            }

            if (tempPerVertex.Count == 0)
            {
                tempPerVertex.Add(new BoneWeight1 { boneIndex = fallbackBoneIndex, weight = 1f });
            }
            else
            {
                // ウェイト降順ソート＆正規化
                tempPerVertex.Sort((a, b) => b.weight.CompareTo(a.weight));
                float sum = 0f;
                for (int m = 0; m < tempPerVertex.Count; m++) sum += tempPerVertex[m].weight;
                if (sum > 1e-8f)
                {
                    float inv = 1f / sum;
                    for (int m = 0; m < tempPerVertex.Count; m++)
                    {
                        var item = tempPerVertex[m];
                        item.weight *= inv;
                        tempPerVertex[m] = item;
                    }
                }
            }

            byte finalCount = (byte)Mathf.Min(tempPerVertex.Count, 255);
            outCountsList[v] = finalCount;
            for (int m = 0; m < finalCount; m++)
                outWeightsList.Add(tempPerVertex[m]);
        }

        using var nativeCounts = new NativeArray<byte>(outCountsList, Allocator.Temp);
        using var nativeWeights = new NativeArray<BoneWeight1>(outWeightsList.ToArray(), Allocator.Temp);
        dstMesh.SetBoneWeights(nativeCounts, nativeWeights);
    }

    /// <summary>
    /// 参照メッシュの各頂点から空間グリッドで最近傍頂点を高速検索し、ボーンウェイトを転写する。
    /// </summary>
    private static void TransferNearestBoneWeights(Mesh refMesh, Vector3[] alignedVerts, int dstBoneCount, Mesh outMesh)
    {
        Vector3[] refVerts = refMesh.vertices;
        NativeArray<byte> refCounts = refMesh.GetBonesPerVertex();
        NativeArray<BoneWeight1> refWeights = refMesh.GetAllBoneWeights();

        int refVCount = refVerts.Length;
        int[] refWeightOffsets = new int[refVCount];
        int acc = 0;
        for (int i = 0; i < refVCount; i++)
        {
            refWeightOffsets[i] = acc;
            acc += i < refCounts.Length ? refCounts[i] : 0;
        }

        // 空間グリッド構築
        Bounds b = refMesh.bounds;
        float cellSize = Mathf.Max(b.size.magnitude / 32f, 1e-4f);
        float invCell = 1f / cellSize;
        var grid = new Dictionary<Vector3Int, List<int>>(refVCount);

        for (int i = 0; i < refVCount; i++)
        {
            Vector3Int cell = ToCellCoord(refVerts[i], invCell);
            if (!grid.TryGetValue(cell, out var list))
            {
                list = new List<int>(4);
                grid[cell] = list;
            }
            list.Add(i);
        }

        int dstVCount = alignedVerts.Length;
        byte[] outCounts = new byte[dstVCount];
        var outWeights = new List<BoneWeight1>(dstVCount * 4);

        for (int v = 0; v < dstVCount; v++)
        {
            Vector3 p = alignedVerts[v];
            Vector3Int centerCell = ToCellCoord(p, invCell);
            int nearestIdx = -1;
            float bestDistSq = float.MaxValue;

            // 近傍セル探索（見つからない場合は全頂点フォールバック）
            for (int ring = 1; ring <= 2 && nearestIdx < 0; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    for (int dy = -ring; dy <= ring; dy++)
                    {
                        for (int dz = -ring; dz <= ring; dz++)
                        {
                            var c = new Vector3Int(centerCell.x + dx, centerCell.y + dy, centerCell.z + dz);
                            if (!grid.TryGetValue(c, out var bucket)) continue;
                            for (int k = 0; k < bucket.Count; k++)
                            {
                                int idx = bucket[k];
                                float d2 = (refVerts[idx] - p).sqrMagnitude;
                                if (d2 < bestDistSq)
                                {
                                    bestDistSq = d2;
                                    nearestIdx = idx;
                                }
                            }
                        }
                    }
                }
            }

            if (nearestIdx < 0)
            {
                for (int i = 0; i < refVCount; i++)
                {
                    float d2 = (refVerts[i] - p).sqrMagnitude;
                    if (d2 < bestDistSq) { bestDistSq = d2; nearestIdx = i; }
                }
            }

            int wCount = (nearestIdx >= 0 && nearestIdx < refCounts.Length) ? refCounts[nearestIdx] : 0;
            int wOffset = nearestIdx >= 0 ? refWeightOffsets[nearestIdx] : 0;
            byte added = 0;
            for (int k = 0; k < wCount; k++)
            {
                BoneWeight1 bw = refWeights[wOffset + k];
                if (bw.boneIndex >= 0 && bw.boneIndex < dstBoneCount && bw.weight > 0f)
                {
                    outWeights.Add(bw);
                    added++;
                }
            }

            if (added == 0)
            {
                outWeights.Add(new BoneWeight1 { boneIndex = 0, weight = 1f });
                added = 1;
            }
            outCounts[v] = added;
        }

        using var nativeCounts = new NativeArray<byte>(outCounts, Allocator.Temp);
        using var nativeWeights = new NativeArray<BoneWeight1>(outWeights.ToArray(), Allocator.Temp);
        outMesh.SetBoneWeights(nativeCounts, nativeWeights);
    }

    private static Vector3Int ToCellCoord(Vector3 p, float invCell)
    {
        return new Vector3Int(
            Mathf.FloorToInt(p.x * invCell),
            Mathf.FloorToInt(p.y * invCell),
            Mathf.FloorToInt(p.z * invCell));
    }

    private static void AssignUniformSingleBoneWeight(Mesh outMesh, int vertexCount, int boneIndex)
    {
        byte[] counts = new byte[vertexCount];
        BoneWeight1[] weights = new BoneWeight1[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            counts[i] = 1;
            weights[i] = new BoneWeight1 { boneIndex = boneIndex, weight = 1f };
        }
        using var nc = new NativeArray<byte>(counts, Allocator.Temp);
        using var nw = new NativeArray<BoneWeight1>(weights, Allocator.Temp);
        outMesh.SetBoneWeights(nc, nw);
    }

    private static void CopyRawBoneWeights(Mesh src, Mesh dst)
    {
        NativeArray<byte> counts = src.GetBonesPerVertex();
        NativeArray<BoneWeight1> weights = src.GetAllBoneWeights();
        if (counts.Length > 0 && weights.Length > 0)
            dst.SetBoneWeights(counts, weights);
    }

    private static int CountWeightedVertices(Mesh m)
    {
        if (m == null) return 0;
        NativeArray<byte> counts = m.GetBonesPerVertex();
        int nonZero = 0;
        for (int i = 0; i < counts.Length; i++)
            if (counts[i] > 0) nonZero++;
        return nonZero;
    }

    private static bool HasAnyBoneWeights(Mesh m)
    {
        return m != null && m.GetAllBoneWeights().Length > 0;
    }

    private static void CopyAllUVChannels(Mesh src, Mesh dst)
    {
        for (int ch = 0; ch < 8; ch++)
        {
            VertexAttribute attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
            if (!src.HasVertexAttribute(attr)) continue;

            int dim = src.GetVertexAttributeDimension(attr);
            if (dim == 4)
            {
                var uvs = new List<Vector4>(src.vertexCount);
                src.GetUVs(ch, uvs);
                if (uvs.Count > 0) dst.SetUVs(ch, uvs);
            }
            else if (dim == 3)
            {
                var uvs = new List<Vector3>(src.vertexCount);
                src.GetUVs(ch, uvs);
                if (uvs.Count > 0) dst.SetUVs(ch, uvs);
            }
            else
            {
                var uvs = new List<Vector2>(src.vertexCount);
                src.GetUVs(ch, uvs);
                if (uvs.Count > 0) dst.SetUVs(ch, uvs);
            }
        }
    }

    private static void FlipTopologyWindingInPlace(int[] indices, MeshTopology topo)
    {
        if (indices == null) return;
        if (topo == MeshTopology.Triangles)
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int tmp = indices[i + 1];
                indices[i + 1] = indices[i + 2];
                indices[i + 2] = tmp;
            }
        }
        else if (topo == MeshTopology.Quads)
        {
            for (int i = 0; i + 3 < indices.Length; i += 4)
            {
                int tmp = indices[i + 1];
                indices[i + 1] = indices[i + 3];
                indices[i + 3] = tmp;
            }
        }
    }

    private static Matrix4x4 ScaleMatrix3x3(Matrix4x4 m, float factor)
    {
        Matrix4x4 r = Matrix4x4.identity;
        r.m00 = m.m00 * factor; r.m01 = m.m01 * factor; r.m02 = m.m02 * factor;
        r.m10 = m.m10 * factor; r.m11 = m.m11 * factor; r.m12 = m.m12 * factor;
        r.m20 = m.m20 * factor; r.m21 = m.m21 * factor; r.m22 = m.m22 * factor;
        return r;
    }

    private static int CountMeshTriangles(Mesh m)
    {
        if (m == null) return 0;
        int total = 0;
        for (int s = 0; s < m.subMeshCount; s++)
        {
            if (m.GetTopology(s) == MeshTopology.Triangles)
                total += (int)m.GetIndexCount(s) / 3;
        }
        return total;
    }

    // ─────────────────────────────────────────────────────────
    //  リアルタイムプレビュー制御
    // ─────────────────────────────────────────────────────────

    private void StartOrUpdatePreview()
    {
        Mesh srcMesh = GetSelectedSourceMesh();
        if (_targetSmr == null || !IsSceneObject(_targetSmr.gameObject) || srcMesh == null)
            return;

        if (!_isPreviewActive || _previewSmr != _targetSmr)
        {
            StopPreview();
            _previewSmr = _targetSmr;
            _originalSmrMeshBeforePreview = _targetSmr.sharedMesh;
            _originalSmrLocalBoundsBeforePreview = _targetSmr.localBounds;
            _isPreviewActive = true;
        }

        Mesh built = BuildAlignedMesh(srcMesh, $"{srcMesh.name} (Updater Preview)", out _lastBoneSummary);
        _hasBoneSummary = true;
        if (built == null)
            return;

        built.hideFlags = HideFlags.HideAndDontSave;

        if (_previewTempMesh != null)
            DestroyImmediate(_previewTempMesh);
        _previewTempMesh = built;

        ApplyPreviewVisibilityState();
        SceneView.RepaintAll();
    }

    private void ApplyPreviewVisibilityState()
    {
        if (!_isPreviewActive || _previewSmr == null)
            return;

        if (_previewShowOriginal)
        {
            _previewSmr.sharedMesh = _originalSmrMeshBeforePreview;
            _previewSmr.localBounds = _originalSmrLocalBoundsBeforePreview;
        }
        else if (_previewTempMesh != null)
        {
            _previewSmr.sharedMesh = _previewTempMesh;
            _previewSmr.localBounds = _previewTempMesh.bounds;
        }
        SceneView.RepaintAll();
    }

    public void StopPreview()
    {
        if (_isPreviewActive && _previewSmr != null)
        {
            _previewSmr.sharedMesh = _originalSmrMeshBeforePreview;
            _previewSmr.localBounds = _originalSmrLocalBoundsBeforePreview;
        }

        if (_previewTempMesh != null)
        {
            DestroyImmediate(_previewTempMesh);
            _previewTempMesh = null;
        }

        _isPreviewActive = false;
        _previewShowOriginal = false;
        _previewSmr = null;
        _originalSmrMeshBeforePreview = null;
        SceneView.RepaintAll();
    }

    // ─────────────────────────────────────────────────────────
    //  保存実行（.mesh 新規作成 / .fbx 上書き）
    // ─────────────────────────────────────────────────────────

    private void ExecuteSave()
    {
        Mesh srcMesh = GetSelectedSourceMesh();
        if (_targetSmr == null || !IsSceneObject(_targetSmr.gameObject) || srcMesh == null)
        {
            SetStatus("対象の SkinnedMeshRenderer と新しい Mesh/FBX を指定してください。", MessageType.Warning);
            return;
        }

        // 保存前に必ずプレビューを解除してSMRを元の状態に戻す
        StopPreview();

        if (_saveMode == SaveMode.CreateNewMesh)
        {
            ExecuteSaveAsNewMesh(srcMesh);
        }
        else
        {
            ExecuteOverwriteFbx(srcMesh);
        }
    }

    private void ExecuteSaveAsNewMesh(Mesh srcMesh)
    {
        try
        {
            string srcAssetPath = AssetDatabase.GetAssetPath(srcMesh);
            string folder = string.IsNullOrEmpty(_meshSaveFolder)
                ? DetermineDefaultMeshSaveFolder(srcAssetPath)
                : _meshSaveFolder.Trim().TrimEnd('/', '\\');

            EnsureAssetFolderExists(folder);

            string baseName = SanitizeFileName(StripUpdatedSuffix(srcMesh.name));
            if (string.IsNullOrEmpty(baseName)) baseName = "UpdatedMesh";

            string savePath = GenerateUniqueMeshAssetPath(folder, baseName);
            string meshAssetName = Path.GetFileNameWithoutExtension(savePath);

            Mesh newMesh = BuildAlignedMesh(srcMesh, meshAssetName, out _lastBoneSummary);
            _hasBoneSummary = true;
            if (newMesh == null)
            {
                SetStatus("メッシュの構築に失敗しました。", MessageType.Error);
                return;
            }

            AssetDatabase.CreateAsset(newMesh, savePath);
            AssetDatabase.SaveAssets();

            if (_assignToSmrAfterSave && _targetSmr != null)
            {
                Undo.RecordObject(_targetSmr, "Update SkinnedMeshRenderer Mesh");
                _targetSmr.sharedMesh = newMesh;
                _targetSmr.localBounds = newMesh.bounds;
                PrefabUtility.RecordPrefabInstancePropertyModifications(_targetSmr);
                EditorUtility.SetDirty(_targetSmr);
            }

            EditorGUIUtility.PingObject(newMesh);
            SetStatus($"新しいメッシュを作成して適用しました: {savePath}", MessageType.Info);
            SceneView.RepaintAll();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            SetStatus($"メッシュ保存中にエラーが発生しました: {ex.Message}", MessageType.Error);
        }
    }

    private void ExecuteOverwriteFbx(Mesh srcMesh)
    {
        string fbxAssetPath = AssetDatabase.GetAssetPath(srcMesh);
        if (!IsEditableFbxAssetPath(fbxAssetPath))
        {
            SetStatus("Assets フォルダ配下の .fbx アセットのみ上書き可能です。", MessageType.Error);
            return;
        }

        string fullFbxPath = Path.GetFullPath(fbxAssetPath);
        if (!File.Exists(fullFbxPath))
        {
            SetStatus($"対象のFBXファイルが見つかりません: {fbxAssetPath}", MessageType.Error);
            return;
        }

        try
        {
            string targetMeshName = srcMesh.name;

            // 1. 補正後メッシュを構築
            Mesh alignedMesh = BuildAlignedMesh(srcMesh, targetMeshName, out _lastBoneSummary);
            _hasBoneSummary = true;
            if (alignedMesh == null)
            {
                SetStatus("補正メッシュの構築に失敗しました。", MessageType.Error);
                return;
            }

            try
            {
                // 2. 上書き前バックアップを Temp に保存（Assets外なのでUnityに重複インポートされない）
                Directory.CreateDirectory(TEMP_BACKUP_DIR);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string backupPath = Path.Combine(TEMP_BACKUP_DIR, $"{stamp}_{Path.GetFileName(fbxAssetPath)}");
                File.Copy(fullFbxPath, backupPath, true);
                _lastBackupTempPath = backupPath;
                _lastOverwrittenFbxAssetPath = fbxAssetPath;

                // 3. 同一FBX内に他の兄弟メッシュが存在する場合はそれらも収集して保持
                var payloads = CollectFbxPayloadsForOverwrite(fbxAssetPath, srcMesh, alignedMesh);

                // 4. ModelImporter 設定確認（bakeAxisConversionによる二重回転防止・5本以上ウェイト対応・globalScale補正）
                var importer = AssetImporter.GetAtPath(fbxAssetPath) as ModelImporter;
                float importerGlobalScale = 1f;
                bool importerDirty = false;
                if (importer != null)
                {
                    importerGlobalScale = Mathf.Abs(importer.globalScale) > 1e-6f ? importer.globalScale : 1f;
                    if (importer.bakeAxisConversion)
                    {
                        importer.bakeAxisConversion = false;
                        importerDirty = true;
                    }

                    int maxBpV = GetMaxBonesPerVertexInPayloads(payloads);
                    if (maxBpV > 4 && importer.skinWeights == ModelImporterSkinWeights.Standard)
                    {
                        importer.skinWeights = ModelImporterSkinWeights.Custom;
                        importer.maxBonesPerVertex = Mathf.Clamp(maxBpV, 4, 32);
                        importerDirty = true;
                    }

                    if (importerDirty)
                        EditorUtility.SetDirty(importer);
                }

                // 5. 一時ファイルにBinary FBX 7400を書き出してからアトミックに置換
                string tempWritePath = fullFbxPath + ".tmp_write";
                BinaryFbx7400Writer.WriteFbxFile(tempWritePath, payloads, 1f / importerGlobalScale);
                File.Copy(tempWritePath, fullFbxPath, true);
                File.Delete(tempWritePath);
            }
            finally
            {
                DestroyImmediate(alignedMesh);
            }

            // 6. FBX 再インポートと SMR への再割り当て
            AssetDatabase.ImportAsset(fbxAssetPath, ImportAssetOptions.ForceUpdate);

            Mesh reimportedMesh = FindSubMeshAssetByName(fbxAssetPath, targetMeshName);
            if (_assignToSmrAfterSave && _targetSmr != null && reimportedMesh != null)
            {
                Undo.RecordObject(_targetSmr, "Update SkinnedMeshRenderer FBX Mesh");
                _targetSmr.sharedMesh = reimportedMesh;
                _targetSmr.localBounds = reimportedMesh.bounds;
                PrefabUtility.RecordPrefabInstancePropertyModifications(_targetSmr);
                EditorUtility.SetDirty(_targetSmr);
            }

            // キャッシュを再リフレッシュし、再適用による二重変換を防ぐため補正パラメータを初期値へリセット
            RefreshAvailableSourceMeshes(autoSelectBestMatch: false);
            ResetTransformParams();
            UpdateBoneSummaryPreview();

            if (reimportedMesh != null)
                EditorGUIUtility.PingObject(reimportedMesh);

            SetStatus($"FBX を上書き保存して再インポートしました: {fbxAssetPath}（パラメータは二重適用防止のためリセット済み）", MessageType.Info);
            SceneView.RepaintAll();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            SetStatus($"FBX上書き保存中にエラーが発生しました: {ex.Message}", MessageType.Error);
        }
    }

    private List<FbxMeshPayload> CollectFbxPayloadsForOverwrite(string fbxAssetPath, Mesh targetSubMesh, Mesh alignedReplacementMesh)
    {
        var payloads = new List<FbxMeshPayload>();
        var existingRoot = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);

        string[] ResolveTargetMatNames(int subMeshCount)
        {
            var names = new string[subMeshCount];
            Material[] mats = _targetSmr != null ? _targetSmr.sharedMaterials : null;
            for (int i = 0; i < subMeshCount; i++)
            {
                string mName = (mats != null && i < mats.Length && mats[i] != null) ? mats[i].name : $"Material_{i}";
                names[i] = string.IsNullOrEmpty(mName) ? $"Material_{i}" : mName;
            }
            return names;
        }

        Transform[] targetBones = (_fixBonesAndBindposes && _targetSmr != null && _targetSmr.bones != null && _targetSmr.bones.Length > 0)
            ? _targetSmr.bones
            : null;
        Transform targetRootBone = _targetSmr != null ? _targetSmr.rootBone : null;

        bool replacedAdded = false;

        if (existingRoot != null)
        {
            foreach (var smr in existingRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                if (smr.sharedMesh == targetSubMesh || smr.sharedMesh.name == targetSubMesh.name)
                {
                    if (!replacedAdded)
                    {
                        payloads.Add(new FbxMeshPayload
                        {
                            MeshName = targetSubMesh.name,
                            MeshData = alignedReplacementMesh,
                            Bones = targetBones ?? smr.bones,
                            RootBone = targetRootBone != null ? targetRootBone : smr.rootBone,
                            MaterialNames = ResolveTargetMatNames(alignedReplacementMesh.subMeshCount)
                        });
                        replacedAdded = true;
                    }
                }
                else
                {
                    payloads.Add(new FbxMeshPayload
                    {
                        MeshName = smr.sharedMesh.name,
                        MeshData = smr.sharedMesh,
                        Bones = smr.bones,
                        RootBone = smr.rootBone,
                        MaterialNames = ExtractMaterialNames(smr.sharedMaterials, smr.sharedMesh.subMeshCount)
                    });
                }
            }

            foreach (var mf in existingRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (mf.sharedMesh == targetSubMesh || mf.sharedMesh.name == targetSubMesh.name)
                {
                    if (!replacedAdded)
                    {
                        payloads.Add(new FbxMeshPayload
                        {
                            MeshName = targetSubMesh.name,
                            MeshData = alignedReplacementMesh,
                            Bones = targetBones,
                            RootBone = targetRootBone,
                            MaterialNames = ResolveTargetMatNames(alignedReplacementMesh.subMeshCount)
                        });
                        replacedAdded = true;
                    }
                }
                else
                {
                    var mr = mf.GetComponent<MeshRenderer>();
                    payloads.Add(new FbxMeshPayload
                    {
                        MeshName = mf.sharedMesh.name,
                        MeshData = mf.sharedMesh,
                        Bones = null,
                        RootBone = null,
                        MaterialNames = ExtractMaterialNames(mr != null ? mr.sharedMaterials : null, mf.sharedMesh.subMeshCount)
                    });
                }
            }
        }

        if (!replacedAdded)
        {
            payloads.Insert(0, new FbxMeshPayload
            {
                MeshName = targetSubMesh.name,
                MeshData = alignedReplacementMesh,
                Bones = targetBones,
                RootBone = targetRootBone,
                MaterialNames = ResolveTargetMatNames(alignedReplacementMesh.subMeshCount)
            });
        }

        return payloads;
    }

    private static string[] ExtractMaterialNames(Material[] mats, int count)
    {
        int n = Mathf.Max(1, count);
        var res = new string[n];
        for (int i = 0; i < n; i++)
        {
            res[i] = (mats != null && i < mats.Length && mats[i] != null && !string.IsNullOrEmpty(mats[i].name))
                ? mats[i].name
                : $"Material_{i}";
        }
        return res;
    }

    private static int GetMaxBonesPerVertexInPayloads(List<FbxMeshPayload> payloads)
    {
        int maxB = 0;
        foreach (var p in payloads)
        {
            if (p.MeshData == null) continue;
            NativeArray<byte> counts = p.MeshData.GetBonesPerVertex();
            for (int i = 0; i < counts.Length; i++)
                if (counts[i] > maxB) maxB = counts[i];
        }
        return maxB;
    }

    private static Mesh FindSubMeshAssetByName(string fbxAssetPath, string meshName)
    {
        var all = AssetDatabase.LoadAllAssetsAtPath(fbxAssetPath).OfType<Mesh>().ToList();
        foreach (var m in all)
            if (m != null && m.name == meshName) return m;
        return all.FirstOrDefault(m => m != null);
    }

    private bool CanRestoreLastFbxBackup()
    {
        return !string.IsNullOrEmpty(_lastBackupTempPath)
               && !string.IsNullOrEmpty(_lastOverwrittenFbxAssetPath)
               && File.Exists(_lastBackupTempPath)
               && IsEditableFbxAssetPath(_lastOverwrittenFbxAssetPath);
    }

    private void RestoreLastFbxBackup()
    {
        if (!CanRestoreLastFbxBackup())
            return;

        StopPreview();

        try
        {
            string fullDst = Path.GetFullPath(_lastOverwrittenFbxAssetPath);
            File.Copy(_lastBackupTempPath, fullDst, true);
            AssetDatabase.ImportAsset(_lastOverwrittenFbxAssetPath, ImportAssetOptions.ForceUpdate);

            RefreshAvailableSourceMeshes(autoSelectBestMatch: false);
            Mesh restored = GetSelectedSourceMesh();
            if (_targetSmr != null && restored != null)
            {
                Undo.RecordObject(_targetSmr, "Restore FBX Backup");
                _targetSmr.sharedMesh = restored;
                _targetSmr.localBounds = restored.bounds;
                PrefabUtility.RecordPrefabInstancePropertyModifications(_targetSmr);
                EditorUtility.SetDirty(_targetSmr);
            }

            UpdateBoneSummaryPreview();
            SceneView.RepaintAll();
            SetStatus($"バックアップから上書き前のFBXを復元しました: {_lastOverwrittenFbxAssetPath}", MessageType.Info);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            SetStatus($"FBXバックアップ復元に失敗しました: {ex.Message}", MessageType.Error);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  パス・ユーティリティ
    // ─────────────────────────────────────────────────────────

    private static bool IsEditableFbxAssetPath(string assetPath)
    {
        return !string.IsNullOrEmpty(assetPath)
               && assetPath.StartsWith("Assets/", StringComparison.Ordinal)
               && assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetermineDefaultMeshSaveFolder(string srcAssetPath)
    {
        if (!string.IsNullOrEmpty(srcAssetPath) && srcAssetPath.StartsWith("Assets/", StringComparison.Ordinal))
        {
            string dir = Path.GetDirectoryName(srcAssetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir))
                return dir;
        }
        return DEFAULT_SAVE_DIR;
    }

    private static void EnsureAssetFolderExists(string folderPath)
    {
        string normalized = folderPath.Replace('\\', '/').TrimEnd('/');
        if (AssetDatabase.IsValidFolder(normalized)) return;

        string[] parts = normalized.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    private static string GenerateUniqueMeshAssetPath(string folder, string baseName)
    {
        for (int i = 0; i < 10000; i++)
        {
            string candidate = $"{folder}/{baseName}{UPDATED_SUFFIX_MARKER}{i:000}.mesh";
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(candidate) == null && !File.Exists(Path.GetFullPath(candidate)))
                return candidate;
        }
        return AssetDatabase.GenerateUniqueAssetPath($"{folder}/{baseName}.mesh");
    }

    private static string StripUpdatedSuffix(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        int idx = name.IndexOf(UPDATED_SUFFIX_MARKER, StringComparison.Ordinal);
        return idx > 0 ? name.Substring(0, idx) : name;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Mesh";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private static string ToAssetsRelativePath(string fullPath)
    {
        string normFull = Path.GetFullPath(fullPath).Replace('\\', '/').TrimEnd('/');
        string normAssets = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
        if (string.Equals(normFull, normAssets, StringComparison.OrdinalIgnoreCase))
            return "Assets";
        if (normFull.StartsWith(normAssets + "/", StringComparison.OrdinalIgnoreCase))
            return "Assets/" + normFull.Substring(normAssets.Length + 1);
        return null;
    }

    private void SetStatus(string msg, MessageType type)
    {
        _statusMessage = msg;
        _statusType = type;
        Repaint();
    }

    // ═════════════════════════════════════════════════════════
    //  純C# Binary FBX 7400 エクスポータ（外部パッケージ依存ゼロ）
    // ═════════════════════════════════════════════════════════

    private static class BinaryFbx7400Writer
    {
        private static readonly byte[] HeaderMagic =
        {
            (byte)'K', (byte)'a', (byte)'y', (byte)'d', (byte)'a', (byte)'r', (byte)'a', (byte)' ',
            (byte)'F', (byte)'B', (byte)'X', (byte)' ', (byte)'B', (byte)'i', (byte)'n', (byte)'a',
            (byte)'r', (byte)'y', (byte)' ', (byte)' ', 0x00, 0x1A, 0x00
        };

        // Blender標準エポック (1970-01-01 10:00:00:000) ペアの正当な FileId / FooterId / FooterMagic
        private static readonly byte[] EpochFileId =
        {
            0x28, 0xB3, 0x2A, 0xEB, 0xB6, 0x24, 0xCC, 0xC2,
            0xBF, 0xC8, 0xB0, 0x2A, 0xA9, 0x2B, 0xFC, 0xF1
        };

        private static readonly byte[] EpochFooterId =
        {
            0xFA, 0xBC, 0xAB, 0x09, 0xD0, 0xC8, 0xD4, 0x66,
            0xB1, 0x76, 0xFB, 0x83, 0x1C, 0xF7, 0x26, 0x7E
        };

        private static readonly byte[] FooterMagic =
        {
            0xF8, 0x5A, 0x8C, 0x6A, 0xDE, 0xF5, 0xD9, 0x7E,
            0xEC, 0xE9, 0x0C, 0xE3, 0x75, 0x8F, 0x29, 0x0B
        };

        private class FbxProp
        {
            public char TypeCode;
            public object Value;
        }

        private class FbxNode
        {
            public string Name;
            public bool ForceNullRecordWhenEmpty;
            public readonly List<FbxProp> Props = new List<FbxProp>();
            public readonly List<FbxNode> Children = new List<FbxNode>();

            public FbxNode(string name, bool forceNullWhenEmpty = false)
            {
                Name = name;
                ForceNullRecordWhenEmpty = forceNullWhenEmpty;
            }

            public FbxNode AddI(int v) { Props.Add(new FbxProp { TypeCode = 'I', Value = v }); return this; }
            public FbxNode AddL(long v) { Props.Add(new FbxProp { TypeCode = 'L', Value = v }); return this; }
            public FbxNode AddD(double v) { Props.Add(new FbxProp { TypeCode = 'D', Value = v }); return this; }
            public FbxNode AddS(string v) { Props.Add(new FbxProp { TypeCode = 'S', Value = v ?? "" }); return this; }
            public FbxNode AddR(byte[] v) { Props.Add(new FbxProp { TypeCode = 'R', Value = v }); return this; }
            public FbxNode AddArrI(int[] v) { Props.Add(new FbxProp { TypeCode = 'i', Value = v }); return this; }
            public FbxNode AddArrD(double[] v) { Props.Add(new FbxProp { TypeCode = 'd', Value = v }); return this; }

            public FbxNode Child(string name, bool forceNullWhenEmpty = false)
            {
                var c = new FbxNode(name, forceNullWhenEmpty);
                Children.Add(c);
                return c;
            }
        }

        private class BoneExportEntry
        {
            public long ModelId;
            public long NodeAttrId;
            public string Name;
            public int ParentEntryIndex = -1;
            public Matrix4x4 UnityWorldFromBoneRest = Matrix4x4.identity;
        }

        public static void WriteFbxFile(string outputFilePath, List<FbxMeshPayload> payloads, float positionCoordMultiplier)
        {
            long nextId = 100000000L;
            long NextId() => nextId++;

            var objectsNode = new FbxNode("Objects", true);
            var connectionsNode = new FbxNode("Connections", true);

            void ConnectOO(long childId, long parentId)
            {
                connectionsNode.Child("C").AddS("OO").AddL(childId).AddL(parentId);
            }

            int modelCount = 0;
            int geomCount = 0;
            int matCount = 0;
            int deformerCount = 0;
            int nodeAttrCount = 0;

            // 全ペイロードで共有するボーン階層構築
            var boneEntries = new List<BoneExportEntry>();
            var transformToEntryIdx = new Dictionary<Transform, int>();

            int EnsureBoneHierarchyEntry(Transform t, Matrix4x4? knownBindpose)
            {
                if (t == null) return -1;
                if (transformToEntryIdx.TryGetValue(t, out int existingIdx))
                {
                    if (knownBindpose.HasValue && IsValidBindposeMatrix(knownBindpose.Value))
                        boneEntries[existingIdx].UnityWorldFromBoneRest = knownBindpose.Value.inverse;
                    return existingIdx;
                }

                // ルート（SceneルートやSMR親）までの親ボーンも必要に応じて登録
                int parentIdx = -1;
                if (t.parent != null && !HasSkinnedMeshRendererOnSelf(t.parent))
                {
                    // 親のワールド相対行列を推定して先に登録
                    parentIdx = EnsureBoneHierarchyEntry(t.parent, null);
                }

                var entry = new BoneExportEntry
                {
                    ModelId = NextId(),
                    NodeAttrId = NextId(),
                    Name = t.name,
                    ParentEntryIndex = parentIdx,
                    UnityWorldFromBoneRest = (knownBindpose.HasValue && IsValidBindposeMatrix(knownBindpose.Value))
                        ? knownBindpose.Value.inverse
                        : Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale)
                };

                int idx = boneEntries.Count;
                boneEntries.Add(entry);
                transformToEntryIdx[t] = idx;
                return idx;
            }

            // まず全ペイロードの明示バインドポーズ付きボーンを先に収集
            foreach (var payload in payloads)
            {
                if (payload.Bones == null || payload.MeshData == null) continue;
                Matrix4x4[] bps = payload.MeshData.bindposes;
                for (int i = 0; i < payload.Bones.Length; i++)
                {
                    Transform bt = payload.Bones[i];
                    if (bt == null) continue;
                    Matrix4x4? bp = (bps != null && i < bps.Length) ? bps[i] : (Matrix4x4?)null;
                    EnsureBoneHierarchyEntry(bt, bp);
                }
            }

            // 親がバインドポーズ未設定のアンセスタだった場合、子のバインドポーズまたはTransformから整合させる
            for (int i = 0; i < boneEntries.Count; i++)
            {
                // NodeAttribute ("LimbNode") + Model ("LimbNode") を出力
                var be = boneEntries[i];
                var na = objectsNode.Child("NodeAttribute").AddL(be.NodeAttrId).AddS(be.Name + "\0\x01NodeAttribute").AddS("LimbNode");
                na.Child("TypeFlags").AddS("Skeleton");
                nodeAttrCount++;

                Matrix4x4 localUnity = be.ParentEntryIndex >= 0
                    ? (boneEntries[be.ParentEntryIndex].UnityWorldFromBoneRest.inverse * be.UnityWorldFromBoneRest)
                    : be.UnityWorldFromBoneRest;

                localUnity.m03 *= positionCoordMultiplier;
                localUnity.m13 *= positionCoordMultiplier;
                localUnity.m23 *= positionCoordMultiplier;

                DecomposeUnityToFbxLocalTRS(localUnity, out Vector3 fbxT, out Vector3 fbxEulerDeg, out Vector3 fbxS);

                var bm = objectsNode.Child("Model").AddL(be.ModelId).AddS(be.Name + "\0\x01Model").AddS("LimbNode");
                bm.Child("Version").AddI(232);
                var p70 = bm.Child("Properties70", true);
                AddP70Vec3(p70, "Lcl Translation", "Lcl Translation", fbxT);
                AddP70Vec3(p70, "Lcl Rotation", "Lcl Rotation", fbxEulerDeg);
                AddP70Vec3(p70, "Lcl Scaling", "Lcl Scaling", fbxS);
                bm.Child("Shading").AddS("Y");
                bm.Child("Culling").AddS("CullingOff");
                modelCount++;

                ConnectOO(be.NodeAttrId, be.ModelId);
                ConnectOO(be.ModelId, be.ParentEntryIndex >= 0 ? boneEntries[be.ParentEntryIndex].ModelId : 0L);
            }

            // マテリアル重複共有用マップ
            var materialNameToId = new Dictionary<string, long>(StringComparer.Ordinal);
            long EnsureMaterialNode(string matName)
            {
                if (string.IsNullOrEmpty(matName)) matName = "Material";
                if (materialNameToId.TryGetValue(matName, out long id))
                    return id;

                id = NextId();
                var mat = objectsNode.Child("Material").AddL(id).AddS(matName + "\0\x01Material").AddS("");
                mat.Child("Version").AddI(102);
                mat.Child("ShadingModel").AddS("phong");
                mat.Child("MultiLayer").AddI(0);
                mat.Child("Properties70", true);
                materialNameToId[matName] = id;
                matCount++;
                return id;
            }

            // 各メッシュペイロードのシリアライズ
            foreach (var payload in payloads)
            {
                Mesh mesh = payload.MeshData;
                if (mesh == null) continue;

                string meshName = string.IsNullOrEmpty(payload.MeshName) ? mesh.name : payload.MeshName;
                long geomId = NextId();
                long meshModelId = NextId();

                // Mesh Model ノード
                var modelNode = objectsNode.Child("Model").AddL(meshModelId).AddS(meshName + "\0\x01Model").AddS("Mesh");
                modelNode.Child("Version").AddI(232);
                modelNode.Child("Properties70", true);
                modelNode.Child("Shading").AddS("Y");
                modelNode.Child("Culling").AddS("CullingOff");
                modelCount++;

                // Geometry ノード構築
                WriteMeshGeometryNode(
                    objectsNode,
                    connectionsNode,
                    NextId,
                    geomId,
                    meshName,
                    mesh,
                    positionCoordMultiplier,
                    ref geomCount,
                    ref deformerCount);

                ConnectOO(geomId, meshModelId);
                ConnectOO(meshModelId, 0L);

                // SubMesh Materials 接続
                string[] matNames = payload.MaterialNames ?? new[] { "Material" };
                for (int s = 0; s < matNames.Length; s++)
                {
                    long mId = EnsureMaterialNode(matNames[s]);
                    ConnectOO(mId, meshModelId);
                }

                // Skin & Cluster (ボーンウェイト・バインドポーズ)
                Transform[] bones = payload.Bones;
                Matrix4x4[] bindposes = mesh.bindposes;
                NativeArray<byte> bpv = mesh.GetBonesPerVertex();
                NativeArray<BoneWeight1> allWeights = mesh.GetAllBoneWeights();

                if (bones != null && bones.Length > 0 && allWeights.Length > 0)
                {
                    long skinId = NextId();
                    var skin = objectsNode.Child("Deformer").AddL(skinId).AddS(meshName + "\0\x01Deformer").AddS("Skin");
                    skin.Child("Version").AddI(101);
                    skin.Child("Link_DeformAcuracy").AddD(50.0);
                    deformerCount++;
                    ConnectOO(skinId, geomId);

                    // ボーンごとの頂点インデックス・ウェイトリストを集計
                    int boneCount = bones.Length;
                    var boneVertIndices = new List<int>[boneCount];
                    var boneVertWeights = new List<double>[boneCount];
                    for (int b = 0; b < boneCount; b++)
                    {
                        boneVertIndices[b] = new List<int>();
                        boneVertWeights[b] = new List<double>();
                    }

                    int cursor = 0;
                    int vCount = mesh.vertexCount;
                    for (int v = 0; v < vCount; v++)
                    {
                        int c = v < bpv.Length ? bpv[v] : 0;
                        for (int k = 0; k < c && cursor < allWeights.Length; k++, cursor++)
                        {
                            BoneWeight1 bw = allWeights[cursor];
                            if (bw.boneIndex >= 0 && bw.boneIndex < boneCount && bw.weight > 0f)
                            {
                                boneVertIndices[bw.boneIndex].Add(v);
                                boneVertWeights[bw.boneIndex].Add(bw.weight);
                            }
                        }
                    }

                    for (int b = 0; b < boneCount; b++)
                    {
                        long boneModelId;
                        string boneName;
                        if (bones[b] != null && transformToEntryIdx.TryGetValue(bones[b], out int entryIdx))
                        {
                            boneModelId = boneEntries[entryIdx].ModelId;
                            boneName = boneEntries[entryIdx].Name;
                        }
                        else
                        {
                            // nullスロットがある場合のダミーLimbNode作成（インデックスずれ防止）
                            boneName = $"MissingBone_{b}";
                            boneModelId = NextId();
                            var dm = objectsNode.Child("Model").AddL(boneModelId).AddS(boneName + "\0\x01Model").AddS("LimbNode");
                            dm.Child("Version").AddI(232);
                            dm.Child("Properties70", true);
                            modelCount++;
                            ConnectOO(boneModelId, 0L);
                        }

                        Matrix4x4 unityBp = (bindposes != null && b < bindposes.Length && IsValidBindposeMatrix(bindposes[b]))
                            ? bindposes[b]
                            : Matrix4x4.identity;

                        // positionCoordMultiplier をバインドポーズの並進成分へ反映
                        Matrix4x4 scaledBp = ScaleUnityBindposeTranslation(unityBp, positionCoordMultiplier);
                        Matrix4x4 scaledBpInv = scaledBp.inverse;

                        long clusterId = NextId();
                        var cluster = objectsNode.Child("Deformer").AddL(clusterId).AddS(boneName + "\0\x01SubDeformer").AddS("Cluster");
                        cluster.Child("Version").AddI(100);
                        cluster.Child("UserData").AddS("").AddS("");
                        if (boneVertIndices[b].Count > 0)
                        {
                            cluster.Child("Indexes").AddArrI(boneVertIndices[b].ToArray());
                            cluster.Child("Weights").AddArrD(boneVertWeights[b].ToArray());
                        }
                        cluster.Child("Transform").AddArrD(UnityMatrixToFbxColumnMajorArray(scaledBp));
                        cluster.Child("TransformLink").AddArrD(UnityMatrixToFbxColumnMajorArray(scaledBpInv));
                        deformerCount++;

                        ConnectOO(clusterId, skinId);
                        ConnectOO(boneModelId, clusterId);
                    }
                }
            }

            // ルートノード群の組み立て
            var rootNodes = new List<FbxNode>();

            // 1. FBXHeaderExtension
            var hdr = new FbxNode("FBXHeaderExtension");
            hdr.Child("FBXHeaderVersion").AddI(1003);
            hdr.Child("FBXVersion").AddI(7400);
            hdr.Child("EncryptionType").AddI(0);
            var ts = hdr.Child("CreationTimeStamp");
            ts.Child("Version").AddI(1000);
            ts.Child("Year").AddI(1970);
            ts.Child("Month").AddI(1);
            ts.Child("Day").AddI(1);
            ts.Child("Hour").AddI(10);
            ts.Child("Minute").AddI(0);
            ts.Child("Second").AddI(0);
            ts.Child("Millisecond").AddI(0);
            hdr.Child("Creator").AddS("Cocoa's Toolbox SkinnedMeshUpdater");
            rootNodes.Add(hdr);

            // 2. FileId / CreationTime / Creator
            rootNodes.Add(new FbxNode("FileId").AddR(EpochFileId));
            rootNodes.Add(new FbxNode("CreationTime").AddS("1970-01-01 10:00:00:000"));
            rootNodes.Add(new FbxNode("Creator").AddS("Cocoa's Toolbox SkinnedMeshUpdater"));

            // 3. GlobalSettings (UnitScaleFactor = 100.0 => 1 FBX unit = 1m = Unity fileScale 1.0)
            var gs = new FbxNode("GlobalSettings");
            gs.Child("Version").AddI(1000);
            var gsP70 = gs.Child("Properties70", true);
            AddP70Int(gsP70, "UpAxis", 1);
            AddP70Int(gsP70, "UpAxisSign", 1);
            AddP70Int(gsP70, "FrontAxis", 2);
            AddP70Int(gsP70, "FrontAxisSign", 1);
            AddP70Int(gsP70, "CoordAxis", 0);
            AddP70Int(gsP70, "CoordAxisSign", 1);
            AddP70Int(gsP70, "OriginalUpAxis", 1);
            AddP70Int(gsP70, "OriginalUpAxisSign", 1);
            AddP70Double(gsP70, "UnitScaleFactor", 100.0);
            AddP70Double(gsP70, "OriginalUnitScaleFactor", 100.0);
            rootNodes.Add(gs);

            // 4. Documents & References
            var docs = new FbxNode("Documents");
            docs.Child("Count").AddI(1);
            var doc = docs.Child("Document").AddL(99999999L).AddS("Scene").AddS("Scene");
            doc.Child("Properties70", true);
            doc.Child("RootNode").AddL(0L);
            rootNodes.Add(docs);

            rootNodes.Add(new FbxNode("References", true));

            // 5. Definitions
            var defs = new FbxNode("Definitions");
            defs.Child("Version").AddI(100);
            int totalDefs = 1 + modelCount + geomCount + matCount + deformerCount + nodeAttrCount;
            defs.Child("Count").AddI(totalDefs);
            defs.Child("ObjectType").AddS("GlobalSettings").Child("Count").AddI(1);
            if (modelCount > 0) defs.Child("ObjectType").AddS("Model").Child("Count").AddI(modelCount);
            if (geomCount > 0) defs.Child("ObjectType").AddS("Geometry").Child("Count").AddI(geomCount);
            if (matCount > 0) defs.Child("ObjectType").AddS("Material").Child("Count").AddI(matCount);
            if (deformerCount > 0) defs.Child("ObjectType").AddS("Deformer").Child("Count").AddI(deformerCount);
            if (nodeAttrCount > 0) defs.Child("ObjectType").AddS("NodeAttribute").Child("Count").AddI(nodeAttrCount);
            rootNodes.Add(defs);

            // 6. Objects & Connections
            rootNodes.Add(objectsNode);
            rootNodes.Add(connectionsNode);

            // バイナリストリーム書き出し
            using var fs = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(fs, Encoding.UTF8);

            writer.Write(HeaderMagic);
            writer.Write((uint)7400);

            foreach (var rn in rootNodes)
                WriteNodeRecursive(writer, rn);

            // トップレベル13バイトNULL終端
            WriteNullRecord(writer);

            // フッター書き出し
            writer.Write(EpochFooterId);
            int pad = (int)(16 - (fs.Position % 16));
            for (int i = 0; i < pad; i++) writer.Write((byte)0);
            writer.Write((uint)7400);
            for (int i = 0; i < 120; i++) writer.Write((byte)0);
            writer.Write(FooterMagic);
        }

        private static void WriteMeshGeometryNode(
            FbxNode objectsNode,
            FbxNode connectionsNode,
            Func<long> nextId,
            long geomId,
            string meshName,
            Mesh mesh,
            float posScale,
            ref int geomCount,
            ref int deformerCount)
        {
            var geom = objectsNode.Child("Geometry").AddL(geomId).AddS(meshName + "\0\x01Geometry").AddS("Mesh");
            var geomP70 = geom.Child("Properties70", true);
            int bsCount = mesh.blendShapeCount;
            for (int b = 0; b < bsCount; b++)
            {
                geomP70.Child("P").AddS(mesh.GetBlendShapeName(b)).AddS("Number").AddS("").AddS("A").AddD(0.0);
            }

            geom.Child("GeometryVersion").AddI(124);
            geomCount++;

            // 頂点（Unity左手系 -> FBX右手系: X反転）
            Vector3[] verts = mesh.vertices;
            int vCount = verts.Length;
            double[] fbxVerts = new double[vCount * 3];
            for (int i = 0; i < vCount; i++)
            {
                Vector3 v = verts[i] * posScale;
                fbxVerts[i * 3 + 0] = -v.x;
                fbxVerts[i * 3 + 1] = v.y;
                fbxVerts[i * 3 + 2] = v.z;
            }
            geom.Child("Vertices").AddArrD(fbxVerts);

            // サブメッシュ三角形収集（巻き順 i0, i2, i1 で右手系へ変換）
            var polyVertIndices = new List<int>();
            var rawPolyCornerVerts = new List<int>();
            var polyMatIndices = new List<int>();
            int subCount = Mathf.Max(1, mesh.subMeshCount);

            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int[] tris = mesh.GetTriangles(s, true);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int i0 = tris[t];
                    int i1 = tris[t + 1];
                    int i2 = tris[t + 2];
                    rawPolyCornerVerts.Add(i0);
                    rawPolyCornerVerts.Add(i2);
                    rawPolyCornerVerts.Add(i1);

                    polyVertIndices.Add(i0);
                    polyVertIndices.Add(i2);
                    polyVertIndices.Add(~i1);
                    polyMatIndices.Add(s);
                }
            }

            geom.Child("PolygonVertexIndex").AddArrI(polyVertIndices.ToArray());

            int polyCornerCount = rawPolyCornerVerts.Count;
            int polyCount = polyMatIndices.Count;

            // 法線 (ByPolygonVertex / Direct)
            Vector3[] normals = mesh.normals;
            bool hasNormals = normals != null && normals.Length == vCount && polyCornerCount > 0;
            if (hasNormals)
            {
                double[] fbxNormals = new double[polyCornerCount * 3];
                for (int c = 0; c < polyCornerCount; c++)
                {
                    Vector3 n = normals[rawPolyCornerVerts[c]];
                    fbxNormals[c * 3 + 0] = -n.x;
                    fbxNormals[c * 3 + 1] = n.y;
                    fbxNormals[c * 3 + 2] = n.z;
                }
                var leNorm = geom.Child("LayerElementNormal").AddI(0);
                leNorm.Child("Version").AddI(101);
                leNorm.Child("Name").AddS("");
                leNorm.Child("MappingInformationType").AddS("ByPolygonVertex");
                leNorm.Child("ReferenceInformationType").AddS("Direct");
                leNorm.Child("Normals").AddArrD(fbxNormals);
            }

            // 頂点カラー
            Color32[] colors = mesh.colors32;
            bool hasColors = colors != null && colors.Length == vCount && polyCornerCount > 0;
            if (hasColors)
            {
                double[] fbxColors = new double[vCount * 4];
                for (int i = 0; i < vCount; i++)
                {
                    Color32 col = colors[i];
                    fbxColors[i * 4 + 0] = col.r / 255.0;
                    fbxColors[i * 4 + 1] = col.g / 255.0;
                    fbxColors[i * 4 + 2] = col.b / 255.0;
                    fbxColors[i * 4 + 3] = col.a / 255.0;
                }
                var leCol = geom.Child("LayerElementColor").AddI(0);
                leCol.Child("Version").AddI(101);
                leCol.Child("Name").AddS("colorSet1");
                leCol.Child("MappingInformationType").AddS("ByPolygonVertex");
                leCol.Child("ReferenceInformationType").AddS("IndexToDirect");
                leCol.Child("Colors").AddArrD(fbxColors);
                leCol.Child("ColorIndex").AddArrI(rawPolyCornerVerts.ToArray());
            }

            // UVチャンネル（最大8チャンネル）
            var presentUvChannels = new List<int>();
            int[] sharedCornerIndices = rawPolyCornerVerts.ToArray();
            for (int ch = 0; ch < 8; ch++)
            {
                var uvs = new List<Vector2>();
                mesh.GetUVs(ch, uvs);
                if (uvs.Count != vCount || polyCornerCount == 0) continue;

                int uvTypedIndex = presentUvChannels.Count;
                presentUvChannels.Add(uvTypedIndex);

                double[] fbxUv = new double[vCount * 2];
                for (int i = 0; i < vCount; i++)
                {
                    fbxUv[i * 2 + 0] = uvs[i].x;
                    fbxUv[i * 2 + 1] = uvs[i].y;
                }

                var leUv = geom.Child("LayerElementUV").AddI(uvTypedIndex);
                leUv.Child("Version").AddI(101);
                leUv.Child("Name").AddS(ch == 0 ? "map1" : $"UVChannel_{ch + 1}");
                leUv.Child("MappingInformationType").AddS("ByPolygonVertex");
                leUv.Child("ReferenceInformationType").AddS("IndexToDirect");
                leUv.Child("UV").AddArrD(fbxUv);
                leUv.Child("UVIndex").AddArrI(sharedCornerIndices);
            }

            // マテリアル割り当て (ByPolygon / IndexToDirect)
            if (polyCount > 0)
            {
                var leMat = geom.Child("LayerElementMaterial").AddI(0);
                leMat.Child("Version").AddI(101);
                leMat.Child("Name").AddS("");
                leMat.Child("MappingInformationType").AddS("ByPolygon");
                leMat.Child("ReferenceInformationType").AddS("IndexToDirect");
                leMat.Child("Materials").AddArrI(polyMatIndices.ToArray());
            }

            // Layer 0 定義
            var layer0 = geom.Child("Layer").AddI(0);
            layer0.Child("Version").AddI(100);
            if (hasNormals)
            {
                var le = layer0.Child("LayerElement");
                le.Child("Type").AddS("LayerElementNormal");
                le.Child("TypedIndex").AddI(0);
            }
            if (hasColors)
            {
                var le = layer0.Child("LayerElement");
                le.Child("Type").AddS("LayerElementColor");
                le.Child("TypedIndex").AddI(0);
            }
            if (presentUvChannels.Count > 0)
            {
                var le = layer0.Child("LayerElement");
                le.Child("Type").AddS("LayerElementUV");
                le.Child("TypedIndex").AddI(0);
            }
            if (polyCount > 0)
            {
                var le = layer0.Child("LayerElement");
                le.Child("Type").AddS("LayerElementMaterial");
                le.Child("TypedIndex").AddI(0);
            }

            // 追加UVレイヤー (Layer 1..N)
            for (int k = 1; k < presentUvChannels.Count; k++)
            {
                var layerK = geom.Child("Layer").AddI(k);
                layerK.Child("Version").AddI(100);
                var le = layerK.Child("LayerElement");
                le.Child("Type").AddS("LayerElementUV");
                le.Child("TypedIndex").AddI(presentUvChannels[k]);
            }

            // BlendShapes (Deformer "BlendShape" -> "BlendShapeChannel" -> Geometry "Shape")
            if (bsCount > 0)
            {
                long bsDeformerId = nextId();
                var bsDef = objectsNode.Child("Deformer").AddL(bsDeformerId).AddS(meshName + "\0\x01Deformer").AddS("BlendShape");
                bsDef.Child("Version").AddI(100);
                deformerCount++;
                connectionsNode.Child("C").AddS("OO").AddL(bsDeformerId).AddL(geomId);

                var dv = new Vector3[vCount];
                var dn = new Vector3[vCount];
                var dt = new Vector3[vCount];
                var sparseIdx = new List<int>();
                var sparseV = new List<double>();
                var sparseN = new List<double>();

                for (int b = 0; b < bsCount; b++)
                {
                    string shapeName = mesh.GetBlendShapeName(b);
                    int fCount = mesh.GetBlendShapeFrameCount(b);
                    if (fCount <= 0) continue;

                    long bscId = nextId();
                    double[] fullWeights = new double[fCount];
                    for (int f = 0; f < fCount; f++)
                        fullWeights[f] = mesh.GetBlendShapeFrameWeight(b, f);

                    var bsc = objectsNode.Child("Deformer").AddL(bscId).AddS(shapeName + "\0\x01SubDeformer").AddS("BlendShapeChannel");
                    bsc.Child("Version").AddI(100);
                    bsc.Child("DeformPercent").AddD(0.0);
                    bsc.Child("FullWeights").AddArrD(fullWeights);
                    deformerCount++;
                    connectionsNode.Child("C").AddS("OO").AddL(bscId).AddL(bsDeformerId);

                    for (int f = 0; f < fCount; f++)
                    {
                        mesh.GetBlendShapeFrameVertices(b, f, dv, dn, dt);
                        sparseIdx.Clear();
                        sparseV.Clear();
                        sparseN.Clear();

                        for (int v = 0; v < vCount; v++)
                        {
                            Vector3 sv = dv[v] * posScale;
                            Vector3 sn = dn[v];
                            if (sv.sqrMagnitude > 1e-16f || sn.sqrMagnitude > 1e-16f)
                            {
                                sparseIdx.Add(v);
                                sparseV.Add(-sv.x); sparseV.Add(sv.y); sparseV.Add(sv.z);
                                sparseN.Add(-sn.x); sparseN.Add(sn.y); sparseN.Add(sn.z);
                            }
                        }

                        long shapeGeomId = nextId();
                        string frameObjName = fCount == 1 ? shapeName : $"{shapeName}_{f}";
                        var shapeGeom = objectsNode.Child("Geometry").AddL(shapeGeomId).AddS(frameObjName + "\0\x01Geometry").AddS("Shape");
                        shapeGeom.Child("Properties70", true);
                        shapeGeom.Child("Version").AddI(100);
                        shapeGeom.Child("Indexes").AddArrI(sparseIdx.ToArray());
                        shapeGeom.Child("Vertices").AddArrD(sparseV.ToArray());
                        shapeGeom.Child("Normals").AddArrD(sparseN.ToArray());
                        geomCount++;

                        connectionsNode.Child("C").AddS("OO").AddL(shapeGeomId).AddL(bscId);
                    }
                }
            }
        }

        private static bool HasSkinnedMeshRendererOnSelf(Transform t)
        {
            return t != null && t.GetComponent<SkinnedMeshRenderer>() != null;
        }

        private static Matrix4x4 ScaleUnityBindposeTranslation(Matrix4x4 bp, float posMultiplier)
        {
            if (Mathf.Abs(posMultiplier - 1f) < 1e-6f)
                return bp;

            Matrix4x4 inv = bp.inverse;
            inv.m03 *= posMultiplier;
            inv.m13 *= posMultiplier;
            inv.m23 *= posMultiplier;
            return inv.inverse;
        }

        /// <summary>
        /// Unity左手系行列 U を FBX右手系行列 F = Sx * U * Sx に共役変換し、FBX列優先16要素配列へ変換する。
        /// </summary>
        private static double[] UnityMatrixToFbxColumnMajorArray(Matrix4x4 u)
        {
            // Sx = diag(-1, 1, 1, 1): 行0と列0の非対角要素の符号を反転
            Matrix4x4 f = u;
            f.m01 = -f.m01; f.m02 = -f.m02; f.m03 = -f.m03;
            f.m10 = -f.m10; f.m20 = -f.m20; f.m30 = -f.m30;

            return new double[]
            {
                f.m00, f.m10, f.m20, f.m30,
                f.m01, f.m11, f.m21, f.m31,
                f.m02, f.m12, f.m22, f.m32,
                f.m03, f.m13, f.m23, f.m33
            };
        }

        private static void DecomposeUnityToFbxLocalTRS(Matrix4x4 unityLocal, out Vector3 fbxT, out Vector3 fbxEulerDeg, out Vector3 fbxS)
        {
            Matrix4x4 f = unityLocal;
            f.m01 = -f.m01; f.m02 = -f.m02; f.m03 = -f.m03;
            f.m10 = -f.m10; f.m20 = -f.m20; f.m30 = -f.m30;

            fbxT = new Vector3(f.m03, f.m13, f.m23);

            Vector3 col0 = new Vector3(f.m00, f.m10, f.m20);
            Vector3 col1 = new Vector3(f.m01, f.m11, f.m21);
            Vector3 col2 = new Vector3(f.m02, f.m12, f.m22);

            float sx = col0.magnitude;
            float sy = col1.magnitude;
            float sz = col2.magnitude;
            if (sx < 1e-8f) sx = 1f;
            if (sy < 1e-8f) sy = 1f;
            if (sz < 1e-8f) sz = 1f;

            Vector3 r0 = col0 / sx;
            Vector3 r1 = col1 / sy;
            Vector3 r2 = col2 / sz;
            if (Vector3.Dot(Vector3.Cross(r0, r1), r2) < 0f)
            {
                sx = -sx;
                r0 = -r0;
            }
            fbxS = new Vector3(sx, sy, sz);

            // FBX既定 RotationOrder = eEulerXYZ: R = Rz(z) * Ry(y) * Rx(x)
            float r20 = Mathf.Clamp(r0.z, -1f, 1f);
            float ry = Mathf.Asin(-r20);
            float rx, rz;
            if (Mathf.Abs(Mathf.Cos(ry)) > 1e-5f)
            {
                rx = Mathf.Atan2(r1.z, r2.z);
                rz = Mathf.Atan2(r0.y, r0.x);
            }
            else
            {
                rx = Mathf.Atan2(-r2.y, r1.y);
                rz = 0f;
            }
            fbxEulerDeg = new Vector3(rx * Mathf.Rad2Deg, ry * Mathf.Rad2Deg, rz * Mathf.Rad2Deg);
        }

        private static void AddP70Int(FbxNode p70, string name, int val)
        {
            p70.Child("P").AddS(name).AddS("int").AddS("Integer").AddS("").AddI(val);
        }

        private static void AddP70Double(FbxNode p70, string name, double val)
        {
            p70.Child("P").AddS(name).AddS("double").AddS("Number").AddS("").AddD(val);
        }

        private static void AddP70Vec3(FbxNode p70, string name, string subType, Vector3 v)
        {
            p70.Child("P").AddS(name).AddS(subType).AddS("").AddS("A").AddD(v.x).AddD(v.y).AddD(v.z);
        }

        private static void WriteNodeRecursive(BinaryWriter bw, FbxNode node)
        {
            Stream s = bw.BaseStream;
            long headerPos = s.Position;
            bw.Write((uint)0); // endOffset placeholder
            bw.Write((uint)node.Props.Count);
            bw.Write((uint)0); // propListLen placeholder

            byte[] nameBytes = Encoding.ASCII.GetBytes(node.Name);
            bw.Write((byte)nameBytes.Length);
            bw.Write(nameBytes);

            long propsStart = s.Position;
            foreach (var p in node.Props)
                WriteProperty(bw, p);
            long propsEnd = s.Position;

            if (node.Children.Count > 0)
            {
                foreach (var child in node.Children)
                    WriteNodeRecursive(bw, child);
                WriteNullRecord(bw);
            }
            else if (node.ForceNullRecordWhenEmpty)
            {
                WriteNullRecord(bw);
            }

            long endPos = s.Position;
            s.Position = headerPos;
            bw.Write((uint)endPos);
            bw.Write((uint)node.Props.Count);
            bw.Write((uint)(propsEnd - propsStart));
            s.Position = endPos;
        }

        private static void WriteProperty(BinaryWriter bw, FbxProp p)
        {
            bw.Write((byte)p.TypeCode);
            switch (p.TypeCode)
            {
                case 'I':
                    bw.Write((int)p.Value);
                    break;
                case 'L':
                    bw.Write((long)p.Value);
                    break;
                case 'D':
                    bw.Write((double)p.Value);
                    break;
                case 'S':
                {
                    byte[] bytes = Encoding.UTF8.GetBytes((string)p.Value);
                    bw.Write(bytes.Length);
                    bw.Write(bytes);
                    break;
                }
                case 'R':
                {
                    byte[] bytes = (byte[])p.Value;
                    bw.Write(bytes.Length);
                    bw.Write(bytes);
                    break;
                }
                case 'i':
                {
                    int[] arr = (int[])p.Value;
                    bw.Write((uint)arr.Length);
                    bw.Write((uint)0); // uncompressed
                    bw.Write((uint)(arr.Length * 4));
                    for (int i = 0; i < arr.Length; i++) bw.Write(arr[i]);
                    break;
                }
                case 'd':
                {
                    double[] arr = (double[])p.Value;
                    bw.Write((uint)arr.Length);
                    bw.Write((uint)0); // uncompressed
                    bw.Write((uint)(arr.Length * 8));
                    for (int i = 0; i < arr.Length; i++) bw.Write(arr[i]);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unsupported FBX property type: {p.TypeCode}");
            }
        }

        private static void WriteNullRecord(BinaryWriter bw)
        {
            for (int i = 0; i < 13; i++)
                bw.Write((byte)0);
        }
    }
}
#endif
