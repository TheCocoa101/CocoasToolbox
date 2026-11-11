#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Blendshape Mesh Editor (Experimental) — Cocoa's Toolbox
/// SkinnedMeshRenderer に対して、SceneView 上で Blender の編集モード（Edit Mode）のように
/// 頂点・辺・面・アイランドを選択・変形し、その差分を BlendShape（シェイプキー）として新規 .mesh に保存するツール。
///
/// [主な機能]
/// ・Blender Edit Mode 風の選択（頂点/辺/面/アイランドLキー、Box選択B、ブラシ選択C、透過選択Alt+Z、選択拡大縮小）
/// ・同一座標頂点（UVシーム・法線スプリット）の自動結合移動（シーム裂け防止）
/// ・プロポーショナル編集（Oキー、半径調整、各種減衰カーブ、接続トポロジーのみ減衰対応）
/// ・X軸ミラー対称編集（X-Mirror）
/// ・移動(G/W) / 回転(R/E) / スケール(S) ギズモ ＆ グラブ / プッシュ・プル / スムーズブラシ
/// ・ボーン回転・スケール状態下でも見た目通りに変形できる逆スキニング計算
/// ・既存 BlendShape の読み込み・微調整・派生作成
/// ・全頂点属性・BoneWeights・Bindposes・全UV・既存全BlendShapeを完全維持した非破壊 .mesh 保存
/// </summary>
public class BlendshapeMeshEditor : EditorWindow
{
    // ─────────────────────────────────────────────────────────
    //  定数・列挙型
    // ─────────────────────────────────────────────────────────

    private const string WINDOW_TITLE = "Blendshape Mesh Editor (Experimental)";
    private const string DEFAULT_SAVE_DIR = "Assets/EditedBlendshapeMeshes";
    private const string ASSET_SUFFIX_MARKER = "_BSEdit-";

    public enum SelectElementMode
    {
        Vertex = 0,
        Edge = 1,
        Face = 2,
        Island = 3
    }

    public enum SelectionTool
    {
        ClickOrBox = 0,
        CircleBrush = 1
    }

    public enum TransformTool
    {
        GizmoMove = 0,
        GizmoRotate = 1,
        GizmoScale = 2,
        GrabBrush = 3,
        PushPullBrush = 4,
        SmoothBrush = 5
    }

    public enum FalloffType
    {
        Smooth = 0,
        Sphere = 1,
        Root = 2,
        Sharp = 3,
        Linear = 4,
        Constant = 5
    }

    public enum PivotSpace
    {
        Global = 0,
        Local = 1,
        Normal = 2
    }

    public enum BaseShapeMode
    {
        NewShape = 0,
        ModifyExistingShape = 1
    }

    public enum ApplyMode
    {
        ReplaceInPlace = 0,
        DuplicateObject = 1,
        SaveAssetOnly = 2
    }

    // ─────────────────────────────────────────────────────────
    //  シリアライズ状態
    // ─────────────────────────────────────────────────────────

    [SerializeField] private GameObject _targetRootOrGo;
    [SerializeField] private SkinnedMeshRenderer _activeSMR;
    [SerializeField] private bool _editModeActive = false;

    // ベースシェイプ設定
    [SerializeField] private BaseShapeMode _baseShapeMode = BaseShapeMode.NewShape;
    [SerializeField] private int _sourceBlendShapeIndex = -1;
    [SerializeField] private string _newShapeName = "New_Shape";
    [SerializeField] private bool _recalculateNormals = true;

    // 編集ツール設定
    [SerializeField] private SelectElementMode _elementMode = SelectElementMode.Vertex;
    [SerializeField] private SelectionTool _selectionTool = SelectionTool.ClickOrBox;
    [SerializeField] private TransformTool _transformTool = TransformTool.GizmoMove;
    [SerializeField] private PivotSpace _pivotSpace = PivotSpace.Global;
    [SerializeField] private bool _xRaySelect = false;
    [SerializeField] private bool _weldCoincidentVertices = true;

    // プロポーショナル編集設定
    [SerializeField] private bool _proportionalEditing = true;
    [SerializeField] private float _proportionalRadius = 0.08f;
    [SerializeField] private FalloffType _falloffType = FalloffType.Smooth;
    [SerializeField] private bool _proportionalConnectedOnly = false;

    // 対称編集
    [SerializeField] private bool _xMirror = false;
    [SerializeField] private float _xMirrorThreshold = 0.001f;

    // ブラシ設定
    [SerializeField] private float _brushRadius = 0.05f;
    [SerializeField] private float _brushStrength = 0.5f;

    // プレビュー設定
    [SerializeField] private float _previewWeight = 100f; // 0..100
    [SerializeField] private bool _livePreview = true;

    // 保存設定
    [SerializeField] private ApplyMode _applyMode = ApplyMode.ReplaceInPlace;
    [SerializeField] private string _saveFolderPath = "";
    [SerializeField] private bool _autoSetWeightAfterSave = true;

    // UI表示
    [SerializeField] private Vector2 _scrollMain;
    [SerializeField] private bool _foldTarget = true;
    [SerializeField] private bool _foldTools = true;
    [SerializeField] private bool _foldProportional = true;
    [SerializeField] private bool _foldQuickDeform = true;
    [SerializeField] private bool _foldSave = true;
    [SerializeField] private string _statusMessage = "";
    [SerializeField] private MessageType _statusType = MessageType.Info;

    // ─────────────────────────────────────────────────────────
    //  ランタイム状態
    // ─────────────────────────────────────────────────────────

    private List<SkinnedMeshRenderer> _candidateSMRs = new List<SkinnedMeshRenderer>();
    private string[] _candidateSMRNames = Array.Empty<string>();
    private string[] _existingBlendShapeNames = Array.Empty<string>();

    // メッシュトポロジー・スキニング・デルタ管理
    private BSEditTopology _topology;
    private BSEditSkinning _skinning;

    // 頂点ごとのローカル BlendShape デルタ（バインドポーズ空間）
    // 頂点数 vc と同一長
    private Vector3[] _localDeltas;

    // 選択状態（クラスタIDの HashSet）
    private readonly HashSet<int> _selectedClusters = new HashSet<int>();

    // ギズモ操作中の変形開始時デルタバックアップ
    private Vector3[] _dragInitialDeltas;
    private Vector3 _dragStartWorldPos;
    private Quaternion _dragStartWorldRot;
    private Vector3 _dragStartWorldScale;
    private bool _isManipulatingGizmo = false;

    // ブラシドラッグ状態
    private bool _isBrushDragging = false;
    private Vector3 _brushLastRayOrigin;
    private Vector3 _brushLastRayDir;

    // 矩形選択 (Box Select) 状態
    private bool _isBoxSelecting = false;
    private Vector2 _boxSelectStartPos;
    private Vector2 _boxSelectCurrentPos;

    // サークル選択 (Circle Brush) 状態
    private bool _isCircleSelecting = false;
    private float _circleSelectRadiusPx = 30f;

    // ホバー要素
    private int _hoveredClusterId = -1;
    private int _hoveredTriangleId = -1;

    // プレビュー用一時メッシュ
    private Mesh _originalSharedMesh;
    private Mesh _tempPreviewMesh;
    private bool _previewApplied = false;

    // 元の BlendShape ウェイト退避（既存シェイプ修正時、二重変形を防ぐため一時的に 0 にする）
    private float _savedSourceShapeWeight = -1f;

    // GUIStyle
    private GUIStyle _headerTitleStyle;
    private GUIStyle _subHeaderStyle;
    private GUIStyle _modeBtnActiveStyle;
    private GUIStyle _modeBtnIdleStyle;
    private GUIStyle _actionBtnStyle;

    private GUIStyle HeaderTitleStyle => _headerTitleStyle ?? (_headerTitleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 });
    private GUIStyle SubHeaderStyle => _subHeaderStyle ?? (_subHeaderStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true });
    private GUIStyle ModeBtnActiveStyle => _modeBtnActiveStyle ?? (_modeBtnActiveStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.2f, 0.9f, 1f) } });
    private GUIStyle ModeBtnIdleStyle => _modeBtnIdleStyle ?? (_modeBtnIdleStyle = new GUIStyle(GUI.skin.button));
    private GUIStyle ActionBtnStyle => _actionBtnStyle ?? (_actionBtnStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fixedHeight = 32f });

    // ─────────────────────────────────────────────────────────
    //  メニュー登録
    // ─────────────────────────────────────────────────────────

    [MenuItem("Tools/Cocoa's Toolbox/Blendshape Mesh Editor (Experimental)")]
    public static void Open()
    {
        var win = GetWindow<BlendshapeMeshEditor>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 660f);
        win.Show();
        if (win._activeSMR == null && Selection.activeGameObject != null)
        {
            win.SetTarget(Selection.activeGameObject);
        }
    }

    [MenuItem("GameObject/Cocoa's Toolbox/Blendshape Mesh Editor (Experimental)", false, 26)]
    private static void OpenFromHierarchy(MenuCommand command)
    {
        GameObject go = command.context as GameObject ?? Selection.activeGameObject;
        var win = GetWindow<BlendshapeMeshEditor>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 660f);
        win.Show();
        win.Focus();
        if (go != null)
        {
            win.SetTarget(go);
        }
    }

    [MenuItem("CONTEXT/SkinnedMeshRenderer/Blendshape Mesh Editor (Experimental)")]
    private static void OpenFromSMRContext(MenuCommand command)
    {
        var smr = command.context as SkinnedMeshRenderer;
        var win = GetWindow<BlendshapeMeshEditor>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 660f);
        win.Show();
        win.Focus();
        if (smr != null)
        {
            win.SetDirectSMR(smr);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  ライフサイクル管理
    // ─────────────────────────────────────────────────────────

    private void OnEnable()
    {
        minSize = new Vector2(440f, 660f);
        SceneView.duringSceneGui -= OnSceneGUI;
        SceneView.duringSceneGui += OnSceneGUI;
        Undo.undoRedoPerformed -= OnUndoRedo;
        Undo.undoRedoPerformed += OnUndoRedo;
        AssemblyReloadEvents.beforeAssemblyReload -= StopPreviewAndRestore;
        AssemblyReloadEvents.beforeAssemblyReload += StopPreviewAndRestore;
        EditorApplication.playModeStateChanged -= OnPlayModeChange;
        EditorApplication.playModeStateChanged += OnPlayModeChange;

        if (_activeSMR != null)
        {
            RebuildContext(preserveDeltas: true);
        }
        else if (_targetRootOrGo != null)
        {
            SetTarget(_targetRootOrGo);
        }
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        Undo.undoRedoPerformed -= OnUndoRedo;
        AssemblyReloadEvents.beforeAssemblyReload -= StopPreviewAndRestore;
        EditorApplication.playModeStateChanged -= OnPlayModeChange;
        StopPreviewAndRestore();
    }

    private void OnDestroy()
    {
        StopPreviewAndRestore();
    }

    private void OnPlayModeChange(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
        {
            StopPreviewAndRestore();
        }
    }

    private void OnUndoRedo()
    {
        if (_editModeActive && _activeSMR != null)
        {
            ApplyPreviewMesh();
            Repaint();
            SceneView.RepaintAll();
        }
    }

    // ─────────────────────────────────────────────────────────
    //  ターゲット設定 & コンテキスト構築
    // ─────────────────────────────────────────────────────────

    public void SetTarget(GameObject go)
    {
        StopPreviewAndRestore();
        _targetRootOrGo = go;
        _candidateSMRs.Clear();

        if (go != null)
        {
            _candidateSMRs.AddRange(go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(s => s != null && s.sharedMesh != null));
        }

        _candidateSMRNames = _candidateSMRs.Select((s, idx) => $"{idx}: {s.gameObject.name}").ToArray();

        var directSmr = go != null ? go.GetComponent<SkinnedMeshRenderer>() : null;
        if (directSmr != null && _candidateSMRs.Contains(directSmr))
            _activeSMR = directSmr;
        else
            _activeSMR = _candidateSMRs.Count > 0 ? _candidateSMRs[0] : null;

        RebuildContext(preserveDeltas: false);
        Repaint();
        SceneView.RepaintAll();
    }

    public void SetDirectSMR(SkinnedMeshRenderer smr)
    {
        if (smr == null) return;
        StopPreviewAndRestore();
        _targetRootOrGo = smr.gameObject;
        _candidateSMRs = new List<SkinnedMeshRenderer> { smr };
        _candidateSMRNames = new[] { $"0: {smr.gameObject.name}" };
        _activeSMR = smr;
        RebuildContext(preserveDeltas: false);
        Repaint();
        SceneView.RepaintAll();
    }

    private void RebuildContext(bool preserveDeltas)
    {
        StopPreviewAndRestore();

        if (_activeSMR == null || _activeSMR.sharedMesh == null)
        {
            _topology = null;
            _skinning = null;
            _localDeltas = null;
            _selectedClusters.Clear();
            _existingBlendShapeNames = Array.Empty<string>();
            return;
        }

        Mesh mesh = _activeSMR.sharedMesh;
        _originalSharedMesh = mesh;

        // トポロジー解析
        _topology = new BSEditTopology(mesh);
        _topology.BuildTopology(_weldCoincidentVertices, _xMirrorThreshold);

        // スキニング解析
        _skinning = new BSEditSkinning(_activeSMR, mesh, _topology);
        _skinning.UpdateWorldPositions();

        // 既存 BlendShape 一覧
        int bsCount = mesh.blendShapeCount;
        _existingBlendShapeNames = new string[bsCount];
        for (int i = 0; i < bsCount; i++)
            _existingBlendShapeNames[i] = mesh.GetBlendShapeName(i);

        if (_sourceBlendShapeIndex < 0 || _sourceBlendShapeIndex >= bsCount)
            _sourceBlendShapeIndex = bsCount > 0 ? 0 : -1;

        int vc = mesh.vertexCount;
        if (!preserveDeltas || _localDeltas == null || _localDeltas.Length != vc)
        {
            _localDeltas = new Vector3[vc];
            _selectedClusters.Clear();

            // 既存シェイプ修正モードならデルタを読み込む
            if (_baseShapeMode == BaseShapeMode.ModifyExistingShape && _sourceBlendShapeIndex >= 0 && _sourceBlendShapeIndex < bsCount)
            {
                LoadExistingShapeDeltas(_sourceBlendShapeIndex);
            }
        }

        if (_editModeActive)
        {
            ApplyPreviewMesh();
        }

        SetStatus($"「{_activeSMR.gameObject.name}」を読み込みました（頂点数: {vc:N0} / 三角面数: {mesh.triangles.Length / 3:N0} / クラスタ数: {_topology.ClusterCount:N0}）。", MessageType.Info);
    }

    private void LoadExistingShapeDeltas(int shapeIndex)
    {
        if (_activeSMR == null || _originalSharedMesh == null || _localDeltas == null) return;
        Mesh m = _originalSharedMesh;
        if (shapeIndex < 0 || shapeIndex >= m.blendShapeCount) return;

        int vc = m.vertexCount;
        int frameCount = m.GetBlendShapeFrameCount(shapeIndex);
        if (frameCount == 0) return;

        // 最終フレーム（100% デルタ）を読み込む
        var dV = new Vector3[vc];
        var dN = new Vector3[vc];
        var dT = new Vector3[vc];
        m.GetBlendShapeFrameVertices(shapeIndex, frameCount - 1, dV, dN, dT);

        Array.Copy(dV, _localDeltas, vc);

        // 読み込んだシェイプ名をデフォルト名に反映
        _newShapeName = m.GetBlendShapeName(shapeIndex) + "_Modified";

        // 二重変形防止のため、元のシェイプの Weight を退避して 0 にする
        _savedSourceShapeWeight = _activeSMR.GetBlendShapeWeight(shapeIndex);
        _activeSMR.SetBlendShapeWeight(shapeIndex, 0f);
    }

    // ─────────────────────────────────────────────────────────
    //  プレビューシステム（TempMesh リアルタイム生成 & 反映）
    // ─────────────────────────────────────────────────────────

    private void StartEditMode()
    {
        if (_activeSMR == null || _activeSMR.sharedMesh == null) return;
        _editModeActive = true;
        if (_baseShapeMode == BaseShapeMode.ModifyExistingShape && _sourceBlendShapeIndex >= 0)
        {
            _savedSourceShapeWeight = _activeSMR.GetBlendShapeWeight(_sourceBlendShapeIndex);
            _activeSMR.SetBlendShapeWeight(_sourceBlendShapeIndex, 0f);
        }
        ApplyPreviewMesh();
        SceneView.RepaintAll();
        Repaint();
    }

    private void ExitEditMode()
    {
        _editModeActive = false;
        StopPreviewAndRestore();
        SceneView.RepaintAll();
        Repaint();
    }

    private void ApplyPreviewMesh()
    {
        if (!_editModeActive || _activeSMR == null || _originalSharedMesh == null || _localDeltas == null) return;

        if (!_previewApplied)
        {
            _previewApplied = true;
        }

        // 新しいプレビュー用 Mesh を手動複製
        Mesh newTemp = CreatePreviewMeshInstance(_originalSharedMesh, _localDeltas, _previewWeight / 100f, _recalculateNormals);
        newTemp.hideFlags = HideFlags.HideAndDontSave;

        // 先に SMR に刺して画面チラつきを防止
        _activeSMR.sharedMesh = newTemp;

        // 古いプレビューメッシュを破棄
        if (_tempPreviewMesh != null)
        {
            DestroyImmediate(_tempPreviewMesh);
        }
        _tempPreviewMesh = newTemp;

        // スキニングワールド座標を更新
        if (_skinning != null)
        {
            _skinning.UpdateWorldPositions();
        }

        SceneView.RepaintAll();
    }

    private void StopPreviewAndRestore()
    {
        if (_previewApplied && _activeSMR != null && _originalSharedMesh != null)
        {
            _activeSMR.sharedMesh = _originalSharedMesh;
            if (_savedSourceShapeWeight >= 0f && _sourceBlendShapeIndex >= 0 && _sourceBlendShapeIndex < _originalSharedMesh.blendShapeCount)
            {
                _activeSMR.SetBlendShapeWeight(_sourceBlendShapeIndex, _savedSourceShapeWeight);
                _savedSourceShapeWeight = -1f;
            }
            _previewApplied = false;
        }

        if (_tempPreviewMesh != null)
        {
            DestroyImmediate(_tempPreviewMesh);
            _tempPreviewMesh = null;
        }

        if (_skinning != null)
        {
            _skinning.UpdateWorldPositions();
        }
    }

    private static Mesh CreatePreviewMeshInstance(Mesh srcMesh, Vector3[] deltas, float weight01, bool recalcNormals)
    {
        int vc = srcMesh.vertexCount;
        var tmp = new Mesh
        {
            name = srcMesh.name + "_BSEditPreview",
            indexFormat = vc >= 65536 ? IndexFormat.UInt32 : srcMesh.indexFormat
        };

        // 基本頂点属性のコピー
        var baseVerts = srcMesh.vertices;
        var previewVerts = new Vector3[vc];
        for (int i = 0; i < vc; i++)
        {
            previewVerts[i] = baseVerts[i] + deltas[i] * weight01;
        }
        tmp.vertices = previewVerts;

        // 法線
        if (recalcNormals)
        {
            // まず元の法線をコピーし、後で RecalculateNormals
            tmp.normals = srcMesh.normals;
        }
        else
        {
            tmp.normals = srcMesh.normals;
        }

        tmp.tangents = srcMesh.tangents;
        tmp.uv = srcMesh.uv;
        tmp.uv2 = srcMesh.uv2;
        tmp.uv3 = srcMesh.uv3;
        tmp.uv4 = srcMesh.uv4;
        tmp.colors32 = srcMesh.colors32;
        tmp.boneWeights = srcMesh.boneWeights;
        tmp.bindposes = srcMesh.bindposes;

        tmp.subMeshCount = srcMesh.subMeshCount;
        for (int s = 0; s < srcMesh.subMeshCount; s++)
            tmp.SetTriangles(srcMesh.GetTriangles(s), s);

        if (recalcNormals)
        {
            tmp.RecalculateNormals();
            tmp.RecalculateTangents();
        }

        // 既存 BlendShape をすべてコピー
        for (int bi = 0; bi < srcMesh.blendShapeCount; bi++)
        {
            string shapeName = srcMesh.GetBlendShapeName(bi);
            int frameCount = srcMesh.GetBlendShapeFrameCount(bi);
            for (int fi = 0; fi < frameCount; fi++)
            {
                float fw = srcMesh.GetBlendShapeFrameWeight(bi, fi);
                var fV = new Vector3[vc];
                var fN = new Vector3[vc];
                var fTan = new Vector3[vc];
                srcMesh.GetBlendShapeFrameVertices(bi, fi, fV, fN, fTan);
                tmp.AddBlendShapeFrame(shapeName, fw, fV, fN, fTan);
            }
        }

        tmp.RecalculateBounds();
        return tmp;
    }

    // ─────────────────────────────────────────────────────────
    //  GUI 描画（Inspector / Window）
    // ─────────────────────────────────────────────────────────

    private void OnGUI()
    {
        _scrollMain = EditorGUILayout.BeginScrollView(_scrollMain);

        DrawHeader();
        EditorGUILayout.Space(4f);

        DrawTargetAndBaseSection();
        EditorGUILayout.Space(6f);

        using (new EditorGUI.DisabledScope(_activeSMR == null || _activeSMR.sharedMesh == null))
        {
            DrawEditModeControlsSection();
            EditorGUILayout.Space(6f);

            DrawProportionalAndMirrorSection();
            EditorGUILayout.Space(6f);

            DrawQuickDeformSection();
            EditorGUILayout.Space(6f);

            DrawSaveSection();
        }

        EditorGUILayout.EndScrollView();

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            EditorGUILayout.Space(2f);
            EditorGUILayout.HelpBox(_statusMessage, _statusType);
            EditorGUILayout.Space(2f);
        }
    }

    private void DrawHeader()
    {
        EditorGUILayout.LabelField("Blendshape Mesh Editor (Experimental)", HeaderTitleStyle);
        EditorGUILayout.LabelField("Cocoa's Toolbox — SceneView 上でメッシュを変形して Blendshape として新規 .mesh 保存", SubHeaderStyle);
        var lineRect = GUILayoutUtility.GetRect(0f, 1f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(lineRect, EditorStyles.label.normal.textColor * 0.3f);
    }

    private void DrawTargetAndBaseSection()
    {
        _foldTarget = EditorGUILayout.Foldout(_foldTarget, "① 対象メッシュ ＆ ベースシェイプ設定", true, EditorStyles.foldoutHeader);
        if (!_foldTarget) return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // 対象オブジェクト
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var nextGo = (GameObject)EditorGUILayout.ObjectField("対象 GameObject", _targetRootOrGo, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck())
                {
                    SetTarget(nextGo);
                }

                using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
                {
                    if (GUILayout.Button("選択中をセット", EditorStyles.miniButton, GUILayout.Width(92f)))
                    {
                        SetTarget(Selection.activeGameObject);
                    }
                }
            }

            // SMR ドロップダウン
            if (_candidateSMRs.Count > 1)
            {
                int currentIdx = Mathf.Max(0, _candidateSMRs.IndexOf(_activeSMR));
                EditorGUI.BeginChangeCheck();
                int nextIdx = EditorGUILayout.Popup("対象 SkinnedMesh", currentIdx, _candidateSMRNames);
                if (EditorGUI.EndChangeCheck() && nextIdx >= 0 && nextIdx < _candidateSMRs.Count)
                {
                    _activeSMR = _candidateSMRs[nextIdx];
                    RebuildContext(preserveDeltas: false);
                }
            }
            else if (_activeSMR != null)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("対象 Renderer", _activeSMR, typeof(SkinnedMeshRenderer), true);
                }
            }

            EditorGUILayout.Space(4f);

            // ベースシェイプモード
            EditorGUI.BeginChangeCheck();
            _baseShapeMode = (BaseShapeMode)EditorGUILayout.EnumPopup("変形ベース", _baseShapeMode);
            if (EditorGUI.EndChangeCheck())
            {
                if (_baseShapeMode == BaseShapeMode.ModifyExistingShape && _sourceBlendShapeIndex >= 0)
                {
                    LoadExistingShapeDeltas(_sourceBlendShapeIndex);
                }
                else
                {
                    ResetAllDeltas();
                    _savedSourceShapeWeight = -1f;
                }
                if (_editModeActive) ApplyPreviewMesh();
            }

            if (_baseShapeMode == BaseShapeMode.ModifyExistingShape)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    if (_existingBlendShapeNames.Length > 0)
                    {
                        EditorGUI.BeginChangeCheck();
                        _sourceBlendShapeIndex = EditorGUILayout.Popup("読み込む BlendShape", _sourceBlendShapeIndex, _existingBlendShapeNames);
                        if (EditorGUI.EndChangeCheck())
                        {
                            LoadExistingShapeDeltas(_sourceBlendShapeIndex);
                            if (_editModeActive) ApplyPreviewMesh();
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("メッシュに既存の BlendShape が存在しません。", MessageType.Warning);
                    }
                }
            }

            _newShapeName = EditorGUILayout.TextField("新規 BlendShape 名", _newShapeName);
            _recalculateNormals = EditorGUILayout.Toggle(new GUIContent("法線デルタを再計算", "ON: 変形に合わせて陰影・法線デルタを自動計算します。OFF: トゥーンシェーダー等の元法線を維持します。"), _recalculateNormals);
        }
    }

    private void DrawEditModeControlsSection()
    {
        _foldTools = EditorGUILayout.Foldout(_foldTools, "② Blender Edit Mode 風 操作 ＆ ツール設定", true, EditorStyles.foldoutHeader);
        if (!_foldTools) return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // 編集モード ON/OFF 大ボタン
            Color prevBg = GUI.backgroundColor;
            if (_editModeActive) GUI.backgroundColor = new Color(0.35f, 0.85f, 1f, 1f);

            string btnLabel = _editModeActive
                ? "🛠️ SceneView 編集モード：編集中（クリックで終了・プレビュー解除）"
                : "🛠️ SceneView 編集モードを開始する（クリックで ON）";

            if (GUILayout.Button(btnLabel, ActionBtnStyle))
            {
                if (_editModeActive) ExitEditMode();
                else StartEditMode();
            }
            GUI.backgroundColor = prevBg;

            EditorGUILayout.Space(4f);

            // 選択単位（頂点 / 辺 / 面 / アイランド）
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("選択単位");
                if (GUILayout.Toggle(_elementMode == SelectElementMode.Vertex, "頂点 (1)", _elementMode == SelectElementMode.Vertex ? ModeBtnActiveStyle : ModeBtnIdleStyle))
                    _elementMode = SelectElementMode.Vertex;
                if (GUILayout.Toggle(_elementMode == SelectElementMode.Edge, "辺 (2)", _elementMode == SelectElementMode.Edge ? ModeBtnActiveStyle : ModeBtnIdleStyle))
                    _elementMode = SelectElementMode.Edge;
                if (GUILayout.Toggle(_elementMode == SelectElementMode.Face, "面 (3)", _elementMode == SelectElementMode.Face ? ModeBtnActiveStyle : ModeBtnIdleStyle))
                    _elementMode = SelectElementMode.Face;
                if (GUILayout.Toggle(_elementMode == SelectElementMode.Island, "島 (L)", _elementMode == SelectElementMode.Island ? ModeBtnActiveStyle : ModeBtnIdleStyle))
                    _elementMode = SelectElementMode.Island;
            }

            // 変形ツール
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("変形ツール");
                if (GUILayout.Toggle(_transformTool == TransformTool.GizmoMove, "移動 (G)", _transformTool == TransformTool.GizmoMove ? ModeBtnActiveStyle : ModeBtnIdleStyle))
                    _transformTool = TransformTool.GizmoMove;
                if (GUILayout.Toggle(_transformTool == TransformTool.GizmoRotate, "回転 (R)", _transformTool == TransformTool.GizmoRotate ? ModeBtnActiveStyle : ModeBtnIdleStyle))
                    _transformTool = TransformTool.GizmoRotate;
                if (GUILayout.Toggle(_transformTool == TransformTool.GizmoScale, "拡縮 (S)", _transformTool == TransformTool.GizmoScale ? ModeBtnActiveStyle : ModeBtnIdleStyle))
                    _transformTool = TransformTool.GizmoScale;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("ブラシ変形");
                if (GUILayout.Toggle(_transformTool == TransformTool.GrabBrush, "グラブ", _transformTool == TransformTool.GrabBrush ? ModeBtnActiveStyle : ModeBtnIdleStyle))
                    _transformTool = TransformTool.GrabBrush;
                if (GUILayout.Toggle(_transformTool == TransformTool.PushPullBrush, "法線押出", _transformTool == TransformTool.PushPullBrush ? ModeBtnActiveStyle : ModeBtnIdleStyle))
                    _transformTool = TransformTool.PushPullBrush;
                if (GUILayout.Toggle(_transformTool == TransformTool.SmoothBrush, "スムーズ", _transformTool == TransformTool.SmoothBrush ? ModeBtnActiveStyle : ModeBtnIdleStyle))
                    _transformTool = TransformTool.SmoothBrush;
            }

            if (_transformTool == TransformTool.GrabBrush || _transformTool == TransformTool.PushPullBrush || _transformTool == TransformTool.SmoothBrush)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    _brushRadius = EditorGUILayout.Slider("ブラシ半径 (m)", _brushRadius, 0.005f, 0.5f);
                    _brushStrength = EditorGUILayout.Slider("ブラシ強度", _brushStrength, 0.05f, 1.0f);
                }
            }

            // ギズモ座標軸
            if (_transformTool == TransformTool.GizmoMove || _transformTool == TransformTool.GizmoRotate || _transformTool == TransformTool.GizmoScale)
            {
                _pivotSpace = (PivotSpace)EditorGUILayout.EnumPopup("ギズモ座標系", _pivotSpace);
            }

            // 選択ツール & オプション
            _selectionTool = (SelectionTool)EditorGUILayout.Popup("選択手法", (int)_selectionTool, new[] { "クリック / 矩形Box選択 (B)", "サークルブラシ選択 (C)" });
            _xRaySelect = EditorGUILayout.Toggle(new GUIContent("透過選択 (Alt+Z 相当)", "ON: 裏側の隠れた頂点も一緒に貫通選択します。OFF: 表に見えている頂点のみ選択。"), _xRaySelect);

            EditorGUI.BeginChangeCheck();
            _weldCoincidentVertices = EditorGUILayout.Toggle(new GUIContent("同座標頂点を結合 (シーム裂け防止)", "ON: UVシームや法線スプリットで複数に分かれた同一3D座標頂点をまとめて選択・移動します（推奨）。"), _weldCoincidentVertices);
            if (EditorGUI.EndChangeCheck() && _topology != null)
            {
                _topology.BuildTopology(_weldCoincidentVertices, _xMirrorThreshold);
            }

            EditorGUILayout.Space(2f);

            // 選択ショートカットボタン
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全選択 (A)", EditorStyles.miniButtonLeft)) SelectAll();
                if (GUILayout.Button("全解除", EditorStyles.miniButtonMid)) ClearSelection();
                if (GUILayout.Button("選択反転", EditorStyles.miniButtonMid)) InvertSelection();
                if (GUILayout.Button("選択拡大 (+)", EditorStyles.miniButtonMid)) GrowSelection();
                if (GUILayout.Button("選択縮小 (-)", EditorStyles.miniButtonRight)) ShrinkSelection();
            }

            if (_topology != null)
            {
                int totalVerts = _activeSMR.sharedMesh.vertexCount;
                int selVerts = CountSelectedVertices();
                EditorGUILayout.LabelField($"選択中: {_selectedClusters.Count:N0} クラスタ / 頂点 {selVerts:N0} of {totalVerts:N0} ({selVerts * 100f / totalVerts:0.0}%)", EditorStyles.miniBoldLabel);
            }
        }
    }

    private void DrawProportionalAndMirrorSection()
    {
        _foldProportional = EditorGUILayout.Foldout(_foldProportional, "③ プロポーショナル編集 (O) ＆ X軸ミラー (X)", true, EditorStyles.foldoutHeader);
        if (!_foldProportional) return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // プロポーショナル編集
            using (new EditorGUILayout.HorizontalScope())
            {
                _proportionalEditing = EditorGUILayout.ToggleLeft("プロポーショナル編集を有効化 (O)", _proportionalEditing, EditorStyles.boldLabel);
            }

            if (_proportionalEditing)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    _proportionalRadius = EditorGUILayout.Slider("影響半径 (m) [ [ / ] ]", _proportionalRadius, 0.005f, 0.5f);
                    _falloffType = (FalloffType)EditorGUILayout.EnumPopup("減衰カーブ", _falloffType);
                    _proportionalConnectedOnly = EditorGUILayout.Toggle(new GUIContent("接続トポロジーのみ (Connected)", "ON: 空間距離ではなくメッシュのエッジ接続に沿って減衰を計算します（離れた唇や髪の巻き込みを防止）。"), _proportionalConnectedOnly);
                }
            }

            EditorGUILayout.Space(4f);

            // X軸ミラー
            _xMirror = EditorGUILayout.ToggleLeft("X軸ミラー対称編集 (X-Mirror)", _xMirror, EditorStyles.boldLabel);
            if (_xMirror)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    _xMirrorThreshold = EditorGUILayout.FloatField("対称判定の許容誤差 (m)", _xMirrorThreshold);
                }
            }
        }
    }

    private void DrawQuickDeformSection()
    {
        _foldQuickDeform = EditorGUILayout.Foldout(_foldQuickDeform, "④ クイック変形 ＆ 変形リセット", true, EditorStyles.foldoutHeader);
        if (!_foldQuickDeform) return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // 法線方向押し出し/収縮
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("選択頂点を法線へ膨張 (+0.5cm)", EditorStyles.miniButtonLeft))
                {
                    PushSelectedAlongNormals(0.005f);
                }
                if (GUILayout.Button("選択頂点を法線へ収縮 (-0.5cm)", EditorStyles.miniButtonRight))
                {
                    PushSelectedAlongNormals(-0.005f);
                }
            }

            // スムーズ
            if (GUILayout.Button("選択頂点の変形をスムーズ化（均し）", EditorStyles.miniButton))
            {
                SmoothSelectedDeltas(0.35f);
            }

            EditorGUILayout.Space(2f);

            // リセット
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("選択頂点の変形をリセット", EditorStyles.miniButtonLeft))
                {
                    ResetSelectedDeltas();
                }
                if (GUILayout.Button("全変形を初期化（リセット）", EditorStyles.miniButtonRight))
                {
                    if (EditorUtility.DisplayDialog("変形リセット", "編集中のすべての変形デルタをゼロにリセットしますか？", "リセット", "キャンセル"))
                    {
                        ResetAllDeltas();
                    }
                }
            }
        }
    }

    private void DrawSaveSection()
    {
        _foldSave = EditorGUILayout.Foldout(_foldSave, "⑤ プレビューウェイト確認 ＆ .mesh 保存", true, EditorStyles.foldoutHeader);
        if (!_foldSave) return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // プレビューウェイト
            EditorGUI.BeginChangeCheck();
            _previewWeight = EditorGUILayout.Slider("プレビュー Weight (%)", _previewWeight, 0f, 100f);
            _livePreview = EditorGUILayout.ToggleLeft("変形中のリアルタイムメッシュ更新 (Live Preview)", _livePreview);
            if (EditorGUI.EndChangeCheck() && _editModeActive)
            {
                ApplyPreviewMesh();
            }

            EditorGUILayout.Space(4f);

            // 保存先
            _applyMode = (ApplyMode)EditorGUILayout.Popup("保存後の適用方法", (int)_applyMode, new[]
            {
                "その場で差し替え（SMR.sharedMesh を新規 .mesh に変更・Undo対応）",
                "複製して生成（元のオブジェクトを非表示にして複製側に適用・非破壊）",
                ".mesh アセットの保存のみ"
            });

            _autoSetWeightAfterSave = EditorGUILayout.Toggle("保存後に新規シェイプ Weight を 100 にセット", _autoSetWeightAfterSave);

            using (new EditorGUILayout.HorizontalScope())
            {
                string disp = string.IsNullOrEmpty(_saveFolderPath) ? $"（自動: {ResolveSaveFolder()}）" : _saveFolderPath;
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("保存フォルダ", disp);
                }
                if (GUILayout.Button("…", GUILayout.Width(28f)))
                {
                    string abs = EditorUtility.OpenFolderPanel("保存先フォルダを選択", Application.dataPath, "");
                    if (!string.IsNullOrEmpty(abs))
                    {
                        string dataPath = Application.dataPath.Replace('\\', '/');
                        string normAbs = abs.Replace('\\', '/');
                        if (normAbs.Equals(dataPath, StringComparison.OrdinalIgnoreCase))
                            _saveFolderPath = "Assets";
                        else if (normAbs.StartsWith(dataPath + "/", StringComparison.OrdinalIgnoreCase))
                            _saveFolderPath = "Assets/" + normAbs.Substring(dataPath.Length + 1);
                        else
                            SetStatus("保存先はプロジェクトの Assets フォルダ内を指定してください。", MessageType.Warning);
                    }
                }
                if (!string.IsNullOrEmpty(_saveFolderPath) && GUILayout.Button("戻す", EditorStyles.miniButton, GUILayout.Width(42f)))
                {
                    _saveFolderPath = "";
                }
            }

            EditorGUILayout.Space(6f);

            // 保存実行ボタン
            bool hasDeltas = HasAnyDeltas();
            using (new EditorGUI.DisabledScope(!hasDeltas || string.IsNullOrWhiteSpace(_newShapeName)))
            {
                if (GUILayout.Button("✦  BlendShape を追加して新規 .mesh 保存  ✦", ActionBtnStyle))
                {
                    BakeAndSaveMesh();
                    GUIUtility.ExitGUI();
                }
            }

            if (!hasDeltas)
            {
                EditorGUILayout.HelpBox("メッシュの頂点に変形が加えられていません。SceneView 上で頂点を移動してから保存してください。", MessageType.Info);
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  SceneView インタラクション (OnSceneGUI)
    // ─────────────────────────────────────────────────────────

    private void OnSceneGUI(SceneView sceneView)
    {
        if (!_editModeActive || _activeSMR == null || _activeSMR.sharedMesh == null || _topology == null || _skinning == null)
            return;

        Event e = Event.current;

        // 1. SceneView HUD 描画
        DrawSceneHUD();

        // 2. ホバー判定 & レイキャスト
        UpdateHoverState(e);

        // 3. キーボードショートカット処理
        HandleShortcuts(e);

        // 4. マウス操作処理（ギズモ / ブラシ / 選択）
        HandleMouseInteraction(e);

        // 5. GL オーバーレイ描画
        if (e.type == EventType.Repaint)
        {
            DrawSceneOverlays();
        }
    }

    private void DrawSceneHUD()
    {
        Handles.BeginGUI();
        var hudRect = new Rect(12f, 12f, 340f, 96f);
        EditorGUI.DrawRect(hudRect, new Color(0.12f, 0.12f, 0.12f, 0.90f));
        EditorGUI.DrawRect(new Rect(hudRect.x, hudRect.y, 4f, hudRect.height), new Color(0.25f, 0.85f, 1f, 1f));

        GUILayout.BeginArea(new Rect(hudRect.x + 8f, hudRect.y + 6f, hudRect.width - 14f, hudRect.height - 10f));
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("🛠️ Blendshape Mesh Editor [編集中]", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("編集終了", EditorStyles.miniButton, GUILayout.Width(60f)))
            {
                ExitEditMode();
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label($"選択: {_elementMode} | ツール: {_transformTool}", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            _proportionalEditing = GUILayout.Toggle(_proportionalEditing, "O: プロポーショナル", EditorStyles.miniButton, GUILayout.Width(116f));
        }

        if (_proportionalEditing)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"半径: {_proportionalRadius * 100f:0.0}cm [ [ / ] ]", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                _xMirror = GUILayout.Toggle(_xMirror, "X: ミラー", EditorStyles.miniButton, GUILayout.Width(62f));
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("左クリック: 選択/変形  Shift: 追加  Ctrl: 解除", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (_selectedClusters.Count > 0 && GUILayout.Button("選択解除", EditorStyles.miniButton, GUILayout.Width(58f)))
            {
                ClearSelection();
            }
        }

        GUILayout.EndArea();
        Handles.EndGUI();
    }

    private void UpdateHoverState(Event e)
    {
        if (e.type != EventType.MouseMove) return;

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        if (_skinning.RaycastMesh(ray, out int hitTri, out int hitCluster, out _))
        {
            if (_hoveredClusterId != hitCluster || _hoveredTriangleId != hitTri)
            {
                _hoveredClusterId = hitCluster;
                _hoveredTriangleId = hitTri;
                SceneView.RepaintAll();
            }
        }
        else
        {
            if (_hoveredClusterId != -1 || _hoveredTriangleId != -1)
            {
                _hoveredClusterId = -1;
                _hoveredTriangleId = -1;
                SceneView.RepaintAll();
            }
        }
    }

    private void HandleShortcuts(Event e)
    {
        if (e.type != EventType.KeyDown) return;

        // O: プロポーショナル編集トグル
        if (e.keyCode == KeyCode.O && !e.control && !e.alt)
        {
            _proportionalEditing = !_proportionalEditing;
            e.Use();
            Repaint();
            SceneView.RepaintAll();
            return;
        }

        // X: X軸ミラー対称トグル
        if (e.keyCode == KeyCode.X && !e.control && !e.alt && _elementMode != SelectElementMode.Vertex)
        {
            _xMirror = !_xMirror;
            e.Use();
            Repaint();
            SceneView.RepaintAll();
            return;
        }

        // G: 移動ツール
        if (e.keyCode == KeyCode.G)
        {
            _transformTool = TransformTool.GizmoMove;
            e.Use();
            Repaint();
            SceneView.RepaintAll();
            return;
        }

        // R: 回転ツール
        if (e.keyCode == KeyCode.R && !e.control)
        {
            _transformTool = TransformTool.GizmoRotate;
            e.Use();
            Repaint();
            SceneView.RepaintAll();
            return;
        }

        // S: スケールツール
        if (e.keyCode == KeyCode.S && !e.control)
        {
            _transformTool = TransformTool.GizmoScale;
            e.Use();
            Repaint();
            SceneView.RepaintAll();
            return;
        }

        // B: ボックス選択
        if (e.keyCode == KeyCode.B)
        {
            _selectionTool = SelectionTool.ClickOrBox;
            e.Use();
            Repaint();
            SceneView.RepaintAll();
            return;
        }

        // C: サークル選択
        if (e.keyCode == KeyCode.C && !e.control)
        {
            _selectionTool = SelectionTool.CircleBrush;
            e.Use();
            Repaint();
            SceneView.RepaintAll();
            return;
        }

        // L: リンク選択
        if (e.keyCode == KeyCode.L)
        {
            if (_hoveredClusterId >= 0)
            {
                SelectIslandByCluster(_hoveredClusterId, addMode: !e.shift && !e.control);
                e.Use();
            }
            return;
        }

        // A: 全選択 / 全解除
        if (e.keyCode == KeyCode.A && !e.control)
        {
            if (_selectedClusters.Count > 0) ClearSelection();
            else SelectAll();
            e.Use();
            return;
        }

        // [ / ]: プロポーショナル半径縮小・拡大
        if (e.keyCode == KeyCode.LeftBracket)
        {
            _proportionalRadius = Mathf.Max(0.005f, _proportionalRadius * 0.85f);
            e.Use();
            Repaint();
            SceneView.RepaintAll();
            return;
        }
        if (e.keyCode == KeyCode.RightBracket)
        {
            _proportionalRadius = Mathf.Min(0.5f, _proportionalRadius * 1.18f);
            e.Use();
            Repaint();
            SceneView.RepaintAll();
            return;
        }
    }

    private void HandleMouseInteraction(Event e)
    {
        if (e.alt || e.button == 1 || e.button == 2)
            return; // カメラ操作中はスキップ

        // 1. ギズモ操作（選択頂点が存在し、かつギズモツールの場合）
        if (_selectedClusters.Count > 0 &&
            (_transformTool == TransformTool.GizmoMove || _transformTool == TransformTool.GizmoRotate || _transformTool == TransformTool.GizmoScale))
        {
            if (HandleTransformGizmo(e))
                return;
        }

        // 2. ブラシ変形操作（Grab / PushPull / Smooth）
        if (_transformTool == TransformTool.GrabBrush || _transformTool == TransformTool.PushPullBrush || _transformTool == TransformTool.SmoothBrush)
        {
            if (HandleBrushDeform(e))
                return;
        }

        // 3. 選択操作（クリック / Box / Circle）
        HandleSelectionInput(e);
    }

    private bool HandleTransformGizmo(Event e)
    {
        // 選択されたクラスタ群の中心・姿勢を計算
        Vector3 pivotPos = GetSelectionPivotPosition();
        Quaternion pivotRot = GetSelectionPivotRotation(pivotPos);

        EditorGUI.BeginChangeCheck();
        Vector3 newPos = pivotPos;
        Quaternion newRot = pivotRot;
        Vector3 newScale = Vector3.one;

        if (_transformTool == TransformTool.GizmoMove)
        {
            newPos = Handles.PositionHandle(pivotPos, pivotRot);
        }
        else if (_transformTool == TransformTool.GizmoRotate)
        {
            newRot = Handles.RotationHandle(pivotRot, pivotPos);
        }
        else if (_transformTool == TransformTool.GizmoScale)
        {
            newScale = Handles.ScaleHandle(Vector3.one, pivotPos, pivotRot, HandleUtility.GetHandleSize(pivotPos));
        }

        if (EditorGUI.EndChangeCheck())
        {
            if (!_isManipulatingGizmo)
            {
                Undo.RecordObject(this, "Deform Mesh with Gizmo");
                _dragInitialDeltas = (Vector3[])_localDeltas.Clone();
                _dragStartWorldPos = pivotPos;
                _dragStartWorldRot = pivotRot;
                _dragStartWorldScale = Vector3.one;
                _isManipulatingGizmo = true;
            }

            // ワールド変形行列を作成
            Matrix4x4 deltaMatrix;
            if (_transformTool == TransformTool.GizmoMove)
            {
                Vector3 deltaMove = newPos - _dragStartWorldPos;
                deltaMatrix = Matrix4x4.Translate(deltaMove);
            }
            else if (_transformTool == TransformTool.GizmoRotate)
            {
                Quaternion deltaRot = newRot * Quaternion.Inverse(_dragStartWorldRot);
                deltaMatrix = Matrix4x4.TRS(_dragStartWorldPos, deltaRot, Vector3.one) * Matrix4x4.Translate(-_dragStartWorldPos);
            }
            else
            {
                Vector3 s = newScale;
                deltaMatrix = Matrix4x4.TRS(_dragStartWorldPos, pivotRot, s) * Matrix4x4.TRS(Vector3.zero, Quaternion.Inverse(pivotRot), Vector3.one) * Matrix4x4.Translate(-_dragStartWorldPos);
            }

            ApplyDeformationMatrix(deltaMatrix, pivotPos);
            if (_livePreview) ApplyPreviewMesh();
            return true;
        }
        else if (e.type == EventType.MouseUp && _isManipulatingGizmo)
        {
            _isManipulatingGizmo = false;
            _dragInitialDeltas = null;
            if (!_livePreview) ApplyPreviewMesh();
        }

        return false;
    }

    private bool HandleBrushDeform(Event e)
    {
        int controlId = GUIUtility.GetControlID(FocusType.Passive);

        if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (_skinning.RaycastMesh(ray, out int hitTri, out int hitCluster, out Vector3 hitPoint))
            {
                GUIUtility.hotControl = controlId;
                _isBrushDragging = true;
                _brushLastRayOrigin = ray.origin;
                _brushLastRayDir = ray.direction;
                Undo.RecordObject(this, "Brush Mesh Deform");
                _dragInitialDeltas = (Vector3[])_localDeltas.Clone();

                ApplyBrushStep(hitPoint, Vector3.zero, ray);
                ApplyPreviewMesh();
                e.Use();
                return true;
            }
        }
        else if (e.type == EventType.MouseDrag && _isBrushDragging && GUIUtility.hotControl == controlId)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            // カメラ平面上でのドラッグ移動量
            Camera cam = SceneView.currentDrawingSceneView?.camera;
            Vector3 camForward = cam != null ? cam.transform.forward : Vector3.forward;
            Plane dragPlane = new Plane(-camForward, _skinning.GetClusterWorldPosition(_hoveredClusterId >= 0 ? _hoveredClusterId : 0));

            if (dragPlane.Raycast(ray, out float enter) && dragPlane.Raycast(new Ray(_brushLastRayOrigin, _brushLastRayDir), out float lastEnter))
            {
                Vector3 pCurrent = ray.GetPoint(enter);
                Vector3 pLast = new Ray(_brushLastRayOrigin, _brushLastRayDir).GetPoint(lastEnter);
                Vector3 worldDelta = pCurrent - pLast;

                ApplyBrushStep(pCurrent, worldDelta, ray);
                if (_livePreview) ApplyPreviewMesh();
            }

            _brushLastRayOrigin = ray.origin;
            _brushLastRayDir = ray.direction;
            e.Use();
            return true;
        }
        else if (e.type == EventType.MouseUp && _isBrushDragging && GUIUtility.hotControl == controlId)
        {
            GUIUtility.hotControl = 0;
            _isBrushDragging = false;
            _dragInitialDeltas = null;
            if (!_livePreview) ApplyPreviewMesh();
            e.Use();
            return true;
        }

        return false;
    }

    private void ApplyBrushStep(Vector3 centerWorld, Vector3 dragDeltaWorld, Ray ray)
    {
        if (_skinning == null || _topology == null) return;

        float r = _brushRadius;
        float strength = _brushStrength;

        var affectedClusters = _skinning.FindClustersInWorldSphere(centerWorld, r);

        foreach (var pair in affectedClusters)
        {
            int cId = pair.Key;
            float dist = pair.Value;
            float falloff = EvaluateFalloff(dist / r, FalloffType.Smooth);
            float factor = falloff * strength;

            Vector3 worldMove = Vector3.zero;

            if (_transformTool == TransformTool.GrabBrush)
            {
                worldMove = dragDeltaWorld * factor;
            }
            else if (_transformTool == TransformTool.PushPullBrush)
            {
                // 法線方向へ押し出し
                Vector3 wn = _skinning.GetClusterWorldNormal(cId);
                float stepDist = 0.003f * factor;
                worldMove = wn * stepDist;
            }
            else if (_transformTool == TransformTool.SmoothBrush)
            {
                // 隣接頂点の平均位置へ引き寄せる
                Vector3 currentWp = _skinning.GetClusterWorldPosition(cId);
                Vector3 avgWp = _skinning.GetClusterNeighborAverageWorldPosition(cId);
                worldMove = (avgWp - currentWp) * factor * 0.4f;
            }

            ApplyWorldMoveToCluster(cId, worldMove);

            // X軸ミラー
            if (_xMirror && _topology.ClusterMirrorMap.TryGetValue(cId, out int mirrorCId) && mirrorCId != cId)
            {
                Vector3 mirrorMove = new Vector3(-worldMove.x, worldMove.y, worldMove.z);
                ApplyWorldMoveToCluster(mirrorCId, mirrorMove);
            }
        }
    }

    private void HandleSelectionInput(Event e)
    {
        int controlId = GUIUtility.GetControlID(FocusType.Passive);

        if (_selectionTool == SelectionTool.ClickOrBox)
        {
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                _isBoxSelecting = true;
                _boxSelectStartPos = e.mousePosition;
                _boxSelectCurrentPos = e.mousePosition;
                GUIUtility.hotControl = controlId;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _isBoxSelecting && GUIUtility.hotControl == controlId)
            {
                _boxSelectCurrentPos = e.mousePosition;
                SceneView.RepaintAll();
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _isBoxSelecting && GUIUtility.hotControl == controlId)
            {
                GUIUtility.hotControl = 0;
                _isBoxSelecting = false;

                float dragDist = (_boxSelectCurrentPos - _boxSelectStartPos).magnitude;
                bool addMode = e.shift;
                bool removeMode = e.control;

                if (dragDist < 4f)
                {
                    // 単一クリック選択
                    Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                    if (_skinning.RaycastMesh(ray, out int hitTri, out int hitCluster, out _))
                    {
                        ApplyElementSelection(hitCluster, hitTri, addMode, removeMode);
                    }
                    else if (!addMode && !removeMode)
                    {
                        ClearSelection();
                    }
                }
                else
                {
                    // 矩形ボックス選択
                    Rect selectRect = GetScreenRect(_boxSelectStartPos, _boxSelectCurrentPos);
                    ApplyBoxSelection(selectRect, addMode, removeMode);
                }

                e.Use();
            }
        }
        else if (_selectionTool == SelectionTool.CircleBrush)
        {
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                _isCircleSelecting = true;
                GUIUtility.hotControl = controlId;
                ApplyCircleSelection(e.mousePosition, !e.control);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _isCircleSelecting && GUIUtility.hotControl == controlId)
            {
                ApplyCircleSelection(e.mousePosition, !e.control);
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _isCircleSelecting && GUIUtility.hotControl == controlId)
            {
                GUIUtility.hotControl = 0;
                _isCircleSelecting = false;
                e.Use();
            }
        }
    }

    private void ApplyElementSelection(int clusterId, int triangleId, bool addMode, bool removeMode)
    {
        Undo.RecordObject(this, "Select Mesh Element");

        if (!addMode && !removeMode)
        {
            _selectedClusters.Clear();
        }

        HashSet<int> targetClusters = new HashSet<int>();

        if (_elementMode == SelectElementMode.Vertex)
        {
            targetClusters.Add(clusterId);
        }
        else if (_elementMode == SelectElementMode.Edge)
        {
            // 三角形の直近エッジの頂点を追加
            var tri = _topology.Triangles[triangleId];
            targetClusters.Add(_topology.VertexToCluster[tri.V0]);
            targetClusters.Add(_topology.VertexToCluster[tri.V1]);
        }
        else if (_elementMode == SelectElementMode.Face)
        {
            var tri = _topology.Triangles[triangleId];
            targetClusters.Add(_topology.VertexToCluster[tri.V0]);
            targetClusters.Add(_topology.VertexToCluster[tri.V1]);
            targetClusters.Add(_topology.VertexToCluster[tri.V2]);
        }
        else if (_elementMode == SelectElementMode.Island)
        {
            int islandId = _topology.ClusterToIsland[clusterId];
            targetClusters.UnionWith(_topology.Islands[islandId]);
        }

        foreach (int c in targetClusters)
        {
            if (removeMode) _selectedClusters.Remove(c);
            else _selectedClusters.Add(c);

            if (_xMirror && _topology.ClusterMirrorMap.TryGetValue(c, out int m) && m != c)
            {
                if (removeMode) _selectedClusters.Remove(m);
                else _selectedClusters.Add(m);
            }
        }

        SceneView.RepaintAll();
        Repaint();
    }

    private void ApplyBoxSelection(Rect screenRect, bool addMode, bool removeMode)
    {
        Undo.RecordObject(this, "Box Select Mesh");

        if (!addMode && !removeMode)
        {
            _selectedClusters.Clear();
        }

        Camera cam = SceneView.currentDrawingSceneView?.camera;
        if (cam == null) return;

        for (int c = 0; c < _topology.ClusterCount; c++)
        {
            Vector3 wp = _skinning.GetClusterWorldPosition(c);
            Vector3 screenPos = HandleUtility.WorldToGUIPoint(wp);

            if (screenRect.Contains(screenPos))
            {
                // 透過選択OFFの場合、裏面カリング判定
                if (!_xRaySelect)
                {
                    Vector3 wn = _skinning.GetClusterWorldNormal(c);
                    Vector3 toCam = (cam.transform.position - wp).normalized;
                    if (Vector3.Dot(wn, toCam) <= 0.05f) continue;
                }

                if (removeMode) _selectedClusters.Remove(c);
                else _selectedClusters.Add(c);

                if (_xMirror && _topology.ClusterMirrorMap.TryGetValue(c, out int m) && m != c)
                {
                    if (removeMode) _selectedClusters.Remove(m);
                    else _selectedClusters.Add(m);
                }
            }
        }

        SceneView.RepaintAll();
        Repaint();
    }

    private void ApplyCircleSelection(Vector2 mousePos, bool addMode)
    {
        Camera cam = SceneView.currentDrawingSceneView?.camera;
        if (cam == null) return;

        for (int c = 0; c < _topology.ClusterCount; c++)
        {
            Vector3 wp = _skinning.GetClusterWorldPosition(c);
            Vector2 screenPos = HandleUtility.WorldToGUIPoint(wp);

            if ((screenPos - mousePos).sqrMagnitude <= _circleSelectRadiusPx * _circleSelectRadiusPx)
            {
                if (!_xRaySelect)
                {
                    Vector3 wn = _skinning.GetClusterWorldNormal(c);
                    Vector3 toCam = (cam.transform.position - wp).normalized;
                    if (Vector3.Dot(wn, toCam) <= 0.05f) continue;
                }

                if (addMode) _selectedClusters.Add(c);
                else _selectedClusters.Remove(c);

                if (_xMirror && _topology.ClusterMirrorMap.TryGetValue(c, out int m) && m != c)
                {
                    if (addMode) _selectedClusters.Add(m);
                    else _selectedClusters.Remove(m);
                }
            }
        }

        SceneView.RepaintAll();
        Repaint();
    }

    // ─────────────────────────────────────────────────────────
    //  変形適用ロジック（逆スキニング ＆ プロポーショナル計算）
    // ─────────────────────────────────────────────────────────

    private void ApplyDeformationMatrix(Matrix4x4 worldTransformMatrix, Vector3 pivotWorldPos)
    {
        if (_dragInitialDeltas == null || _skinning == null || _topology == null) return;

        // 一旦ドラッグ開始時のデルタに戻す
        Array.Copy(_dragInitialDeltas, _localDeltas, _localDeltas.Length);

        // 各選択クラスタおよびプロポーショナル影響クラスタに適用
        Dictionary<int, float> weights = new Dictionary<int, float>();

        if (!_proportionalEditing)
        {
            foreach (int c in _selectedClusters) weights[c] = 1f;
        }
        else
        {
            weights = CalculateProportionalWeights(pivotWorldPos, _proportionalRadius, _falloffType, _proportionalConnectedOnly);
        }

        foreach (var kvp in weights)
        {
            int cId = kvp.Key;
            float weight = kvp.Value;
            if (weight <= 1e-4f) continue;

            Vector3 origWp = _skinning.GetClusterWorldPosition(cId);
            Vector3 targetWp = worldTransformMatrix.MultiplyPoint3x4(origWp);
            Vector3 fullMove = targetWp - origWp;
            Vector3 finalMove = fullMove * weight;

            ApplyWorldMoveToCluster(cId, finalMove);

            // X軸ミラー
            if (_xMirror && _topology.ClusterMirrorMap.TryGetValue(cId, out int mId) && mId != cId)
            {
                Vector3 mirrorMove = new Vector3(-finalMove.x, finalMove.y, finalMove.z);
                ApplyWorldMoveToCluster(mId, mirrorMove);
            }
        }
    }

    private void ApplyWorldMoveToCluster(int clusterId, Vector3 worldDelta)
    {
        int[] vertIndices = _topology.Clusters[clusterId];
        for (int i = 0; i < vertIndices.Length; i++)
        {
            int v = vertIndices[i];
            // 逆スキニング行列により、ワールド移動量をメッシュローカルデルタに変換
            Vector3 localDeltaMove = _skinning.TransformWorldVectorToLocalDelta(v, worldDelta);
            _localDeltas[v] += localDeltaMove;
        }
    }

    private Dictionary<int, float> CalculateProportionalWeights(Vector3 centerWorld, float radius, FalloffType falloff, bool connectedOnly)
    {
        var result = new Dictionary<int, float>();

        if (!connectedOnly)
        {
            // ユークリッド距離による減衰
            for (int c = 0; c < _topology.ClusterCount; c++)
            {
                if (_selectedClusters.Contains(c))
                {
                    result[c] = 1f;
                    continue;
                }

                Vector3 wp = _skinning.GetClusterWorldPosition(c);
                float dist = Vector3.Distance(centerWorld, wp);
                if (dist < radius)
                {
                    result[c] = EvaluateFalloff(dist / radius, falloff);
                }
            }
        }
        else
        {
            // トポロジー（エッジ接続）距離による Dijkstra 測地線減衰
            var distMap = new Dictionary<int, float>();
            var pq = new SortedSet<(float dist, int cluster)>(Comparer<(float, int)>.Create((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2)));

            foreach (int c in _selectedClusters)
            {
                distMap[c] = 0f;
                pq.Add((0f, c));
                result[c] = 1f;
            }

            while (pq.Count > 0)
            {
                var current = pq.Min;
                pq.Remove(current);

                float d = current.dist;
                int u = current.cluster;

                if (d >= radius) continue;

                Vector3 uPos = _skinning.GetClusterWorldPosition(u);

                foreach (int v in _topology.ClusterNeighbors[u])
                {
                    Vector3 vPos = _skinning.GetClusterWorldPosition(v);
                    float edgeLen = Vector3.Distance(uPos, vPos);
                    float newDist = d + edgeLen;

                    if (newDist < radius)
                    {
                        if (!distMap.TryGetValue(v, out float oldDist) || newDist < oldDist)
                        {
                            if (oldDist > 0f) pq.Remove((oldDist, v));
                            distMap[v] = newDist;
                            pq.Add((newDist, v));
                            result[v] = EvaluateFalloff(newDist / radius, falloff);
                        }
                    }
                }
            }
        }

        return result;
    }

    private static float EvaluateFalloff(float t, FalloffType type)
    {
        t = Mathf.Clamp01(t);
        switch (type)
        {
            case FalloffType.Smooth:
                return (1f - t * t) * (1f - t * t);
            case FalloffType.Sphere:
                return Mathf.Sqrt(Mathf.Max(0f, 1f - t * t));
            case FalloffType.Root:
                return Mathf.Sqrt(Mathf.Max(0f, 1f - t));
            case FalloffType.Sharp:
                return (1f - t) * (1f - t);
            case FalloffType.Linear:
                return 1f - t;
            case FalloffType.Constant:
                return 1f;
            default:
                return 1f - t;
        }
    }

    // ─────────────────────────────────────────────────────────
    //  クイック変形（法線押し出し・スムーズ・リセット）
    // ─────────────────────────────────────────────────────────

    private void PushSelectedAlongNormals(float amount)
    {
        if (_selectedClusters.Count == 0 || _skinning == null) return;
        Undo.RecordObject(this, "Push Along Normals");

        foreach (int c in _selectedClusters)
        {
            Vector3 wn = _skinning.GetClusterWorldNormal(c);
            Vector3 move = wn * amount;
            ApplyWorldMoveToCluster(c, move);

            if (_xMirror && _topology.ClusterMirrorMap.TryGetValue(c, out int m) && m != c)
            {
                Vector3 mirrorMove = new Vector3(-move.x, move.y, move.z);
                ApplyWorldMoveToCluster(m, mirrorMove);
            }
        }

        ApplyPreviewMesh();
    }

    private void SmoothSelectedDeltas(float strength)
    {
        if (_selectedClusters.Count == 0 || _topology == null) return;
        Undo.RecordObject(this, "Smooth Deltas");

        var newDeltas = (Vector3[])_localDeltas.Clone();

        foreach (int c in _selectedClusters)
        {
            var neighbors = _topology.ClusterNeighbors[c];
            if (neighbors.Count == 0) continue;

            Vector3 avgDelta = Vector3.zero;
            foreach (int nc in neighbors)
            {
                int firstV = _topology.Clusters[nc][0];
                avgDelta += _localDeltas[firstV];
            }
            avgDelta /= neighbors.Count;

            foreach (int v in _topology.Clusters[c])
            {
                newDeltas[v] = Vector3.Lerp(_localDeltas[v], avgDelta, strength);
            }
        }

        _localDeltas = newDeltas;
        ApplyPreviewMesh();
    }

    private void ResetSelectedDeltas()
    {
        if (_selectedClusters.Count == 0 || _localDeltas == null) return;
        Undo.RecordObject(this, "Reset Selected Deltas");

        foreach (int c in _selectedClusters)
        {
            foreach (int v in _topology.Clusters[c])
            {
                _localDeltas[v] = Vector3.zero;
            }
            if (_xMirror && _topology.ClusterMirrorMap.TryGetValue(c, out int m))
            {
                foreach (int mv in _topology.Clusters[m])
                    _localDeltas[mv] = Vector3.zero;
            }
        }

        ApplyPreviewMesh();
    }

    private void ResetAllDeltas()
    {
        if (_localDeltas == null) return;
        Undo.RecordObject(this, "Reset All Deltas");
        Array.Clear(_localDeltas, 0, _localDeltas.Length);
        ApplyPreviewMesh();
    }

    // ─────────────────────────────────────────────────────────
    //  選択ヘルパー
    // ─────────────────────────────────────────────────────────

    private void SelectAll()
    {
        Undo.RecordObject(this, "Select All");
        _selectedClusters.Clear();
        for (int i = 0; i < _topology.ClusterCount; i++) _selectedClusters.Add(i);
        SceneView.RepaintAll();
        Repaint();
    }

    private void ClearSelection()
    {
        if (_selectedClusters.Count == 0) return;
        Undo.RecordObject(this, "Clear Selection");
        _selectedClusters.Clear();
        SceneView.RepaintAll();
        Repaint();
    }

    private void InvertSelection()
    {
        Undo.RecordObject(this, "Invert Selection");
        for (int i = 0; i < _topology.ClusterCount; i++)
        {
            if (!_selectedClusters.Remove(i))
                _selectedClusters.Add(i);
        }
        SceneView.RepaintAll();
        Repaint();
    }

    private void GrowSelection()
    {
        Undo.RecordObject(this, "Grow Selection");
        var next = new HashSet<int>(_selectedClusters);
        foreach (int c in _selectedClusters)
        {
            foreach (int n in _topology.ClusterNeighbors[c])
                next.Add(n);
        }
        _selectedClusters.Clear();
        _selectedClusters.UnionWith(next);
        SceneView.RepaintAll();
        Repaint();
    }

    private void ShrinkSelection()
    {
        Undo.RecordObject(this, "Shrink Selection");
        var next = new HashSet<int>(_selectedClusters);
        foreach (int c in _selectedClusters)
        {
            foreach (int n in _topology.ClusterNeighbors[c])
            {
                if (!_selectedClusters.Contains(n))
                {
                    next.Remove(c);
                    break;
                }
            }
        }
        _selectedClusters.Clear();
        _selectedClusters.UnionWith(next);
        SceneView.RepaintAll();
        Repaint();
    }

    private void SelectIslandByCluster(int clusterId, bool addMode)
    {
        Undo.RecordObject(this, "Select Island");
        if (!addMode) _selectedClusters.Clear();
        int islandId = _topology.ClusterToIsland[clusterId];
        _selectedClusters.UnionWith(_topology.Islands[islandId]);
        SceneView.RepaintAll();
        Repaint();
    }

    private int CountSelectedVertices()
    {
        if (_topology == null) return 0;
        int count = 0;
        foreach (int c in _selectedClusters)
            count += _topology.Clusters[c].Length;
        return count;
    }

    private Vector3 GetSelectionPivotPosition()
    {
        if (_selectedClusters.Count == 0 || _skinning == null)
            return _activeSMR != null ? _activeSMR.transform.position : Vector3.zero;

        Vector3 sum = Vector3.zero;
        foreach (int c in _selectedClusters)
        {
            sum += _skinning.GetClusterWorldPosition(c);
        }
        return sum / _selectedClusters.Count;
    }

    private Quaternion GetSelectionPivotRotation(Vector3 pivotPos)
    {
        if (_pivotSpace == PivotSpace.Local && _activeSMR != null)
            return _activeSMR.transform.rotation;

        if (_pivotSpace == PivotSpace.Normal && _selectedClusters.Count > 0 && _skinning != null)
        {
            Vector3 avgNormal = Vector3.zero;
            foreach (int c in _selectedClusters)
                avgNormal += _skinning.GetClusterWorldNormal(c);
            if (avgNormal.sqrMagnitude > 1e-6f)
                return Quaternion.FromToRotation(Vector3.up, avgNormal.normalized);
        }

        return Quaternion.identity;
    }

    private static Rect GetScreenRect(Vector2 p1, Vector2 p2)
    {
        return Rect.MinMaxRect(Mathf.Min(p1.x, p2.x), Mathf.Min(p1.y, p2.y), Mathf.Max(p1.x, p2.x), Mathf.Max(p1.y, p2.y));
    }

    private bool HasAnyDeltas()
    {
        if (_localDeltas == null) return false;
        for (int i = 0; i < _localDeltas.Length; i++)
        {
            if (_localDeltas[i].sqrMagnitude > 1e-10f) return true;
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────
    //  SceneView GL オーバーレイ描画
    // ─────────────────────────────────────────────────────────

    private static Material _overlayZTestMat;
    private static Material _overlayAlwaysMat;

    private static void EnsureOverlayMaterials()
    {
        if (_overlayZTestMat == null)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            _overlayZTestMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _overlayZTestMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _overlayZTestMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _overlayZTestMat.SetInt("_Cull", (int)CullMode.Off);
            _overlayZTestMat.SetInt("_ZWrite", 0);
            _overlayZTestMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
        }

        if (_overlayAlwaysMat == null)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            _overlayAlwaysMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _overlayAlwaysMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _overlayAlwaysMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _overlayAlwaysMat.SetInt("_Cull", (int)CullMode.Off);
            _overlayAlwaysMat.SetInt("_ZWrite", 0);
            _overlayAlwaysMat.SetInt("_ZTest", (int)CompareFunction.Always);
        }
    }

    private void DrawSceneOverlays()
    {
        EnsureOverlayMaterials();

        // 1. ワイヤーフレーム
        _overlayZTestMat.SetPass(0);
        GL.PushMatrix();
        GL.Begin(GL.LINES);
        GL.Color(new Color(0.7f, 0.7f, 0.7f, 0.22f));

        var tris = _topology.Triangles;
        for (int t = 0; t < tris.Length; t++)
        {
            var tri = tris[t];
            Vector3 p0 = _skinning.GetVertexWorldPosition(tri.V0);
            Vector3 p1 = _skinning.GetVertexWorldPosition(tri.V1);
            Vector3 p2 = _skinning.GetVertexWorldPosition(tri.V2);

            GL.Vertex(p0); GL.Vertex(p1);
            GL.Vertex(p1); GL.Vertex(p2);
            GL.Vertex(p2); GL.Vertex(p0);
        }
        GL.End();
        GL.PopMatrix();

        // 2. 選択頂点（オレンジ色・ドット＆枠線）
        if (_selectedClusters.Count > 0)
        {
            _overlayAlwaysMat.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            GL.Color(new Color(1.0f, 0.45f, 0.1f, 0.95f));

            foreach (int c in _selectedClusters)
            {
                Vector3 p = _skinning.GetClusterWorldPosition(c);
                float s = 0.003f;
                GL.Vertex(p - Vector3.right * s); GL.Vertex(p + Vector3.right * s);
                GL.Vertex(p - Vector3.up * s); GL.Vertex(p + Vector3.up * s);
                GL.Vertex(p - Vector3.forward * s); GL.Vertex(p + Vector3.forward * s);
            }
            GL.End();
            GL.PopMatrix();
        }

        // 3. ホバー中のクラスタ（黄色）
        if (_hoveredClusterId >= 0 && _hoveredClusterId < _topology.ClusterCount)
        {
            _overlayAlwaysMat.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            GL.Color(new Color(1.0f, 0.92f, 0.15f, 1.0f));
            Vector3 hp = _skinning.GetClusterWorldPosition(_hoveredClusterId);
            float hs = 0.006f;
            GL.Vertex(hp - Vector3.right * hs); GL.Vertex(hp + Vector3.right * hs);
            GL.Vertex(hp - Vector3.up * hs); GL.Vertex(hp + Vector3.up * hs);
            GL.Vertex(hp - Vector3.forward * hs); GL.Vertex(hp + Vector3.forward * hs);
            GL.End();
            GL.PopMatrix();
        }

        // 4. プロポーショナル編集の範囲円
        if (_proportionalEditing && (_isManipulatingGizmo || _transformTool == TransformTool.GrabBrush || _transformTool == TransformTool.PushPullBrush || _transformTool == TransformTool.SmoothBrush))
        {
            Vector3 center = _selectedClusters.Count > 0 ? GetSelectionPivotPosition() : (_hoveredClusterId >= 0 ? _skinning.GetClusterWorldPosition(_hoveredClusterId) : Vector3.zero);
            float r = (_transformTool == TransformTool.GrabBrush || _transformTool == TransformTool.PushPullBrush || _transformTool == TransformTool.SmoothBrush) ? _brushRadius : _proportionalRadius;

            Camera cam = SceneView.currentDrawingSceneView?.camera;
            Vector3 normal = cam != null ? cam.transform.forward : Vector3.up;

            Handles.color = new Color(1f, 1f, 1f, 0.45f);
            Handles.DrawWireDisc(center, normal, r);
        }

        // 5. ボックス選択枠の描画
        if (_isBoxSelecting)
        {
            Handles.BeginGUI();
            Rect r = GetScreenRect(_boxSelectStartPos, _boxSelectCurrentPos);
            EditorGUI.DrawRect(r, new Color(0.2f, 0.6f, 1f, 0.18f));
            Handles.color = new Color(0.3f, 0.7f, 1f, 0.85f);
            Handles.DrawSolidRectangleWithOutline(r, Color.clear, new Color(0.3f, 0.7f, 1f, 0.85f));
            Handles.EndGUI();
        }

        // 6. サークルブラシ枠の描画
        if (_selectionTool == SelectionTool.CircleBrush && !_isManipulatingGizmo)
        {
            Handles.BeginGUI();
            Vector2 m = Event.current.mousePosition;
            Handles.color = new Color(0.3f, 0.8f, 1f, 0.75f);
            Handles.DrawWireDisc(m, Vector3.forward, _circleSelectRadiusPx);
            Handles.EndGUI();
        }
    }

    // ─────────────────────────────────────────────────────────
    //  .mesh アセット保存処理
    // ─────────────────────────────────────────────────────────

    private string ResolveSaveFolder()
    {
        if (!string.IsNullOrEmpty(_saveFolderPath)) return _saveFolderPath;
        if (_originalSharedMesh != null)
        {
            string p = AssetDatabase.GetAssetPath(_originalSharedMesh);
            if (!string.IsNullOrEmpty(p) && p.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.GetDirectoryName(p)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
        }
        return DEFAULT_SAVE_DIR;
    }

    private void BakeAndSaveMesh()
    {
        if (_activeSMR == null || _originalSharedMesh == null || !HasAnyDeltas()) return;

        string targetShapeName = string.IsNullOrWhiteSpace(_newShapeName) ? "New_Shape" : _newShapeName.Trim();

        // プレビューを一旦解除して元メッシュ状態に戻す
        StopPreviewAndRestore();

        try
        {
            Mesh srcMesh = _originalSharedMesh;
            int vc = srcMesh.vertexCount;

            // 新規メッシュインスタンスを作成
            var newMesh = new Mesh
            {
                indexFormat = vc >= 65536 ? IndexFormat.UInt32 : srcMesh.indexFormat
            };

            // 基本頂点属性のコピー
            newMesh.vertices = srcMesh.vertices;
            newMesh.normals = srcMesh.normals;
            newMesh.tangents = srcMesh.tangents;
            newMesh.uv = srcMesh.uv;
            newMesh.uv2 = srcMesh.uv2;
            newMesh.uv3 = srcMesh.uv3;
            newMesh.uv4 = srcMesh.uv4;
            newMesh.colors32 = srcMesh.colors32;
            newMesh.boneWeights = srcMesh.boneWeights;
            newMesh.bindposes = srcMesh.bindposes;

            newMesh.subMeshCount = srcMesh.subMeshCount;
            for (int s = 0; s < srcMesh.subMeshCount; s++)
                newMesh.SetTriangles(srcMesh.GetTriangles(s), s);

            // 法線デルタ・接線デルタの計算
            var dV = new Vector3[vc];
            var dN = new Vector3[vc];
            var dT = new Vector3[vc];
            Array.Copy(_localDeltas, dV, vc);

            if (_recalculateNormals)
            {
                // 変形後メッシュの法線を計算して差分法線デルタを求める
                var tempPosed = CreatePreviewMeshInstance(srcMesh, _localDeltas, 1.0f, true);
                var posedNormals = tempPosed.normals;
                var baseNormals = srcMesh.normals;
                if (posedNormals != null && baseNormals != null && posedNormals.Length == vc && baseNormals.Length == vc)
                {
                    for (int i = 0; i < vc; i++)
                    {
                        dN[i] = posedNormals[i] - baseNormals[i];
                    }
                }
                DestroyImmediate(tempPosed);
            }

            // 既存 BlendShape をすべてコピー
            bool overwritten = false;
            for (int bi = 0; bi < srcMesh.blendShapeCount; bi++)
            {
                string shapeName = srcMesh.GetBlendShapeName(bi);
                int frameCount = srcMesh.GetBlendShapeFrameCount(bi);

                if (shapeName.Equals(targetShapeName, StringComparison.OrdinalIgnoreCase))
                {
                    // 同名の場合は新しいデルタで上書き
                    newMesh.AddBlendShapeFrame(targetShapeName, 100f, dV, dN, dT);
                    overwritten = true;
                    continue;
                }

                for (int fi = 0; fi < frameCount; fi++)
                {
                    float fw = srcMesh.GetBlendShapeFrameWeight(bi, fi);
                    var fV = new Vector3[vc];
                    var fN = new Vector3[vc];
                    var fTan = new Vector3[vc];
                    srcMesh.GetBlendShapeFrameVertices(bi, fi, fV, fN, fTan);
                    newMesh.AddBlendShapeFrame(shapeName, fw, fV, fN, fTan);
                }
            }

            if (!overwritten)
            {
                // 新規シェイプとして追加
                newMesh.AddBlendShapeFrame(targetShapeName, 100f, dV, dN, dT);
            }

            newMesh.RecalculateBounds();

            // 保存先ファイルパスを生成
            string saveDir = ResolveSaveFolder();
            EnsureFolderExists(saveDir);

            string baseName = StripSuffixMarker(srcMesh.name, ASSET_SUFFIX_MARKER);
            string assetPath = GenerateUniqueAssetPath(saveDir, baseName, ASSET_SUFFIX_MARKER, ".mesh");

            newMesh.name = Path.GetFileNameWithoutExtension(assetPath);
            AssetDatabase.CreateAsset(newMesh, assetPath);
            AssetDatabase.SaveAssets();

            var savedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);

            // 適用処理
            Undo.SetCurrentGroupName("Bake Blendshape Mesh");
            int undoGroup = Undo.GetCurrentGroup();

            if (_applyMode == ApplyMode.ReplaceInPlace)
            {
                Undo.RecordObject(_activeSMR, "Assign Edited Mesh");
                _activeSMR.sharedMesh = savedMesh;
                if (_autoSetWeightAfterSave)
                {
                    int newShapeIdx = savedMesh.GetBlendShapeIndex(targetShapeName);
                    if (newShapeIdx >= 0)
                        _activeSMR.SetBlendShapeWeight(newShapeIdx, 100f);
                }
                EditorUtility.SetDirty(_activeSMR);
            }
            else if (_applyMode == ApplyMode.DuplicateObject)
            {
                GameObject srcGo = _activeSMR.gameObject;
                GameObject dupGo = Instantiate(srcGo, srcGo.transform.parent);
                dupGo.name = srcGo.name + "_Edited";
                Undo.RegisterCreatedObjectUndo(dupGo, "Create Duplicated Mesh Object");
                dupGo.transform.SetSiblingIndex(srcGo.transform.GetSiblingIndex() + 1);

                var dupSmr = dupGo.GetComponent<SkinnedMeshRenderer>();
                if (dupSmr != null)
                {
                    dupSmr.sharedMesh = savedMesh;
                    if (_autoSetWeightAfterSave)
                    {
                        int newShapeIdx = savedMesh.GetBlendShapeIndex(targetShapeName);
                        if (newShapeIdx >= 0)
                            dupSmr.SetBlendShapeWeight(newShapeIdx, 100f);
                    }
                }

                Undo.RecordObject(srcGo, "Deactivate Source Object");
                srcGo.SetActive(false);
            }

            Undo.CollapseUndoOperations(undoGroup);

            // 編集モードを終了して再構築
            _editModeActive = false;
            _originalSharedMesh = savedMesh;
            RebuildContext(preserveDeltas: false);

            EditorGUIUtility.PingObject(savedMesh);
            SetStatus($"保存完了！新しい .mesh アセットを生成しました: {assetPath} （BlendShape「{targetShapeName}」を追加）", MessageType.Info);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            SetStatus($"保存中にエラーが発生しました: {ex.Message}", MessageType.Error);
        }
    }

    private static string StripSuffixMarker(string name, string marker)
    {
        int idx = name.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx > 0) return name.Substring(0, idx);
        return name;
    }

    private static string GenerateUniqueAssetPath(string folderPath, string baseName, string suffixMarker, string ext)
    {
        folderPath = folderPath.Replace('\\', '/').TrimEnd('/');
        for (int i = 0; i < 1000; i++)
        {
            string candidate = $"{folderPath}/{baseName}{suffixMarker}{i:000}{ext}";
            if (AssetDatabase.LoadMainAssetAtPath(candidate) == null && !File.Exists(candidate))
                return candidate;
        }
        return AssetDatabase.GenerateUniqueAssetPath($"{folderPath}/{baseName}{suffixMarker}999{ext}");
    }

    private static void EnsureFolderExists(string folderPath)
    {
        string norm = folderPath.Replace('\\', '/').TrimEnd('/');
        if (AssetDatabase.IsValidFolder(norm)) return;

        string[] parts = norm.Split('/');
        if (parts.Length == 0 || !parts[0].Equals("Assets", StringComparison.OrdinalIgnoreCase)) return;

        string current = "Assets";
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }
            current = next;
        }
    }

    private void SetStatus(string msg, MessageType type)
    {
        _statusMessage = msg;
        _statusType = type;
        Repaint();
    }
}

// ─────────────────────────────────────────────────────────────
//  トポロジー解析クラス（クラスタリング・隣接・アイランド・Xミラー）
// ─────────────────────────────────────────────────────────────

public class BSEditTopology
{
    public struct Triangle
    {
        public int V0;
        public int V1;
        public int V2;
        public int SubMesh;
    }

    public Mesh SourceMesh { get; private set; }
    public int VertexCount { get; private set; }
    public Triangle[] Triangles { get; private set; }

    public int ClusterCount => Clusters.Count;
    public List<int[]> Clusters { get; private set; } = new List<int[]>();
    public int[] VertexToCluster { get; private set; }
    public List<HashSet<int>> ClusterNeighbors { get; private set; } = new List<HashSet<int>>();

    public List<HashSet<int>> Islands { get; private set; } = new List<HashSet<int>>();
    public int[] ClusterToIsland { get; private set; }

    public Dictionary<int, int> ClusterMirrorMap { get; private set; } = new Dictionary<int, int>();

    public BSEditTopology(Mesh mesh)
    {
        SourceMesh = mesh;
        VertexCount = mesh.vertexCount;
    }

    public void BuildTopology(bool weldCoincident, float mirrorThreshold)
    {
        var verts = SourceMesh.vertices;
        Clusters.Clear();
        VertexToCluster = new int[VertexCount];

        // 1. 同一座標頂点クラスタリング
        if (weldCoincident)
        {
            Bounds b = SourceMesh.bounds;
            float maxExt = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            float eps = Mathf.Max(1e-5f, maxExt * 1e-4f);
            double invEps = 1.0 / eps;

            var spatialMap = new Dictionary<(long, long, long), List<int>>();
            for (int v = 0; v < VertexCount; v++)
            {
                Vector3 p = verts[v];
                long qx = (long)Math.Round(p.x * invEps);
                long qy = (long)Math.Round(p.y * invEps);
                long qz = (long)Math.Round(p.z * invEps);
                var key = (qx, qy, qz);
                if (!spatialMap.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    spatialMap[key] = list;
                }
                list.Add(v);
            }

            foreach (var list in spatialMap.Values)
            {
                int cId = Clusters.Count;
                int[] arr = list.ToArray();
                Clusters.Add(arr);
                for (int i = 0; i < arr.Length; i++)
                    VertexToCluster[arr[i]] = cId;
            }
        }
        else
        {
            for (int v = 0; v < VertexCount; v++)
            {
                Clusters.Add(new[] { v });
                VertexToCluster[v] = v;
            }
        }

        // 2. 三角形リストの抽出
        var triList = new List<Triangle>();
        int subCount = SourceMesh.subMeshCount;
        for (int s = 0; s < subCount; s++)
        {
            int[] idxs = SourceMesh.GetTriangles(s);
            for (int i = 0; i + 2 < idxs.Length; i += 3)
            {
                triList.Add(new Triangle
                {
                    V0 = idxs[i],
                    V1 = idxs[i + 1],
                    V2 = idxs[i + 2],
                    SubMesh = s
                });
            }
        }
        Triangles = triList.ToArray();

        // 3. クラスタ隣接グラフ
        ClusterNeighbors.Clear();
        for (int c = 0; c < ClusterCount; c++)
            ClusterNeighbors.Add(new HashSet<int>());

        for (int t = 0; t < Triangles.Length; t++)
        {
            var tri = Triangles[t];
            int c0 = VertexToCluster[tri.V0];
            int c1 = VertexToCluster[tri.V1];
            int c2 = VertexToCluster[tri.V2];

            if (c0 != c1) { ClusterNeighbors[c0].Add(c1); ClusterNeighbors[c1].Add(c0); }
            if (c1 != c2) { ClusterNeighbors[c1].Add(c2); ClusterNeighbors[c2].Add(c1); }
            if (c2 != c0) { ClusterNeighbors[c2].Add(c0); ClusterNeighbors[c0].Add(c2); }
        }

        // 4. メッシュアイランド（連結成分）分割
        Islands.Clear();
        ClusterToIsland = new int[ClusterCount];
        for (int i = 0; i < ClusterCount; i++) ClusterToIsland[i] = -1;

        for (int c = 0; c < ClusterCount; c++)
        {
            if (ClusterToIsland[c] >= 0) continue;

            int islandId = Islands.Count;
            var islandSet = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(c);
            ClusterToIsland[c] = islandId;
            islandSet.Add(c);

            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                foreach (int n in ClusterNeighbors[u])
                {
                    if (ClusterToIsland[n] < 0)
                    {
                        ClusterToIsland[n] = islandId;
                        islandSet.Add(n);
                        queue.Enqueue(n);
                    }
                }
            }
            Islands.Add(islandSet);
        }

        // 5. X軸ミラー対応表
        ClusterMirrorMap.Clear();
        var clusterPos = new Vector3[ClusterCount];
        for (int c = 0; c < ClusterCount; c++)
        {
            Vector3 avg = Vector3.zero;
            int[] vArr = Clusters[c];
            for (int i = 0; i < vArr.Length; i++) avg += verts[vArr[i]];
            clusterPos[c] = avg / vArr.Length;
        }

        float tol = Mathf.Max(1e-4f, mirrorThreshold);
        float tolSq = tol * tol;

        for (int c = 0; c < ClusterCount; c++)
        {
            if (ClusterMirrorMap.ContainsKey(c)) continue;

            Vector3 p = clusterPos[c];
            if (Mathf.Abs(p.x) < tol * 0.5f)
            {
                // 中央（正中線）上の頂点は自己ミラー
                ClusterMirrorMap[c] = c;
                continue;
            }

            Vector3 mirrorTarget = new Vector3(-p.x, p.y, p.z);
            int bestMatch = -1;
            float bestDistSq = tolSq;

            for (int oc = 0; oc < ClusterCount; oc++)
            {
                if (oc == c) continue;
                float dSq = (clusterPos[oc] - mirrorTarget).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestMatch = oc;
                }
            }

            if (bestMatch >= 0)
            {
                ClusterMirrorMap[c] = bestMatch;
                ClusterMirrorMap[bestMatch] = c;
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────
//  スキニング計算 ＆ 逆スキニングクラス
// ─────────────────────────────────────────────────────────────

public class BSEditSkinning
{
    private readonly SkinnedMeshRenderer _smr;
    private readonly Mesh _mesh;
    private readonly BSEditTopology _topology;

    private readonly Vector3[] _bakedWorldVertices;
    private readonly Vector3[] _bakedWorldNormals;

    // 各頂点のスキニング線形変換行列（ボーン変形アフィン変換）
    private readonly Matrix4x4[] _vertexSkinMatrices;
    private readonly Matrix4x4[] _inverseSkinMatrices;

    public BSEditSkinning(SkinnedMeshRenderer smr, Mesh mesh, BSEditTopology topology)
    {
        _smr = smr;
        _mesh = mesh;
        _topology = topology;

        int vc = mesh.vertexCount;
        _bakedWorldVertices = new Vector3[vc];
        _bakedWorldNormals = new Vector3[vc];
        _vertexSkinMatrices = new Matrix4x4[vc];
        _inverseSkinMatrices = new Matrix4x4[vc];

        ComputeVertexSkinMatrices();
    }

    private void ComputeVertexSkinMatrices()
    {
        int vc = _mesh.vertexCount;
        Transform[] bones = _smr.bones;
        Matrix4x4[] bindposes = _mesh.bindposes;

        bool hasBones = bones != null && bones.Length > 0 && bindposes != null && bindposes.Length > 0;

        if (hasBones)
        {
            // 各ボーンの (Bone.localToWorldMatrix * Bindpose) を事前計算
            int boneCount = bones.Length;
            var boneWorldMatrices = new Matrix4x4[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                if (bones[b] != null && b < bindposes.Length)
                    boneWorldMatrices[b] = bones[b].localToWorldMatrix * bindposes[b];
                else
                    boneWorldMatrices[b] = _smr.transform.localToWorldMatrix;
            }

            // BoneWeight1 可変長ウェイトまたは Legacy BoneWeight から頂点ごとのスキニング行列を構築
            var bonesPerVert = _mesh.GetBonesPerVertex();
            var allWeights = _mesh.GetAllBoneWeights();

            if (bonesPerVert.IsCreated && allWeights.IsCreated && bonesPerVert.Length == vc && allWeights.Length > 0)
            {
                int weightOffset = 0;
                for (int v = 0; v < vc; v++)
                {
                    byte count = bonesPerVert[v];
                    Matrix4x4 m = Matrix4x4.zero;
                    for (int k = 0; k < count; k++)
                    {
                        var bw = allWeights[weightOffset + k];
                        int bIdx = bw.boneIndex;
                        float w = bw.weight;
                        if (bIdx >= 0 && bIdx < boneCount && w > 0f)
                        {
                            Matrix4x4 bm = boneWorldMatrices[bIdx];
                            for (int r = 0; r < 16; r++) m[r] += bm[r] * w;
                        }
                    }
                    weightOffset += count;
                    _vertexSkinMatrices[v] = m;
                    _inverseSkinMatrices[v] = m.inverse;
                }
            }
            else
            {
                var legacyBw = _mesh.boneWeights;
                if (legacyBw != null && legacyBw.Length == vc)
                {
                    for (int v = 0; v < vc; v++)
                    {
                        var bw = legacyBw[v];
                        Matrix4x4 m = Matrix4x4.zero;
                        if (bw.weight0 > 0f && bw.boneIndex0 < boneCount) AccumulateMatrix(ref m, boneWorldMatrices[bw.boneIndex0], bw.weight0);
                        if (bw.weight1 > 0f && bw.boneIndex1 < boneCount) AccumulateMatrix(ref m, boneWorldMatrices[bw.boneIndex1], bw.weight1);
                        if (bw.weight2 > 0f && bw.boneIndex2 < boneCount) AccumulateMatrix(ref m, boneWorldMatrices[bw.boneIndex2], bw.weight2);
                        if (bw.weight3 > 0f && bw.boneIndex3 < boneCount) AccumulateMatrix(ref m, boneWorldMatrices[bw.boneIndex3], bw.weight3);
                        _vertexSkinMatrices[v] = m;
                        _inverseSkinMatrices[v] = m.inverse;
                    }
                }
                else
                {
                    Matrix4x4 l2w = _smr.transform.localToWorldMatrix;
                    Matrix4x4 w2l = _smr.transform.worldToLocalMatrix;
                    for (int v = 0; v < vc; v++)
                    {
                        _vertexSkinMatrices[v] = l2w;
                        _inverseSkinMatrices[v] = w2l;
                    }
                }
            }
        }
        else
        {
            Matrix4x4 l2w = _smr.transform.localToWorldMatrix;
            Matrix4x4 w2l = _smr.transform.worldToLocalMatrix;
            for (int v = 0; v < vc; v++)
            {
                _vertexSkinMatrices[v] = l2w;
                _inverseSkinMatrices[v] = w2l;
            }
        }
    }

    private static void AccumulateMatrix(ref Matrix4x4 dst, Matrix4x4 src, float weight)
    {
        for (int i = 0; i < 16; i++) dst[i] += src[i] * weight;
    }

    public void UpdateWorldPositions()
    {
        int vc = _mesh.vertexCount;
        Mesh currentAssigned = _smr.sharedMesh;

        // BakeMesh による高速な現在の変形頂点取得
        var baked = new Mesh();
        try
        {
            _smr.BakeMesh(baked, false);
            var bVerts = baked.vertices;
            var bNorms = baked.normals;

            Matrix4x4 rigidToWorld = Matrix4x4.TRS(_smr.transform.position, _smr.transform.rotation, Vector3.one);
            Quaternion rot = _smr.transform.rotation;

            bool hasNorms = bNorms != null && bNorms.Length == vc;

            if (bVerts != null && bVerts.Length == vc)
            {
                for (int i = 0; i < vc; i++)
                {
                    _bakedWorldVertices[i] = rigidToWorld.MultiplyPoint3x4(bVerts[i]);
                    if (hasNorms)
                    {
                        Vector3 n = rot * bNorms[i];
                        _bakedWorldNormals[i] = n.sqrMagnitude > 1e-20f ? n.normalized : Vector3.up;
                    }
                    else
                    {
                        _bakedWorldNormals[i] = Vector3.up;
                    }
                }
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(baked);
        }
    }

    public Vector3 GetVertexWorldPosition(int vIdx)
    {
        return _bakedWorldVertices[vIdx];
    }

    public Vector3 GetClusterWorldPosition(int clusterId)
    {
        int[] vArr = _topology.Clusters[clusterId];
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < vArr.Length; i++)
            sum += _bakedWorldVertices[vArr[i]];
        return sum / vArr.Length;
    }

    public Vector3 GetClusterWorldNormal(int clusterId)
    {
        int[] vArr = _topology.Clusters[clusterId];
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < vArr.Length; i++)
            sum += _bakedWorldNormals[vArr[i]];
        return sum.sqrMagnitude > 1e-20f ? sum.normalized : Vector3.up;
    }

    public Vector3 GetClusterNeighborAverageWorldPosition(int clusterId)
    {
        var neighbors = _topology.ClusterNeighbors[clusterId];
        if (neighbors.Count == 0) return GetClusterWorldPosition(clusterId);
        Vector3 sum = Vector3.zero;
        foreach (int n in neighbors)
            sum += GetClusterWorldPosition(n);
        return sum / neighbors.Count;
    }

    /// <summary>
    /// ワールド空間での移動量を、頂点のスキニング逆変換によりローカルバインドポーズの BlendShape デルタへ変換する。
    /// </summary>
    public Vector3 TransformWorldVectorToLocalDelta(int vertexIndex, Vector3 worldVector)
    {
        Matrix4x4 inv = _inverseSkinMatrices[vertexIndex];
        return inv.MultiplyVector(worldVector);
    }

    public bool RaycastMesh(Ray worldRay, out int hitTriangle, out int hitCluster, out Vector3 hitPoint)
    {
        hitTriangle = -1;
        hitCluster = -1;
        hitPoint = Vector3.zero;

        float bestDist = float.MaxValue;
        var tris = _topology.Triangles;

        for (int t = 0; t < tris.Length; t++)
        {
            var tri = tris[t];
            Vector3 v0 = _bakedWorldVertices[tri.V0];
            Vector3 v1 = _bakedWorldVertices[tri.V1];
            Vector3 v2 = _bakedWorldVertices[tri.V2];

            if (RayIntersectsTriangle(worldRay, v0, v1, v2, out float d, out Vector3 p))
            {
                if (d >= 0f && d < bestDist)
                {
                    bestDist = d;
                    hitTriangle = t;
                    hitPoint = p;

                    // 最も近い頂点のクラスタを取得
                    float d0 = (p - v0).sqrMagnitude;
                    float d1 = (p - v1).sqrMagnitude;
                    float d2 = (p - v2).sqrMagnitude;
                    int closestV = (d0 <= d1 && d0 <= d2) ? tri.V0 : ((d1 <= d2) ? tri.V1 : tri.V2);
                    hitCluster = _topology.VertexToCluster[closestV];
                }
            }
        }

        return hitTriangle >= 0;
    }

    public List<KeyValuePair<int, float>> FindClustersInWorldSphere(Vector3 centerWorld, float radius)
    {
        var result = new List<KeyValuePair<int, float>>();
        float rSq = radius * radius;

        for (int c = 0; c < _topology.ClusterCount; c++)
        {
            Vector3 wp = GetClusterWorldPosition(c);
            float dSq = (wp - centerWorld).sqrMagnitude;
            if (dSq <= rSq)
            {
                result.Add(new KeyValuePair<int, float>(c, Mathf.Sqrt(dSq)));
            }
        }

        return result;
    }

    private static bool RayIntersectsTriangle(Ray ray, Vector3 v0, Vector3 v1, Vector3 v2, out float dist, out Vector3 hitPoint)
    {
        dist = 0f;
        hitPoint = Vector3.zero;
        const float EPSILON = 1e-7f;

        Vector3 edge1 = v1 - v0;
        Vector3 edge2 = v2 - v0;
        Vector3 h = Vector3.Cross(ray.direction, edge2);
        float a = Vector3.Dot(edge1, h);

        if (a > -EPSILON && a < EPSILON) return false;

        float f = 1.0f / a;
        Vector3 s = ray.origin - v0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0.0f || u > 1.0f) return false;

        Vector3 q = Vector3.Cross(s, edge1);
        float v = f * Vector3.Dot(ray.direction, q);
        if (v < 0.0f || u + v > 1.0f) return false;

        float t = f * Vector3.Dot(edge2, q);
        if (t > EPSILON)
        {
            dist = t;
            hitPoint = ray.origin + ray.direction * t;
            return true;
        }

        return false;
    }
}
#endif
