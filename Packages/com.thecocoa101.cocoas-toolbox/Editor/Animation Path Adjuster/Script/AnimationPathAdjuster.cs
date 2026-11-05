#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Animation Path Adjuster (Experimental) — Cocoa's Toolbox
/// アニメーションクリップ（.anim）内のパスを一括で調整・書き換えできるエディタ拡張ツール。
///
/// [主な機能]
/// ・複数の .anim ファイル / フォルダ / AnimatorController をまとめて登録して一括パス変更
/// ・1つの .anim（または複数 .anim）内の個別要素（シェイプキー・Active切替・マテリアル等）を複数選択（Shift範囲選択対応）してピンポイント変更
/// ・「親階層を追加（例: Body → ○○/Body）」「指定パスへ直接書換（Hierarchyドロップ自動入力対応）」「親階層を削る」「文字列置換」の4モード
/// ・基準アバター（Animator Root）を指定すると Missing 状態を自動判定し、アバター階層から移動先パスをワンクリック自動探索可能
/// ・Floatカーブ（BlendShape / Transform / Active / Shader値）と ObjectReferenceカーブ（マテリアル差替等）の両方に完全対応
/// ・元ファイルの直接更新（Ctrl+Z Undo対応）と、別フォルダへの非破壊複製保存の両対応
/// </summary>
public class AnimationPathAdjuster : EditorWindow
{
    // ─────────────────────────────────────────────────────────
    //  定数・列挙型
    // ─────────────────────────────────────────────────────────

    private const string WINDOW_TITLE = "Animation Path Adjuster (Experimental)";
    private const string HEADER_TITLE = "Animation Path Adjuster (Experimental)";
    private const string DEFAULT_COPY_DIR = "Assets/AdjustedAnimations";
    private const float VIRTUAL_ROW_HEIGHT = 22f;

    private enum SelectionViewMode
    {
        ByPathGroup = 0,
        ByIndividualElement = 1
    }

    private enum PropertyCategoryFilter
    {
        All = 0,
        BlendShape = 1,
        GameObjectActive = 2,
        MaterialAndShader = 3,
        Transform = 4,
        Other = 5
    }

    private enum EditRuleMode
    {
        PrependParent = 0,
        SetDirectPath = 1,
        StripParent = 2,
        FindAndReplace = 3
    }

    private enum PrependInsertMode
    {
        AtRoot = 0,
        BeforeLeaf = 1
    }

    private enum ReplaceMatchMode
    {
        LeadingSegment = 0,
        ExactFullPath = 1,
        Substring = 2
    }

    private enum SaveOutputMode
    {
        OverwriteInPlace = 0,
        DuplicateToFolder = 1
    }

    private enum PathValidationState
    {
        Unknown = 0,
        Valid = 1,
        MissingObject = 2,
        MissingPropertyOrComponent = 3
    }

    // ─────────────────────────────────────────────────────────
    //  内部データ構造
    // ─────────────────────────────────────────────────────────

    [Serializable]
    private class ClipEntry
    {
        public AnimationClip Clip;
        public bool Enabled = true;
        public bool IsReadOnly;
        public string AssetPath;
    }

    private class CurveElementEntry
    {
        public int GlobalId;
        public int ClipEntryIndex;
        public AnimationClip Clip;
        public EditorCurveBinding Binding;
        public bool IsObjectReference;
        public int KeyCount;
        public PropertyCategoryFilter Category;
        public string FriendlyPropertyLabel;
        public bool Selected;
    }

    private class PathGroupEntry
    {
        public string Path;
        public bool Selected = true;
        public int TotalElementCount;
        public int BlendShapeCount;
        public int ClipCount;
        public string SummaryBadge;
        public bool IsRootAnimatorCurveOnly;
        public readonly List<CurveElementEntry> Elements = new List<CurveElementEntry>();
    }

    [Serializable]
    private class ExecutionResultItem
    {
        public AnimationClip OutputClip;
        public string ClipName;
        public string AssetPath;
        public int UpdatedCurveCount;
        public bool WasDuplicated;
    }

    // ─────────────────────────────────────────────────────────
    //  シリアライズ状態
    // ─────────────────────────────────────────────────────────

    [SerializeField] private List<ClipEntry> _clips = new List<ClipEntry>();
    [SerializeField] private bool _foldClipList = false;
    [SerializeField] private Transform _avatarRoot;

    [SerializeField] private SelectionViewMode _selectionMode = SelectionViewMode.ByPathGroup;
    [SerializeField] private PropertyCategoryFilter _categoryFilter = PropertyCategoryFilter.All;
    [SerializeField] private string _searchFilter = "";
    [SerializeField] private int _individualClipFilterIndex = 0; // 0 = すべて, 1..N = 個別Clip

    [SerializeField] private EditRuleMode _editRuleMode = EditRuleMode.PrependParent;
    [SerializeField] private string _prependFolderText = "";
    [SerializeField] private PrependInsertMode _prependInsertMode = PrependInsertMode.AtRoot;
    [SerializeField] private string _directTargetPathText = "";
    [SerializeField] private Transform _droppedTargetTransform;
    [SerializeField] private int _stripDepthCount = 1;
    [SerializeField] private string _stripPrefixText = "";
    [SerializeField] private string _findText = "";
    [SerializeField] private string _replaceText = "";
    [SerializeField] private ReplaceMatchMode _replaceMatchMode = ReplaceMatchMode.LeadingSegment;
    [SerializeField] private bool _includeRootAnimatorBindings = false;

    // アバター階層からの自動マッピング辞書（旧パス -> 自動検出新パス）
    private readonly Dictionary<string, string> _autoDetectedPathMap = new Dictionary<string, string>(StringComparer.Ordinal);
    private bool _useAutoDetectedMap = false;

    [SerializeField] private SaveOutputMode _saveOutputMode = SaveOutputMode.OverwriteInPlace;
    [SerializeField] private string _copySaveFolder = "";
    [SerializeField] private bool _foldResults = true;
    [SerializeField] private List<ExecutionResultItem> _results = new List<ExecutionResultItem>();

    [SerializeField] private Vector2 _scrollMain;
    [SerializeField] private Vector2 _scrollClips;
    [SerializeField] private Vector2 _scrollList;
    [SerializeField] private string _statusMessage = "";
    [SerializeField] private MessageType _statusType = MessageType.Info;

    // ─────────────────────────────────────────────────────────
    //  非シリアライズ スキャンキャッシュ
    // ─────────────────────────────────────────────────────────

    private readonly List<CurveElementEntry> _allElements = new List<CurveElementEntry>();
    private readonly List<PathGroupEntry> _pathGroups = new List<PathGroupEntry>();
    private bool _needsRescan = true;
    private int _lastClickedPathIndex = -1;
    private int _lastClickedElementVisibleIndex = -1;

    // ─────────────────────────────────────────────────────────
    //  GUIStyle キャッシュ
    // ─────────────────────────────────────────────────────────

    private GUIStyle _executeButtonStyle;
    private GUIStyle _dropBoxStyle;
    private GUIStyle _badgeRightStyle;
    private GUIStyle _previewArrowStyle;
    private GUIStyle _missingBadgeStyle;
    private GUIStyle _validBadgeStyle;
    private GUIStyle _wrapMiniStyle;
    private GUIStyle _rowLabelButtonStyle;

    private GUIStyle ExecuteButtonStyle => _executeButtonStyle ?? (_executeButtonStyle = new GUIStyle(GUI.skin.button)
    {
        fontSize = 13,
        fontStyle = FontStyle.Bold
    });

    private GUIStyle DropBoxStyle => _dropBoxStyle ?? (_dropBoxStyle = new GUIStyle(GUI.skin.box)
    {
        alignment = TextAnchor.MiddleCenter,
        fontStyle = FontStyle.Bold,
        wordWrap = true
    });

    private GUIStyle BadgeRightStyle => _badgeRightStyle ?? (_badgeRightStyle = new GUIStyle(EditorStyles.miniLabel)
    {
        alignment = TextAnchor.MiddleRight
    });

    private GUIStyle PreviewArrowStyle => _previewArrowStyle ?? (_previewArrowStyle = new GUIStyle(EditorStyles.miniBoldLabel)
    {
        normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.45f, 0.88f, 1f) : new Color(0.05f, 0.42f, 0.72f) },
        clipping = TextClipping.Clip
    });

    private GUIStyle MissingBadgeStyle => _missingBadgeStyle ?? (_missingBadgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
    {
        normal = { textColor = EditorGUIUtility.isProSkin ? new Color(1f, 0.75f, 0.25f) : new Color(0.78f, 0.42f, 0f) }
    });

    private GUIStyle ValidBadgeStyle => _validBadgeStyle ?? (_validBadgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
    {
        normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.45f, 0.92f, 0.52f) : new Color(0.12f, 0.55f, 0.18f) }
    });

    private GUIStyle WrapMiniStyle => _wrapMiniStyle ?? (_wrapMiniStyle = new GUIStyle(EditorStyles.miniLabel)
    {
        wordWrap = true
    });

    private GUIStyle RowLabelButtonStyle => _rowLabelButtonStyle ?? (_rowLabelButtonStyle = new GUIStyle(EditorStyles.label)
    {
        alignment = TextAnchor.MiddleLeft,
        clipping = TextClipping.Clip,
        wordWrap = false
    });

    // ─────────────────────────────────────────────────────────
    //  メニュー登録（すべて (Experimental) 付き）
    // ─────────────────────────────────────────────────────────

    [MenuItem("Tools/Cocoa's Toolbox/Animation Path Adjuster (Experimental)")]
    public static void Open()
    {
        var win = GetWindow<AnimationPathAdjuster>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(500f, 600f);
        win.Show();
    }

    [MenuItem("Assets/Cocoa's Toolbox/Animation Path Adjuster (Experimental)", false, 30)]
    private static void OpenFromProjectSelection()
    {
        var win = GetWindow<AnimationPathAdjuster>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(500f, 600f);
        win.Show();
        win.Focus();
        win.AddObjectsFromCurrentProjectSelection(clearExisting: false);
    }

    [MenuItem("CONTEXT/AnimationClip/Animation Path Adjuster (Experimental)")]
    private static void OpenFromClipContext(MenuCommand command)
    {
        var clip = command.context as AnimationClip;
        var win = GetWindow<AnimationPathAdjuster>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(500f, 600f);
        win.Show();
        win.Focus();
        if (clip != null)
        {
            win.AddAssetOrContainer(clip);
            win.ScanAllClips(preserveSelection: false);
        }
    }

    [MenuItem("CONTEXT/Animator/Animation Path Adjuster (Experimental)")]
    private static void OpenFromAnimatorContext(MenuCommand command)
    {
        var animator = command.context as Animator;
        var win = GetWindow<AnimationPathAdjuster>(false, WINDOW_TITLE, true);
        win.minSize = new Vector2(500f, 600f);
        win.Show();
        win.Focus();
        if (animator != null)
        {
            win._avatarRoot = animator.transform;
            if (animator.runtimeAnimatorController != null)
            {
                win.AddAssetOrContainer(animator.runtimeAnimatorController);
                win.ScanAllClips(preserveSelection: false);
            }
            win.Repaint();
        }
    }

    // ─────────────────────────────────────────────────────────
    //  ライフサイクル
    // ─────────────────────────────────────────────────────────

    private void OnEnable()
    {
        minSize = new Vector2(500f, 600f);
        Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        Undo.undoRedoPerformed += OnUndoRedoPerformed;
        _needsRescan = true;
    }

    private void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedoPerformed;
    }

    private void OnUndoRedoPerformed()
    {
        ScanAllClips(preserveSelection: true);
        Repaint();
    }

    // ─────────────────────────────────────────────────────────
    //  OnGUI
    // ─────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (_needsRescan)
        {
            ScanAllClips(preserveSelection: true);
        }

        _scrollMain = EditorGUILayout.BeginScrollView(_scrollMain);

        DrawHeader();
        EditorGUILayout.Space(6f);

        DrawClipInputSection();
        EditorGUILayout.Space(8f);

        DrawSelectionSection();
        EditorGUILayout.Space(8f);

        DrawEditRuleSection();
        EditorGUILayout.Space(8f);

        bool triggerExecute = DrawSaveAndExecuteSection();

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
            ExecutePathAdjustment();
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
    //  ① 対象アニメーション登録・基準ルート設定セクション
    // ─────────────────────────────────────────────────────────

    private void DrawClipInputSection()
    {
        EditorGUILayout.LabelField("① 対象のアニメーション (.anim)", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // ドラッグ＆ドロップエリア
            Rect dropRect = GUILayoutUtility.GetRect(0f, 40f, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, "ここに .anim ファイル / フォルダ / AnimatorController をドロップ\n（複数まとめてドロップ可能）", DropBoxStyle);
            HandleClipDragAndDrop(dropRect);

            EditorGUILayout.Space(3f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Project選択中を追加", "Projectウィンドウで選択中の .anim ファイル・フォルダ・AnimatorController を一括追加します。")))
                {
                    AddObjectsFromCurrentProjectSelection(clearExisting: false);
                }

                using (new EditorGUI.DisabledScope(_clips.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent("再スキャン", "Animationウィンドウ等で外部編集した内容を再読み込みします。"), GUILayout.Width(78f)))
                    {
                        ScanAllClips(preserveSelection: true);
                        SetStatus("アニメーション要素を再スキャンしました。", MessageType.Info);
                    }

                    if (GUILayout.Button("クリア", GUILayout.Width(56f)))
                    {
                        _clips.Clear();
                        _autoDetectedPathMap.Clear();
                        _useAutoDetectedMap = false;
                        ScanAllClips(preserveSelection: false);
                        SetStatus("リストをクリアしました。", MessageType.Info);
                    }
                }
            }

            // 登録クリップ一覧（折りたたみ）
            if (_clips.Count > 0)
            {
                int activeCount = _clips.Count(c => c != null && c.Clip != null && c.Enabled);
                int readOnlyCount = _clips.Count(c => c != null && c.Clip != null && c.Enabled && c.IsReadOnly);
                string roSuffix = readOnlyCount > 0 ? $" / 読取専用 {readOnlyCount} 件" : "";
                _foldClipList = EditorGUILayout.Foldout(
                    _foldClipList,
                    $"登録クリップ一覧 ({activeCount} / {_clips.Count} 件有効・計 {_allElements.Count} 要素{roSuffix})",
                    true);

                if (_foldClipList)
                {
                    float h = Mathf.Min(_clips.Count * 22f + 6f, 130f);
                    _scrollClips = EditorGUILayout.BeginScrollView(_scrollClips, GUILayout.Height(h));
                    int removeIdx = -1;
                    bool toggled = false;

                    for (int i = 0; i < _clips.Count; i++)
                    {
                        var ce = _clips[i];
                        if (ce == null || ce.Clip == null) continue;

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            bool nextOn = EditorGUILayout.Toggle(ce.Enabled, GUILayout.Width(18f));
                            if (nextOn != ce.Enabled)
                            {
                                ce.Enabled = nextOn;
                                toggled = true;
                            }

                            Texture icon = EditorGUIUtility.ObjectContent(ce.Clip, typeof(AnimationClip)).image;
                            if (GUILayout.Button(new GUIContent(" " + ce.Clip.name, icon, ce.AssetPath), RowLabelButtonStyle, GUILayout.ExpandWidth(true), GUILayout.Height(19f)))
                            {
                                EditorGUIUtility.PingObject(ce.Clip);
                            }

                            if (ce.IsReadOnly)
                                GUILayout.Label("[読取専用]", MissingBadgeStyle, GUILayout.Width(64f));

                            if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22f)))
                                removeIdx = i;
                        }
                    }

                    EditorGUILayout.EndScrollView();

                    if (removeIdx >= 0)
                    {
                        _clips.RemoveAt(removeIdx);
                        toggled = true;
                    }
                    if (toggled)
                        ScanAllClips(preserveSelection: true);
                }
            }

            EditorGUILayout.Space(3f);

            // 基準アバター（任意）
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _avatarRoot = (Transform)EditorGUILayout.ObjectField(
                    new GUIContent("基準アバターRoot (任意)", "Animatorが付いているアバターのルートTransformを指定すると、Missing状態の自動判定や、移動先パスの自動探索が使えます。"),
                    _avatarRoot, typeof(Transform), true);
                if (EditorGUI.EndChangeCheck())
                {
                    _autoDetectedPathMap.Clear();
                    _useAutoDetectedMap = false;
                }

                if (GUILayout.Button(new GUIContent("選択中から取得", "Hierarchyで選択中のオブジェクト（またはその親のAnimatorルート）を基準アバターとしてセットします。"), EditorStyles.miniButton, GUILayout.Width(96f)))
                {
                    AssignAvatarRootFromSelection();
                }
            }
        }
    }

    private void HandleClipDragAndDrop(Rect dropRect)
    {
        Event evt = Event.current;
        if (!dropRect.Contains(evt.mousePosition))
            return;

        if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
        {
            bool hasValid = DragAndDrop.objectReferences != null && DragAndDrop.objectReferences.Length > 0;
            if (!hasValid) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                bool wasEmpty = _clips.Count == 0;
                int added = 0;
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    added += AddAssetOrContainer(obj);
                }
                ScanAllClips(preserveSelection: !wasEmpty);
                SetStatus(added > 0 ? $"{added} 件のアニメーションクリップを追加しました。" : "新規追加できる .anim が見つかりませんでした。", MessageType.Info);
            }
            evt.Use();
        }
    }

    // ─────────────────────────────────────────────────────────
    //  ② 書き換え対象（パス一括 / 個別要素複数選択）セクション
    // ─────────────────────────────────────────────────────────

    private void DrawSelectionSection()
    {
        EditorGUILayout.LabelField("② 変更する対象の選択", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            int nextMode = GUILayout.Toolbar((int)_selectionMode, new[]
            {
                "パス単位でまとめて選択（複数anim横断）",
                "中の要素を個別に複数選択（Shift選択対応）"
            }, GUILayout.Height(24f));

            if (nextMode != (int)_selectionMode)
            {
                _selectionMode = (SelectionViewMode)nextMode;
                // モード切替時にグループ選択状態を要素側へ同期（パス単位で選んでいた対象をそのまま個別画面でも維持）
                if (_selectionMode == SelectionViewMode.ByIndividualElement)
                    SyncPathGroupSelectionToElements();
            }

            EditorGUILayout.Space(4f);

            // 要素種別フィルタ＆検索欄
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _categoryFilter = (PropertyCategoryFilter)EditorGUILayout.Popup(
                    "要素フィルタ",
                    (int)_categoryFilter,
                    new[]
                    {
                        "すべて（全プロパティ）",
                        "シェイプキー (BlendShape) のみ",
                        "GameObject ON/OFF (m_IsActive) のみ",
                        "マテリアル・シェーダー値のみ",
                        "Transform (位置・回転・スケール) のみ",
                        "その他のコンポーネントのみ"
                    });
                if (EditorGUI.EndChangeCheck() && _selectionMode == SelectionViewMode.ByPathGroup)
                {
                    RebuildPathGroupsFromFilteredElements(preserveGroupChecks: true);
                }

                GUILayout.Space(6f);
                GUILayout.Label("検索:", GUILayout.Width(32f));
                _searchFilter = EditorGUILayout.TextField(_searchFilter, GUILayout.MinWidth(90f));
                if (!string.IsNullOrEmpty(_searchFilter) && GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20f)))
                {
                    _searchFilter = "";
                    GUI.FocusControl(null);
                }
            }

            if (_clips.Count == 0 || _allElements.Count == 0)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.HelpBox("①に .anim ファイルを登録すると、ここにパス・アニメーション要素の一覧が表示されます。", MessageType.Info);
                return;
            }

            if (_selectionMode == SelectionViewMode.ByPathGroup)
            {
                DrawPathGroupList();
            }
            else
            {
                DrawIndividualElementList();
            }
        }
    }

    private void DrawPathGroupList()
    {
        var visibleGroups = GetVisiblePathGroups();

        // 一括選択操作バー
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("表示中を全選択", EditorStyles.miniButtonLeft))
            {
                foreach (var g in visibleGroups)
                {
                    if (!g.IsRootAnimatorCurveOnly || _includeRootAnimatorBindings)
                        g.Selected = true;
                }
            }
            if (GUILayout.Button("全解除", EditorStyles.miniButtonMid))
            {
                foreach (var g in _pathGroups) g.Selected = false;
            }
            if (GUILayout.Button("反転", EditorStyles.miniButtonMid))
            {
                foreach (var g in visibleGroups)
                {
                    if (!g.IsRootAnimatorCurveOnly || _includeRootAnimatorBindings)
                        g.Selected = !g.Selected;
                }
            }

            using (new EditorGUI.DisabledScope(_avatarRoot == null))
            {
                if (GUILayout.Button(new GUIContent("⚠ Missingのみ選択", "基準アバターRoot上で見つからないパスだけを一括チェックします。"), EditorStyles.miniButtonRight))
                {
                    foreach (var g in _pathGroups) g.Selected = false;
                    foreach (var g in visibleGroups)
                    {
                        if (g.IsRootAnimatorCurveOnly) continue;
                        var st = ValidatePathGroupOnAvatar(g.Path, g.Elements);
                        if (st == PathValidationState.MissingObject || st == PathValidationState.MissingPropertyOrComponent)
                            g.Selected = true;
                    }
                }
            }
        }

        EditorGUILayout.Space(3f);

        int selectedGroupCount = _pathGroups.Count(g => g.Selected && (!g.IsRootAnimatorCurveOnly || _includeRootAnimatorBindings));
        EditorGUILayout.LabelField(
            $"検出パス: {visibleGroups.Count} 件表示中（選択中: {selectedGroupCount} パス） — 行クリック・Shift範囲選択に対応",
            EditorStyles.miniLabel);

        float boxHeight = Mathf.Clamp(visibleGroups.Count * VIRTUAL_ROW_HEIGHT + 8f, 66f, 220f);
        _scrollList = EditorGUILayout.BeginScrollView(_scrollList, "box", GUILayout.Height(boxHeight));

        int totalRows = visibleGroups.Count;
        int firstRow = Mathf.Clamp(Mathf.FloorToInt(_scrollList.y / VIRTUAL_ROW_HEIGHT) - 2, 0, Mathf.Max(0, totalRows - 1));
        int visibleRowCount = Mathf.CeilToInt(boxHeight / VIRTUAL_ROW_HEIGHT) + 5;
        int lastRow = Mathf.Min(totalRows, firstRow + visibleRowCount);

        if (firstRow > 0)
            GUILayout.Space(firstRow * VIRTUAL_ROW_HEIGHT);

        for (int vi = firstRow; vi < lastRow; vi++)
        {
            var g = visibleGroups[vi];
            bool disabledRoot = g.IsRootAnimatorCurveOnly && !_includeRootAnimatorBindings;

            using (new EditorGUI.DisabledScope(disabledRoot))
            using (new EditorGUILayout.HorizontalScope(GUILayout.Height(VIRTUAL_ROW_HEIGHT - 2f)))
            {
                bool curChecked = !disabledRoot && g.Selected;
                EditorGUI.BeginChangeCheck();
                bool nextChecked = EditorGUILayout.Toggle(curChecked, GUILayout.Width(18f));
                if (EditorGUI.EndChangeCheck())
                {
                    ApplyRangeOrSingleTogglePathGroups(visibleGroups, vi, nextChecked);
                }

                string displayPath = string.IsNullOrEmpty(g.Path) ? "(Root / ルート直下)" : g.Path;
                if (_avatarRoot != null && !g.IsRootAnimatorCurveOnly)
                {
                    var st = ValidatePathGroupOnAvatar(g.Path, g.Elements);
                    if (st == PathValidationState.MissingObject)
                        GUILayout.Label(new GUIContent("⚠Missing", "基準アバター上にこのパスのオブジェクトが存在しません。"), MissingBadgeStyle, GUILayout.Width(58f));
                    else if (st == PathValidationState.MissingPropertyOrComponent)
                        GUILayout.Label(new GUIContent("⚠要素欠損", "オブジェクトは存在しますが対象のシェイプキーまたはコンポーネントが見つかりません。"), MissingBadgeStyle, GUILayout.Width(64f));
                    else if (st == PathValidationState.Valid)
                        GUILayout.Label(new GUIContent("✓", "基準アバター上で実在確認済みです。"), ValidBadgeStyle, GUILayout.Width(16f));
                }

                if (GUILayout.Button(new GUIContent(displayPath, $"{displayPath}\n{g.SummaryBadge}"), RowLabelButtonStyle, GUILayout.MinWidth(90f), GUILayout.ExpandWidth(true)))
                {
                    ApplyRangeOrSingleTogglePathGroups(visibleGroups, vi, !curChecked);
                }

                // 変更後パスのライブプレビュー
                if (curChecked)
                {
                    string previewNew = ComputePreviewPath(g.Path);
                    if (!string.Equals(previewNew, g.Path, StringComparison.Ordinal))
                    {
                        string newDisp = string.IsNullOrEmpty(previewNew) ? "(Root)" : previewNew;
                        string okMark = "";
                        if (_avatarRoot != null)
                        {
                            var afterSt = ValidatePathGroupOnAvatar(previewNew, g.Elements);
                            if (afterSt == PathValidationState.Valid) okMark = " ✓";
                        }
                        GUILayout.Label(new GUIContent($"→ {newDisp}{okMark}", $"変更後パス: {newDisp}"), PreviewArrowStyle, GUILayout.MaxWidth(210f));
                    }
                }

                GUILayout.Label(g.SummaryBadge, BadgeRightStyle, GUILayout.Width(135f));
            }
        }

        if (lastRow < totalRows)
            GUILayout.Space((totalRows - lastRow) * VIRTUAL_ROW_HEIGHT);

        EditorGUILayout.EndScrollView();
    }

    private void DrawIndividualElementList()
    {
        // 対象クリップ絞り込みドロップダウン（複数登録時）
        if (_clips.Count > 1)
        {
            var clipLabels = new List<string> { $"すべての有効な .anim を表示 ({_clips.Count(c => c.Enabled)} 件)" };
            for (int i = 0; i < _clips.Count; i++)
            {
                string cName = _clips[i].Clip != null ? _clips[i].Clip.name : "(Missing)";
                clipLabels.Add($"{i + 1}: {cName}");
            }
            _individualClipFilterIndex = Mathf.Clamp(_individualClipFilterIndex, 0, clipLabels.Count - 1);
            _individualClipFilterIndex = EditorGUILayout.Popup("表示する .anim", _individualClipFilterIndex, clipLabels.ToArray());
        }
        else
        {
            _individualClipFilterIndex = 0;
        }

        var visibleElements = GetVisibleIndividualElements();

        // 一括選択ボタンバー
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("表示中を全選択", EditorStyles.miniButtonLeft))
            {
                foreach (var el in visibleElements)
                {
                    if (!IsProtectedRootAnimatorElement(el) || _includeRootAnimatorBindings)
                        el.Selected = true;
                }
            }
            if (GUILayout.Button("全解除", EditorStyles.miniButtonMid))
            {
                foreach (var el in _allElements) el.Selected = false;
            }
            if (GUILayout.Button("表示中を反転", EditorStyles.miniButtonMid))
            {
                foreach (var el in visibleElements)
                {
                    if (!IsProtectedRootAnimatorElement(el) || _includeRootAnimatorBindings)
                        el.Selected = !el.Selected;
                }
            }

            using (new EditorGUI.DisabledScope(_avatarRoot == null))
            {
                if (GUILayout.Button("⚠ Missing要素のみ選択", EditorStyles.miniButtonRight))
                {
                    foreach (var el in _allElements) el.Selected = false;
                    foreach (var el in visibleElements)
                    {
                        if (IsProtectedRootAnimatorElement(el)) continue;
                        var st = ValidateSingleElementOnAvatar(el.Binding.path, el.Binding);
                        if (st != PathValidationState.Valid)
                            el.Selected = true;
                    }
                }
            }
        }

        EditorGUILayout.Space(3f);

        int totalSelected = _allElements.Count(e => e.Selected && (!IsProtectedRootAnimatorElement(e) || _includeRootAnimatorBindings));
        EditorGUILayout.LabelField(
            $"表示要素: {visibleElements.Count} / {_allElements.Count} 件（選択中: {totalSelected} 要素） — Shift+クリックで範囲選択可能",
            EditorStyles.miniLabel);

        float boxHeight = Mathf.Clamp(visibleElements.Count * VIRTUAL_ROW_HEIGHT + 8f, 80f, 240f);
        _scrollList = EditorGUILayout.BeginScrollView(_scrollList, "box", GUILayout.Height(boxHeight));

        int totalRows = visibleElements.Count;
        int firstRow = Mathf.Clamp(Mathf.FloorToInt(_scrollList.y / VIRTUAL_ROW_HEIGHT) - 2, 0, Mathf.Max(0, totalRows - 1));
        int visibleRowCount = Mathf.CeilToInt(boxHeight / VIRTUAL_ROW_HEIGHT) + 5;
        int lastRow = Mathf.Min(totalRows, firstRow + visibleRowCount);

        if (firstRow > 0)
            GUILayout.Space(firstRow * VIRTUAL_ROW_HEIGHT);

        bool showClipTag = _clips.Count > 1 && _individualClipFilterIndex == 0;

        for (int vi = firstRow; vi < lastRow; vi++)
        {
            var el = visibleElements[vi];
            bool disabledRoot = IsProtectedRootAnimatorElement(el) && !_includeRootAnimatorBindings;

            using (new EditorGUI.DisabledScope(disabledRoot))
            using (new EditorGUILayout.HorizontalScope(GUILayout.Height(VIRTUAL_ROW_HEIGHT - 2f)))
            {
                bool curChecked = !disabledRoot && el.Selected;
                EditorGUI.BeginChangeCheck();
                bool nextChecked = EditorGUILayout.Toggle(curChecked, GUILayout.Width(18f));
                if (EditorGUI.EndChangeCheck())
                {
                    ApplyRangeOrSingleToggleElements(visibleElements, vi, nextChecked);
                }

                if (_avatarRoot != null && !IsProtectedRootAnimatorElement(el))
                {
                    var st = ValidateSingleElementOnAvatar(el.Binding.path, el.Binding);
                    if (st != PathValidationState.Valid)
                        GUILayout.Label("⚠", MissingBadgeStyle, GUILayout.Width(16f));
                    else
                        GUILayout.Label("✓", ValidBadgeStyle, GUILayout.Width(14f));
                }

                string pathDisp = string.IsNullOrEmpty(el.Binding.path) ? "(Root)" : el.Binding.path;
                string clipPrefix = showClipTag && el.Clip != null ? $"[{el.Clip.name}] " : "";
                string rowText = $"{clipPrefix}{pathDisp}  :  {el.FriendlyPropertyLabel}";
                string tooltip = $"Clip: {(el.Clip != null ? el.Clip.name : "")}\nPath: {pathDisp}\nType: {el.Binding.type?.Name}\nProperty: {el.Binding.propertyName}\nKeys: {el.KeyCount}";

                if (GUILayout.Button(new GUIContent(rowText, tooltip), RowLabelButtonStyle, GUILayout.MinWidth(120f), GUILayout.ExpandWidth(true)))
                {
                    ApplyRangeOrSingleToggleElements(visibleElements, vi, !curChecked);
                }

                if (curChecked)
                {
                    string previewNew = ComputePreviewPath(el.Binding.path);
                    if (!string.Equals(previewNew, el.Binding.path, StringComparison.Ordinal))
                    {
                        string newDisp = string.IsNullOrEmpty(previewNew) ? "(Root)" : previewNew;
                        GUILayout.Label($"→ {newDisp}", PreviewArrowStyle, GUILayout.MaxWidth(175f));
                    }
                }

                GUILayout.Label($"{el.KeyCount} keys", BadgeRightStyle, GUILayout.Width(52f));
            }
        }

        if (lastRow < totalRows)
            GUILayout.Space((totalRows - lastRow) * VIRTUAL_ROW_HEIGHT);

        EditorGUILayout.EndScrollView();
    }

    // ─────────────────────────────────────────────────────────
    //  ③ パス変更ルール設定セクション
    // ─────────────────────────────────────────────────────────

    private void DrawEditRuleSection()
    {
        EditorGUILayout.LabelField("③ パスの変更方法", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // アバター階層からのワンクリック自動探索バー
            if (_avatarRoot != null && _allElements.Count > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(
                        new GUIContent("🔍 基準アバター階層から移動先パスを自動探索してセット", "アバター配下から同名オブジェクト（および対象シェイプキーを持つメッシュ）を探し、移動後の正しいパスを自動設定します。"),
                        GUILayout.Height(23f)))
                    {
                        RunAutoDetectPathsFromAvatarHierarchy();
                    }

                    if (_useAutoDetectedMap && GUILayout.Button("自動探索解除", GUILayout.Width(90f), GUILayout.Height(23f)))
                    {
                        _useAutoDetectedMap = false;
                        _autoDetectedPathMap.Clear();
                    }
                }

                if (_useAutoDetectedMap && _autoDetectedPathMap.Count > 0)
                {
                    EditorGUILayout.HelpBox(
                        $"アバター階層から {_autoDetectedPathMap.Count} 件の移動先パスを自動マッピング中です（上のプレビュー欄 `→ ... ✓` で確認できます）。手動ルールに戻す場合は下のタブまたは「自動探索解除」を押してください。",
                        MessageType.Info);
                }

                EditorGUILayout.Space(3f);
            }

            EditorGUI.BeginChangeCheck();
            int nextRule = GUILayout.Toolbar((int)_editRuleMode, new[]
            {
                "親階層を追加 (○○/Body)",
                "指定パスへ直接書換",
                "親階層を削る",
                "文字列置換"
            }, GUILayout.Height(24f));
            if (EditorGUI.EndChangeCheck())
            {
                _editRuleMode = (EditRuleMode)nextRule;
                _useAutoDetectedMap = false;
            }

            EditorGUILayout.Space(4f);

            switch (_editRuleMode)
            {
                case EditRuleMode.PrependParent:
                    DrawRulePrependParent();
                    break;
                case EditRuleMode.SetDirectPath:
                    DrawRuleSetDirectPath();
                    break;
                case EditRuleMode.StripParent:
                    DrawRuleStripParent();
                    break;
                case EditRuleMode.FindAndReplace:
                    DrawRuleFindAndReplace();
                    break;
            }
        }
    }

    private void DrawRulePrependParent()
    {
        EditorGUI.BeginChangeCheck();
        _prependFolderText = EditorGUILayout.TextField(
            new GUIContent("追加する親階層名", "例: 「Meshes」や「GroupA/Sub」と入力すると、元のパス「Body」が「Meshes/Body」や「GroupA/Sub/Body」になります（前後の / は自動整形されます）。"),
            _prependFolderText);

        _prependInsertMode = (PrependInsertMode)EditorGUILayout.Popup(
            "追加する位置",
            (int)_prependInsertMode,
            new[]
            {
                "パスの先頭に追加（例: Body → ○○/Body）",
                "末尾オブジェクトの直前に挿入（例: Parent/Body → Parent/○○/Body）"
            });
        if (EditorGUI.EndChangeCheck())
            _useAutoDetectedMap = false;

        // Hierarchyドロップで親階層名を自動取得
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            var droppedParent = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("Hierarchyから親フォルダ指定", "挟んだ親オブジェクト（または移動後のBody自身）をHierarchyからドロップすると、追加する親階層名を自動計算します。"),
                null, typeof(Transform), true);
            if (EditorGUI.EndChangeCheck() && droppedParent != null)
            {
                AutoFillPrependFolderFromTransform(droppedParent);
                _useAutoDetectedMap = false;
            }
        }

        EditorGUILayout.LabelField("例: 「FaceGroup」と入力すると `Body` → `FaceGroup/Body` に一括追記されます。", WrapMiniStyle);
    }

    private void DrawRuleSetDirectPath()
    {
        EditorGUI.BeginChangeCheck();
        _directTargetPathText = EditorGUILayout.TextField(
            new GUIContent("変更後のパス", "選択したすべての要素のパスをこのパスへ直接書き換えます（例: Meshes/Body）。"),
            _directTargetPathText);
        if (EditorGUI.EndChangeCheck())
            _useAutoDetectedMap = false;

        EditorGUI.BeginChangeCheck();
        var nextTargetTf = (Transform)EditorGUILayout.ObjectField(
            new GUIContent("移動先オブジェクト (自動入力)", "Hierarchy上の移動先オブジェクト（例: ○○/Body）をここへドロップすると、Animatorルートからの相対パスを自動入力します！"),
            _droppedTargetTransform, typeof(Transform), true);
        if (EditorGUI.EndChangeCheck())
        {
            _droppedTargetTransform = nextTargetTf;
            if (_droppedTargetTransform != null)
            {
                AutoFillDirectPathFromTransform(_droppedTargetTransform);
                _useAutoDetectedMap = false;
            }
        }

        EditorGUILayout.LabelField("※Hierarchyから移動後の `Body` をドロップするだけで、手入力なしで正確なパス（`○○/Body`）が入ります。", WrapMiniStyle);
    }

    private void DrawRuleStripParent()
    {
        EditorGUI.BeginChangeCheck();
        _stripDepthCount = EditorGUILayout.IntSlider(
            new GUIContent("先頭から削る階層数", "例: 1 にすると「○○/Body」→「Body」のように親階層を1つ外します。"),
            _stripDepthCount, 1, 8);

        _stripPrefixText = EditorGUILayout.TextField(
            new GUIContent("特定プレフィックスのみ削る (任意)", "指定した場合はこの親パスで始まる要素のみ先頭を削ります（空欄なら選択中の全要素から指定階層数を削ります）。"),
            _stripPrefixText);
        if (EditorGUI.EndChangeCheck())
            _useAutoDetectedMap = false;
    }

    private void DrawRuleFindAndReplace()
    {
        EditorGUI.BeginChangeCheck();
        _findText = EditorGUILayout.TextField("検索するパス文字列", _findText);
        _replaceText = EditorGUILayout.TextField("置換後のパス文字列", _replaceText);
        _replaceMatchMode = (ReplaceMatchMode)EditorGUILayout.Popup(
            "一致条件",
            (int)_replaceMatchMode,
            new[]
            {
                "先頭パス階層一致（推奨: \"Body\" で \"UpperBody\" が誤爆しない）",
                "パス完全一致のみ",
                "部分文字列一致"
            });
        if (EditorGUI.EndChangeCheck())
            _useAutoDetectedMap = false;
    }

    // ─────────────────────────────────────────────────────────
    //  ④ 保存オプション＆実行セクション
    // ─────────────────────────────────────────────────────────

    private bool DrawSaveAndExecuteSection()
    {
        bool trigger = false;

        CountPendingChanges(
            out int affectedClipCount,
            out int affectedCurveCount,
            out int collisionCount,
            out bool anyReadOnlyClipAffected);

        EditorGUILayout.LabelField("④ 保存方法と実行", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            _saveOutputMode = (SaveOutputMode)GUILayout.Toolbar((int)_saveOutputMode, new[]
            {
                "元の .anim を直接更新（Undo対応）",
                "別フォルダに .anim を複製して保存"
            }, GUILayout.Height(23f));

            if (_saveOutputMode == SaveOutputMode.DuplicateToFolder)
            {
                EditorGUILayout.Space(3f);
                string autoFolder = DetermineDefaultCopySaveFolder();
                string dispFolder = string.IsNullOrEmpty(_copySaveFolder) ? autoFolder : _copySaveFolder;

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    string edited = EditorGUILayout.TextField("複製先フォルダ", dispFolder);
                    if (EditorGUI.EndChangeCheck())
                        _copySaveFolder = edited == autoFolder ? "" : edited;

                    if (GUILayout.Button("参照", GUILayout.Width(44f)))
                    {
                        string init = Directory.Exists(dispFolder) ? dispFolder : "Assets";
                        string picked = EditorUtility.OpenFolderPanel("複製先フォルダを選択", init, "");
                        if (!string.IsNullOrEmpty(picked))
                        {
                            string rel = ToAssetsRelativePath(picked);
                            if (!string.IsNullOrEmpty(rel))
                                _copySaveFolder = rel;
                            else
                                SetStatus("プロジェクトの Assets フォルダ配下を選択してください。", MessageType.Warning);
                        }
                    }

                    if (!string.IsNullOrEmpty(_copySaveFolder) && GUILayout.Button("自動", GUILayout.Width(42f)))
                        _copySaveFolder = "";
                }
            }
            else if (anyReadOnlyClipAffected)
            {
                EditorGUILayout.Space(3f);
                EditorGUILayout.HelpBox(
                    "変更対象に読み取り専用のクリップ（FBX内包クリップや Packages 配下）が含まれています。\n" +
                    "「別フォルダに .anim を複製して保存」へ切り替えると出力できます。",
                    MessageType.Warning);
            }

            if (collisionCount > 0)
            {
                EditorGUILayout.Space(3f);
                EditorGUILayout.HelpBox(
                    $"変更後、同一クリップ内で同じパス・同じプロパティ名のカーブが {collisionCount} 件重複します（変更元のカーブで上書きされます）。",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(5f);

            EditorGUILayout.LabelField(
                $"変更予定プレビュー: {affectedClipCount} 個の .anim ファイル / 計 {affectedCurveCount} 個のアニメーション要素",
                EditorStyles.boldLabel);

            bool canRun = affectedCurveCount > 0
                          && !(_saveOutputMode == SaveOutputMode.OverwriteInPlace && anyReadOnlyClipAffected);

            using (new EditorGUI.DisabledScope(!canRun))
            {
                string btnText = _saveOutputMode == SaveOutputMode.OverwriteInPlace
                    ? $"選択した {affectedCurveCount} 要素のパスを一括変更する"
                    : $"パス変更済みの .anim ({affectedClipCount} 件) を複製作成する";

                if (GUILayout.Button(btnText, ExecuteButtonStyle, GUILayout.Height(36f)))
                {
                    trigger = true;
                }
            }
        }

        return trigger;
    }

    private void DrawResultsSection()
    {
        if (_results == null || _results.Count == 0) return;

        _foldResults = EditorGUILayout.Foldout(_foldResults, $"実行結果 ({_results.Count} クリップ更新)", true);
        if (!_foldResults) return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            foreach (var r in _results)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Texture icon = EditorGUIUtility.ObjectContent(r.OutputClip, typeof(AnimationClip)).image;
                    if (GUILayout.Button(new GUIContent(" " + r.ClipName, icon, r.AssetPath), RowLabelButtonStyle, GUILayout.ExpandWidth(true), GUILayout.Height(19f)))
                    {
                        if (r.OutputClip != null)
                        {
                            EditorGUIUtility.PingObject(r.OutputClip);
                            Selection.activeObject = r.OutputClip;
                        }
                    }

                    string modeTag = r.WasDuplicated ? "複製" : "更新";
                    GUILayout.Label($"{modeTag}: {r.UpdatedCurveCount} 要素変更", BadgeRightStyle, GUILayout.Width(115f));
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  アセット収集・スキャン処理
    // ─────────────────────────────────────────────────────────

    private void AddObjectsFromCurrentProjectSelection(bool clearExisting)
    {
        bool wasEmpty = clearExisting || _clips.Count == 0;
        if (clearExisting)
            _clips.Clear();

        int added = 0;
        var selected = Selection.objects;
        if (selected != null)
        {
            foreach (var obj in selected)
                added += AddAssetOrContainer(obj);
        }

        // DeepAssets で選択フォルダ配下の AnimationClip も確実に拾う
        var deepClips = Selection.GetFiltered<AnimationClip>(SelectionMode.DeepAssets);
        if (deepClips != null)
        {
            foreach (var c in deepClips)
                added += AddSingleClip(c);
        }

        ScanAllClips(preserveSelection: !wasEmpty);
        if (added > 0)
            SetStatus($"{added} 件のアニメーションクリップを追加しました。", MessageType.Info);
        else
            SetStatus("Projectウィンドウで .anim ファイル・フォルダ・AnimatorController を選択してください。", MessageType.Info);
    }

    private int AddAssetOrContainer(UnityEngine.Object obj)
    {
        if (obj == null) return 0;

        if (obj is AnimationClip clip)
            return AddSingleClip(clip);

        if (obj is AnimatorController controller)
        {
            int count = 0;
            foreach (var c in CollectClipsFromAnimatorController(controller))
                count += AddSingleClip(c);
            return count;
        }

        if (obj is AnimatorOverrideController overrideController)
        {
            int count = 0;
            var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            overrideController.GetOverrides(overrides);
            foreach (var kv in overrides)
            {
                AnimationClip effective = kv.Value != null ? kv.Value : kv.Key;
                if (effective != null)
                    count += AddSingleClip(effective);
            }
            return count;
        }

        string path = AssetDatabase.GetAssetPath(obj);
        if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
        {
            int count = 0;
            string[] guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { path });
            foreach (string guid in guids)
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                // フォルダ一括検索時は編集可能な .anim メインアセットを優先収集（FBX内包のPreviewクリップを除外）
                if (!p.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)) continue;
                var c = AssetDatabase.LoadAssetAtPath<AnimationClip>(p);
                if (c != null)
                    count += AddSingleClip(c);
            }
            return count;
        }

        return 0;
    }

    private int AddSingleClip(AnimationClip clip)
    {
        if (clip == null) return 0;
        if (clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase)) return 0;
        if (_clips.Any(x => x != null && x.Clip == clip)) return 0;

        string assetPath = AssetDatabase.GetAssetPath(clip);
        bool isEditableAnim = !string.IsNullOrEmpty(assetPath)
                              && assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                              && assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)
                              && AssetDatabase.IsMainAsset(clip);

        _clips.Add(new ClipEntry
        {
            Clip = clip,
            Enabled = true,
            IsReadOnly = !isEditableAnim,
            AssetPath = assetPath
        });
        return 1;
    }

    private static List<AnimationClip> CollectClipsFromAnimatorController(AnimatorController controller)
    {
        var result = new List<AnimationClip>();
        var seen = new HashSet<AnimationClip>();
        if (controller == null) return result;

        void AddMotionClips(Motion m)
        {
            if (m == null) return;
            if (m is AnimationClip ac)
            {
                if (seen.Add(ac)) result.Add(ac);
            }
            else if (m is BlendTree bt && bt.children != null)
            {
                foreach (var ch in bt.children)
                    AddMotionClips(ch.motion);
            }
        }

        void WalkStateMachine(AnimatorStateMachine sm)
        {
            if (sm == null) return;
            if (sm.states != null)
            {
                foreach (var st in sm.states)
                    if (st.state != null) AddMotionClips(st.state.motion);
            }
            if (sm.stateMachines != null)
            {
                foreach (var sub in sm.stateMachines)
                    WalkStateMachine(sub.stateMachine);
            }
        }

        foreach (var layer in controller.layers)
            WalkStateMachine(layer.stateMachine);

        return result;
    }

    private void ScanAllClips(bool preserveSelection)
    {
        _needsRescan = false;
        _clips.RemoveAll(c => c == null || c.Clip == null);

        // 以前の選択状態を記憶
        var prevSelectedPaths = new HashSet<string>(StringComparer.Ordinal);
        var prevUnselectedPaths = new HashSet<string>(StringComparer.Ordinal);
        var prevSelectedElemKeys = new HashSet<string>(StringComparer.Ordinal);

        if (preserveSelection)
        {
            foreach (var g in _pathGroups)
            {
                if (g.Selected) prevSelectedPaths.Add(g.Path);
                else prevUnselectedPaths.Add(g.Path);
            }
            foreach (var el in _allElements)
            {
                if (el.Selected)
                    prevSelectedElemKeys.Add(MakeElementSelectionKey(el));
            }
        }

        _allElements.Clear();
        int globalId = 0;

        for (int ci = 0; ci < _clips.Count; ci++)
        {
            var ce = _clips[ci];
            if (!ce.Enabled || ce.Clip == null) continue;

            string assetPath = AssetDatabase.GetAssetPath(ce.Clip);
            ce.AssetPath = assetPath;
            ce.IsReadOnly = string.IsNullOrEmpty(assetPath)
                            || !assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                            || !assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)
                            || !AssetDatabase.IsMainAsset(ce.Clip);

            // 1. Float Curves
            EditorCurveBinding[] floatBindings = AnimationUtility.GetCurveBindings(ce.Clip);
            foreach (var b in floatBindings)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(ce.Clip, b);
                int keys = curve != null && curve.keys != null ? curve.keys.Length : 0;
                var entry = new CurveElementEntry
                {
                    GlobalId = globalId++,
                    ClipEntryIndex = ci,
                    Clip = ce.Clip,
                    Binding = b,
                    IsObjectReference = false,
                    KeyCount = keys,
                    Category = ClassifyBinding(b, false),
                    FriendlyPropertyLabel = FormatFriendlyProperty(b, false)
                };
                bool defaultSel = !IsProtectedRootAnimatorElement(entry);
                entry.Selected = preserveSelection && prevSelectedElemKeys.Count > 0
                    ? prevSelectedElemKeys.Contains(MakeElementSelectionKey(entry))
                    : defaultSel;
                _allElements.Add(entry);
            }

            // 2. Object Reference Curves (Material差し替え等)
            EditorCurveBinding[] objBindings = AnimationUtility.GetObjectReferenceCurveBindings(ce.Clip);
            foreach (var b in objBindings)
            {
                ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(ce.Clip, b);
                var entry = new CurveElementEntry
                {
                    GlobalId = globalId++,
                    ClipEntryIndex = ci,
                    Clip = ce.Clip,
                    Binding = b,
                    IsObjectReference = true,
                    KeyCount = keys != null ? keys.Length : 0,
                    Category = ClassifyBinding(b, true),
                    FriendlyPropertyLabel = FormatFriendlyProperty(b, true)
                };
                bool defaultSel = !IsProtectedRootAnimatorElement(entry);
                entry.Selected = preserveSelection && prevSelectedElemKeys.Count > 0
                    ? prevSelectedElemKeys.Contains(MakeElementSelectionKey(entry))
                    : defaultSel;
                _allElements.Add(entry);
            }
        }

        RebuildPathGroupsFromFilteredElements(preserveGroupChecks: false);

        if (preserveSelection && (prevSelectedPaths.Count > 0 || prevUnselectedPaths.Count > 0))
        {
            foreach (var g in _pathGroups)
            {
                if (prevSelectedPaths.Contains(g.Path)) g.Selected = true;
                else if (prevUnselectedPaths.Contains(g.Path)) g.Selected = false;
            }
        }
    }

    private void RebuildPathGroupsFromFilteredElements(bool preserveGroupChecks)
    {
        var oldChecks = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (preserveGroupChecks)
        {
            foreach (var g in _pathGroups)
                oldChecks[g.Path] = g.Selected;
        }

        _pathGroups.Clear();
        var map = new Dictionary<string, PathGroupEntry>(StringComparer.Ordinal);

        foreach (var el in _allElements)
        {
            if (!MatchesCategoryFilter(el, _categoryFilter))
                continue;

            string p = el.Binding.path ?? "";
            if (!map.TryGetValue(p, out var grp))
            {
                grp = new PathGroupEntry { Path = p };
                map[p] = grp;
                _pathGroups.Add(grp);
            }
            grp.Elements.Add(el);
            grp.TotalElementCount++;
            if (el.Category == PropertyCategoryFilter.BlendShape)
                grp.BlendShapeCount++;
        }

        foreach (var grp in _pathGroups)
        {
            grp.ClipCount = grp.Elements.Select(e => e.Clip).Distinct().Count();
            grp.IsRootAnimatorCurveOnly = string.IsNullOrEmpty(grp.Path) && grp.Elements.All(IsProtectedRootAnimatorElement);

            if (oldChecks.TryGetValue(grp.Path, out bool prev))
                grp.Selected = prev;
            else
                grp.Selected = !grp.IsRootAnimatorCurveOnly;

            if (grp.BlendShapeCount == grp.TotalElementCount && grp.BlendShapeCount > 0)
                grp.SummaryBadge = $"シェイプキー×{grp.BlendShapeCount} ({grp.ClipCount} anim)";
            else if (grp.BlendShapeCount > 0)
                grp.SummaryBadge = $"計{grp.TotalElementCount} (BS×{grp.BlendShapeCount}) / {grp.ClipCount} anim";
            else
                grp.SummaryBadge = $"要素×{grp.TotalElementCount} ({grp.ClipCount} anim)";
        }

        _pathGroups.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
    }

    private void SyncPathGroupSelectionToElements()
    {
        var selectedPathSet = new HashSet<string>(
            _pathGroups.Where(g => g.Selected).Select(g => g.Path),
            StringComparer.Ordinal);

        foreach (var el in _allElements)
        {
            if (!MatchesCategoryFilter(el, _categoryFilter))
            {
                el.Selected = false;
                continue;
            }
            el.Selected = selectedPathSet.Contains(el.Binding.path ?? "")
                          && (!IsProtectedRootAnimatorElement(el) || _includeRootAnimatorBindings);
        }
    }

    private static string MakeElementSelectionKey(CurveElementEntry e)
    {
        return $"{e.Clip.GetInstanceID()}|{e.IsObjectReference}|{e.Binding.path}|{e.Binding.type?.FullName}|{e.Binding.propertyName}";
    }

    private static bool IsProtectedRootAnimatorElement(CurveElementEntry e)
    {
        return string.IsNullOrEmpty(e.Binding.path) && e.Binding.type == typeof(Animator);
    }

    private static PropertyCategoryFilter ClassifyBinding(EditorCurveBinding b, bool isObjRef)
    {
        string prop = b.propertyName ?? "";
        if (b.type == typeof(SkinnedMeshRenderer) && prop.StartsWith("blendShape.", StringComparison.Ordinal))
            return PropertyCategoryFilter.BlendShape;
        if (b.type == typeof(GameObject) && prop == "m_IsActive")
            return PropertyCategoryFilter.GameObjectActive;
        if (typeof(Renderer).IsAssignableFrom(b.type) &&
            (isObjRef || prop.StartsWith("material.", StringComparison.Ordinal) || prop.StartsWith("m_Materials", StringComparison.Ordinal)))
            return PropertyCategoryFilter.MaterialAndShader;
        if (b.type == typeof(Transform) || b.type == typeof(RectTransform))
            return PropertyCategoryFilter.Transform;
        return PropertyCategoryFilter.Other;
    }

    private static bool MatchesCategoryFilter(CurveElementEntry el, PropertyCategoryFilter filter)
    {
        return filter == PropertyCategoryFilter.All || el.Category == filter;
    }

    private static string FormatFriendlyProperty(EditorCurveBinding b, bool isObjRef)
    {
        string prop = b.propertyName ?? "";
        if (b.type == typeof(SkinnedMeshRenderer) && prop.StartsWith("blendShape.", StringComparison.Ordinal))
            return $"シェイプキー: {prop.Substring("blendShape.".Length)}";
        if (b.type == typeof(GameObject) && prop == "m_IsActive")
            return "GameObject Active (表示ON/OFF)";
        if (isObjRef && typeof(Renderer).IsAssignableFrom(b.type) && prop.StartsWith("m_Materials", StringComparison.Ordinal))
            return $"Material差替 ({prop})";
        string typeName = b.type != null ? b.type.Name : "Unknown";
        return $"{typeName}.{prop}";
    }

    // ─────────────────────────────────────────────────────────
    //  表示フィルタ・Shift範囲選択ヘルパー
    // ─────────────────────────────────────────────────────────

    private List<PathGroupEntry> GetVisiblePathGroups()
    {
        if (string.IsNullOrWhiteSpace(_searchFilter))
            return _pathGroups;

        string q = _searchFilter.Trim();
        return _pathGroups.Where(g =>
            g.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
            g.Elements.Any(e => e.FriendlyPropertyLabel.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                e.Binding.propertyName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
    }

    private List<CurveElementEntry> GetVisibleIndividualElements()
    {
        string q = string.IsNullOrWhiteSpace(_searchFilter) ? "" : _searchFilter.Trim();
        int targetClipIdx = _individualClipFilterIndex - 1;

        var list = new List<CurveElementEntry>(_allElements.Count);
        foreach (var el in _allElements)
        {
            if (targetClipIdx >= 0 && el.ClipEntryIndex != targetClipIdx)
                continue;
            if (!MatchesCategoryFilter(el, _categoryFilter))
                continue;
            if (!string.IsNullOrEmpty(q))
            {
                bool match = el.Binding.path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                             || el.FriendlyPropertyLabel.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                             || el.Binding.propertyName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                             || (el.Clip != null && el.Clip.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!match) continue;
            }
            list.Add(el);
        }
        return list;
    }

    private void ApplyRangeOrSingleTogglePathGroups(List<PathGroupEntry> visible, int clickedIndex, bool targetValue)
    {
        bool shift = Event.current != null && Event.current.shift;
        if (shift && _lastClickedPathIndex >= 0 && _lastClickedPathIndex < visible.Count)
        {
            int from = Mathf.Min(_lastClickedPathIndex, clickedIndex);
            int to = Mathf.Max(_lastClickedPathIndex, clickedIndex);
            for (int i = from; i <= to; i++)
            {
                if (!visible[i].IsRootAnimatorCurveOnly || _includeRootAnimatorBindings)
                    visible[i].Selected = targetValue;
            }
        }
        else
        {
            visible[clickedIndex].Selected = targetValue;
        }
        _lastClickedPathIndex = clickedIndex;
    }

    private void ApplyRangeOrSingleToggleElements(List<CurveElementEntry> visible, int clickedIndex, bool targetValue)
    {
        bool shift = Event.current != null && Event.current.shift;
        if (shift && _lastClickedElementVisibleIndex >= 0 && _lastClickedElementVisibleIndex < visible.Count)
        {
            int from = Mathf.Min(_lastClickedElementVisibleIndex, clickedIndex);
            int to = Mathf.Max(_lastClickedElementVisibleIndex, clickedIndex);
            for (int i = from; i <= to; i++)
            {
                if (!IsProtectedRootAnimatorElement(visible[i]) || _includeRootAnimatorBindings)
                    visible[i].Selected = targetValue;
            }
        }
        else
        {
            visible[clickedIndex].Selected = targetValue;
        }
        _lastClickedElementVisibleIndex = clickedIndex;
    }

    // ─────────────────────────────────────────────────────────
    //  Hierarchy 連携・Missing 検証・移動先自動探索
    // ─────────────────────────────────────────────────────────

    private void AssignAvatarRootFromSelection()
    {
        GameObject active = Selection.activeGameObject;
        if (active == null)
        {
            SetStatus("Hierarchy上でアバター（またはその子オブジェクト）を選択してください。", MessageType.Info);
            return;
        }

        Animator anim = active.GetComponentInParent<Animator>();
        _avatarRoot = anim != null ? anim.transform : active.transform.root;
        _autoDetectedPathMap.Clear();
        _useAutoDetectedMap = false;
        SetStatus($"基準アバターRootに「{_avatarRoot.name}」をセットしました。", MessageType.Info);
    }

    private void AutoFillDirectPathFromTransform(Transform targetTf)
    {
        if (targetTf == null) return;

        if (_avatarRoot == null || (!targetTf.IsChildOf(_avatarRoot) && targetTf != _avatarRoot))
        {
            Animator parentAnim = targetTf.GetComponentInParent<Animator>();
            if (parentAnim != null && parentAnim.transform != targetTf)
                _avatarRoot = parentAnim.transform;
            else if (targetTf.root != null && targetTf.root != targetTf)
                _avatarRoot = targetTf.root;
        }

        if (_avatarRoot != null && targetTf.IsChildOf(_avatarRoot))
        {
            _directTargetPathText = AnimationUtility.CalculateTransformPath(targetTf, _avatarRoot);
            SetStatus($"「{_avatarRoot.name}」からの相対パス `{_directTargetPathText}` を自動入力しました。", MessageType.Info);
        }
        else
        {
            _directTargetPathText = targetTf.name;
            SetStatus($"オブジェクト名 `{_directTargetPathText}` をセットしました（①に基準アバターRootを指定するとフル相対パスを自動計算します）。", MessageType.Info);
        }
    }

    private void AutoFillPrependFolderFromTransform(Transform droppedTf)
    {
        if (droppedTf == null) return;

        if (_avatarRoot == null || !droppedTf.IsChildOf(_avatarRoot))
        {
            Animator parentAnim = droppedTf.GetComponentInParent<Animator>();
            if (parentAnim != null && parentAnim.transform != droppedTf)
                _avatarRoot = parentAnim.transform;
        }

        if (_avatarRoot != null && droppedTf.IsChildOf(_avatarRoot) && droppedTf != _avatarRoot)
        {
            string fullRel = AnimationUtility.CalculateTransformPath(droppedTf, _avatarRoot);
            // もしドロップされたオブジェクトが現在選択中の末尾名（例: Body）自身なら、その親パス部分だけを抽出！
            string leafName = droppedTf.name;
            bool matchesExistingLeaf = _pathGroups.Any(g => g.Selected && GetLeafSegment(g.Path) == leafName);
            if (matchesExistingLeaf && fullRel.Contains("/"))
            {
                int lastSlash = fullRel.LastIndexOf('/');
                _prependFolderText = fullRel.Substring(0, lastSlash);
            }
            else
            {
                _prependFolderText = fullRel;
            }
            SetStatus($"追加する親階層 `{_prependFolderText}` を自動入力しました。", MessageType.Info);
        }
        else
        {
            _prependFolderText = droppedTf.name;
        }
    }

    private PathValidationState ValidatePathGroupOnAvatar(string path, List<CurveElementEntry> sampleElements)
    {
        if (_avatarRoot == null) return PathValidationState.Unknown;
        Transform found = string.IsNullOrEmpty(path) ? _avatarRoot : _avatarRoot.Find(path);
        if (found == null) return PathValidationState.MissingObject;

        if (sampleElements != null)
        {
            foreach (var el in sampleElements)
            {
                if (!HasRequiredComponentAndProperty(found, el.Binding))
                    return PathValidationState.MissingPropertyOrComponent;
            }
        }
        return PathValidationState.Valid;
    }

    private PathValidationState ValidateSingleElementOnAvatar(string path, EditorCurveBinding binding)
    {
        if (_avatarRoot == null) return PathValidationState.Unknown;
        Transform found = string.IsNullOrEmpty(path) ? _avatarRoot : _avatarRoot.Find(path);
        if (found == null) return PathValidationState.MissingObject;
        return HasRequiredComponentAndProperty(found, binding)
            ? PathValidationState.Valid
            : PathValidationState.MissingPropertyOrComponent;
    }

    private static bool HasRequiredComponentAndProperty(Transform target, EditorCurveBinding b)
    {
        if (target == null || b.type == null) return false;
        if (b.type == typeof(GameObject) || b.type == typeof(Transform))
            return true;

        Component comp = target.GetComponent(b.type);
        if (comp == null) return false;

        if (comp is SkinnedMeshRenderer smr && b.propertyName != null && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
        {
            string bsName = b.propertyName.Substring("blendShape.".Length);
            if (smr.sharedMesh == null || smr.sharedMesh.GetBlendShapeIndex(bsName) < 0)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 基準アバター配下を探索し、Missingになっている各パスの移動先（同名オブジェクト＋対応コンポーネント/BlendShape一致）を自動検出する。
    /// </summary>
    private void RunAutoDetectPathsFromAvatarHierarchy()
    {
        if (_avatarRoot == null)
        {
            SetStatus("①に基準アバターRootを指定してください。", MessageType.Warning);
            return;
        }

        _autoDetectedPathMap.Clear();
        Transform[] allDescendants = _avatarRoot.GetComponentsInChildren<Transform>(true);

        int resolvedCount = 0;
        foreach (var grp in _pathGroups)
        {
            if (string.IsNullOrEmpty(grp.Path) || grp.IsRootAnimatorCurveOnly)
                continue;

            // 既にアバター上で完全に有効なパスはスキップ
            if (ValidatePathGroupOnAvatar(grp.Path, grp.Elements) == PathValidationState.Valid)
                continue;

            string leaf = GetLeafSegment(grp.Path);
            Transform bestMatch = null;
            int bestScore = -1;

            foreach (var tf in allDescendants)
            {
                if (tf == _avatarRoot || tf.name != leaf) continue;

                int score = 1;
                int matchedElements = 0;
                foreach (var el in grp.Elements)
                {
                    if (HasRequiredComponentAndProperty(tf, el.Binding))
                        matchedElements++;
                }

                if (grp.Elements.Count > 0 && matchedElements == grp.Elements.Count)
                    score += 100;
                else if (matchedElements > 0)
                    score += 30 + matchedElements;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = tf;
                }
            }

            if (bestMatch != null)
            {
                string relPath = AnimationUtility.CalculateTransformPath(bestMatch, _avatarRoot);
                if (!string.Equals(relPath, grp.Path, StringComparison.Ordinal))
                {
                    _autoDetectedPathMap[grp.Path] = relPath;
                    grp.Selected = true;
                    resolvedCount++;
                }
            }
        }

        if (resolvedCount > 0)
        {
            _useAutoDetectedMap = true;
            if (_selectionMode == SelectionViewMode.ByIndividualElement)
                SyncPathGroupSelectionToElements();
            SetStatus($"アバター階層から {resolvedCount} 件の移動先パスを自動検出しました！プレビューを確認して実行ボタンを押してください。", MessageType.Info);
        }
        else
        {
            _useAutoDetectedMap = false;
            SetStatus("Missingパスの移動先候補がアバター階層内に見つかりませんでした（すべて既に正常か、同名オブジェクトが存在しません）。", MessageType.Info);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  パス変換ロジック
    // ─────────────────────────────────────────────────────────

    private string ComputePreviewPath(string originalPath)
    {
        string orig = originalPath ?? "";

        if (_useAutoDetectedMap && _autoDetectedPathMap.TryGetValue(orig, out string autoMapped))
            return NormalizeAnimPath(autoMapped);

        switch (_editRuleMode)
        {
            case EditRuleMode.PrependParent:
            {
                string prefix = NormalizeAnimPath(_prependFolderText);
                if (string.IsNullOrEmpty(prefix))
                    return orig;

                if (string.IsNullOrEmpty(orig))
                    return prefix;

                if (_prependInsertMode == PrependInsertMode.AtRoot || !orig.Contains("/"))
                    return $"{prefix}/{orig}";

                int lastSlash = orig.LastIndexOf('/');
                string parentPart = orig.Substring(0, lastSlash);
                string leafPart = orig.Substring(lastSlash + 1);
                return $"{parentPart}/{prefix}/{leafPart}";
            }

            case EditRuleMode.SetDirectPath:
            {
                if (string.IsNullOrWhiteSpace(_directTargetPathText))
                    return orig;
                return NormalizeAnimPath(_directTargetPathText);
            }

            case EditRuleMode.StripParent:
            {
                if (string.IsNullOrEmpty(orig))
                    return orig;

                string reqPrefix = NormalizeAnimPath(_stripPrefixText);
                if (!string.IsNullOrEmpty(reqPrefix))
                {
                    if (string.Equals(orig, reqPrefix, StringComparison.Ordinal))
                        return "";
                    if (orig.StartsWith(reqPrefix + "/", StringComparison.Ordinal))
                        return orig.Substring(reqPrefix.Length + 1);
                    return orig;
                }

                string[] parts = orig.Split('/');
                if (_stripDepthCount >= parts.Length)
                    return parts[parts.Length - 1]; // オブジェクト名自体は残す安全設計
                return string.Join("/", parts.Skip(_stripDepthCount));
            }

            case EditRuleMode.FindAndReplace:
            {
                if (string.IsNullOrEmpty(_findText))
                    return orig;

                string findNorm = NormalizeAnimPath(_findText);
                string repNorm = NormalizeAnimPath(_replaceText);

                if (_replaceMatchMode == ReplaceMatchMode.ExactFullPath)
                {
                    return string.Equals(orig, findNorm, StringComparison.Ordinal) ? repNorm : orig;
                }
                if (_replaceMatchMode == ReplaceMatchMode.LeadingSegment)
                {
                    if (string.Equals(orig, findNorm, StringComparison.Ordinal))
                        return repNorm;
                    if (!string.IsNullOrEmpty(findNorm) && orig.StartsWith(findNorm + "/", StringComparison.Ordinal))
                    {
                        string rest = orig.Substring(findNorm.Length + 1);
                        return string.IsNullOrEmpty(repNorm) ? rest : $"{repNorm}/{rest}";
                    }
                    return orig;
                }

                // Substring
                return NormalizeAnimPath(orig.Replace(_findText, _replaceText ?? ""));
            }

            default:
                return orig;
        }
    }

    private static string NormalizeAnimPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Trim().Replace('\\', '/');
        while (s.Contains("//"))
            s = s.Replace("//", "/");
        return s.Trim('/');
    }

    private static string GetLeafSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        int idx = path.LastIndexOf('/');
        return idx >= 0 ? path.Substring(idx + 1) : path;
    }

    // ─────────────────────────────────────────────────────────
    //  変更対象の集計＆2フェーズ安全な一括書き換え実行
    // ─────────────────────────────────────────────────────────

    private HashSet<int> CollectTargetedElementGlobalIds()
    {
        var ids = new HashSet<int>();
        if (_selectionMode == SelectionViewMode.ByPathGroup)
        {
            foreach (var g in _pathGroups)
            {
                if (!g.Selected) continue;
                if (g.IsRootAnimatorCurveOnly && !_includeRootAnimatorBindings) continue;
                foreach (var el in g.Elements)
                {
                    if (!IsProtectedRootAnimatorElement(el) || _includeRootAnimatorBindings)
                        ids.Add(el.GlobalId);
                }
            }
        }
        else
        {
            foreach (var el in _allElements)
            {
                if (!el.Selected) continue;
                if (!IsProtectedRootAnimatorElement(el) || _includeRootAnimatorBindings)
                    ids.Add(el.GlobalId);
            }
        }
        return ids;
    }

    private void CountPendingChanges(
        out int affectedClipCount,
        out int affectedCurveCount,
        out int collisionCount,
        out bool anyReadOnlyClipAffected)
    {
        affectedClipCount = 0;
        affectedCurveCount = 0;
        collisionCount = 0;
        anyReadOnlyClipAffected = false;

        if (_allElements.Count == 0) return;

        HashSet<int> targetIds = CollectTargetedElementGlobalIds();
        if (targetIds.Count == 0) return;

        var byClip = _allElements.GroupBy(e => e.ClipEntryIndex);
        foreach (var clipGroup in byClip)
        {
            int changedInClip = 0;
            var occupiedAfter = new HashSet<string>(StringComparer.Ordinal);

            foreach (var el in clipGroup)
            {
                string finalPath = el.Binding.path ?? "";
                if (targetIds.Contains(el.GlobalId))
                {
                    string candidate = ComputePreviewPath(finalPath);
                    if (!string.Equals(candidate, finalPath, StringComparison.Ordinal))
                    {
                        finalPath = candidate;
                        changedInClip++;
                    }
                }

                string sig = $"{el.IsObjectReference}|{finalPath}|{el.Binding.type?.FullName}|{el.Binding.propertyName}";
                if (!occupiedAfter.Add(sig))
                    collisionCount++;
            }

            if (changedInClip > 0)
            {
                affectedClipCount++;
                affectedCurveCount += changedInClip;
                if (_clips[clipGroup.Key].IsReadOnly)
                    anyReadOnlyClipAffected = true;
            }
        }
    }

    private void ExecutePathAdjustment()
    {
        HashSet<int> targetIds = CollectTargetedElementGlobalIds();
        if (targetIds.Count == 0)
        {
            SetStatus("変更対象の要素が選択されていません。", MessageType.Warning);
            return;
        }

        _results.Clear();

        // クリップごとに変更対象があるものを抽出
        var clipsToProcess = new List<(ClipEntry entry, List<CurveElementEntry> elements)>();
        foreach (var grp in _allElements.GroupBy(e => e.ClipEntryIndex))
        {
            bool hasActualChange = grp.Any(el =>
                targetIds.Contains(el.GlobalId) &&
                !string.Equals(ComputePreviewPath(el.Binding.path), el.Binding.path ?? "", StringComparison.Ordinal));

            if (hasActualChange)
                clipsToProcess.Add((_clips[grp.Key], grp.ToList()));
        }

        if (clipsToProcess.Count == 0)
        {
            SetStatus("変更前と変更後のパスが同一のため、更新が必要な要素はありません。", MessageType.Info);
            return;
        }

        try
        {
            if (_saveOutputMode == SaveOutputMode.OverwriteInPlace)
            {
                AnimationClip[] undoTargets = clipsToProcess.Select(x => x.entry.Clip).ToArray();
                Undo.RegisterCompleteObjectUndo(undoTargets, "Adjust Animation Paths");

                int totalUpdatedCurves = 0;
                foreach (var (entry, elemList) in clipsToProcess)
                {
                    int updated = ApplyRemapToClipTwoPhase(entry.Clip, elemList, targetIds);
                    EditorUtility.SetDirty(entry.Clip);
                    totalUpdatedCurves += updated;

                    _results.Add(new ExecutionResultItem
                    {
                        OutputClip = entry.Clip,
                        ClipName = entry.Clip.name,
                        AssetPath = entry.AssetPath,
                        UpdatedCurveCount = updated,
                        WasDuplicated = false
                    });
                }

                AssetDatabase.SaveAssets();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

                _useAutoDetectedMap = false;
                _autoDetectedPathMap.Clear();
                ScanAllClips(preserveSelection: false);
                _foldResults = true;

                SetStatus(
                    $"完了！ {_results.Count} 個の .anim ファイル（計 {totalUpdatedCurves} 要素）のパスを一括更新しました（Ctrl+Z で元に戻せます）。",
                    MessageType.Info);
            }
            else
            {
                string outFolder = string.IsNullOrEmpty(_copySaveFolder)
                    ? DetermineDefaultCopySaveFolder()
                    : _copySaveFolder.Trim().TrimEnd('/', '\\');

                EnsureAssetFolderExists(outFolder);

                int totalUpdatedCurves = 0;
                foreach (var (entry, elemList) in clipsToProcess)
                {
                    AnimationClip copyClip = Instantiate(entry.Clip);
                    copyClip.name = entry.Clip.name;

                    int updated = ApplyRemapToClipTwoPhase(copyClip, elemList, targetIds);
                    string savePath = AssetDatabase.GenerateUniqueAssetPath($"{outFolder}/{SanitizeFileName(entry.Clip.name)}.anim");
                    AssetDatabase.CreateAsset(copyClip, savePath);
                    totalUpdatedCurves += updated;

                    _results.Add(new ExecutionResultItem
                    {
                        OutputClip = copyClip,
                        ClipName = Path.GetFileNameWithoutExtension(savePath),
                        AssetPath = savePath,
                        UpdatedCurveCount = updated,
                        WasDuplicated = true
                    });
                }

                AssetDatabase.SaveAssets();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                _foldResults = true;

                if (_results.Count > 0 && _results[0].OutputClip != null)
                    EditorGUIUtility.PingObject(_results[0].OutputClip);

                SetStatus(
                    $"完了！ パス変更済みの .anim を {_results.Count} 件（計 {totalUpdatedCurves} 要素変更）複製作成しました: {outFolder}",
                    MessageType.Info);
            }
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            SetStatus($"パス更新中にエラーが発生しました: {ex.Message}", MessageType.Error);
        }
    }

    /// <summary>
    /// 連鎖移動（A→B かつ B→C）や入れ替え（A↔B）でも既存カーブが消えないよう、
    /// 全移動対象カーブをメモリに退避→旧バインディング一括削除→新バインディング一括設定の2フェーズで書き換える。
    /// </summary>
    private int ApplyRemapToClipTwoPhase(
        AnimationClip targetClip,
        List<CurveElementEntry> clipElements,
        HashSet<int> targetedGlobalIds)
    {
        var floatMoves = new List<(EditorCurveBinding oldB, EditorCurveBinding newB, AnimationCurve curve)>();
        var objRefMoves = new List<(EditorCurveBinding oldB, EditorCurveBinding newB, ObjectReferenceKeyframe[] keys)>();

        foreach (var el in clipElements)
        {
            if (!targetedGlobalIds.Contains(el.GlobalId))
                continue;

            string oldPath = el.Binding.path ?? "";
            string newPath = ComputePreviewPath(oldPath);
            if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
                continue;

            EditorCurveBinding newBinding = el.Binding;
            newBinding.path = newPath;

            if (el.IsObjectReference)
            {
                ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(targetClip, el.Binding);
                objRefMoves.Add((el.Binding, newBinding, keys));
            }
            else
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(targetClip, el.Binding);
                floatMoves.Add((el.Binding, newBinding, curve));
            }
        }

        // Phase 1: 旧バインディングをすべて削除
        foreach (var (oldB, _, _) in floatMoves)
            AnimationUtility.SetEditorCurve(targetClip, oldB, null);

        foreach (var (oldB, _, _) in objRefMoves)
            AnimationUtility.SetObjectReferenceCurve(targetClip, oldB, null);

        // Phase 2: 新バインディングをすべて書き込み
        foreach (var (_, newB, curve) in floatMoves)
            AnimationUtility.SetEditorCurve(targetClip, newB, curve);

        foreach (var (_, newB, keys) in objRefMoves)
            AnimationUtility.SetObjectReferenceCurve(targetClip, newB, keys);

        return floatMoves.Count + objRefMoves.Count;
    }

    // ─────────────────────────────────────────────────────────
    //  パス・フォルダユーティリティ
    // ─────────────────────────────────────────────────────────

    private string DetermineDefaultCopySaveFolder()
    {
        foreach (var ce in _clips)
        {
            if (ce != null && ce.Enabled && !string.IsNullOrEmpty(ce.AssetPath) && ce.AssetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                string dir = Path.GetDirectoryName(ce.AssetPath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir))
                    return $"{dir}/Adjusted";
            }
        }
        return DEFAULT_COPY_DIR;
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

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Animation";
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
}
#endif
