#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Hair Mesh Color Changer — Cocoa's Toolbox
/// 髪メッシュの一本一本（アイランド / 房）を SceneView 上でクリックして選択（Blender 編集モードの Lキー風リンク選択）し、
/// 4つの基本色彩調整スライダー（色相・彩度・明度・コントラスト）で部分的に髪の色を変えたり、
/// lilToon 用のマスクテクスチャ（.png）を作成・自動設定できるエディタ拡張ツール。
/// SceneView でのクリック選択と同時に、テクスチャ上のどのUV部分が選択されているかをリアルタイム表示するテクスチャビュワー付き。
/// </summary>
public class HairMeshColorChanger : EditorWindow
{
    // ─────────────────────────────────────────────────────────
    //  定数・列挙型
    // ─────────────────────────────────────────────────────────

    private const string WINDOW_TITLE = "Hair Mesh Color Changer (Experimental)";
    private const string DEFAULT_SAVE_DIR = "Assets/HairColorChanged";
    private const string COLOR_TEX_SUFFIX = "_HairColor-";
    private const string MASK_TEX_SUFFIX = "_HairMask-";
    private const string SPLIT_MESH_SUFFIX = "_HairSplit-";
    private const int PREVIEW_MAX_RES = 512;

    private enum ViewerDisplayMode
    {
        ColorPreview = 0, // 色変更プレビュー + UV選択表示
        MaskPreview = 1,  // lilToon マスク (白黒) + UV選択表示
        OriginalTex = 2   // 元テクスチャ + UV選択表示
    }

    private enum SceneHighlightMode
    {
        WireOnly = 0,     // ワイヤーのみ（色味確認の邪魔にならない・推奨）
        FillAndWire = 1,  // 半透明塗り＋ワイヤー
        HoverOnly = 2,    // ホバー中の房のみ表示（選択済みの色味をそのまま確認）
        None = 3          // 非表示
    }

    private enum ColorApplyMode
    {
        DuplicateMaterial = 0,        // マテリアルを複製して割り当て（非破壊・推奨）
        SplitSubMeshAndMaterial = 1,  // 選択した房だけ別マテリアルスロットに分離（UV重複対策・新規 .mesh + 複製Material）
        ModifyCurrentMaterial = 2,    // 現在のマテリアルのテクスチャを直接差し替え
        SaveTextureOnly = 3           // テクスチャ (.png) の保存のみ
    }

    private enum MaskResolutionMode
    {
        MatchSource = 0,
        Res512 = 512,
        Res1024 = 1024,
        Res2048 = 2048,
        Res4096 = 4096
    }

    [Serializable]
    private class OutputRecord
    {
        public string Label;
        public UnityEngine.Object MainAsset;
        public string AssetPath;
        public UnityEngine.Object SecondaryAsset;
        public string SecondaryPath;
    }

    // ─────────────────────────────────────────────────────────
    //  シリアライズ状態（Undo / Domain Reload 対応）
    // ─────────────────────────────────────────────────────────

    [SerializeField] private GameObject _targetRootOrGo;
    [SerializeField] private Component _activeRenderer; // SkinnedMeshRenderer or MeshRenderer
    [SerializeField] private int _selectedSubMeshIndex = 0; // -1 = 全サブメッシュ対象
    [SerializeField] private bool _filterBySubMesh = true;

    // 選択設定
    [SerializeField] private bool _scenePickActive = true;
    [SerializeField] private HairLinkMode _linkMode = HairLinkMode.Spatial3D;
    [SerializeField] private bool _splitIslandsBySubMesh = true;
    [SerializeField] private HairClickBehavior _clickBehavior = HairClickBehavior.Toggle;
    [SerializeField] private bool _raycastBothSides = true;
    [SerializeField] private SceneHighlightMode _sceneHighlightMode = SceneHighlightMode.WireOnly;

    // 選択中のアイランドIDリスト（Undo対応のため List でシリアライズ）
    [SerializeField] private List<int> _selectedIslandList = new List<int>();
    private readonly HashSet<int> _selectedIslands = new HashSet<int>();

    // 4つの基本色彩調整スライダー
    [SerializeField] private float _hueShift = 0f;        // -180 .. +180
    [SerializeField] private float _saturationDelta = 0f; // -100 .. +100
    [SerializeField] private float _brightnessDelta = 0f; // -100 .. +100
    [SerializeField] private float _contrastDelta = 0f;   // -100 .. +100

    // オプション：色乗せ（Tint / Colorize）
    [SerializeField] private bool _foldTintOption = false;
    [SerializeField] private bool _useTint = false;
    [SerializeField] private Color _tintColor = new Color(0.95f, 0.35f, 0.55f, 1f);
    [SerializeField] private float _tintStrength = 0.65f;

    // マスク境界・UVパディング設定
    [SerializeField] private int _uvPaddingPx = 6;
    [SerializeField] private int _uvFeatherPx = 1;
    [SerializeField] private bool _protectUnselectedUV = true;

    // テクスチャビュワー設定
    [SerializeField] private ViewerDisplayMode _viewerDisplayMode = ViewerDisplayMode.ColorPreview;
    [SerializeField] private float _viewerHeight = 250f;
    [SerializeField] private HairTextureUVViewer _uvViewer = new HairTextureUVViewer();

    // リアルタイム SceneView プレビュー設定
    [SerializeField] private bool _liveScenePreview = true;
    [SerializeField] private bool _previewSplitSubMeshForOverlap = false;

    // 保存・出力設定
    [SerializeField] private ColorApplyMode _colorApplyMode = ColorApplyMode.DuplicateMaterial;
    [SerializeField] private MaskResolutionMode _maskResolutionMode = MaskResolutionMode.MatchSource;
    [SerializeField] private bool _invertMask = false;
    [SerializeField] private bool _autoAssignLilToon2ndMask = false;
    [SerializeField] private bool _duplicateMatOnLilToonAssign = true;
    [SerializeField] private string _saveFolderPath = "";

    // UIスクロール・ステータス
    [SerializeField] private Vector2 _scrollMain;
    [SerializeField] private bool _foldOutputs = true;
    [SerializeField] private List<OutputRecord> _outputs = new List<OutputRecord>();
    [SerializeField] private string _statusMessage = "";
    [SerializeField] private MessageType _statusType = MessageType.Info;

    // ─────────────────────────────────────────────────────────
    //  ランタイム・キャッシュ状態
    // ─────────────────────────────────────────────────────────

    private HairMeshContext _ctx;
    private List<Component> _candidateRenderers = new List<Component>();
    private string[] _candidateRendererNames = Array.Empty<string>();
    private string[] _subMeshLabels = Array.Empty<string>();

    private int _hoveredIslandId = -1;
    private bool _isSceneDragging = false;
    private bool _sceneDragAddMode = true;
    private int _overlappingUnselectedCount = 0;

    // プレビューテクスチャ・マスクキャッシュ
    private Texture _cachedSourceTex;
    private string _cachedMainTexProperty = "_MainTex";
    private int _previewWidth = 512;
    private int _previewHeight = 512;
    private Color32[] _previewSrcPixels;
    private float[] _previewMask;
    private Color32[] _previewDstPixels;
    private Texture2D _previewColorTex;
    private Texture2D _previewMaskTex;
    private bool _maskDirty = true;
    private bool _colorDirty = true;

    // SceneView リアルタイムプレビュー用の元状態退避
    private bool _isScenePreviewApplied = false;
    private Component _previewAppliedRenderer;
    private Material[] _originalSharedMaterials;
    private Mesh _originalSharedMesh;
    private Material[] _tempPreviewMaterials;
    private Mesh _tempPreviewSplitMesh;

    // GUIStyle キャッシュ
    private GUIStyle _headerTitleStyle;
    private GUIStyle _pickActiveBtnStyle;
    private GUIStyle _pickIdleBtnStyle;
    private GUIStyle _executeBtnStyle;
    private GUIStyle _maskBtnStyle;
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
        fontSize = 12,
        fontStyle = FontStyle.Bold,
        fixedHeight = 32f
    });

    private GUIStyle MaskBtnStyle => _maskBtnStyle ?? (_maskBtnStyle = new GUIStyle(GUI.skin.button)
    {
        fontSize = 12,
        fontStyle = FontStyle.Bold,
        fixedHeight = 28f
    });

    private GUIStyle WrapMiniStyle => _wrapMiniStyle ?? (_wrapMiniStyle = new GUIStyle(EditorStyles.miniLabel)
    {
        wordWrap = true
    });

    // ─────────────────────────────────────────────────────────
    //  メニュー登録
    // ─────────────────────────────────────────────────────────

    [MenuItem("Tools/Cocoa's Toolbox/Hair Mesh Color Changer (Experimental)")]
    public static void Open()
    {
        var win = GetWindow<HairMeshColorChanger>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 640f);
        win.Show();
        if (win._targetRootOrGo == null && Selection.activeGameObject != null && Selection.activeGameObject.scene.IsValid())
        {
            win.SetTargetFromGameObject(Selection.activeGameObject);
        }
    }

    [MenuItem("GameObject/Cocoa's Toolbox/Hair Mesh Color Changer (Experimental)", false, 24)]
    private static void OpenFromHierarchyContext(MenuCommand command)
    {
        GameObject go = command.context as GameObject ?? Selection.activeGameObject;
        var win = GetWindow<HairMeshColorChanger>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 640f);
        win.Show();
        win.Focus();
        if (go != null)
        {
            win.SetTargetFromGameObject(go);
        }
    }

    [MenuItem("CONTEXT/SkinnedMeshRenderer/Hair Mesh Color Changer (Experimental)")]
    private static void OpenFromSMRContext(MenuCommand command)
    {
        var smr = command.context as SkinnedMeshRenderer;
        var win = GetWindow<HairMeshColorChanger>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 640f);
        win.Show();
        win.Focus();
        if (smr != null)
        {
            win.SetDirectRenderer(smr);
        }
    }

    [MenuItem("CONTEXT/MeshRenderer/Hair Mesh Color Changer (Experimental)")]
    private static void OpenFromMRContext(MenuCommand command)
    {
        var mr = command.context as MeshRenderer;
        var win = GetWindow<HairMeshColorChanger>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(440f, 640f);
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
        minSize = new Vector2(440f, 640f);
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
        RestoreScenePreview();
        DestroyPreviewTextures();
    }

    private void OnDestroy()
    {
        RestoreScenePreview();
        DestroyPreviewTextures();
    }

    private void OnBeforeAssemblyReload()
    {
        RestoreScenePreview();
        DestroyPreviewTextures();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
        {
            RestoreScenePreview();
        }
    }

    private void OnUndoRedo()
    {
        SyncHashSetFromSerializedList();
        _maskDirty = true;
        _colorDirty = true;
        UpdatePreviewAndScene();
        Repaint();
        SceneView.RepaintAll();
    }

    // ─────────────────────────────────────────────────────────
    //  ターゲット設定・コンテキスト構築
    // ─────────────────────────────────────────────────────────

    public void SetTargetFromGameObject(GameObject go)
    {
        RestoreScenePreview();
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
        RestoreScenePreview();
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

        // "Hair" や "髪" 等を含むオブジェクトを最優先で自動選択
        string[] hairKeywords = { "hair", "髪", "front", "back", "bang", "side", "twin", "tail", "ahoge", "kemomimi" };
        Component best = _candidateRenderers.FirstOrDefault(c =>
            hairKeywords.Any(k => c.gameObject.name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0));

        _activeRenderer = best ?? _candidateRenderers[0];
    }

    private void RebuildContext(bool keepSelection)
    {
        RestoreScenePreview();

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

        // サブメッシュ（マテリアルスロット）一覧を構築
        BuildSubMeshLabels();

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

        LoadSourceTextureForActiveSlot();
        _maskDirty = true;
        _colorDirty = true;
        UpdatePreviewAndScene();

        SetStatus(
            $"「{_ctx.TargetGameObject.name}」を読み込みました（全 {_ctx.Islands.Count} 房 / {_ctx.Triangles.Length:N0} tris）。SceneView 上で髪の毛をクリックして選択してください。",
            MessageType.Info);
    }

    private void BuildSubMeshLabels()
    {
        if (_ctx == null)
        {
            _subMeshLabels = Array.Empty<string>();
            return;
        }

        Material[] mats = GetRendererSharedMaterials(_ctx.TargetRenderer);
        int count = _ctx.SubMeshCount;
        _subMeshLabels = new string[count];

        for (int i = 0; i < count; i++)
        {
            Material m = (mats != null && i < mats.Length) ? mats[i] : null;
            string matName = m != null ? m.name : "Materialなし";
            _subMeshLabels[i] = $"Slot {i}: {matName}";
        }

        if (_selectedSubMeshIndex < 0 || _selectedSubMeshIndex >= count)
            _selectedSubMeshIndex = 0;
    }

    private void LoadSourceTextureForActiveSlot()
    {
        _cachedSourceTex = null;
        _cachedMainTexProperty = "_MainTex";

        if (_ctx == null) return;

        Material mat = GetActiveTargetMaterial();
        if (mat != null)
        {
            _cachedSourceTex = HairTextureProcessor.GetMainTextureFromMaterial(mat, out _cachedMainTexProperty);
        }

        int srcW = _cachedSourceTex != null ? _cachedSourceTex.width : 512;
        int srcH = _cachedSourceTex != null ? _cachedSourceTex.height : 512;

        float aspect = (float)srcW / Mathf.Max(1, srcH);
        if (srcW >= srcH)
        {
            _previewWidth = Mathf.Min(PREVIEW_MAX_RES, Mathf.Max(128, srcW));
            _previewHeight = Mathf.Clamp(Mathf.RoundToInt(_previewWidth / aspect), 128, PREVIEW_MAX_RES);
        }
        else
        {
            _previewHeight = Mathf.Min(PREVIEW_MAX_RES, Mathf.Max(128, srcH));
            _previewWidth = Mathf.Clamp(Mathf.RoundToInt(_previewHeight * aspect), 128, PREVIEW_MAX_RES);
        }

        _previewSrcPixels = HairTextureProcessor.ReadTexturePixelsSRGB(_cachedSourceTex, _previewWidth, _previewHeight);
        _previewDstPixels = new Color32[_previewWidth * _previewHeight];
        Array.Copy(_previewSrcPixels, _previewDstPixels, _previewSrcPixels.Length);

        _maskDirty = true;
        _colorDirty = true;
    }

    private Material GetActiveTargetMaterial()
    {
        if (_ctx == null || _ctx.TargetRenderer == null) return null;
        Material[] mats = _isScenePreviewApplied && _originalSharedMaterials != null
            ? _originalSharedMaterials
            : GetRendererSharedMaterials(_ctx.TargetRenderer);

        if (mats == null || mats.Length == 0) return null;
        int idx = Mathf.Clamp(_selectedSubMeshIndex, 0, mats.Length - 1);
        return mats[idx];
    }

    private static Material[] GetRendererSharedMaterials(Component r)
    {
        if (r is SkinnedMeshRenderer smr) return smr.sharedMaterials;
        if (r is MeshRenderer mr) return mr.sharedMaterials;
        return Array.Empty<Material>();
    }

    private static void SetRendererSharedMaterials(Component r, Material[] mats)
    {
        if (r is SkinnedMeshRenderer smr) smr.sharedMaterials = mats;
        else if (r is MeshRenderer mr) mr.sharedMaterials = mats;
    }

    // ─────────────────────────────────────────────────────────
    //  選択集合の同期と操作
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

    private void RecordSelectionChange(string undoLabel)
    {
        Undo.RecordObject(this, undoLabel);
        SyncSerializedListFromHashSet();
        _maskDirty = true;
        _colorDirty = true;
        UpdatePreviewAndScene();
        Repaint();
        SceneView.RepaintAll();
    }

    private void ApplyIslandSelectionAction(int islandId, bool addMode, bool singleSelect)
    {
        if (_ctx == null || islandId < 0 || islandId >= _ctx.Islands.Count) return;

        bool changed = false;
        if (singleSelect)
        {
            if (_selectedIslands.Count != 1 || !_selectedIslands.Contains(islandId))
            {
                Undo.RecordObject(this, "Select Hair Island");
                _selectedIslands.Clear();
                _selectedIslands.Add(islandId);
                changed = true;
            }
        }
        else if (addMode)
        {
            if (!_selectedIslands.Contains(islandId))
            {
                Undo.RecordObject(this, "Add Hair Island Selection");
                _selectedIslands.Add(islandId);
                changed = true;
            }
        }
        else
        {
            if (_selectedIslands.Contains(islandId))
            {
                Undo.RecordObject(this, "Remove Hair Island Selection");
                _selectedIslands.Remove(islandId);
                changed = true;
            }
        }

        if (changed)
        {
            SyncSerializedListFromHashSet();
            _maskDirty = true;
            _colorDirty = true;
            UpdatePreviewAndScene();
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
        _maskDirty = true;
        _colorDirty = true;
        UpdatePreviewAndScene();
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
        _maskDirty = true;
        _colorDirty = true;
        UpdatePreviewAndScene();
    }

    private void ClearIslandSelection()
    {
        if (_selectedIslands.Count == 0) return;
        Undo.RecordObject(this, "Clear Hair Island Selection");
        _selectedIslands.Clear();
        SyncSerializedListFromHashSet();
        _maskDirty = true;
        _colorDirty = true;
        UpdatePreviewAndScene();
    }

    private int GetEffectiveSubMeshFilter()
    {
        if (_ctx == null || _ctx.SubMeshCount <= 1 || !_filterBySubMesh) return -1;
        return _selectedSubMeshIndex;
    }

    // ─────────────────────────────────────────────────────────
    //  リアルタイムプレビュー計算 & SceneView 反映
    // ─────────────────────────────────────────────────────────

    private void UpdatePreviewAndScene()
    {
        if (_ctx == null || !_ctx.IsValid) return;

        int subFilter = GetEffectiveSubMeshFilter();

        if (_maskDirty || _previewMask == null || _previewMask.Length != _previewWidth * _previewHeight)
        {
            _previewMask = HairTextureProcessor.RasterizeUVMask(
                _ctx,
                _selectedIslands,
                subFilter,
                _previewWidth,
                _previewHeight,
                Mathf.RoundToInt(_uvPaddingPx * ((float)_previewWidth / Mathf.Max(512, _cachedSourceTex != null ? _cachedSourceTex.width : 512))),
                Mathf.RoundToInt(_uvFeatherPx * ((float)_previewWidth / Mathf.Max(512, _cachedSourceTex != null ? _cachedSourceTex.width : 512))),
                _protectUnselectedUV);

            _overlappingUnselectedCount = _ctx.CountOverlappingUnselectedIslands(_selectedIslands, subFilter);
            UpdatePreviewMaskTexture();
            _maskDirty = false;
            _colorDirty = true;
        }

        if (_colorDirty)
        {
            if (_previewSrcPixels == null || _previewSrcPixels.Length != _previewWidth * _previewHeight)
            {
                LoadSourceTextureForActiveSlot();
            }

            // サブメッシュ分離プレビュー時は選択された房全体に色変更が乗るため、全領域またはマスク領域に色変更を適用
            float[] effectiveMask = (_previewSplitSubMeshForOverlap || _colorApplyMode == ColorApplyMode.SplitSubMeshAndMaterial)
                ? null // サブメッシュ分離時は新マテリアル全体（またはマスク）に適用
                : _previewMask;

            HairTextureProcessor.ApplyColorAdjustments(
                _previewSrcPixels,
                effectiveMask != null ? effectiveMask : _previewMask,
                _previewDstPixels,
                _hueShift,
                _saturationDelta,
                _brightnessDelta,
                _contrastDelta,
                _useTint,
                _tintColor,
                _tintStrength);

            if (_previewColorTex == null || _previewColorTex.width != _previewWidth || _previewColorTex.height != _previewHeight)
            {
                if (_previewColorTex != null) DestroyImmediate(_previewColorTex);
                _previewColorTex = new Texture2D(_previewWidth, _previewHeight, TextureFormat.RGBA32, false, false)
                {
                    name = "__HairColorPreviewTex__",
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            _previewColorTex.SetPixels32(_previewDstPixels);
            _previewColorTex.Apply(false, false);
            _colorDirty = false;
        }

        if (_liveScenePreview && _selectedIslands.Count > 0 && HasAnyColorAdjustment())
        {
            ApplyScenePreview();
        }
        else
        {
            RestoreScenePreview();
        }
    }

    private void UpdatePreviewMaskTexture()
    {
        if (_previewMask == null) return;
        if (_previewMaskTex == null || _previewMaskTex.width != _previewWidth || _previewMaskTex.height != _previewHeight)
        {
            if (_previewMaskTex != null) DestroyImmediate(_previewMaskTex);
            _previewMaskTex = new Texture2D(_previewWidth, _previewHeight, TextureFormat.RGBA32, false, false)
            {
                name = "__HairMaskPreviewTex__",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        Color32[] maskPixels = HairTextureProcessor.BuildMaskPixels(_previewMask, _invertMask);
        _previewMaskTex.SetPixels32(maskPixels);
        _previewMaskTex.Apply(false, false);
    }

    private bool HasAnyColorAdjustment()
    {
        return !Mathf.Approximately(_hueShift, 0f) ||
               !Mathf.Approximately(_saturationDelta, 0f) ||
               !Mathf.Approximately(_brightnessDelta, 0f) ||
               !Mathf.Approximately(_contrastDelta, 0f) ||
               (_useTint && _tintStrength > 0.001f);
    }

    private void ApplyScenePreview()
    {
        if (_ctx == null || _ctx.TargetRenderer == null || _previewColorTex == null) return;

        // 別のRendererにプレビューが当たっていたら先に復元
        if (_isScenePreviewApplied && _previewAppliedRenderer != _ctx.TargetRenderer)
        {
            RestoreScenePreview();
        }

        if (!_isScenePreviewApplied)
        {
            _previewAppliedRenderer = _ctx.TargetRenderer;
            _originalSharedMaterials = GetRendererSharedMaterials(_previewAppliedRenderer).ToArray();
            _originalSharedMesh = _ctx.SourceMesh;
            _isScenePreviewApplied = true;
        }

        DestroyTempPreviewAssetsOnly();

        bool useSplitSubMesh = (_colorApplyMode == ColorApplyMode.SplitSubMeshAndMaterial) || _previewSplitSubMeshForOverlap;
        int targetSlot = Mathf.Clamp(_selectedSubMeshIndex, 0, Mathf.Max(0, _originalSharedMaterials.Length - 1));
        Material baseMat = (_originalSharedMaterials.Length > targetSlot) ? _originalSharedMaterials[targetSlot] : null;

        if (baseMat == null) return;

        Material previewMat = new Material(baseMat)
        {
            name = baseMat.name + " (ColorPreview)",
            hideFlags = HideFlags.HideAndDontSave
        };
        AssignTextureToMaterial(previewMat, _previewColorTex, _cachedMainTexProperty);

        if (useSplitSubMesh)
        {
            // 選択されたアイランドだけを新規サブメッシュスロットへ分離した一時メッシュを構築（UV重複していても選んだ房だけ色が変わる！）
            _tempPreviewSplitMesh = BuildSubMeshSplitMesh(_originalSharedMesh, _ctx, _selectedIslands, targetSlot);
            _tempPreviewSplitMesh.hideFlags = HideFlags.HideAndDontSave;

            var newMats = new Material[_originalSharedMaterials.Length + 1];
            Array.Copy(_originalSharedMaterials, newMats, _originalSharedMaterials.Length);
            newMats[newMats.Length - 1] = previewMat;
            _tempPreviewMaterials = new[] { previewMat };

            if (_ctx.SMR != null) _ctx.SMR.sharedMesh = _tempPreviewSplitMesh;
            else if (_ctx.MF != null) _ctx.MF.sharedMesh = _tempPreviewSplitMesh;
            SetRendererSharedMaterials(_previewAppliedRenderer, newMats);
        }
        else
        {
            // 元メッシュのまま、対象スロット（または全スロット）にプレビューマテリアルを割り当て
            if (_ctx.SMR != null && _ctx.SMR.sharedMesh != _originalSharedMesh) _ctx.SMR.sharedMesh = _originalSharedMesh;
            else if (_ctx.MF != null && _ctx.MF.sharedMesh != _originalSharedMesh) _ctx.MF.sharedMesh = _originalSharedMesh;

            var newMats = _originalSharedMaterials.ToArray();
            if (!_filterBySubMesh)
            {
                for (int i = 0; i < newMats.Length; i++)
                {
                    if (newMats[i] == baseMat) newMats[i] = previewMat;
                }
            }
            else if (targetSlot < newMats.Length)
            {
                newMats[targetSlot] = previewMat;
            }

            _tempPreviewMaterials = new[] { previewMat };
            SetRendererSharedMaterials(_previewAppliedRenderer, newMats);
        }

        SceneView.RepaintAll();
    }

    private void RestoreScenePreview()
    {
        if (_isScenePreviewApplied)
        {
            if (_previewAppliedRenderer != null)
            {
                if (_originalSharedMaterials != null)
                {
                    SetRendererSharedMaterials(_previewAppliedRenderer, _originalSharedMaterials);
                }
                if (_originalSharedMesh != null)
                {
                    if (_previewAppliedRenderer is SkinnedMeshRenderer smr) smr.sharedMesh = _originalSharedMesh;
                    else if (_previewAppliedRenderer is MeshRenderer mr)
                    {
                        var mf = mr.GetComponent<MeshFilter>();
                        if (mf != null) mf.sharedMesh = _originalSharedMesh;
                    }
                }
            }
            _isScenePreviewApplied = false;
            _previewAppliedRenderer = null;
            _originalSharedMaterials = null;
            _originalSharedMesh = null;
        }

        DestroyTempPreviewAssetsOnly();
        SceneView.RepaintAll();
    }

    private void DestroyTempPreviewAssetsOnly()
    {
        if (_tempPreviewMaterials != null)
        {
            foreach (var m in _tempPreviewMaterials)
            {
                if (m != null) DestroyImmediate(m);
            }
            _tempPreviewMaterials = null;
        }
        if (_tempPreviewSplitMesh != null)
        {
            DestroyImmediate(_tempPreviewSplitMesh);
            _tempPreviewSplitMesh = null;
        }
    }

    private void DestroyPreviewTextures()
    {
        if (_previewColorTex != null)
        {
            DestroyImmediate(_previewColorTex);
            _previewColorTex = null;
        }
        if (_previewMaskTex != null)
        {
            DestroyImmediate(_previewMaskTex);
            _previewMaskTex = null;
        }
    }

    private static void AssignTextureToMaterial(Material mat, Texture tex, string primaryProp)
    {
        if (mat == null) return;
        if (!string.IsNullOrEmpty(primaryProp) && mat.HasProperty(primaryProp))
        {
            mat.SetTexture(primaryProp, tex);
        }
        if (mat.HasProperty("_MainTex"))
        {
            mat.SetTexture("_MainTex", tex);
        }
        if (mat.HasProperty("_BaseMap"))
        {
            mat.SetTexture("_BaseMap", tex);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  SceneView インタラクション（クリック選択 & ハイライト描画）
    // ─────────────────────────────────────────────────────────

    private void OnSceneGUI(SceneView sceneView)
    {
        if (_ctx == null || !_ctx.IsValid) return;

        Event e = Event.current;
        int subFilter = GetEffectiveSubMeshFilter();

        // 1. 選択オーバーレイ描画
        if (e.type == EventType.Repaint && _sceneHighlightMode != SceneHighlightMode.None)
        {
            bool drawFill = _sceneHighlightMode == SceneHighlightMode.FillAndWire;
            bool drawWire = _sceneHighlightMode == SceneHighlightMode.WireOnly || _sceneHighlightMode == SceneHighlightMode.FillAndWire;

            HairSceneOverlayRenderer.DrawSceneSelectionOverlay(
                _ctx,
                _selectedIslands,
                _scenePickActive ? _hoveredIslandId : -1,
                drawFill,
                drawWire,
                new Color(0.15f, 0.85f, 1.0f, 0.25f), // Selected Fill
                new Color(0.15f, 0.90f, 1.0f, 0.85f), // Selected Wire
                new Color(1.0f, 0.90f, 0.15f, 0.38f), // Hover Fill
                new Color(1.0f, 0.95f, 0.20f, 1.0f)); // Hover Wire
        }

        if (!_scenePickActive) return;

        // 2. SceneView 左上 HUD 表示
        HairSceneOverlayRenderer.DrawSceneHUD(
            "Hair Mesh Color Changer (Experimental)",
            _selectedIslands.Count,
            _ctx.Islands.Count,
            _hoveredIslandId,
            () => { ClearIslandSelection(); Repaint(); },
            () => { _scenePickActive = false; _hoveredIslandId = -1; Repaint(); SceneView.RepaintAll(); });

        // カメラ操作（Alt押下、右クリック、中クリック）は一切妨げない
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
            if (_ctx.RaycastIsland(ray, _raycastBothSides, subFilter, null, out int hitId, out _, out _))
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
            if (_ctx.RaycastIsland(ray, _raycastBothSides, subFilter, null, out int hitId, out _, out _))
            {
                _hoveredIslandId = hitId;
                bool isAlreadySelected = _selectedIslands.Contains(hitId);

                if (_clickBehavior == HairClickBehavior.SingleOrShift && !e.shift && !e.control)
                {
                    _isSceneDragging = false;
                    ApplyIslandSelectionAction(hitId, addMode: true, singleSelect: true);
                }
                else
                {
                    _sceneDragAddMode = e.control ? false : (e.shift ? true : !isAlreadySelected);
                    _isSceneDragging = true;
                    ApplyIslandSelectionAction(hitId, _sceneDragAddMode, singleSelect: false);
                }
                e.Use();
            }
        }
        else if (e.type == EventType.MouseDrag && e.button == 0 && _isSceneDragging)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (_ctx.RaycastIsland(ray, _raycastBothSides, subFilter, null, out int hitId, out _, out _))
            {
                _hoveredIslandId = hitId;
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

            DrawColorSlidersSection();
            EditorGUILayout.Space(6f);

            DrawExportAndApplySection();
        }

        if (_outputs != null && _outputs.Count > 0)
        {
            EditorGUILayout.Space(6f);
            DrawOutputRecordsSection();
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
        EditorGUILayout.LabelField("Hair Mesh Color Changer (Experimental)", HeaderTitleStyle);
        EditorGUILayout.LabelField("Cocoa's Toolbox — 髪の房を Lキー風クリック選択 ＆ 4スライダー色調整・lilToonマスク生成", EditorStyles.miniLabel);
        var lineRect = GUILayoutUtility.GetRect(0f, 1f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(lineRect, EditorStyles.label.normal.textColor * 0.3f);
    }

    private void DrawTargetSection()
    {
        EditorGUILayout.LabelField("① 対象メッシュ・マテリアル", EditorStyles.boldLabel);

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
                    _selectedSubMeshIndex = EditorGUILayout.Popup("対象マテリアルスロット", _selectedSubMeshIndex, _subMeshLabels);
                    _filterBySubMesh = EditorGUILayout.ToggleLeft("このスロットのみ選択", _filterBySubMesh, GUILayout.Width(130f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        LoadSourceTextureForActiveSlot();
                        _maskDirty = true;
                        _colorDirty = true;
                        UpdatePreviewAndScene();
                    }
                }
            }
        }
    }

    private void DrawSceneSelectionSection()
    {
        EditorGUILayout.LabelField("② SceneView クリック選択（Blender Lキー風リンク選択）", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // SceneView 選択モード ON/OFF 大ボタン
            Color prevBg = GUI.backgroundColor;
            if (_scenePickActive)
                GUI.backgroundColor = new Color(0.35f, 0.85f, 1.0f, 1f);

            string pickBtnLabel = _scenePickActive
                ? "🎯 SceneView クリック選択モード：ON （髪の房をクリックして選択・解除）"
                : "🎯 SceneView クリック選択モードを開始する（クリックで ON）";

            if (GUILayout.Button(pickBtnLabel, _scenePickActive ? PickActiveBtnStyle : PickIdleBtnStyle))
            {
                _scenePickActive = !_scenePickActive;
                if (!_scenePickActive) _hoveredIslandId = -1;
                SceneView.RepaintAll();
            }
            GUI.backgroundColor = prevBg;

            EditorGUILayout.Space(2f);

            // 連結モード（Lキーの区切り基準）
            EditorGUI.BeginChangeCheck();
            _linkMode = (HairLinkMode)EditorGUILayout.EnumPopup(
                new GUIContent("房の連結基準 (Lキー挙動)",
                    "Spatial3D: シームやハードエッジを越えて3D形状として繋がっている髪の房1本を丸ごと選択します（推奨）\n" +
                    "TopologyOnly: 頂点インデックスを共有する面のみ連結（シームで区切る）\n" +
                    "UVIsland: テクスチャのUV上で繋がっている塊を選択\n" +
                    "SingleTriangle: 1ポリゴン単位で選択"),
                _linkMode);
            if (EditorGUI.EndChangeCheck() && _ctx != null)
            {
                RebuildContext(keepSelection: false);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _clickBehavior = (HairClickBehavior)EditorGUILayout.Popup(
                    "クリック動作",
                    (int)_clickBehavior,
                    new[] { "クリックで追加/解除 (トグル・なぞり対応)", "通常クリックで単一選択 (Shiftで追加)" });

                _sceneHighlightMode = (SceneHighlightMode)EditorGUILayout.Popup(
                    (int)_sceneHighlightMode,
                    new[] { "表示: ワイヤーのみ", "表示: 塗り+ワイヤー", "表示: ホバーのみ", "表示: なし" },
                    GUILayout.Width(130f));
            }

            // 一括選択操作ボタン
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全選択", EditorStyles.miniButtonLeft))
                    SelectAllIslands();
                if (GUILayout.Button("選択を反転", EditorStyles.miniButtonMid))
                    InvertIslandSelection();
                if (GUILayout.Button("選択を全解除", EditorStyles.miniButtonMid))
                    ClearIslandSelection();
                if (GUILayout.Button("ポーズ再取得", EditorStyles.miniButtonRight))
                {
                    _ctx?.UpdateWorldGeometry();
                    SceneView.RepaintAll();
                }
            }

            // 選択統計
            int totalIslands = _ctx != null ? _ctx.Islands.Count : 0;
            int selectedTris = 0;
            if (_ctx != null)
            {
                foreach (int id in _selectedIslands)
                {
                    if (id >= 0 && id < _ctx.Islands.Count)
                        selectedTris += _ctx.Islands[id].GlobalTriangles.Length;
                }
            }

            EditorGUILayout.LabelField(
                $"選択中: {_selectedIslands.Count} / {totalIslands} 房 （{selectedTris:N0} tris）",
                EditorStyles.miniBoldLabel);

            if (_overlappingUnselectedCount > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox(
                        $"選択中の房と、選択していない他の {_overlappingUnselectedCount} 個の房がテクスチャ上の同じUV位置を共有（ミラーUV等）しています。\n" +
                        "選んだ房だけ色を変えたい場合は下の「UV重複対策：選んだ房だけ別マテリアルに分離」をONにしてください。",
                        MessageType.Info);
                }

                EditorGUI.BeginChangeCheck();
                _previewSplitSubMeshForOverlap = EditorGUILayout.ToggleLeft(
                    "UV重複対策：選んだ房だけ別マテリアルスロットに分離して色変更する（他の房への色移りを完全防止）",
                    _previewSplitSubMeshForOverlap);
                if (EditorGUI.EndChangeCheck())
                {
                    if (_previewSplitSubMeshForOverlap)
                        _colorApplyMode = ColorApplyMode.SplitSubMeshAndMaterial;
                    _colorDirty = true;
                    UpdatePreviewAndScene();
                }
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
            // 表示モード切替
            EditorGUI.BeginChangeCheck();
            _viewerDisplayMode = (ViewerDisplayMode)GUILayout.Toolbar(
                (int)_viewerDisplayMode,
                new[] { "色変更プレビュー + UV", "lilToonマスク (白黒) + UV", "元テクスチャ + UV" });
            if (EditorGUI.EndChangeCheck())
            {
                Repaint();
            }

            // ビュワー上部ツールバー
            using (new EditorGUILayout.HorizontalScope())
            {
                _uvViewer.ShowAllUVWire = GUILayout.Toggle(_uvViewer.ShowAllUVWire, "全UV線", EditorStyles.miniButtonLeft, GUILayout.Width(58f));
                _uvViewer.ShowSelectedFill = GUILayout.Toggle(_uvViewer.ShowSelectedFill, "選択塗り", EditorStyles.miniButtonMid, GUILayout.Width(62f));
                _uvViewer.ShowSelectedWire = GUILayout.Toggle(_uvViewer.ShowSelectedWire, "選択枠", EditorStyles.miniButtonRight, GUILayout.Width(58f));

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

            // ビュワー本体キャンバス
            Texture displayTex = _cachedSourceTex;
            if (_viewerDisplayMode == ViewerDisplayMode.ColorPreview && _previewColorTex != null)
                displayTex = _previewColorTex;
            else if (_viewerDisplayMode == ViewerDisplayMode.MaskPreview && _previewMaskTex != null)
                displayTex = _previewMaskTex;

            Rect canvasRect = GUILayoutUtility.GetRect(100f, _viewerHeight, GUILayout.ExpandWidth(true));
            int subFilter = GetEffectiveSubMeshFilter();

            _uvViewer.DrawViewer(
                canvasRect,
                displayTex,
                _ctx,
                subFilter,
                _selectedIslands,
                _hoveredIslandId,
                new Color(0.10f, 0.90f, 1.0f, 1f), // 選択UVハイライト色（シアン）
                new Color(1.0f, 0.92f, 0.15f, 1f), // ホバーUVハイライト色（イエロー）
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
                "💡 SceneView でのクリックとリアルタイム連動します。ビュワー上のUVを直接クリックしても選択/解除できます（ホイール:拡大縮小 / 右・中ドラッグ:移動）。",
                WrapMiniStyle);
        }
    }

    private void DrawColorSlidersSection()
    {
        EditorGUILayout.LabelField("④ 基本的な4つの色彩調整スライダー", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _liveScenePreview = EditorGUILayout.ToggleLeft(
                    "SceneView のモデルにリアルタイムで色変更をプレビュー反映する",
                    _liveScenePreview,
                    EditorStyles.boldLabel);
                if (EditorGUI.EndChangeCheck())
                {
                    UpdatePreviewAndScene();
                }

                if (GUILayout.Button("全リセット", EditorStyles.miniButton, GUILayout.Width(72f)))
                {
                    Undo.RecordObject(this, "Reset Color Sliders");
                    _hueShift = 0f;
                    _saturationDelta = 0f;
                    _brightnessDelta = 0f;
                    _contrastDelta = 0f;
                    _useTint = false;
                    _colorDirty = true;
                    UpdatePreviewAndScene();
                }
            }

            EditorGUILayout.Space(2f);

            EditorGUI.BeginChangeCheck();

            // 1. 色相 (Hue)
            DrawSliderWithReset("① 色相 (Hue)", ref _hueShift, -180f, 180f, 0f, "°");

            // 2. 彩度 (Saturation)
            DrawSliderWithReset("② 彩度 (Saturation)", ref _saturationDelta, -100f, 100f, 0f, "%");

            // 3. 明度 (Brightness / Value)
            DrawSliderWithReset("③ 明度 (Brightness)", ref _brightnessDelta, -100f, 100f, 0f, "%");

            // 4. コントラスト (Contrast)
            DrawSliderWithReset("④ コントラスト (Contrast)", ref _contrastDelta, -100f, 100f, 0f, "%");

            // 補助オプション：色乗せ（黒髪・白髪のカラライズ用）
            _foldTintOption = EditorGUILayout.Foldout(_foldTintOption, "補助オプション：色乗せ（黒髪・白髪に色を付ける場合）", true);
            if (_foldTintOption)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _useTint = EditorGUILayout.ToggleLeft("色乗せ (Tint) を有効化", _useTint, GUILayout.Width(145f));
                        using (new EditorGUI.DisabledScope(!_useTint))
                        {
                            _tintColor = EditorGUILayout.ColorField(_tintColor, GUILayout.Width(70f));
                            _tintStrength = EditorGUILayout.Slider(_tintStrength, 0f, 1f);
                        }
                    }
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(this, "Adjust Hair Color Sliders");
                _colorDirty = true;
                UpdatePreviewAndScene();
            }

            EditorGUILayout.Space(4f);

            // UVマスク境界（パディング・ぼかし）設定
            EditorGUILayout.LabelField("UVマスク境界設定（シームにじみ防止）", EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                _uvPaddingPx = EditorGUILayout.IntSlider("UVパディング (px)", _uvPaddingPx, 0, 32);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _uvFeatherPx = EditorGUILayout.IntSlider("境界ぼかし (px)", _uvFeatherPx, 0, 16);
                _protectUnselectedUV = EditorGUILayout.ToggleLeft("他のUV島への色漏れ防止", _protectUnselectedUV, GUILayout.Width(155f));
            }
            if (EditorGUI.EndChangeCheck())
            {
                _maskDirty = true;
                _colorDirty = true;
                UpdatePreviewAndScene();
            }
        }
    }

    private void DrawSliderWithReset(string label, ref float val, float min, float max, float defaultVal, string unit)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            val = EditorGUILayout.Slider(label, val, min, max);
            GUILayout.Label(unit, EditorStyles.miniLabel, GUILayout.Width(14f));
            using (new EditorGUI.DisabledScope(Mathf.Approximately(val, defaultVal)))
            {
                if (GUILayout.Button("0", EditorStyles.miniButton, GUILayout.Width(24f)))
                {
                    val = defaultVal;
                    GUI.FocusControl(null);
                }
            }
        }
    }

    private void DrawExportAndApplySection()
    {
        EditorGUILayout.LabelField("⑤ 保存・出力（色変更テクスチャ ／ lilToon 用マスクテクスチャ）", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
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

            // ── A. 色変更テクスチャ保存＆マテリアル適用 ──
            EditorGUILayout.LabelField("【A】選択部分の色を変えたテクスチャ (.png) を保存・適用", EditorStyles.miniBoldLabel);

            _colorApplyMode = (ColorApplyMode)EditorGUILayout.Popup(
                "適用方法",
                (int)_colorApplyMode,
                new[]
                {
                    "マテリアルを複製して非破壊で適用（推奨）",
                    "選んだ房だけ別マテリアルスロットに分離して適用（.mesh生成・UV重複対策）",
                    "現在のマテリアルのテクスチャを直接差し替え",
                    "色変更テクスチャ (.png) の保存のみ"
                });

            using (new EditorGUI.DisabledScope(_selectedIslands.Count == 0))
            {
                if (GUILayout.Button("✦  色変更テクスチャ (.png) を保存して適用  ✦", ExecuteBtnStyle))
                {
                    ExecuteSaveColorChangedTextureAndApply();
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUILayout.Space(6f);
            var sepRect = GUILayoutUtility.GetRect(0f, 1f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(sepRect, new Color(0.4f, 0.4f, 0.4f, 0.4f));
            EditorGUILayout.Space(4f);

            // ── B. lilToon 用マスクテクスチャ作成・保存 ──
            EditorGUILayout.LabelField("【B】lilToon 用マスクテクスチャ (.png) を作成・保存", EditorStyles.miniBoldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _maskResolutionMode = (MaskResolutionMode)EditorGUILayout.EnumPopup(
                    "マスク解像度", _maskResolutionMode);

                EditorGUI.BeginChangeCheck();
                _invertMask = EditorGUILayout.ToggleLeft("白黒を反転", _invertMask, GUILayout.Width(90f));
                if (EditorGUI.EndChangeCheck())
                {
                    UpdatePreviewMaskTexture();
                }
            }

            _autoAssignLilToon2ndMask = EditorGUILayout.ToggleLeft(
                "保存後に lilToon のメインカラー 2nd / 3rd マスク (_Main2ndBlendMask) へ自動設定する",
                _autoAssignLilToon2ndMask);

            if (_autoAssignLilToon2ndMask)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    _duplicateMatOnLilToonAssign = EditorGUILayout.ToggleLeft(
                        "元のマテリアルを汚さないようマテリアルを複製して設定する（推奨）",
                        _duplicateMatOnLilToonAssign);
                }
            }

            using (new EditorGUI.DisabledScope(_selectedIslands.Count == 0))
            {
                if (GUILayout.Button("✦  lilToon 用マスクテクスチャ (.png) を保存  ✦", MaskBtnStyle))
                {
                    ExecuteSaveLilToonMaskTexture();
                    GUIUtility.ExitGUI();
                }
            }
        }
    }

    private void DrawOutputRecordsSection()
    {
        _foldOutputs = EditorGUILayout.Foldout(_foldOutputs, $"出力履歴 ({_outputs.Count} 件)", true);
        if (!_foldOutputs) return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            foreach (var rec in _outputs)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(rec.Label, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

                    if (rec.MainAsset != null && GUILayout.Button("テクスチャ", EditorStyles.miniButton, GUILayout.Width(68f)))
                    {
                        EditorGUIUtility.PingObject(rec.MainAsset);
                        Selection.activeObject = rec.MainAsset;
                    }

                    if (rec.SecondaryAsset != null && GUILayout.Button("適用先", EditorStyles.miniButton, GUILayout.Width(54f)))
                    {
                        EditorGUIUtility.PingObject(rec.SecondaryAsset);
                        Selection.activeObject = rec.SecondaryAsset;
                    }
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  保存・出力の実行ロジック
    // ─────────────────────────────────────────────────────────

    private string GetEffectiveSaveFolder()
    {
        if (!string.IsNullOrEmpty(_saveFolderPath))
            return _saveFolderPath;

        UnityEngine.Object refAsset = _cachedSourceTex != null ? (UnityEngine.Object)_cachedSourceTex : _ctx?.SourceMesh;
        return HairTextureProcessor.ResolveDefaultSaveFolder(refAsset, DEFAULT_SAVE_DIR);
    }

    private void ExecuteSaveColorChangedTextureAndApply()
    {
        if (_ctx == null || !_ctx.IsValid || _selectedIslands.Count == 0)
        {
            SetStatus("色を変更する髪の房を1つ以上選択してください。", MessageType.Warning);
            return;
        }

        // プレビュー状態を一旦解除して正確な元状態に対して処理する
        RestoreScenePreview();

        try
        {
            Material origMat = GetActiveTargetMaterial();
            Texture srcTex = _cachedSourceTex;
            int fullW = srcTex != null ? srcTex.width : 1024;
            int fullH = srcTex != null ? srcTex.height : 1024;

            Color32[] fullSrcPixels = HairTextureProcessor.ReadTexturePixelsSRGB(srcTex, fullW, fullH);
            Color32[] fullDstPixels = new Color32[fullW * fullH];

            int subFilter = GetEffectiveSubMeshFilter();
            float[] fullMask = HairTextureProcessor.RasterizeUVMask(
                _ctx,
                _selectedIslands,
                subFilter,
                fullW,
                fullH,
                _uvPaddingPx,
                _uvFeatherPx,
                _protectUnselectedUV);

            HairTextureProcessor.ApplyColorAdjustments(
                fullSrcPixels,
                fullMask,
                fullDstPixels,
                _hueShift,
                _saturationDelta,
                _brightnessDelta,
                _contrastDelta,
                _useTint,
                _tintColor,
                _tintStrength);

            var outTex = new Texture2D(fullW, fullH, TextureFormat.RGBA32, false, false);
            byte[] pngBytes;
            try
            {
                outTex.SetPixels32(fullDstPixels);
                outTex.Apply(false, false);
                pngBytes = outTex.EncodeToPNG();
            }
            finally
            {
                DestroyImmediate(outTex);
            }

            string folder = GetEffectiveSaveFolder();
            string baseName = srcTex != null ? srcTex.name : (_ctx.SourceMesh != null ? _ctx.SourceMesh.name : _ctx.TargetGameObject.name);
            string texAssetPath = HairTextureProcessor.GenerateUniqueAssetPath(folder, baseName, COLOR_TEX_SUFFIX, ".png");

            File.WriteAllBytes(texAssetPath, pngBytes);
            AssetDatabase.ImportAsset(texAssetPath, ImportAssetOptions.ForceSynchronousImport);

            // 元テクスチャのインポート設定（MaxSize等）を引き継ぐ
            CopyTextureImporterSettingsIfPossible(srcTex, texAssetPath, isLinearMask: false);

            var savedTex = AssetDatabase.LoadAssetAtPath<Texture2D>(texAssetPath);
            UnityEngine.Object secondaryResult = null;
            string secondaryPath = "";

            Undo.SetCurrentGroupName("Apply Hair Color Change");
            int undoGroup = Undo.GetCurrentGroup();

            int targetSlot = Mathf.Clamp(_selectedSubMeshIndex, 0, Mathf.Max(0, _ctx.SubMeshCount - 1));
            Material[] currentMats = GetRendererSharedMaterials(_ctx.TargetRenderer).ToArray();

            if (_colorApplyMode == ColorApplyMode.DuplicateMaterial && origMat != null)
            {
                string matPath = HairTextureProcessor.GenerateUniqueAssetPath(folder, origMat.name, COLOR_TEX_SUFFIX, ".mat");
                var newMat = new Material(origMat);
                AssignTextureToMaterial(newMat, savedTex, _cachedMainTexProperty);
                AssetDatabase.CreateAsset(newMat, matPath);

                Undo.RecordObject(_ctx.TargetRenderer, "Assign Duplicated Hair Material");
                if (!_filterBySubMesh)
                {
                    for (int i = 0; i < currentMats.Length; i++)
                        if (currentMats[i] == origMat) currentMats[i] = newMat;
                }
                else if (targetSlot < currentMats.Length)
                {
                    currentMats[targetSlot] = newMat;
                }
                SetRendererSharedMaterials(_ctx.TargetRenderer, currentMats);

                secondaryResult = newMat;
                secondaryPath = matPath;
            }
            else if (_colorApplyMode == ColorApplyMode.SplitSubMeshAndMaterial && origMat != null)
            {
                // 選択した房だけを新しいサブメッシュスロットに分離した .mesh と複製マテリアルを生成
                string meshPath = HairTextureProcessor.GenerateUniqueAssetPath(folder, _ctx.SourceMesh.name, SPLIT_MESH_SUFFIX, ".mesh");
                Mesh splitMesh = BuildSubMeshSplitMesh(_ctx.SourceMesh, _ctx, _selectedIslands, targetSlot);
                splitMesh.name = Path.GetFileNameWithoutExtension(meshPath);
                AssetDatabase.CreateAsset(splitMesh, meshPath);

                string matPath = HairTextureProcessor.GenerateUniqueAssetPath(folder, origMat.name, COLOR_TEX_SUFFIX, ".mat");
                var newMat = new Material(origMat);
                AssignTextureToMaterial(newMat, savedTex, _cachedMainTexProperty);
                AssetDatabase.CreateAsset(newMat, matPath);

                Undo.RecordObject(_ctx.TargetRenderer, "Assign Split Hair Mesh & Material");
                if (_ctx.SMR != null) _ctx.SMR.sharedMesh = splitMesh;
                else if (_ctx.MF != null)
                {
                    Undo.RecordObject(_ctx.MF, "Assign Split Hair Mesh");
                    _ctx.MF.sharedMesh = splitMesh;
                }

                var expandedMats = new Material[currentMats.Length + 1];
                Array.Copy(currentMats, expandedMats, currentMats.Length);
                expandedMats[expandedMats.Length - 1] = newMat;
                SetRendererSharedMaterials(_ctx.TargetRenderer, expandedMats);

                secondaryResult = splitMesh;
                secondaryPath = meshPath;
            }
            else if (_colorApplyMode == ColorApplyMode.ModifyCurrentMaterial && origMat != null)
            {
                Undo.RecordObject(origMat, "Assign Hair Color Texture");
                AssignTextureToMaterial(origMat, savedTex, _cachedMainTexProperty);
                EditorUtility.SetDirty(origMat);
                secondaryResult = origMat;
            }

            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(undoGroup);

            _outputs.Insert(0, new OutputRecord
            {
                Label = $"[色変更] {Path.GetFileName(texAssetPath)}",
                MainAsset = savedTex,
                AssetPath = texAssetPath,
                SecondaryAsset = secondaryResult,
                SecondaryPath = secondaryPath
            });

            // 適用完了後はスライダーをリセットして新しい状態をベースとして再読み込み
            _hueShift = 0f;
            _saturationDelta = 0f;
            _brightnessDelta = 0f;
            _contrastDelta = 0f;
            _useTint = false;
            RebuildContext(keepSelection: true);

            if (savedTex != null)
                EditorGUIUtility.PingObject(savedTex);

            SetStatus($"色変更テクスチャを保存・適用しました: {texAssetPath}", MessageType.Info);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            SetStatus($"保存中にエラーが発生しました: {ex.Message}", MessageType.Error);
        }
    }

    private void ExecuteSaveLilToonMaskTexture()
    {
        if (_ctx == null || !_ctx.IsValid || _selectedIslands.Count == 0)
        {
            SetStatus("マスクに含める髪の房を1つ以上選択してください。", MessageType.Warning);
            return;
        }

        RestoreScenePreview();

        try
        {
            int maskW = 1024;
            int maskH = 1024;
            if (_maskResolutionMode == MaskResolutionMode.MatchSource)
            {
                maskW = _cachedSourceTex != null ? _cachedSourceTex.width : 1024;
                maskH = _cachedSourceTex != null ? _cachedSourceTex.height : 1024;
            }
            else
            {
                maskW = (int)_maskResolutionMode;
                maskH = (int)_maskResolutionMode;
            }

            int subFilter = GetEffectiveSubMeshFilter();
            float[] maskData = HairTextureProcessor.RasterizeUVMask(
                _ctx,
                _selectedIslands,
                subFilter,
                maskW,
                maskH,
                _uvPaddingPx,
                _uvFeatherPx,
                _protectUnselectedUV);

            Color32[] maskPixels = HairTextureProcessor.BuildMaskPixels(maskData, _invertMask);

            var outMaskTex = new Texture2D(maskW, maskH, TextureFormat.RGBA32, false, true);
            byte[] pngBytes;
            try
            {
                outMaskTex.SetPixels32(maskPixels);
                outMaskTex.Apply(false, false);
                pngBytes = outMaskTex.EncodeToPNG();
            }
            finally
            {
                DestroyImmediate(outMaskTex);
            }

            string folder = GetEffectiveSaveFolder();
            string baseName = _cachedSourceTex != null ? _cachedSourceTex.name : (_ctx.SourceMesh != null ? _ctx.SourceMesh.name : _ctx.TargetGameObject.name);
            string maskAssetPath = HairTextureProcessor.GenerateUniqueAssetPath(folder, baseName, MASK_TEX_SUFFIX, ".png");

            File.WriteAllBytes(maskAssetPath, pngBytes);
            AssetDatabase.ImportAsset(maskAssetPath, ImportAssetOptions.ForceSynchronousImport);

            // マスクテクスチャは sRGB = false でインポートすると lilToon でのブレンド境界が正確になる
            CopyTextureImporterSettingsIfPossible(_cachedSourceTex, maskAssetPath, isLinearMask: true);

            var savedMask = AssetDatabase.LoadAssetAtPath<Texture2D>(maskAssetPath);
            UnityEngine.Object assignedMatObj = null;

            if (_autoAssignLilToon2ndMask)
            {
                Material origMat = GetActiveTargetMaterial();
                if (origMat != null)
                {
                    Material targetMat = origMat;
                    int targetSlot = Mathf.Clamp(_selectedSubMeshIndex, 0, Mathf.Max(0, _ctx.SubMeshCount - 1));

                    if (_duplicateMatOnLilToonAssign)
                    {
                        string matPath = HairTextureProcessor.GenerateUniqueAssetPath(folder, origMat.name, MASK_TEX_SUFFIX, ".mat");
                        targetMat = new Material(origMat);
                        AssetDatabase.CreateAsset(targetMat, matPath);

                        Undo.RecordObject(_ctx.TargetRenderer, "Assign lilToon Masked Material");
                        var mats = GetRendererSharedMaterials(_ctx.TargetRenderer).ToArray();
                        if (targetSlot < mats.Length)
                        {
                            mats[targetSlot] = targetMat;
                            SetRendererSharedMaterials(_ctx.TargetRenderer, mats);
                        }
                    }
                    else
                    {
                        Undo.RecordObject(targetMat, "Set lilToon 2nd Mask");
                    }

                    // lilToon の Main Color 2nd プロパティを設定
                    if (targetMat.HasProperty("_UseMain2ndTex"))
                        targetMat.SetInt("_UseMain2ndTex", 1);
                    if (targetMat.HasProperty("_Main2ndBlendMask"))
                        targetMat.SetTexture("_Main2ndBlendMask", savedMask);
                    if (targetMat.HasProperty("_Main2ndTex") && targetMat.GetTexture("_Main2ndTex") == null && _cachedSourceTex != null)
                        targetMat.SetTexture("_Main2ndTex", _cachedSourceTex);

                    EditorUtility.SetDirty(targetMat);
                    assignedMatObj = targetMat;
                }
            }

            AssetDatabase.SaveAssets();

            _outputs.Insert(0, new OutputRecord
            {
                Label = $"[lilToonマスク] {Path.GetFileName(maskAssetPath)} ({maskW}x{maskH})",
                MainAsset = savedMask,
                AssetPath = maskAssetPath,
                SecondaryAsset = assignedMatObj
            });

            if (savedMask != null)
                EditorGUIUtility.PingObject(savedMask);

            UpdatePreviewAndScene();
            SetStatus($"lilToon 用マスクテクスチャを保存しました: {maskAssetPath}", MessageType.Info);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            SetStatus($"マスク保存中にエラーが発生しました: {ex.Message}", MessageType.Error);
        }
    }

    private static void CopyTextureImporterSettingsIfPossible(Texture referenceTex, string newTexAssetPath, bool isLinearMask)
    {
        var newImporter = AssetImporter.GetAtPath(newTexAssetPath) as TextureImporter;
        if (newImporter == null) return;

        if (referenceTex != null)
        {
            string refPath = AssetDatabase.GetAssetPath(referenceTex);
            var refImporter = !string.IsNullOrEmpty(refPath) ? AssetImporter.GetAtPath(refPath) as TextureImporter : null;
            if (refImporter != null)
            {
                newImporter.maxTextureSize = refImporter.maxTextureSize;
                newImporter.textureCompression = refImporter.textureCompression;
                newImporter.streamingMipmaps = refImporter.streamingMipmaps;
                newImporter.mipmapEnabled = refImporter.mipmapEnabled;
            }
        }

        newImporter.sRGBTexture = !isLinearMask;
        newImporter.alphaIsTransparency = !isLinearMask;
        newImporter.SaveAndReimport();
    }

    /// <summary>
    /// 元の Mesh の頂点・ボーンウェイト・BlendShape をすべて維持したまま、
    /// 選択されたアイランドの三角形だけを新しいサブメッシュ（末尾スロット）へ移動した Mesh を生成する。
    /// （ミラーUV等で他の房とUVが重なっていても、選択した房だけに専用マテリアルを当てられる）
    /// </summary>
    private static Mesh BuildSubMeshSplitMesh(Mesh srcMesh, HairMeshContext ctx, HashSet<int> selectedIslands, int targetSubMeshFilter)
    {
        int vc = srcMesh.vertexCount;
        var dst = new Mesh
        {
            name = srcMesh.name + "_Split",
            indexFormat = vc >= 65536 ? IndexFormat.UInt32 : srcMesh.indexFormat
        };

        dst.vertices = srcMesh.vertices;
        dst.normals = srcMesh.normals;
        dst.tangents = srcMesh.tangents;
        dst.colors32 = srcMesh.colors32;

        var uv2 = new List<Vector2>();
        var uv3 = new List<Vector3>();
        var uv4 = new List<Vector4>();
        for (int ch = 0; ch < 8; ch++)
        {
            int dim = srcMesh.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + ch));
            if (dim == 2) { uv2.Clear(); srcMesh.GetUVs(ch, uv2); if (uv2.Count == vc) dst.SetUVs(ch, uv2); }
            else if (dim == 3) { uv3.Clear(); srcMesh.GetUVs(ch, uv3); if (uv3.Count == vc) dst.SetUVs(ch, uv3); }
            else if (dim == 4) { uv4.Clear(); srcMesh.GetUVs(ch, uv4); if (uv4.Count == vc) dst.SetUVs(ch, uv4); }
        }

        dst.bindposes = srcMesh.bindposes;
        var bonesPerVert = srcMesh.GetBonesPerVertex();
        var allWeights = srcMesh.GetAllBoneWeights();
        if (bonesPerVert.IsCreated && allWeights.IsCreated && bonesPerVert.Length == vc)
        {
            dst.SetBoneWeights(bonesPerVert, allWeights);
        }
        else if (srcMesh.boneWeights != null && srcMesh.boneWeights.Length == vc)
        {
            dst.boneWeights = srcMesh.boneWeights;
        }

        // 選択されたアイランドに属するグローバル三角形インデックス集合
        var selectedTriSet = new HashSet<int>();
        foreach (int islId in selectedIslands)
        {
            if (islId < 0 || islId >= ctx.Islands.Count) continue;
            var isl = ctx.Islands[islId];
            foreach (int tIdx in isl.GlobalTriangles)
                selectedTriSet.Add(tIdx);
        }

        int origSubCount = Mathf.Max(1, srcMesh.subMeshCount);
        dst.subMeshCount = origSubCount + 1;

        var splitTris = new List<int>();
        int globalTriCursor = 0;

        for (int s = 0; s < origSubCount; s++)
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

                bool moveThis = selectedTriSet.Contains(globalTriCursor);
                globalTriCursor++;

                if (moveThis)
                {
                    splitTris.Add(v0);
                    splitTris.Add(v1);
                    splitTris.Add(v2);
                }
                else
                {
                    remainTris.Add(v0);
                    remainTris.Add(v1);
                    remainTris.Add(v2);
                }
            }

            dst.SetTriangles(remainTris, s, false);
        }

        dst.SetTriangles(splitTris, origSubCount, false);

        // BlendShape 完全コピー
        var dV = new Vector3[vc];
        var dN = new Vector3[vc];
        var dT = new Vector3[vc];
        for (int bi = 0; bi < srcMesh.blendShapeCount; bi++)
        {
            string bsName = srcMesh.GetBlendShapeName(bi);
            int fc = srcMesh.GetBlendShapeFrameCount(bi);
            for (int fi = 0; fi < fc; fi++)
            {
                float fw = srcMesh.GetBlendShapeFrameWeight(bi, fi);
                srcMesh.GetBlendShapeFrameVertices(bi, fi, dV, dN, dT);
                dst.AddBlendShapeFrame(bsName, fw, dV, dN, dT);
            }
        }

        dst.bounds = srcMesh.bounds;
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
