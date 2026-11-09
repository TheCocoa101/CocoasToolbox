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
/// Hair Mesh Deleter — Cocoa's Toolbox
/// 髪メッシュの一本一本（アイランド / 房）を SceneView 上でクリックして選択（Blender 編集モードの Lキー風リンク選択）し、
/// 選択した部分をリアルタイムにプレビュー確認しながら削除して、新しい .mesh アセットとして非破壊保存するエディタ拡張ツール。
/// SceneView でのクリック選択と同時に、テクスチャ上のどのUV部分が選択されているかをリアルタイム表示するテクスチャビュワー付き。
///
/// [メッシュ保存品質]
/// ・削除された三角形および孤立頂点を完全に除去・インデックス再構築（ポリゴン数・頂点数を実際に削減）
/// ・BoneWeights（5ボーン以上の可変ウェイト含む）・Bindposes・全8チャンネルUV（2D/3D/4D）・頂点カラー・法線・接線を完全維持
/// ・全 BlendShape（シェイプキー、マルチフレーム対応）の頂点デルタを新頂点インデックスへ正確にリマップして保持
/// </summary>
public class HairMeshDeleter : EditorWindow
{
    // ─────────────────────────────────────────────────────────
    //  定数・列挙型
    // ─────────────────────────────────────────────────────────

    private const string WINDOW_TITLE = "Hair Mesh Deleter (Experimental)";
    private const string DEFAULT_SAVE_DIR = "Assets/DeletedHairMeshes";
    private const string DELETED_MESH_SUFFIX = "_Deleted-";

    private enum ApplyOutputMode
    {
        ReplaceInPlace = 0,     // 現在の Renderer の Mesh を新規 .mesh に差し替え（Undo対応）
        DuplicateObject = 1,    // オブジェクトを複製して適用（元を非表示・非破壊）
        SaveMeshAssetOnly = 2   // .mesh アセットの保存のみ
    }

    [Serializable]
    private class DeleteResultRecord
    {
        public string Label;
        public Mesh SavedMesh;
        public string MeshAssetPath;
        public GameObject TargetObject;
        public int OldVertices;
        public int NewVertices;
        public int OldTriangles;
        public int NewTriangles;
    }

    // ─────────────────────────────────────────────────────────
    //  シリアライズ状態（Undo / Domain Reload 対応）
    // ─────────────────────────────────────────────────────────

    [SerializeField] private GameObject _targetRootOrGo;
    [SerializeField] private Component _activeRenderer; // SkinnedMeshRenderer or MeshRenderer
    [SerializeField] private int _viewerMaterialSlot = 0;
    [SerializeField] private bool _restrictPickToSlot = false;

    // 選択設定
    [SerializeField] private bool _scenePickActive = true;
    [SerializeField] private HairLinkMode _linkMode = HairLinkMode.Spatial3D;
    [SerializeField] private bool _splitIslandsBySubMesh = true;
    [SerializeField] private HairClickBehavior _clickBehavior = HairClickBehavior.Toggle;
    [SerializeField] private bool _raycastBothSides = true;

    // リアルタイム削除プレビュー設定
    [SerializeField] private bool _liveDeletePreview = true;
    [SerializeField] private bool _showDeletedGhostAndAllowRepick = false;

    // 削除対象として選択中のアイランドIDリスト
    [SerializeField] private List<int> _selectedIslandList = new List<int>();
    private readonly HashSet<int> _selectedIslands = new HashSet<int>();

    // テクスチャビュワー設定
    [SerializeField] private float _viewerHeight = 250f;
    [SerializeField] private HairTextureUVViewer _uvViewer = new HairTextureUVViewer();

    // 保存・適用設定
    [SerializeField] private ApplyOutputMode _applyMode = ApplyOutputMode.ReplaceInPlace;
    [SerializeField] private bool _hideOriginalOnDuplicate = true;
    [SerializeField] private bool _keepOriginalBounds = true;
    [SerializeField] private string _saveFolderPath = "";

    // UIスクロール・履歴・ステータス
    [SerializeField] private Vector2 _scrollMain;
    [SerializeField] private bool _foldResults = true;
    [SerializeField] private List<DeleteResultRecord> _results = new List<DeleteResultRecord>();
    [SerializeField] private string _statusMessage = "";
    [SerializeField] private MessageType _statusType = MessageType.Info;

    // ─────────────────────────────────────────────────────────
    //  ランタイム・キャッシュ状態
    // ─────────────────────────────────────────────────────────

    private HairMeshContext _ctx;
    private List<Component> _candidateRenderers = new List<Component>();
    private string[] _candidateRendererNames = Array.Empty<string>();
    private string[] _materialSlotLabels = Array.Empty<string>();
    private Texture _currentSlotTexture;

    private int _hoveredIslandId = -1;
    private bool _isSceneDragging = false;
    private bool _sceneDragAddMode = true;
    private int _overlappingUnselectedCount = 0;

    // リアルタイム削除プレビュー用の一時Mesh管理
    private bool _isDeletePreviewApplied = false;
    private Component _previewAppliedRenderer;
    private Mesh _originalSharedMesh;
    private Mesh _tempPreviewMesh;

    // GUIStyle キャッシュ
    private GUIStyle _headerTitleStyle;
    private GUIStyle _pickActiveBtnStyle;
    private GUIStyle _pickIdleBtnStyle;
    private GUIStyle _executeBtnStyle;
    private GUIStyle _wrapMiniStyle;

    private GUIStyle HeaderTitleStyle => _headerTitleStyle ?? (_headerTitleStyle = new GUIStyle(EditorStyles.boldLabel)
    {
        fontSize = 13
    });

    private GUIStyle PickActiveBtnStyle => _pickActiveBtnStyle ?? (_pickActiveBtnStyle = new GUIStyle(GUI.skin.button)
    {
        fontSize = 12,
        fontStyle = FontStyle.Bold,
        fixedHeight = 30f
    });

    private GUIStyle PickIdleBtnStyle => _pickIdleBtnStyle ?? (_pickIdleBtnStyle = new GUIStyle(GUI.skin.button)
    {
        fontSize = 12,
        fixedHeight = 30f
    });

    private GUIStyle ExecuteBtnStyle => _executeBtnStyle ?? (_executeBtnStyle = new GUIStyle(GUI.skin.button)
    {
        fontSize = 13,
        fontStyle = FontStyle.Bold,
        fixedHeight = 34f
    });

    private GUIStyle WrapMiniStyle => _wrapMiniStyle ?? (_wrapMiniStyle = new GUIStyle(EditorStyles.miniLabel)
    {
        wordWrap = true
    });

    // ─────────────────────────────────────────────────────────
    //  メニュー登録
    // ─────────────────────────────────────────────────────────

    [MenuItem("Tools/Cocoa's Toolbox/Hair Mesh Deleter (Experimental)")]
    public static void Open()
    {
        var win = GetWindow<HairMeshDeleter>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 620f);
        win.Show();
        if (win._targetRootOrGo == null && Selection.activeGameObject != null && Selection.activeGameObject.scene.IsValid())
        {
            win.SetTargetFromGameObject(Selection.activeGameObject);
        }
    }

    [MenuItem("GameObject/Cocoa's Toolbox/Hair Mesh Deleter (Experimental)", false, 25)]
    private static void OpenFromHierarchyContext(MenuCommand command)
    {
        GameObject go = command.context as GameObject ?? Selection.activeGameObject;
        var win = GetWindow<HairMeshDeleter>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 620f);
        win.Show();
        win.Focus();
        if (go != null)
        {
            win.SetTargetFromGameObject(go);
        }
    }

    [MenuItem("CONTEXT/SkinnedMeshRenderer/Hair Mesh Deleter (Experimental)")]
    private static void OpenFromSMRContext(MenuCommand command)
    {
        var smr = command.context as SkinnedMeshRenderer;
        var win = GetWindow<HairMeshDeleter>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 620f);
        win.Show();
        win.Focus();
        if (smr != null)
        {
            win.SetDirectRenderer(smr);
        }
    }

    [MenuItem("CONTEXT/MeshFilter/Hair Mesh Deleter (Experimental)")]
    private static void OpenFromMFContext(MenuCommand command)
    {
        var mf = command.context as MeshFilter;
        var win = GetWindow<HairMeshDeleter>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 620f);
        win.Show();
        win.Focus();
        if (mf != null)
        {
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr != null) win.SetDirectRenderer(mr);
            else win.SetTargetFromGameObject(mf.gameObject);
        }
    }

    [MenuItem("CONTEXT/MeshRenderer/Hair Mesh Deleter (Experimental)")]
    private static void OpenFromMRContext(MenuCommand command)
    {
        var mr = command.context as MeshRenderer;
        var win = GetWindow<HairMeshDeleter>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 620f);
        win.Show();
        win.Focus();
        if (mr != null)
        {
            win.SetDirectRenderer(mr);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  ライフサイクル管理
    // ─────────────────────────────────────────────────────────

    private void OnEnable()
    {
        minSize = new Vector2(440f, 620f);
        SceneView.duringSceneGui -= OnSceneGUI;
        SceneView.duringSceneGui += OnSceneGUI;
        Undo.undoRedoPerformed -= OnUndoRedo;
        Undo.undoRedoPerformed += OnUndoRedo;
        AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

        SyncHashSetFromSerializedList();
        if (_activeRenderer != null || _targetRootOrGo != null)
        {
            RebuildContext(keepSelection: true);
        }
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        Undo.undoRedoPerformed -= OnUndoRedo;
        AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        RestoreDeletePreview();
    }

    private void OnDestroy()
    {
        RestoreDeletePreview();
    }

    private void OnBeforeAssemblyReload()
    {
        RestoreDeletePreview();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
        {
            RestoreDeletePreview();
        }
    }

    private void OnUndoRedo()
    {
        SyncHashSetFromSerializedList();
        UpdateSelectionStatsAndPreview();
        Repaint();
        SceneView.RepaintAll();
    }

    // ─────────────────────────────────────────────────────────
    //  ターゲット設定・コンテキスト構築
    // ─────────────────────────────────────────────────────────

    public void SetTargetFromGameObject(GameObject go)
    {
        RestoreDeletePreview();
        _targetRootOrGo = go;
        CollectCandidateRenderers(go, autoPickBestHair: true);
        RebuildContext(keepSelection: false);
        _scenePickActive = true;
        Repaint();
        SceneView.RepaintAll();
    }

    public void SetDirectRenderer(Component renderer)
    {
        if (renderer == null) return;
        RestoreDeletePreview();
        _targetRootOrGo = renderer.gameObject;
        CollectCandidateRenderers(renderer.gameObject, autoPickBestHair: false);
        _activeRenderer = renderer;
        RebuildContext(keepSelection: false);
        _scenePickActive = true;
        Repaint();
        SceneView.RepaintAll();
    }

    private void CollectCandidateRenderers(GameObject root, bool autoPickBestHair)
    {
        _candidateRenderers.Clear();
        if (root == null)
        {
            _candidateRendererNames = Array.Empty<string>();
            _activeRenderer = null;
            return;
        }

        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr != null && smr.sharedMesh != null)
                _candidateRenderers.Add(smr);
        }

        foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (mr != null)
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                    _candidateRenderers.Add(mr);
            }
        }

        _candidateRendererNames = _candidateRenderers
            .Select((c, idx) => $"{idx}: {c.gameObject.name} ({c.GetType().Name})")
            .ToArray();

        if (_candidateRenderers.Count == 0)
        {
            _activeRenderer = null;
            return;
        }

        if (!autoPickBestHair && _activeRenderer != null && _candidateRenderers.Contains(_activeRenderer))
            return;

        string[] hairKeywords = { "hair", "髪", "front", "back", "bang", "side", "twin", "tail", "ahoge", "kemomimi" };
        Component best = _candidateRenderers.FirstOrDefault(c =>
            hairKeywords.Any(k => c.gameObject.name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0));

        _activeRenderer = best ?? _candidateRenderers[0];
    }

    private void RebuildContext(bool keepSelection)
    {
        RestoreDeletePreview();

        if (_activeRenderer == null && _targetRootOrGo != null)
        {
            CollectCandidateRenderers(_targetRootOrGo, autoPickBestHair: true);
        }

        if (_activeRenderer == null)
        {
            _ctx = null;
            _selectedIslands.Clear();
            _selectedIslandList.Clear();
            return;
        }

        _ctx = HairMeshContext.Create(_activeRenderer);
        if (_ctx == null || !_ctx.IsValid)
        {
            SetStatus("有効なメッシュが見つかりませんでした。", MessageType.Warning);
            return;
        }

        _ctx.BuildIslands(_linkMode, _splitIslandsBySubMesh);
        BuildMaterialSlotLabelsAndTexture();

        if (!keepSelection)
        {
            _selectedIslands.Clear();
            _selectedIslandList.Clear();
            _uvViewer.ResetView();
        }
        else
        {
            _selectedIslands.RemoveWhere(id => id < 0 || id >= _ctx.Islands.Count);
            SyncSerializedListFromHashSet();
        }

        UpdateSelectionStatsAndPreview();

        SetStatus(
            $"「{_ctx.TargetGameObject.name}」を読み込みました（全 {_ctx.Islands.Count} 房 / {_ctx.Triangles.Length:N0} tris）。SceneView 上で削除したい髪の毛をクリックしてください。",
            MessageType.Info);
    }

    private void BuildMaterialSlotLabelsAndTexture()
    {
        _currentSlotTexture = null;
        if (_ctx == null)
        {
            _materialSlotLabels = Array.Empty<string>();
            return;
        }

        Material[] mats = GetRendererSharedMaterials(_ctx.TargetRenderer);
        int count = _ctx.SubMeshCount;
        _materialSlotLabels = new string[count];

        for (int i = 0; i < count; i++)
        {
            Material m = (mats != null && i < mats.Length) ? mats[i] : null;
            string matName = m != null ? m.name : "Materialなし";
            _materialSlotLabels[i] = $"Slot {i}: {matName}";
        }

        if (_viewerMaterialSlot < 0 || _viewerMaterialSlot >= count)
            _viewerMaterialSlot = 0;

        if (mats != null && _viewerMaterialSlot < mats.Length && mats[_viewerMaterialSlot] != null)
        {
            _currentSlotTexture = HairTextureProcessor.GetMainTextureFromMaterial(mats[_viewerMaterialSlot], out _);
        }
    }

    private static Material[] GetRendererSharedMaterials(Component r)
    {
        if (r is SkinnedMeshRenderer smr) return smr.sharedMaterials;
        if (r is MeshRenderer mr) return mr.sharedMaterials;
        return Array.Empty<Material>();
    }

    // ─────────────────────────────────────────────────────────
    //  選択操作 & リアルタイム削除プレビュー
    // ─────────────────────────────────────────────────────────

    private void SyncHashSetFromSerializedList()
    {
        _selectedIslands.Clear();
        if (_selectedIslandList != null)
        {
            foreach (int id in _selectedIslandList)
                _selectedIslands.Add(id);
        }
    }

    private void SyncSerializedListFromHashSet()
    {
        _selectedIslandList.Clear();
        _selectedIslandList.AddRange(_selectedIslands.OrderBy(x => x));
    }

    private int GetEffectiveSubMeshFilter()
    {
        if (_ctx == null || _ctx.SubMeshCount <= 1 || !_restrictPickToSlot) return -1;
        return _viewerMaterialSlot;
    }

    private void ApplyIslandSelectionAction(int islandId, bool addMode, bool singleSelect)
    {
        if (_ctx == null || islandId < 0 || islandId >= _ctx.Islands.Count) return;

        bool changed = false;
        if (singleSelect)
        {
            if (_selectedIslands.Count != 1 || !_selectedIslands.Contains(islandId))
            {
                Undo.RecordObject(this, "Select Hair Island To Delete");
                _selectedIslands.Clear();
                _selectedIslands.Add(islandId);
                changed = true;
            }
        }
        else if (addMode)
        {
            if (!_selectedIslands.Contains(islandId))
            {
                Undo.RecordObject(this, "Mark Hair Island For Deletion");
                _selectedIslands.Add(islandId);
                changed = true;
            }
        }
        else
        {
            if (_selectedIslands.Contains(islandId))
            {
                Undo.RecordObject(this, "Unmark Hair Island For Deletion");
                _selectedIslands.Remove(islandId);
                changed = true;
            }
        }

        if (changed)
        {
            SyncSerializedListFromHashSet();
            UpdateSelectionStatsAndPreview();
            Repaint();
            SceneView.RepaintAll();
        }
    }

    private void SelectAllIslands()
    {
        if (_ctx == null) return;
        int subFilter = GetEffectiveSubMeshFilter();
        Undo.RecordObject(this, "Select All Hair Islands");
        _selectedIslands.Clear();
        foreach (var isl in _ctx.Islands)
        {
            if (subFilter < 0 || isl.PrimarySubMesh == subFilter)
                _selectedIslands.Add(isl.IslandId);
        }
        SyncSerializedListFromHashSet();
        UpdateSelectionStatsAndPreview();
    }

    private void InvertIslandSelection()
    {
        if (_ctx == null) return;
        int subFilter = GetEffectiveSubMeshFilter();
        Undo.RecordObject(this, "Invert Hair Island Selection");
        foreach (var isl in _ctx.Islands)
        {
            if (subFilter >= 0 && isl.PrimarySubMesh != subFilter) continue;
            if (!_selectedIslands.Remove(isl.IslandId))
                _selectedIslands.Add(isl.IslandId);
        }
        SyncSerializedListFromHashSet();
        UpdateSelectionStatsAndPreview();
    }

    private void ClearIslandSelection()
    {
        if (_selectedIslands.Count == 0) return;
        Undo.RecordObject(this, "Clear Hair Island Selection");
        _selectedIslands.Clear();
        SyncSerializedListFromHashSet();
        UpdateSelectionStatsAndPreview();
    }

    private void UpdateSelectionStatsAndPreview()
    {
        if (_ctx == null || !_ctx.IsValid) return;

        int subFilter = GetEffectiveSubMeshFilter();
        _overlappingUnselectedCount = _ctx.CountOverlappingUnselectedIslands(_selectedIslands, subFilter);

        if (_liveDeletePreview && _selectedIslands.Count > 0)
        {
            ApplyDeletePreview();
        }
        else
        {
            RestoreDeletePreview();
        }
    }

    private void ApplyDeletePreview()
    {
        if (_ctx == null || _ctx.TargetRenderer == null) return;

        if (_isDeletePreviewApplied && _previewAppliedRenderer != _ctx.TargetRenderer)
        {
            RestoreDeletePreview();
        }

        if (!_isDeletePreviewApplied)
        {
            _previewAppliedRenderer = _ctx.TargetRenderer;
            _originalSharedMesh = _ctx.SourceMesh;
            _isDeletePreviewApplied = true;
        }

        if (_tempPreviewMesh != null)
        {
            DestroyImmediate(_tempPreviewMesh);
            _tempPreviewMesh = null;
        }

        // プレビュー用メッシュは高速生成のため、頂点配列はそのまま維持し、削除された三角形だけ抜いた軽量サブメッシュを作る
        _tempPreviewMesh = BuildFastPreviewDeletedMesh(_originalSharedMesh, _ctx, _selectedIslands);
        _tempPreviewMesh.hideFlags = HideFlags.HideAndDontSave;

        if (_ctx.SMR != null) _ctx.SMR.sharedMesh = _tempPreviewMesh;
        else if (_ctx.MF != null) _ctx.MF.sharedMesh = _tempPreviewMesh;

        SceneView.RepaintAll();
    }

    private void RestoreDeletePreview()
    {
        if (_isDeletePreviewApplied)
        {
            if (_previewAppliedRenderer != null && _originalSharedMesh != null)
            {
                if (_previewAppliedRenderer is SkinnedMeshRenderer smr)
                    smr.sharedMesh = _originalSharedMesh;
                else if (_previewAppliedRenderer is MeshRenderer mr)
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = _originalSharedMesh;
                }
            }
            _isDeletePreviewApplied = false;
            _previewAppliedRenderer = null;
            _originalSharedMesh = null;
        }

        if (_tempPreviewMesh != null)
        {
            DestroyImmediate(_tempPreviewMesh);
            _tempPreviewMesh = null;
        }

        SceneView.RepaintAll();
    }

    /// <summary>
    /// リアルタイムプレビュー用の高速削除メッシュ生成（インデックス配列のみフィルタリング、数ミリ秒で動作）。
    /// ※本番保存時は BuildCompactedMeshWithoutSelectedIslands で未使用頂点・BlendShape・BoneWeightまで完全にコンパクションする。
    /// </summary>
    private static Mesh BuildFastPreviewDeletedMesh(Mesh srcMesh, HairMeshContext ctx, HashSet<int> deletedIslands)
    {
        Mesh tmp = Instantiate(srcMesh);
        tmp.name = srcMesh.name + "_DeletePreview";
        tmp.hideFlags = HideFlags.HideAndDontSave;

        var deletedTriSet = new HashSet<int>();
        foreach (int islId in deletedIslands)
        {
            if (islId < 0 || islId >= ctx.Islands.Count) continue;
            foreach (int tIdx in ctx.Islands[islId].GlobalTriangles)
                deletedTriSet.Add(tIdx);
        }

        int vc = srcMesh.vertexCount;
        int subCount = Mathf.Max(1, srcMesh.subMeshCount);
        int globalTriCursor = 0;

        for (int s = 0; s < subCount; s++)
        {
            int[] origTris = srcMesh.GetTriangles(s);
            var remainTris = new List<int>(origTris.Length);

            for (int i = 0; i + 2 < origTris.Length; i += 3)
            {
                int v0 = origTris[i];
                int v1 = origTris[i + 1];
                int v2 = origTris[i + 2];
                if (v0 < 0 || v0 >= vc || v1 < 0 || v1 >= vc || v2 < 0 || v2 >= vc)
                    continue;

                bool isDeleted = deletedTriSet.Contains(globalTriCursor);
                globalTriCursor++;

                if (!isDeleted)
                {
                    remainTris.Add(v0);
                    remainTris.Add(v1);
                    remainTris.Add(v2);
                }
            }

            tmp.SetTriangles(remainTris, s, false);
        }

        return tmp;
    }

    // ─────────────────────────────────────────────────────────
    //  SceneView インタラクション（クリック選択 & ハイライト描画）
    // ─────────────────────────────────────────────────────────

    private void OnSceneGUI(SceneView sceneView)
    {
        if (_ctx == null || !_ctx.IsValid) return;

        Event e = Event.current;
        int subFilter = GetEffectiveSubMeshFilter();

        // 削除プレビューON かつ ゴースト非表示 のときは、すでに消したアイランドをスキップして奥の髪をクリックできるようにする
        bool skipDeletedOnRaycast = _liveDeletePreview && !_showDeletedGhostAndAllowRepick;
        HashSet<int> ignoredOnRaycast = skipDeletedOnRaycast ? _selectedIslands : null;

        // 1. 選択＆ホバーのオーバーレイ描画
        if (e.type == EventType.Repaint)
        {
            bool drawSelected = !_liveDeletePreview || _showDeletedGhostAndAllowRepick;
            Color fillCol = _liveDeletePreview
                ? new Color(1.0f, 0.25f, 0.25f, 0.16f) // ゴースト表示時は薄い赤
                : new Color(1.0f, 0.22f, 0.22f, 0.42f); // 通常ハイライト時ははっきりした赤
            Color wireCol = _liveDeletePreview
                ? new Color(1.0f, 0.35f, 0.35f, 0.45f)
                : new Color(1.0f, 0.30f, 0.25f, 0.92f);

            HairSceneOverlayRenderer.DrawSceneSelectionOverlay(
                _ctx,
                _selectedIslands,
                _scenePickActive ? _hoveredIslandId : -1,
                drawSelected,
                drawSelected,
                fillCol,
                wireCol,
                new Color(1.0f, 0.75f, 0.15f, 0.42f), // Hover Fill (Orange-Yellow)
                new Color(1.0f, 0.90f, 0.20f, 1.0f)); // Hover Wire
        }

        if (!_scenePickActive) return;

        // 2. SceneView 左上 HUD
        HairSceneOverlayRenderer.DrawSceneHUD(
            "Hair Mesh Deleter (Experimental)",
            _selectedIslands.Count,
            _ctx.Islands.Count,
            _hoveredIslandId,
            () => { ClearIslandSelection(); Repaint(); },
            () => { _scenePickActive = false; _hoveredIslandId = -1; Repaint(); SceneView.RepaintAll(); });

        if (e.alt || e.button == 1 || e.button == 2)
            return;

        int controlId = GUIUtility.GetControlID(FocusType.Passive);
        if (e.type == EventType.Layout)
        {
            HandleUtility.AddDefaultControl(controlId);
            return;
        }

        if (e.type == EventType.MouseMove)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            int prevHover = _hoveredIslandId;
            if (_ctx.RaycastIsland(ray, _raycastBothSides, subFilter, ignoredOnRaycast, out int hitId, out _, out _))
            {
                _hoveredIslandId = hitId;
            }
            else
            {
                _hoveredIslandId = -1;
            }

            if (prevHover != _hoveredIslandId)
            {
                sceneView.Repaint();
                Repaint();
            }
        }
        else if (e.type == EventType.MouseDown && e.button == 0)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (_ctx.RaycastIsland(ray, _raycastBothSides, subFilter, ignoredOnRaycast, out int hitId, out _, out _))
            {
                _hoveredIslandId = skipDeletedOnRaycast ? -1 : hitId;
                bool isAlreadySelected = _selectedIslands.Contains(hitId);

                if (_clickBehavior == HairClickBehavior.SingleOrShift && !e.shift && !e.control)
                {
                    _isSceneDragging = false;
                    ApplyIslandSelectionAction(hitId, addMode: true, singleSelect: true);
                }
                else
                {
                    _sceneDragAddMode = e.control ? false : (e.shift ? true : !isAlreadySelected);
                    // 消した房を貫通するモードではドラッグ中の一瞬の再ヒットによる誤解除を防ぐため、ドラッグ移動も維持
                    _isSceneDragging = true;
                    ApplyIslandSelectionAction(hitId, _sceneDragAddMode, singleSelect: false);
                }
                e.Use();
            }
        }
        else if (e.type == EventType.MouseDrag && e.button == 0 && _isSceneDragging)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (_ctx.RaycastIsland(ray, _raycastBothSides, subFilter, ignoredOnRaycast, out int hitId, out _, out _))
            {
                _hoveredIslandId = skipDeletedOnRaycast ? -1 : hitId;
                ApplyIslandSelectionAction(hitId, _sceneDragAddMode, singleSelect: false);
                e.Use();
            }
        }
        else if (e.type == EventType.MouseUp && e.button == 0 && _isSceneDragging)
        {
            _isSceneDragging = false;
            e.Use();
        }
    }

    // ─────────────────────────────────────────────────────────
    //  OnGUI（メインウィンドウ描画）
    // ─────────────────────────────────────────────────────────

    private void OnGUI()
    {
        _scrollMain = EditorGUILayout.BeginScrollView(_scrollMain);

        DrawHeaderSection();
        EditorGUILayout.Space(4f);

        DrawTargetSection();
        EditorGUILayout.Space(6f);

        using (new EditorGUI.DisabledScope(_ctx == null || !_ctx.IsValid))
        {
            DrawSceneSelectionSection();
            EditorGUILayout.Space(6f);

            DrawTextureViewerSection();
            EditorGUILayout.Space(6f);

            DrawSaveMeshSection();
        }

        if (_results != null && _results.Count > 0)
        {
            EditorGUILayout.Space(6f);
            DrawResultsSection();
        }

        EditorGUILayout.EndScrollView();

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            EditorGUILayout.Space(2f);
            EditorGUILayout.HelpBox(_statusMessage, _statusType);
            EditorGUILayout.Space(2f);
        }
    }

    private void DrawHeaderSection()
    {
        EditorGUILayout.LabelField("Hair Mesh Deleter (Experimental)", HeaderTitleStyle);
        EditorGUILayout.LabelField("Cocoa's Toolbox — 髪の房を Lキー風クリック選択 ＆ 部分削除して新規 .mesh 保存", EditorStyles.miniLabel);
        var lineRect = GUILayoutUtility.GetRect(0f, 1f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(lineRect, EditorStyles.label.normal.textColor * 0.3f);
    }

    private void DrawTargetSection()
    {
        EditorGUILayout.LabelField("① 対象メッシュ", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var nextGo = (GameObject)EditorGUILayout.ObjectField(
                    "対象オブジェクト", _targetRootOrGo, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck())
                {
                    SetTargetFromGameObject(nextGo);
                }

                using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
                {
                    if (GUILayout.Button("選択中をセット", EditorStyles.miniButton, GUILayout.Width(92f)))
                    {
                        SetTargetFromGameObject(Selection.activeGameObject);
                    }
                }
            }

            if (_candidateRenderers.Count > 1)
            {
                int currentIdx = Mathf.Max(0, _candidateRenderers.IndexOf(_activeRenderer));
                EditorGUI.BeginChangeCheck();
                int nextIdx = EditorGUILayout.Popup("対象メッシュ (Renderer)", currentIdx, _candidateRendererNames);
                if (EditorGUI.EndChangeCheck() && nextIdx >= 0 && nextIdx < _candidateRenderers.Count)
                {
                    _activeRenderer = _candidateRenderers[nextIdx];
                    RebuildContext(keepSelection: false);
                }
            }
            else if (_ctx != null && _ctx.TargetRenderer != null)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("対象 Renderer", _ctx.TargetRenderer, typeof(Component), true);
                }
            }

            if (_ctx != null && _ctx.SubMeshCount > 1)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    _viewerMaterialSlot = EditorGUILayout.Popup("表示マテリアルスロット", _viewerMaterialSlot, _materialSlotLabels);
                    _restrictPickToSlot = EditorGUILayout.ToggleLeft("このスロットのみ選択", _restrictPickToSlot, GUILayout.Width(130f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        BuildMaterialSlotLabelsAndTexture();
                        UpdateSelectionStatsAndPreview();
                    }
                }
            }
        }
    }

    private void DrawSceneSelectionSection()
    {
        EditorGUILayout.LabelField("② SceneView クリック選択 ＆ 削除プレビュー", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // SceneView 選択モード ON/OFF 大ボタン
            Color prevBg = GUI.backgroundColor;
            if (_scenePickActive)
                GUI.backgroundColor = new Color(1.0f, 0.55f, 0.45f, 1f);

            string pickBtnLabel = _scenePickActive
                ? "🎯 SceneView クリック選択モード：ON （削除したい髪の房をクリック）"
                : "🎯 SceneView クリック選択モードを開始する（クリックで ON）";

            if (GUILayout.Button(pickBtnLabel, _scenePickActive ? PickActiveBtnStyle : PickIdleBtnStyle))
            {
                _scenePickActive = !_scenePickActive;
                if (!_scenePickActive) _hoveredIslandId = -1;
                SceneView.RepaintAll();
            }
            GUI.backgroundColor = prevBg;

            EditorGUILayout.Space(2f);

            // 連結基準
            EditorGUI.BeginChangeCheck();
            _linkMode = (HairLinkMode)EditorGUILayout.EnumPopup(
                new GUIContent("房の連結基準 (Lキー挙動)",
                    "Spatial3D: シームやハードエッジを越えて3D形状として繋がっている髪の房1本を丸ごと選択します（推奨）\n" +
                    "TopologyOnly: 頂点インデックスを共有する面のみ連結（シームで区切る）\n" +
                    "UVIsland: テクスチャのUV上で繋がっている塊を選択\n" +
                    "SingleTriangle: 1ポリゴン単位で細かく選択"),
                _linkMode);
            if (EditorGUI.EndChangeCheck() && _ctx != null)
            {
                RebuildContext(keepSelection: false);
            }

            _clickBehavior = (HairClickBehavior)EditorGUILayout.Popup(
                "クリック動作",
                (int)_clickBehavior,
                new[] { "クリックで削除対象に追加/解除 (トグル・なぞり対応)", "通常クリックで単一選択 (Shiftで追加)" });

            EditorGUILayout.Space(2f);

            // リアルタイム削除プレビュー設定
            EditorGUI.BeginChangeCheck();
            _liveDeletePreview = EditorGUILayout.ToggleLeft(
                "SceneView で選択した房をリアルタイムに非表示にして確認する（削除プレビュー）",
                _liveDeletePreview,
                EditorStyles.boldLabel);

            if (_liveDeletePreview)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    _showDeletedGhostAndAllowRepick = EditorGUILayout.ToggleLeft(
                        "削除選択した房を半透明ゴーストで残す（ON: 再クリックで復活可 ／ OFF: 消えた奥の髪をクリック可）",
                        _showDeletedGhostAndAllowRepick);
                }
            }
            if (EditorGUI.EndChangeCheck())
            {
                UpdateSelectionStatsAndPreview();
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space(2f);

            // 一括選択ボタン
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("選択を全解除", EditorStyles.miniButtonLeft))
                    ClearIslandSelection();
                if (GUILayout.Button("選択を反転 (選んだ房だけ残す)", EditorStyles.miniButtonMid))
                    InvertIslandSelection();
                if (GUILayout.Button("全選択", EditorStyles.miniButtonMid, GUILayout.Width(56f)))
                    SelectAllIslands();
                if (GUILayout.Button("ポーズ再取得", EditorStyles.miniButtonRight, GUILayout.Width(82f)))
                {
                    _ctx?.UpdateWorldGeometry();
                    SceneView.RepaintAll();
                }
            }

            // 削減ポリゴン数の統計表示
            if (_ctx != null)
            {
                int totalTris = _ctx.Triangles.Length;
                int deleteTris = 0;
                foreach (int id in _selectedIslands)
                {
                    if (id >= 0 && id < _ctx.Islands.Count)
                        deleteTris += _ctx.Islands[id].GlobalTriangles.Length;
                }
                int remainTris = Mathf.Max(0, totalTris - deleteTris);
                float pct = totalTris > 0 ? (deleteTris * 100f / totalTris) : 0f;

                EditorGUILayout.LabelField(
                    $"削除対象: {_selectedIslands.Count} / {_ctx.Islands.Count} 房  |  削減: -{deleteTris:N0} tris ({pct:0.0}%)  →  残り: {remainTris:N0} tris",
                    EditorStyles.miniBoldLabel);
            }
        }
    }

    private void DrawTextureViewerSection()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("③ リアルタイム・テクスチャ ＆ UVビュワー", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"Zoom: {_uvViewer.Zoom * 100f:0}%", EditorStyles.miniLabel, GUILayout.Width(72f));
        }

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // ビュワーツールバー
            using (new EditorGUILayout.HorizontalScope())
            {
                _uvViewer.ShowAllUVWire = GUILayout.Toggle(_uvViewer.ShowAllUVWire, "全UV線", EditorStyles.miniButtonLeft, GUILayout.Width(58f));
                _uvViewer.ShowSelectedFill = GUILayout.Toggle(_uvViewer.ShowSelectedFill, "削除塗り", EditorStyles.miniButtonMid, GUILayout.Width(62f));
                _uvViewer.ShowSelectedWire = GUILayout.Toggle(_uvViewer.ShowSelectedWire, "削除枠", EditorStyles.miniButtonRight, GUILayout.Width(58f));

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("選択UVへ拡大", EditorStyles.miniButtonLeft, GUILayout.Width(86f)))
                {
                    _uvViewer.FitToSelected(_ctx, _selectedIslands);
                }
                if (GUILayout.Button("100%に戻す", EditorStyles.miniButtonRight, GUILayout.Width(74f)))
                {
                    _uvViewer.ResetView();
                }
            }

            Rect canvasRect = GUILayoutUtility.GetRect(100f, _viewerHeight, GUILayout.ExpandWidth(true));
            int subFilter = GetEffectiveSubMeshFilter();

            _uvViewer.DrawViewer(
                canvasRect,
                _currentSlotTexture,
                _ctx,
                subFilter,
                _selectedIslands,
                _hoveredIslandId,
                new Color(1.0f, 0.25f, 0.20f, 1f), // 削除選択UV（レッド・オレンジ）
                new Color(1.0f, 0.90f, 0.15f, 1f), // ホバーUV（イエロー）
                (clickedIsl, addMode) =>
                {
                    ApplyIslandSelectionAction(clickedIsl, addMode, singleSelect: false);
                },
                (hoverIsl) =>
                {
                    if (_hoveredIslandId != hoverIsl)
                    {
                        _hoveredIslandId = hoverIsl;
                        Repaint();
                        SceneView.RepaintAll();
                    }
                },
                Repaint);

            EditorGUILayout.LabelField(
                "💡 赤く表示されているUV領域が削除対象です。テクスチャビュワー上をクリックしても削除対象の選択・解除（復活）ができます。",
                WrapMiniStyle);
        }
    }

    private void DrawSaveMeshSection()
    {
        EditorGUILayout.LabelField("④ 新しい .mesh アセットとして保存", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            _applyMode = (ApplyOutputMode)EditorGUILayout.Popup(
                "保存後の適用方法",
                (int)_applyMode,
                new[]
                {
                    "その場で差し替え（現在の Renderer の Mesh を新規 .mesh に変更・Undo対応）",
                    "複製して生成（元のオブジェクトを非表示にして複製側に適用・非破壊）",
                    ".mesh アセットの保存のみ"
                });

            if (_applyMode == ApplyOutputMode.DuplicateObject)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    _hideOriginalOnDuplicate = EditorGUILayout.ToggleLeft(
                        "生成後に元のオブジェクトを非表示にする",
                        _hideOriginalOnDuplicate);
                }
            }

            _keepOriginalBounds = EditorGUILayout.ToggleLeft(
                "元のメッシュの Bounds（視界カリング判定範囲）を維持する（VRChatアバター推奨）",
                _keepOriginalBounds);

            // 保存先フォルダ選択
            using (new EditorGUILayout.HorizontalScope())
            {
                string defaultDir = GetEffectiveSaveFolder();
                string disp = string.IsNullOrEmpty(_saveFolderPath) ? $"（自動: {defaultDir}）" : _saveFolderPath;

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("保存先フォルダ", disp);
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
                        GUIUtility.ExitGUI();
                    }
                }

                if (!string.IsNullOrEmpty(_saveFolderPath) && GUILayout.Button("戻す", EditorStyles.miniButton, GUILayout.Width(42f)))
                {
                    _saveFolderPath = "";
                }
            }

            EditorGUILayout.Space(4f);

            bool canExecute = _ctx != null && _ctx.IsValid && _selectedIslands.Count > 0 && _selectedIslands.Count < _ctx.Islands.Count;
            if (_ctx != null && _selectedIslands.Count >= _ctx.Islands.Count && _ctx.Islands.Count > 0)
            {
                EditorGUILayout.HelpBox("すべての房が削除対象になっています。少なくとも1つ以上の房を残してください。", MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(!canExecute))
            {
                if (GUILayout.Button("✦  選択した部分を削除して .mesh 保存  ✦", ExecuteBtnStyle))
                {
                    ExecuteDeleteAndSaveMesh();
                    GUIUtility.ExitGUI();
                }
            }
        }
    }

    private void DrawResultsSection()
    {
        _foldResults = EditorGUILayout.Foldout(_foldResults, $"保存履歴 ({_results.Count} 件)", true);
        if (!_foldResults) return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            foreach (var rec in _results)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(rec.Label, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

                    if (rec.TargetObject != null && GUILayout.Button("Object", EditorStyles.miniButton, GUILayout.Width(52f)))
                    {
                        EditorGUIUtility.PingObject(rec.TargetObject);
                        Selection.activeGameObject = rec.TargetObject;
                    }

                    if (rec.SavedMesh != null && GUILayout.Button(".mesh", EditorStyles.miniButton, GUILayout.Width(48f)))
                    {
                        EditorGUIUtility.PingObject(rec.SavedMesh);
                        Selection.activeObject = rec.SavedMesh;
                    }
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  メッシュ削除コンパクション ＆ .mesh アセット保存ロジック
    // ─────────────────────────────────────────────────────────

    private string GetEffectiveSaveFolder()
    {
        if (!string.IsNullOrEmpty(_saveFolderPath))
            return _saveFolderPath;

        return HairTextureProcessor.ResolveDefaultSaveFolder(_ctx?.SourceMesh, DEFAULT_SAVE_DIR);
    }

    private void ExecuteDeleteAndSaveMesh()
    {
        if (_ctx == null || !_ctx.IsValid || _selectedIslands.Count == 0)
        {
            SetStatus("削除する髪の房を1つ以上選択してください。", MessageType.Warning);
            return;
        }

        // プレビューによる一時Mesh差し替えを解除して元Meshに戻す
        RestoreDeletePreview();

        try
        {
            Mesh srcMesh = _ctx.SourceMesh;
            int oldVerts = srcMesh.vertexCount;
            int oldTris = _ctx.Triangles.Length;

            Mesh newMesh = BuildCompactedMeshWithoutSelectedIslands(srcMesh, _ctx, _selectedIslands, _keepOriginalBounds);
            if (newMesh == null || newMesh.vertexCount == 0)
            {
                if (newMesh != null) DestroyImmediate(newMesh);
                SetStatus("削除後に残る頂点が0件になるため保存を中止しました。", MessageType.Warning);
                return;
            }

            string folder = GetEffectiveSaveFolder();
            string baseName = !string.IsNullOrEmpty(srcMesh.name) ? srcMesh.name : _ctx.TargetGameObject.name;
            string meshAssetPath = HairTextureProcessor.GenerateUniqueAssetPath(folder, baseName, DELETED_MESH_SUFFIX, ".mesh");

            newMesh.name = Path.GetFileNameWithoutExtension(meshAssetPath);
            AssetDatabase.CreateAsset(newMesh, meshAssetPath);
            AssetDatabase.SaveAssets();

            var savedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
            int newVerts = savedMesh.vertexCount;
            int newTris = savedMesh.triangles.Length / 3;

            Undo.SetCurrentGroupName("Delete Hair Mesh Islands");
            int undoGroup = Undo.GetCurrentGroup();

            GameObject resultTargetGo = _ctx.TargetGameObject;

            if (_applyMode == ApplyOutputMode.ReplaceInPlace)
            {
                if (_ctx.SMR != null)
                {
                    Undo.RecordObject(_ctx.SMR, "Assign Deleted Hair Mesh");
                    _ctx.SMR.sharedMesh = savedMesh;
                    EditorUtility.SetDirty(_ctx.SMR);
                }
                else if (_ctx.MF != null)
                {
                    Undo.RecordObject(_ctx.MF, "Assign Deleted Hair Mesh");
                    _ctx.MF.sharedMesh = savedMesh;
                    EditorUtility.SetDirty(_ctx.MF);
                }
            }
            else if (_applyMode == ApplyOutputMode.DuplicateObject)
            {
                GameObject srcGo = _ctx.TargetGameObject;
                GameObject dupGo = Instantiate(srcGo, srcGo.transform.parent);
                dupGo.name = srcGo.name + "_Deleted";
                Undo.RegisterCreatedObjectUndo(dupGo, "Create Duplicated Hair Mesh Object");
                dupGo.transform.SetSiblingIndex(srcGo.transform.GetSiblingIndex() + 1);
                dupGo.transform.localPosition = srcGo.transform.localPosition;
                dupGo.transform.localRotation = srcGo.transform.localRotation;
                dupGo.transform.localScale = srcGo.transform.localScale;

                var dupSmr = dupGo.GetComponent<SkinnedMeshRenderer>();
                if (dupSmr != null)
                {
                    dupSmr.sharedMesh = savedMesh;
                    _activeRenderer = dupSmr;
                }
                else
                {
                    var dupMf = dupGo.GetComponent<MeshFilter>();
                    if (dupMf != null) dupMf.sharedMesh = savedMesh;
                    _activeRenderer = dupGo.GetComponent<MeshRenderer>();
                }

                if (_hideOriginalOnDuplicate)
                {
                    Undo.RecordObject(srcGo, "Hide Original Hair Object");
                    srcGo.SetActive(false);
                }

                _targetRootOrGo = dupGo;
                resultTargetGo = dupGo;
            }

            Undo.CollapseUndoOperations(undoGroup);

            _results.Insert(0, new DeleteResultRecord
            {
                Label = $"{Path.GetFileName(meshAssetPath)}  (頂点 {oldVerts:N0}→{newVerts:N0} / 面 {oldTris:N0}→{newTris:N0})",
                SavedMesh = savedMesh,
                MeshAssetPath = meshAssetPath,
                TargetObject = resultTargetGo,
                OldVertices = oldVerts,
                NewVertices = newVerts,
                OldTriangles = oldTris,
                NewTriangles = newTris
            });

            // 新しいメッシュでコンテキストを再構築（選択はクリア）
            RebuildContext(keepSelection: false);

            if (savedMesh != null)
                EditorGUIUtility.PingObject(savedMesh);

            SetStatus(
                $"削除完了！新しいメッシュを保存しました: {meshAssetPath} （三角面 {oldTris:N0} → {newTris:N0} / 頂点 {oldVerts:N0} → {newVerts:N0}）",
                MessageType.Info);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            SetStatus($"メッシュ保存中にエラーが発生しました: {ex.Message}", MessageType.Error);
        }
    }

    /// <summary>
    /// 選択されたアイランドの三角形を削除し、使用されなくなった孤立頂点を完全に除去・リマップした新しい Mesh を構築する。
    /// ・BoneWeights（可変長ウェイト対応）、Bindposes、全8チャンネルUV（2D/3D/4D）、Colors32、Normals、Tangents、
    ///   および全 BlendShape（マルチフレーム対応）を新頂点インデックスへ完全にリマップして引き継ぐ。
    /// </summary>
    public static Mesh BuildCompactedMeshWithoutSelectedIslands(
        Mesh srcMesh,
        HairMeshContext ctx,
        HashSet<int> deletedIslands,
        bool keepOriginalBounds)
    {
        int oldVc = srcMesh.vertexCount;
        int subCount = Mathf.Max(1, srcMesh.subMeshCount);

        // 1. 削除対象のグローバル三角形インデックス集合を作成
        var deletedTriSet = new HashSet<int>();
        foreach (int islId in deletedIslands)
        {
            if (islId < 0 || islId >= ctx.Islands.Count) continue;
            foreach (int tIdx in ctx.Islands[islId].GlobalTriangles)
                deletedTriSet.Add(tIdx);
        }

        // 2. サブメッシュごとに残る三角形を抽出し、使用されている頂点をマーク
        var remainingSubMeshTris = new List<int>[subCount];
        bool[] vertexUsed = new bool[oldVc];
        int globalTriCursor = 0;

        for (int s = 0; s < subCount; s++)
        {
            int[] origTris = srcMesh.GetTriangles(s);
            var remain = new List<int>(origTris.Length);

            for (int i = 0; i + 2 < origTris.Length; i += 3)
            {
                int v0 = origTris[i];
                int v1 = origTris[i + 1];
                int v2 = origTris[i + 2];
                if (v0 < 0 || v0 >= oldVc || v1 < 0 || v1 >= oldVc || v2 < 0 || v2 >= oldVc)
                    continue;

                bool isDeleted = deletedTriSet.Contains(globalTriCursor);
                globalTriCursor++;

                if (!isDeleted)
                {
                    remain.Add(v0);
                    remain.Add(v1);
                    remain.Add(v2);
                    vertexUsed[v0] = true;
                    vertexUsed[v1] = true;
                    vertexUsed[v2] = true;
                }
            }

            remainingSubMeshTris[s] = remain;
        }

        // 3. oldVertexIndex -> newVertexIndex のリマップテーブル作成
        int[] oldToNew = new int[oldVc];
        var newToOldList = new List<int>(oldVc);
        for (int v = 0; v < oldVc; v++)
        {
            if (vertexUsed[v])
            {
                oldToNew[v] = newToOldList.Count;
                newToOldList.Add(v);
            }
            else
            {
                oldToNew[v] = -1;
            }
        }

        int newVc = newToOldList.Count;
        if (newVc == 0) return null;

        int[] newToOld = newToOldList.ToArray();

        var dst = new Mesh
        {
            indexFormat = newVc >= 65536 ? IndexFormat.UInt32 : srcMesh.indexFormat
        };

        // 4. 頂点座標・法線・接線・頂点カラーのリマップ
        Vector3[] srcVerts = srcMesh.vertices;
        Vector3[] dstVerts = new Vector3[newVc];
        for (int nv = 0; nv < newVc; nv++)
            dstVerts[nv] = srcVerts[newToOld[nv]];
        dst.vertices = dstVerts;

        Vector3[] srcNorms = srcMesh.normals;
        if (srcNorms != null && srcNorms.Length == oldVc)
        {
            Vector3[] dstNorms = new Vector3[newVc];
            for (int nv = 0; nv < newVc; nv++)
                dstNorms[nv] = srcNorms[newToOld[nv]];
            dst.normals = dstNorms;
        }

        Vector4[] srcTans = srcMesh.tangents;
        if (srcTans != null && srcTans.Length == oldVc)
        {
            Vector4[] dstTans = new Vector4[newVc];
            for (int nv = 0; nv < newVc; nv++)
                dstTans[nv] = srcTans[newToOld[nv]];
            dst.tangents = dstTans;
        }

        Color32[] srcCols = srcMesh.colors32;
        if (srcCols != null && srcCols.Length == oldVc)
        {
            Color32[] dstCols = new Color32[newVc];
            for (int nv = 0; nv < newVc; nv++)
                dstCols[nv] = srcCols[newToOld[nv]];
            dst.colors32 = dstCols;
        }

        // 5. 全8チャンネルの UV（2D / 3D / 4D 次元保持）のリマップ
        var uv2Src = new List<Vector2>();
        var uv3Src = new List<Vector3>();
        var uv4Src = new List<Vector4>();
        for (int ch = 0; ch < 8; ch++)
        {
            var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
            if (!srcMesh.HasVertexAttribute(attr)) continue;

            int dim = srcMesh.GetVertexAttributeDimension(attr);
            if (dim == 2)
            {
                uv2Src.Clear();
                srcMesh.GetUVs(ch, uv2Src);
                if (uv2Src.Count == oldVc)
                {
                    var uv2Dst = new Vector2[newVc];
                    for (int nv = 0; nv < newVc; nv++) uv2Dst[nv] = uv2Src[newToOld[nv]];
                    dst.SetUVs(ch, uv2Dst);
                }
            }
            else if (dim == 3)
            {
                uv3Src.Clear();
                srcMesh.GetUVs(ch, uv3Src);
                if (uv3Src.Count == oldVc)
                {
                    var uv3Dst = new Vector3[newVc];
                    for (int nv = 0; nv < newVc; nv++) uv3Dst[nv] = uv3Src[newToOld[nv]];
                    dst.SetUVs(ch, uv3Dst);
                }
            }
            else if (dim == 4)
            {
                uv4Src.Clear();
                srcMesh.GetUVs(ch, uv4Src);
                if (uv4Src.Count == oldVc)
                {
                    var uv4Dst = new Vector4[newVc];
                    for (int nv = 0; nv < newVc; nv++) uv4Dst[nv] = uv4Src[newToOld[nv]];
                    dst.SetUVs(ch, uv4Dst);
                }
            }
        }

        // 6. Bindposes & BoneWeights（可変長ウェイト完全対応）のリマップ
        Matrix4x4[] bindposes = srcMesh.bindposes;
        if (bindposes != null && bindposes.Length > 0)
        {
            dst.bindposes = bindposes;
        }

        var srcBonesPerVert = srcMesh.GetBonesPerVertex();
        var srcAllWeights = srcMesh.GetAllBoneWeights();
        if (srcBonesPerVert.IsCreated && srcAllWeights.IsCreated && srcBonesPerVert.Length == oldVc && srcAllWeights.Length > 0)
        {
            // 各旧頂点の開始オフセットを算出
            int[] weightStartOffsets = new int[oldVc];
            int runningOffset = 0;
            for (int v = 0; v < oldVc; v++)
            {
                weightStartOffsets[v] = runningOffset;
                runningOffset += srcBonesPerVert[v];
            }

            byte[] dstBonesPerVertArr = new byte[newVc];
            var dstWeightsList = new List<BoneWeight1>(srcAllWeights.Length);

            for (int nv = 0; nv < newVc; nv++)
            {
                int ov = newToOld[nv];
                byte count = srcBonesPerVert[ov];
                dstBonesPerVertArr[nv] = count;
                int start = weightStartOffsets[ov];
                for (int k = 0; k < count; k++)
                {
                    dstWeightsList.Add(srcAllWeights[start + k]);
                }
            }

            using (var nativeBonesPerVert = new NativeArray<byte>(dstBonesPerVertArr, Allocator.Temp))
            using (var nativeWeights = new NativeArray<BoneWeight1>(dstWeightsList.ToArray(), Allocator.Temp))
            {
                dst.SetBoneWeights(nativeBonesPerVert, nativeWeights);
            }
        }
        else
        {
            BoneWeight[] srcBw = srcMesh.boneWeights;
            if (srcBw != null && srcBw.Length == oldVc)
            {
                BoneWeight[] dstBw = new BoneWeight[newVc];
                for (int nv = 0; nv < newVc; nv++)
                    dstBw[nv] = srcBw[newToOld[nv]];
                dst.boneWeights = dstBw;
            }
        }

        // 7. サブメッシュ三角形インデックスのリマップ
        dst.subMeshCount = subCount;
        for (int s = 0; s < subCount; s++)
        {
            var remain = remainingSubMeshTris[s];
            int[] remappedTris = new int[remain.Count];
            for (int i = 0; i < remain.Count; i++)
            {
                remappedTris[i] = oldToNew[remain[i]];
            }
            dst.SetTriangles(remappedTris, s, false);
        }

        // 8. 全 BlendShape（マルチフレーム対応）の頂点デルタを新頂点インデックスへリマップして追加
        int bsCount = srcMesh.blendShapeCount;
        if (bsCount > 0)
        {
            var srcDV = new Vector3[oldVc];
            var srcDN = new Vector3[oldVc];
            var srcDT = new Vector3[oldVc];

            var dstDV = new Vector3[newVc];
            var dstDN = new Vector3[newVc];
            var dstDT = new Vector3[newVc];

            for (int bi = 0; bi < bsCount; bi++)
            {
                string shapeName = srcMesh.GetBlendShapeName(bi);
                int frameCount = srcMesh.GetBlendShapeFrameCount(bi);

                for (int fi = 0; fi < frameCount; fi++)
                {
                    float frameWeight = srcMesh.GetBlendShapeFrameWeight(bi, fi);
                    srcMesh.GetBlendShapeFrameVertices(bi, fi, srcDV, srcDN, srcDT);

                    for (int nv = 0; nv < newVc; nv++)
                    {
                        int ov = newToOld[nv];
                        dstDV[nv] = srcDV[ov];
                        dstDN[nv] = srcDN[ov];
                        dstDT[nv] = srcDT[ov];
                    }

                    dst.AddBlendShapeFrame(shapeName, frameWeight, dstDV, dstDN, dstDT);
                }
            }
        }

        dst.RecalculateBounds();
        if (keepOriginalBounds)
        {
            dst.bounds = srcMesh.bounds;
        }

        return dst;
    }

    private void SetStatus(string msg, MessageType type)
    {
        _statusMessage = msg;
        _statusType = type;
        Repaint();
    }
}
#endif
