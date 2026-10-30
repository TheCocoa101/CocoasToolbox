#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// New Blendshape From Mix — Cocoa's Toolbox
/// 複数Blendshapeを任意Weightで合成し、元MeshアセットへBlendshapeとして追加。
///
/// [プレビュー方式]
/// ・origMesh を完全複製した TempMesh（HideAndDontSave）を生成
/// ・既存 Blendshape を全コピーしたうえで Mix デルタを専用 Blendshape として追加
/// ・SMR.sharedMesh を TempMesh に差し替え、プレビュー用 Weight を 100 にセット
/// ・スキニング（ボーン変形）は Unity に任せるため BakeMesh() は不使用
///   → MA Scale Adjust 等でボーンが動いていても正しくプレビューできる
/// ・停止時は元 sharedMesh に戻し TempMesh を DestroyImmediate（プロジェクトに跡なし）
/// </summary>
public class NewBlendshapeFromMix : EditorWindow
{
    // ─────────────────────────────────────────────────────────
    //  定数
    // ─────────────────────────────────────────────────────────

    private const float DIVIDER_H = 5f;
    private const float SECTION_MIN_H = 40f;
    private const float HEADER_H = 38f;
    private const float STATUS_H = 40f;

    // ─────────────────────────────────────────────────────────
    //  セクション高さ管理
    // ─────────────────────────────────────────────────────────

    private float _h1 = 160f;
    private float _h2 = 260f;
    private float _h3 = 180f;

    private bool _fold1 = true;
    private bool _fold2 = true;
    private bool _fold3 = true;

    private enum DragTarget { None, D12, D23 }
    private DragTarget _drag = DragTarget.None;
    private float _dragStartY;
    private float _dragStartH1, _dragStartH2, _dragStartH3;

    // ─────────────────────────────────────────────────────────
    //  ターゲット
    // ─────────────────────────────────────────────────────────

    private GameObject _rootObject;
    private List<SkinnedMeshRenderer> _smrList = new List<SkinnedMeshRenderer>();
    private bool[] _smrEnabled;
    private bool _useRootMode = true;

    // ─────────────────────────────────────────────────────────
    //  Blendshapeデータ
    // ─────────────────────────────────────────────────────────

    private List<string> _bsNames = new List<string>();
    private float[] _weights;
    private bool[] _enabled;
    private string _filter = "";

    // ─────────────────────────────────────────────────────────
    //  プレビュー状態
    // ─────────────────────────────────────────────────────────

    private bool _previewing;

    // SMR → プレビュー前の元 sharedMesh
    private readonly Dictionary<SkinnedMeshRenderer, Mesh> _origMesh
        = new Dictionary<SkinnedMeshRenderer, Mesh>();

    // SMR → メモリ上の TempMesh（HideAndDontSave）
    private readonly Dictionary<SkinnedMeshRenderer, Mesh> _tempMesh
        = new Dictionary<SkinnedMeshRenderer, Mesh>();

    // ─────────────────────────────────────────────────────────
    //  ベイク
    // ─────────────────────────────────────────────────────────

    private string _fileSavePath = ""; // 保存先フォルダ（Assets/... 相対パス）

    private string _newShapeName = "NewShape";
    private string _statusMessage = "";
    private MessageType _statusType = MessageType.Info;

    // ─────────────────────────────────────────────────────────
    //  スクロール・フォールド
    // ─────────────────────────────────────────────────────────

    private Vector2 _scrollSMR;
    private Vector2 _scrollBS;
    private bool _foldTargetList;       // セクション③ 「対象メッシュ」リストの開閉
    private Vector2 _scrollTargetList;  // 対象メッシュリストのスクロール

    // ─────────────────────────────────────────────────────────
    //  メニュー登録
    // ─────────────────────────────────────────────────────────

    [MenuItem("Tools/Cocoa's Toolbox/New Blendshape From Mix")]
    public static void Open()
    {
        var win = GetWindow<NewBlendshapeFromMix>(false, "Blendshape Mix", true);
        win.minSize = new Vector2(380f, 500f);
        win.Show();
    }

    private void OnDestroy() => StopPreview();
    private void OnDisable() => StopPreview();

    // ─────────────────────────────────────────────────────────
    //  OnGUI
    // ─────────────────────────────────────────────────────────

    private void OnGUI()
    {
        HandleDividerDrag();

        float y = 0f;

        DrawHeader(new Rect(0, y, position.width, HEADER_H));
        y += HEADER_H;

        float h1 = _fold1 ? _h1 : EditorGUIUtility.singleLineHeight + 2;
        float h2 = _fold2 ? _h2 : EditorGUIUtility.singleLineHeight + 2;
        float h3 = _fold3 ? _h3 : EditorGUIUtility.singleLineHeight + 2;

        DrawSection1(new Rect(0, y, position.width, h1));
        y += h1;

        DrawDivider(new Rect(0, y, position.width, DIVIDER_H), DragTarget.D12);
        y += DIVIDER_H;

        DrawSection2(new Rect(0, y, position.width, h2));
        y += h2;

        DrawDivider(new Rect(0, y, position.width, DIVIDER_H), DragTarget.D23);
        y += DIVIDER_H;

        DrawSection3(new Rect(0, y, position.width, h3));
        y += h3;

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            float statusY = position.height - STATUS_H;
            GUILayout.BeginArea(new Rect(0, statusY, position.width, STATUS_H));
            EditorGUILayout.HelpBox(_statusMessage, _statusType);
            GUILayout.EndArea();
        }
    }

    // ─────────────────────────────────────────────────────────
    //  ヘッダー
    // ─────────────────────────────────────────────────────────

    private void DrawHeader(Rect r)
    {
        EditorGUI.LabelField(new Rect(r.x + 8, r.y + 4, r.width - 16, 20),
            "New Blendshape From Mix", EditorStyles.boldLabel);
        EditorGUI.LabelField(new Rect(r.x + 8, r.y + 22, r.width - 16, 14),
            "Cocoa's Toolbox", EditorStyles.miniLabel);
        EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1),
            EditorStyles.label.normal.textColor * 0.3f);
    }

    // ─────────────────────────────────────────────────────────
    //  仕切りバー
    // ─────────────────────────────────────────────────────────

    private void DrawDivider(Rect r, DragTarget target)
    {
        EditorGUI.DrawRect(r, new Color(0.35f, 0.35f, 0.35f, 1f));
        float dotW = 24f;
        EditorGUI.DrawRect(new Rect(r.x + (r.width - dotW) * 0.5f, r.y + 1f, dotW, 3f),
            new Color(0.6f, 0.6f, 0.6f, 1f));
        EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeVertical);
    }

    // ─────────────────────────────────────────────────────────
    //  ドラッグ処理
    // ─────────────────────────────────────────────────────────

    private void HandleDividerDrag()
    {
        var e = Event.current;

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            float h1 = _fold1 ? _h1 : EditorGUIUtility.singleLineHeight + 2;
            float h2 = _fold2 ? _h2 : EditorGUIUtility.singleLineHeight + 2;
            float div12 = HEADER_H + h1;
            float div23 = div12 + DIVIDER_H + h2;

            if (e.mousePosition.y >= div12 && e.mousePosition.y <= div12 + DIVIDER_H)
            {
                _drag = DragTarget.D12;
                _dragStartY = e.mousePosition.y;
                _dragStartH1 = _h1; _dragStartH2 = _h2;
                e.Use();
            }
            else if (e.mousePosition.y >= div23 && e.mousePosition.y <= div23 + DIVIDER_H)
            {
                _drag = DragTarget.D23;
                _dragStartY = e.mousePosition.y;
                _dragStartH2 = _h2; _dragStartH3 = _h3;
                e.Use();
            }
        }
        else if (e.type == EventType.MouseDrag && _drag != DragTarget.None)
        {
            float delta = e.mousePosition.y - _dragStartY;
            if (_drag == DragTarget.D12 && _fold1 && _fold2)
            {
                _h1 = Mathf.Max(SECTION_MIN_H, _dragStartH1 + delta);
                _h2 = Mathf.Max(SECTION_MIN_H, _dragStartH2 - delta);
            }
            else if (_drag == DragTarget.D23 && _fold2 && _fold3)
            {
                _h2 = Mathf.Max(SECTION_MIN_H, _dragStartH2 + delta);
                _h3 = Mathf.Max(SECTION_MIN_H, _dragStartH3 - delta);
            }
            Repaint();
            e.Use();
        }
        else if (e.type == EventType.MouseUp)
        {
            _drag = DragTarget.None;
        }
    }

    // ─────────────────────────────────────────────────────────
    //  セクションヘッダー共通
    // ─────────────────────────────────────────────────────────

    private bool DrawSectionHeader(Rect r, bool fold, string label)
    {
        float lh = EditorGUIUtility.singleLineHeight;
        var hr = new Rect(r.x, r.y, r.width, lh);
        EditorGUI.DrawRect(hr, new Color(0.22f, 0.22f, 0.22f));
        return EditorGUI.Foldout(hr, fold, "  " + label, true, EditorStyles.foldoutHeader);
    }

    // ─────────────────────────────────────────────────────────
    //  セクション① — Target / SMR選択
    // ─────────────────────────────────────────────────────────

    private void DrawSection1(Rect r)
    {
        _fold1 = DrawSectionHeader(r, _fold1, "① Target  —  SkinnedMeshRenderer");
        if (!_fold1) return;

        float lh = EditorGUIUtility.singleLineHeight;
        float sp = EditorGUIUtility.standardVerticalSpacing;
        float pad = 6f;
        float y = r.y + lh + sp;
        float w = r.width - pad * 2;

        // モード選択
        int mode = _useRootMode ? 0 : 1;
        mode = GUI.Toolbar(new Rect(r.x + pad, y, w, lh), mode,
            new[] { "GameObject から自動収集", "SMR を個別に選択" });
        _useRootMode = (mode == 0);
        y += lh + sp;

        if (_useRootMode)
        {
            EditorGUI.LabelField(new Rect(r.x + pad, y, 130f, lh), "Root GameObject");
            EditorGUI.BeginChangeCheck();
            _rootObject = (GameObject)EditorGUI.ObjectField(
                new Rect(r.x + pad + 134f, y, w - 134f, lh),
                _rootObject, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) CollectSMRsFromRoot();
            y += lh + sp;

            if (_rootObject == null)
            {
                EditorGUI.HelpBox(new Rect(r.x + pad, y, w, lh * 2),
                    "GameObjectをセットすると配下のSMRを自動収集します", MessageType.None);
                y += lh * 2 + sp;
            }
        }
        else
        {
            if (GUI.Button(new Rect(r.x + pad, y, 60f, lh), "+ 追加"))
            {
                _smrList.Add(null);
                RebuildSMREnabled();
            }
            y += lh + sp;
        }

        if (_smrList.Count > 0)
        {
            float listH = r.yMax - y - lh - sp * 2 - 2f;
            if (listH < 20f) listH = 20f;
            float rowH = lh + sp;

            _scrollSMR = GUI.BeginScrollView(
                new Rect(r.x + pad, y, w, listH), _scrollSMR,
                new Rect(0, 0, w - 14f, _smrList.Count * rowH));

            for (int i = 0; i < _smrList.Count; i++)
            {
                float ry = i * rowH;
                bool prevEn = _smrEnabled != null && i < _smrEnabled.Length && _smrEnabled[i];
                bool nextEn = GUI.Toggle(new Rect(0, ry, 18f, lh), prevEn, "");

                if (_smrEnabled != null && i < _smrEnabled.Length && prevEn != nextEn)
                {
                    _smrEnabled[i] = nextEn;
                    if (_previewing) ApplyPreview();
                    RefreshBSList();
                }

                if (_useRootMode)
                {
                    GUI.enabled = false;
                    EditorGUI.ObjectField(new Rect(20f, ry, w - 34f, lh),
                        _smrList[i], typeof(SkinnedMeshRenderer), true);
                    GUI.enabled = true;
                }
                else
                {
                    EditorGUI.BeginChangeCheck();
                    _smrList[i] = (SkinnedMeshRenderer)EditorGUI.ObjectField(
                        new Rect(20f, ry, w - 56f, lh),
                        _smrList[i], typeof(SkinnedMeshRenderer), true);
                    if (EditorGUI.EndChangeCheck()) RefreshBSList();

                    if (GUI.Button(new Rect(w - 34f, ry, 28f, lh), "✕"))
                    {
                        _smrList.RemoveAt(i);
                        RebuildSMREnabled();
                        RefreshBSList();
                        break;
                    }
                }
            }
            GUI.EndScrollView();
            y += listH + sp;

            EditorGUI.LabelField(new Rect(r.x + pad, y, w - 80f, lh),
                $"{ActiveSMRs().Count} / {_smrList.Count} SMR 有効", EditorStyles.miniLabel);
            if (GUI.Button(new Rect(r.xMax - pad - 76f, y, 76f, lh), "リスト更新"))
            {
                if (_useRootMode) CollectSMRsFromRoot(); else RefreshBSList();
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  セクション② — Blendshape Mix
    // ─────────────────────────────────────────────────────────

    private void DrawSection2(Rect r)
    {
        _fold2 = DrawSectionHeader(r, _fold2, "② Blendshape Mix  —  ウェイト設定");
        if (!_fold2) return;

        float lh = EditorGUIUtility.singleLineHeight;
        float sp = EditorGUIUtility.standardVerticalSpacing;
        float pad = 6f;
        float y = r.y + lh + sp;
        float w = r.width - pad * 2;

        if (_bsNames.Count == 0)
        {
            EditorGUI.HelpBox(new Rect(r.x + pad, y, w, lh * 2),
                "有効なSMRをセットするとBlendshapeリストが表示されます", MessageType.None);
            return;
        }

        // 検索バー
        _filter = EditorGUI.TextField(new Rect(r.x + pad, y, w - 28f, lh), _filter);
        if (GUI.Button(new Rect(r.xMax - pad - 26f, y, 26f, lh), "✕")) _filter = "";
        y += lh + sp;

        // 一括ボタン
        float bw = (w - sp * 2) / 3f;
        if (GUI.Button(new Rect(r.x + pad, y, bw, lh), "全 ON")) SetAllEnabled(true);
        if (GUI.Button(new Rect(r.x + pad + bw + sp, y, bw, lh), "全 OFF")) SetAllEnabled(false);
        if (GUI.Button(new Rect(r.x + pad + (bw + sp) * 2, y, bw, lh), "Weight リセット")) ResetWeights();
        y += lh + sp;

        // ── プレビュートグル ──
        string previewLabel = _previewing
            ? "■ プレビュー停止  （元のMeshに戻す）"
            : "▶ リアルタイムプレビュー  （既存Weight変形の上に重ねて確認）";
        bool nextPrev = GUI.Toggle(new Rect(r.x + pad, y, w, lh), _previewing, previewLabel, GUI.skin.button);
        if (nextPrev != _previewing)
        {
            _previewing = nextPrev;
            if (_previewing) StartPreview();
            else StopPreview();
        }
        y += lh + sp + 2f;

        // カラムヘッダー
        const float valW = 42f;
        const float sldW = 110f;
        const float chkW = 18f;
        float nameW = w - chkW - sldW - valW - sp * 3;
        EditorGUI.LabelField(new Rect(r.x + pad + chkW + sp, y, nameW, lh), "Blendshape", EditorStyles.miniLabel);
        EditorGUI.LabelField(new Rect(r.x + pad + chkW + nameW + sp * 2, y, sldW, lh), "Weight (0–100)", EditorStyles.miniLabel);
        EditorGUI.LabelField(new Rect(r.x + pad + chkW + nameW + sldW + sp * 3, y, valW, lh), "Val", EditorStyles.miniLabel);
        y += lh + sp;
        EditorGUI.DrawRect(new Rect(r.x + pad, y, w, 1f), new Color(0.4f, 0.4f, 0.4f));
        y += 2f;

        // Blendshapeリスト
        var filtered = new List<int>();
        for (int i = 0; i < _bsNames.Count; i++)
            if (string.IsNullOrEmpty(_filter) ||
                _bsNames[i].IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0)
                filtered.Add(i);

        float listH = r.yMax - y - lh - sp * 2;
        if (listH < 20f) listH = 20f;
        float rowH = lh + sp;

        _scrollBS = GUI.BeginScrollView(
            new Rect(r.x + pad, y, w, listH), _scrollBS,
            new Rect(0, 0, w - 14f, filtered.Count * rowH));

        bool weightChanged = false;
        for (int fi = 0; fi < filtered.Count; fi++)
        {
            int i = filtered[fi];
            float ry = fi * rowH;
            float rw = w - 14f;

            _enabled[i] = GUI.Toggle(new Rect(0, ry, chkW, lh), _enabled[i], "");
            GUI.enabled = _enabled[i];

            EditorGUI.LabelField(
                new Rect(chkW + sp, ry, rw - chkW - sldW - valW - sp * 2, lh),
                _bsNames[i]);

            float prev = _weights[i];
            _weights[i] = GUI.HorizontalSlider(
                new Rect(rw - sldW - valW - sp, ry + 2f, sldW, lh - 2f),
                _weights[i], 0f, 100f);
            _weights[i] = Mathf.Clamp(
                EditorGUI.FloatField(new Rect(rw - valW, ry, valW, lh), _weights[i]),
                0f, 100f);

            if (!Mathf.Approximately(prev, _weights[i])) weightChanged = true;

            GUI.enabled = true;
        }

        GUI.EndScrollView();

        // Weightが変わったらプレビューを再適用
        if (weightChanged && _previewing) ApplyPreview();

        y += listH + sp;
        int activeBS = _enabled != null ? _enabled.Count(e => e) : 0;
        EditorGUI.LabelField(new Rect(r.x + pad, y, w, lh),
            $"ミックス対象: {activeBS} シェイプ", EditorStyles.miniLabel);
    }

    // ─────────────────────────────────────────────────────────
    //  セクション③ — Bake
    // ─────────────────────────────────────────────────────────

    private void DrawSection3(Rect r)
    {
        _fold3 = DrawSectionHeader(r, _fold3, "③ Bake  —  シェイプキーの保存");
        if (!_fold3) return;

        float lh = EditorGUIUtility.singleLineHeight;
        float sp = EditorGUIUtility.standardVerticalSpacing;
        float pad = 6f;
        float y = r.y + lh + sp;
        float w = r.width - pad * 2;

        // 新規シェイプ名
        EditorGUI.LabelField(new Rect(r.x + pad, y, 90f, lh), "新規シェイプ名");
        _newShapeName = EditorGUI.TextField(
            new Rect(r.x + pad + 94f, y, w - 94f, lh), _newShapeName);
        y += lh + sp;

        // 保存先フォルダ
        EditorGUI.LabelField(new Rect(r.x + pad, y, 60f, lh), "保存先");
        float btnW = 28f;
        string displayPath = string.IsNullOrEmpty(_fileSavePath)
            ? "（元ファイルと同じフォルダ）" : _fileSavePath;
        EditorGUI.TextField(
            new Rect(r.x + pad + 64f, y, w - 64f - btnW - sp, lh), displayPath);
        if (GUI.Button(new Rect(r.xMax - pad - btnW, y, btnW, lh), "…"))
        {
            string abs = EditorUtility.OpenFolderPanel(
                "保存先フォルダ", "Assets", "");
            if (!string.IsNullOrEmpty(abs))
            {
                string dataPath = Application.dataPath.Replace("\\", "/");
                abs = abs.Replace("\\", "/");
                if (abs.StartsWith(dataPath))
                    _fileSavePath = "Assets" + abs.Substring(dataPath.Length);
                else
                    _fileSavePath = abs;
            }
        }
        y += lh + sp;

        // ヘルプボックス
        EditorGUI.HelpBox(new Rect(r.x + pad, y, w, lh * 2 + 4f),
            "元のFBX/Meshを変更せず、.mesh を別ファイルとして保存します。\n" +
            "ファイル名は \"元名_Mix-000.mesh\" 形式です。", MessageType.Info);
        y += lh * 2 + sp + 4f;



        // ── 対象メッシュリスト（開閉式） ──
        var affected = GetAffectedMeshList();
        if (affected.Count > 0)
        {
            string foldLabel = $"対象メッシュ ({affected.Count})";
            _foldTargetList = EditorGUI.Foldout(
                new Rect(r.x + pad, y, w, lh), _foldTargetList, foldLabel, true);
            y += lh + sp;

            if (_foldTargetList)
            {
                float listH = Mathf.Min(affected.Count * (lh + sp), lh * 5);
                float rowH = lh + sp;

                _scrollTargetList = GUI.BeginScrollView(
                    new Rect(r.x + pad + 12f, y, w - 12f, listH), _scrollTargetList,
                    new Rect(0, 0, w - 26f, affected.Count * rowH));

                for (int i = 0; i < affected.Count; i++)
                {
                    float ry = i * rowH;
                    EditorGUI.LabelField(
                        new Rect(0, ry, w - 26f, lh),
                        affected[i], EditorStyles.miniLabel);
                }

                GUI.EndScrollView();
                y += listH + sp;
            }
        }

        // ベイクボタン
        y += 2f;
        bool canBake = ActiveSMRs().Count > 0
                    && !string.IsNullOrEmpty(_newShapeName)
                    && _enabled != null && _enabled.Any(e => e);
        GUI.enabled = canBake;
        if (GUI.Button(new Rect(r.x + pad, y, w, lh + 8f), "✦  Mix して .mesh 保存  ✦"))
            BakeMix();
        GUI.enabled = true;
    }

    // ─────────────────────────────────────────────────────────
    //  プレビュー — StartPreview / ApplyPreview / StopPreview
    // ─────────────────────────────────────────────────────────

    // プレビュー用 Blendshape の名前（TempMesh 内部専用）
    private const string PREVIEW_SHAPE_NAME = "__MixPreview__";

    /// <summary>
    /// プレビュー開始。
    /// origMesh を完全複製した TempMesh を作り、そこにMixデルタをBlendshapeとして追加。
    /// SMR.sharedMesh を TempMesh に差し替え、プレビューBlendshapeのWeightを100にセット。
    /// ボーン・スキニングはUnityに任せるため、BakeMesh() は使わない。
    /// </summary>
    private void StartPreview()
    {
        _origMesh.Clear();
        DestroyTempMeshes();

        foreach (var smr in ActiveSMRs())
        {
            if (smr == null || smr.sharedMesh == null) continue;
            _origMesh[smr] = smr.sharedMesh;
        }

        ApplyPreview();
    }

    /// <summary>
    /// TempMesh を再構築して SMR に割り当てる。Weight 変更のたびに呼ばれる。
    /// 旧 TempMesh は新しい Mesh を割り当てた後に破棄する（消える瞬間をなくすため）。
    /// </summary>
    private void ApplyPreview()
    {
        foreach (var smr in ActiveSMRs())
        {
            if (smr == null) continue;
            if (!_origMesh.TryGetValue(smr, out var origMesh) || origMesh == null) continue;

            // 新しい TempMesh を生成
            var next = BuildTempMesh(origMesh);
            if (next == null) continue;

            // ① 先に SMR へ割り当て（モデルが消えないよう旧Meshより先に繋ぐ）
            smr.sharedMesh = next;

            // ② プレビュー用シェイプのウェイトを 100 にセット
            int previewIdx = next.GetBlendShapeIndex(PREVIEW_SHAPE_NAME);
            if (previewIdx >= 0)
                smr.SetBlendShapeWeight(previewIdx, 100f);

            // ③ 旧 TempMesh を破棄（SMR がもう参照していないので安全）
            if (_tempMesh.TryGetValue(smr, out var old) && old != null)
                DestroyImmediate(old);

            _tempMesh[smr] = next;
        }

        SceneView.RepaintAll();
        Repaint();
    }

    /// <summary>
    /// origMesh を完全複製し、Mix デルタを1フレームの Blendshape として追加した TempMesh を返す。
    /// ・ボーン・バインドポーズ・UV など全トポロジーを引き継ぐ
    /// ・BakeMesh() 不使用 → ボーン変形の二重適用が起きない
    /// ・HideAndDontSave → プロジェクトに一切保存されない
    /// </summary>
    private Mesh BuildTempMesh(Mesh origMesh)
    {
        int vc = origMesh.vertexCount;

        // ── Step1: Mix デルタを計算 ──
        var dV = new Vector3[vc];
        var dN = new Vector3[vc];
        var dT = new Vector3[vc];
        var tV = new Vector3[vc];
        var tN = new Vector3[vc];
        var tTan = new Vector3[vc];
        bool any = false;

        for (int si = 0; si < _bsNames.Count; si++)
        {
            if (!_enabled[si] || Mathf.Approximately(_weights[si], 0f)) continue;
            int idx = origMesh.GetBlendShapeIndex(_bsNames[si]);
            if (idx < 0) continue;

            int fc = origMesh.GetBlendShapeFrameCount(idx);
            if (fc == 0) continue;

            // 最終フレーム = 100% デルタ
            origMesh.GetBlendShapeFrameVertices(idx, fc - 1, tV, tN, tTan);

            float t = _weights[si] / 100f;
            for (int v = 0; v < vc; v++)
            {
                dV[v] += tV[v] * t;
                dN[v] += tN[v] * t;
                dT[v] += tTan[v] * t;
            }
            any = true;
        }

        // デルタが全ゼロでも TempMesh は作る（Weight 0 でも形状は維持したい）

        // ── Step2: origMesh を完全複製 ──
        // Instantiate は HideFlags を上書きするため、手動でコピーする
        var tmp = new Mesh();
        tmp.indexFormat = origMesh.vertexCount >= 65536 ? UnityEngine.Rendering.IndexFormat.UInt32 : origMesh.indexFormat;
        tmp.name = origMesh.name + "_MixPreview";
        tmp.hideFlags = HideFlags.HideAndDontSave;

        // 頂点属性をすべて引き継ぐ
        tmp.vertices = origMesh.vertices;
        tmp.normals = origMesh.normals;
        tmp.tangents = origMesh.tangents;
        tmp.uv = origMesh.uv;
        tmp.uv2 = origMesh.uv2;
        tmp.uv3 = origMesh.uv3;
        tmp.uv4 = origMesh.uv4;
        tmp.colors32 = origMesh.colors32;
        tmp.boneWeights = origMesh.boneWeights;
        tmp.bindposes = origMesh.bindposes;

        tmp.subMeshCount = origMesh.subMeshCount;
        for (int s = 0; s < origMesh.subMeshCount; s++)
            tmp.SetTriangles(origMesh.GetTriangles(s), s);

        // 元の Blendshape を全コピー（既存 Weight での変形が正しく動くよう保持する）
        for (int bi = 0; bi < origMesh.blendShapeCount; bi++)
        {
            string shapeName = origMesh.GetBlendShapeName(bi);
            int frameCount = origMesh.GetBlendShapeFrameCount(bi);
            for (int fi = 0; fi < frameCount; fi++)
            {
                float fw = origMesh.GetBlendShapeFrameWeight(bi, fi);
                var fV = new Vector3[vc];
                var fN = new Vector3[vc];
                var fTan = new Vector3[vc];
                origMesh.GetBlendShapeFrameVertices(bi, fi, fV, fN, fTan);
                tmp.AddBlendShapeFrame(shapeName, fw, fV, fN, fTan);
            }
        }

        // ── Step3: Mix デルタを専用 Blendshape として追加 ──
        tmp.AddBlendShapeFrame(PREVIEW_SHAPE_NAME, 100f, dV, dN, dT);

        tmp.RecalculateBounds();
        return tmp;
    }

    /// <summary>
    /// プレビュー停止。元の sharedMesh を復元してから TempMesh を破棄。
    /// </summary>
    private void StopPreview()
    {
        foreach (var kv in _origMesh)
        {
            if (kv.Key != null && kv.Value != null)
                kv.Key.sharedMesh = kv.Value;
        }

        DestroyTempMeshes();
        _origMesh.Clear();
        _previewing = false;
        SceneView.RepaintAll();
    }

    private void DestroyTempMeshes()
    {
        foreach (var kv in _tempMesh)
            if (kv.Value != null) DestroyImmediate(kv.Value);
        _tempMesh.Clear();
    }

    // ─────────────────────────────────────────────────────────
    //  コアロジック
    // ─────────────────────────────────────────────────────────

    private void CollectSMRsFromRoot()
    {
        if (_previewing) StopPreview();
        _smrList.Clear();
        if (_rootObject == null) { RefreshBSList(); return; }

        _smrList.AddRange(_rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true));
        RebuildSMREnabled();
        RefreshBSList();
        SetStatus($"{_smrList.Count} 件の SMR を検出しました", MessageType.Info);
    }

    private void RefreshBSList()
    {
        var actives = ActiveSMRs();
        if (actives.Count == 0)
        {
            _bsNames.Clear();
            _weights = Array.Empty<float>();
            _enabled = Array.Empty<bool>();
            Repaint(); return;
        }

        var nameSet = new HashSet<string>();
        foreach (var smr in actives)
        {
            if (smr == null || smr.sharedMesh == null) continue;
            // プレビュー中はorigMeshを参照して正しいシェイプリストを得る
            Mesh m = (_previewing && _origMesh.ContainsKey(smr)) ? _origMesh[smr] : smr.sharedMesh;
            for (int i = 0; i < m.blendShapeCount; i++)
                nameSet.Add(m.GetBlendShapeName(i));
        }

        var oldNames = _bsNames;
        var oldWeights = _weights;
        var oldEnabled = _enabled;

        _bsNames = nameSet.OrderBy(n => n).ToList();
        _weights = new float[_bsNames.Count];
        _enabled = new bool[_bsNames.Count];

        for (int i = 0; i < _bsNames.Count; i++)
        {
            int oi = oldNames != null ? oldNames.IndexOf(_bsNames[i]) : -1;
            if (oi >= 0 && oldWeights != null && oi < oldWeights.Length)
            {
                _weights[i] = oldWeights[oi];
                _enabled[i] = oldEnabled[oi];
            }
        }

        Repaint();
    }

    private void BakeMix()
    {
        // プレビュー中は停止してから書き込み（TempMesh参照ではなく元Meshに書く）
        if (_previewing) StopPreview();

        var actives = ActiveSMRs();
        int ok = 0;
        var errors = new List<string>();

        foreach (var smr in actives)
        {
            if (smr == null || smr.sharedMesh == null) continue;

            // マッチするBlendshapeがないメッシュはスキップ
            Mesh checkMesh = smr.sharedMesh;
            bool hasMatch = false;
            for (int si = 0; si < _bsNames.Count; si++)
            {
                if (!_enabled[si] || Mathf.Approximately(_weights[si], 0f)) continue;
                if (checkMesh.GetBlendShapeIndex(_bsNames[si]) >= 0) { hasMatch = true; break; }
            }
            if (!hasMatch) continue;

            string err = BakeMixToMeshFile(smr);
            if (err == null) ok++;
            else errors.Add($"{smr.name}: {err}");
        }

        RefreshBSList();

        SetStatus(
            errors.Count == 0
                ? $"{ok} 件のメッシュに「{_newShapeName}」を適用完了"
                : $"{ok} 件のメッシュに適用完了（{errors.Count} 件エラー）\n" + string.Join("\n", errors),
            errors.Count == 0 ? MessageType.Info : MessageType.Warning);
    }



    /// <summary>
    /// 非破壊モード: 元Meshを複製 → シェイプキー追加 → .mesh として保存 → SMRに割り当て
    /// 元のFBX/Meshアセットは一切変更しない。
    /// ファイル名は "元名_Mix-000.mesh" 形式で連番。
    /// </summary>
    private string BakeMixToMeshFile(SkinnedMeshRenderer smr)
    {
        var srcMesh = smr.sharedMesh;
        int vc = srcMesh.vertexCount;

        // ── Step1: Mix デルタを計算 ──
        var dV = new Vector3[vc];
        var dN = new Vector3[vc];
        var dT = new Vector3[vc];
        var tV = new Vector3[vc];
        var tN = new Vector3[vc];
        var tTan = new Vector3[vc];
        bool any = false;

        for (int si = 0; si < _bsNames.Count; si++)
        {
            if (!_enabled[si] || Mathf.Approximately(_weights[si], 0f)) continue;
            int idx = srcMesh.GetBlendShapeIndex(_bsNames[si]);
            if (idx < 0) continue;

            int fc = srcMesh.GetBlendShapeFrameCount(idx);
            if (fc == 0) continue;

            srcMesh.GetBlendShapeFrameVertices(idx, fc - 1, tV, tN, tTan);

            float t = _weights[si] / 100f;
            for (int v = 0; v < vc; v++)
            {
                dV[v] += tV[v] * t;
                dN[v] += tN[v] * t;
                dT[v] += tTan[v] * t;
            }
            any = true;
        }

        if (!any) return "ウェイトが0でないシェイプが存在しません";

        // ── Step2: 元Meshを完全複製 ──
        var newMesh = new Mesh();
        newMesh.indexFormat = srcMesh.vertexCount >= 65536 ? UnityEngine.Rendering.IndexFormat.UInt32 : srcMesh.indexFormat;
        newMesh.vertices  = srcMesh.vertices;
        newMesh.normals   = srcMesh.normals;
        newMesh.tangents  = srcMesh.tangents;
        newMesh.uv        = srcMesh.uv;
        newMesh.uv2       = srcMesh.uv2;
        newMesh.uv3       = srcMesh.uv3;
        newMesh.uv4       = srcMesh.uv4;
        newMesh.colors32   = srcMesh.colors32;
        newMesh.boneWeights = srcMesh.boneWeights;
        newMesh.bindposes  = srcMesh.bindposes;

        newMesh.subMeshCount = srcMesh.subMeshCount;
        for (int s = 0; s < srcMesh.subMeshCount; s++)
            newMesh.SetTriangles(srcMesh.GetTriangles(s), s);

        // 元の Blendshape を全コピー
        for (int bi = 0; bi < srcMesh.blendShapeCount; bi++)
        {
            string shapeName = srcMesh.GetBlendShapeName(bi);
            int frameCount = srcMesh.GetBlendShapeFrameCount(bi);
            for (int fi = 0; fi < frameCount; fi++)
            {
                float fw = srcMesh.GetBlendShapeFrameWeight(bi, fi);
                var fV   = new Vector3[vc];
                var fN   = new Vector3[vc];
                var fTan = new Vector3[vc];
                srcMesh.GetBlendShapeFrameVertices(bi, fi, fV, fN, fTan);
                newMesh.AddBlendShapeFrame(shapeName, fw, fV, fN, fTan);
            }
        }

        // 同名チェック（複製後のMeshに対して）
        if (newMesh.GetBlendShapeIndex(_newShapeName) >= 0)
            return $"「{_newShapeName}」は既に存在します";

        // ── Step3: 新規シェイプキーを追加 ──
        newMesh.AddBlendShapeFrame(_newShapeName, 100f, dV, dN, dT);
        newMesh.RecalculateBounds();

        // ── Step4: 保存先パスを決定 ──
        string srcPath = AssetDatabase.GetAssetPath(srcMesh);
        string saveDir = ResolveSaveDir(srcPath);
        EnsureFolder(saveDir);

        string srcNameNoExt = StripMixSuffix(string.IsNullOrEmpty(srcPath)
            ? srcMesh.name
            : Path.GetFileNameWithoutExtension(srcPath));
        int mixNum = ResolveNextMixNumber(saveDir, srcNameNoExt, ".mesh");
        string newFileName = $"{srcNameNoExt}_Mix-{mixNum:D3}.mesh";
        string assetPath = saveDir + "/" + newFileName;

        newMesh.name = Path.GetFileNameWithoutExtension(newFileName);

        // ── Step5: .mesh アセットとして保存 ──
        AssetDatabase.CreateAsset(newMesh, assetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // ── Step6: SMR の sharedMesh を新しい .mesh に差し替え ──
        Undo.RecordObject(smr, $"Assign .mesh: {assetPath}");
        smr.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        EditorUtility.SetDirty(smr);

        Debug.Log($"[NewBlendshapeFromMix] .mesh 保存完了: {assetPath}");
        return null;
    }

    // ─────────────────────────────────────────────────────────
    //  ファイル保存ユーティリティ
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// 保存先ディレクトリを解決する。ユーザー指定があればそれ、なければ元ファイルと同じフォルダ。
    /// </summary>
    private string ResolveSaveDir(string srcAssetPath)
    {
        if (!string.IsNullOrEmpty(_fileSavePath))
            return _fileSavePath;

        if (string.IsNullOrEmpty(srcAssetPath))
            return "Assets";

        return Path.GetDirectoryName(srcAssetPath).Replace("\\", "/");
    }

    /// <summary>
    /// フォルダが存在しなければ再帰的に作成する。
    /// </summary>
    private void EnsureFolder(string folderPath)
    {
        if (AssetDatabase.IsValidFolder(folderPath)) return;

        string[] parts = folderPath.Split('/');
        string current = parts[0]; // "Assets"
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    /// <summary>
    /// 指定フォルダ内の既存ファイルから「元名_Mix-NNN.ext」形式の最大番号を探し、
    /// 次に使うべき番号を返す。
    /// </summary>
    private int ResolveNextMixNumber(string dir, string baseName, string ext)
    {
        int maxNum = -1;
        // パターン: baseName_Mix-NNN.ext
        string prefix = baseName + "_Mix-";

        string[] guids = AssetDatabase.FindAssets("", new[] { dir });
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string fileName = Path.GetFileName(path);

            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;

            // _Mix- と .ext の間の数字部分を取得
            string numPart = fileName.Substring(prefix.Length,
                fileName.Length - prefix.Length - ext.Length);

            if (int.TryParse(numPart, out int num) && num > maxNum)
                maxNum = num;
        }

        return maxNum + 1;
    }

    /// <summary>
    /// 「Name_Mix-000」のような_Mix-NNNサフィックスを全て剤ぎ取り、元のベース名を返す。
    /// 二回以上の連続適用でサフィックスが重ならないようこれで防ぐ。
    /// </summary>
    private string StripMixSuffix(string name)
    {
        // "_Mix-" が含まれる限り繰り返す
        const string marker = "_Mix-";
        int idx;
        while ((idx = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            // markerの後がゼロ埋め数字か確認
            string rest = name.Substring(idx + marker.Length);
            if (System.Text.RegularExpressions.Regex.IsMatch(rest, @"^\d+$"))
                name = name.Substring(0, idx);
            else
                break;
        }
        return name;
    }

    // ─────────────────────────────────────────────────────────
    //  ユーティリティ
    // ─────────────────────────────────────────────────────────

    private List<SkinnedMeshRenderer> ActiveSMRs()
    {
        var r = new List<SkinnedMeshRenderer>();
        for (int i = 0; i < _smrList.Count; i++)
        {
            if (_smrList[i] == null) continue;
            bool en = _smrEnabled == null || i >= _smrEnabled.Length || _smrEnabled[i];
            if (en) r.Add(_smrList[i]);
        }
        return r;
    }

    /// <summary>
    /// 現在のMix設定で変更が入るメッシュの表示用リストを返す。
    /// 各SMRのMeshが、有効かつWeight>0のBlendshapeを1つでも持つかチェック。
    /// </summary>
    private List<string> GetAffectedMeshList()
    {
        var result = new List<string>();
        if (_enabled == null || _bsNames.Count == 0) return result;

        foreach (var smr in ActiveSMRs())
        {
            if (smr == null || smr.sharedMesh == null) continue;

            Mesh m = (_previewing && _origMesh.ContainsKey(smr)) ? _origMesh[smr] : smr.sharedMesh;
            bool hasMatch = false;

            for (int si = 0; si < _bsNames.Count; si++)
            {
                if (!_enabled[si] || Mathf.Approximately(_weights[si], 0f)) continue;
                if (m.GetBlendShapeIndex(_bsNames[si]) >= 0)
                {
                    hasMatch = true;
                    break;
                }
            }

            if (hasMatch)
            {
                string meshName = m.name;
                string assetPath = AssetDatabase.GetAssetPath(m);
                string display = string.IsNullOrEmpty(assetPath)
                    ? $"  {smr.name}  →  {meshName}"
                    : $"  {smr.name}  →  {meshName}  ({assetPath})";
                result.Add(display);
            }
        }

        return result;
    }

    private void RebuildSMREnabled()
    {
        var prev = _smrEnabled ?? Array.Empty<bool>();
        _smrEnabled = new bool[_smrList.Count];
        for (int i = 0; i < _smrEnabled.Length; i++)
            _smrEnabled[i] = i < prev.Length ? prev[i] : true;
    }

    private void SetAllEnabled(bool v) { if (_enabled != null) for (int i = 0; i < _enabled.Length; i++) _enabled[i] = v; }
    private void ResetWeights() { if (_weights != null) for (int i = 0; i < _weights.Length; i++) _weights[i] = 0f; }
    private void SetStatus(string msg, MessageType t) { _statusMessage = msg; _statusType = t; Repaint(); }
}
#endif