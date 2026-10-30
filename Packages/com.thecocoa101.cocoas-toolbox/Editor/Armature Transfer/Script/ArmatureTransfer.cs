#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Armature Transfer — Cocoa's Toolbox
/// 素体側の Armature の Transform 情報（Position/Rotation/Scale）を
/// 衣装側の Armature の同名ボーンにコピーする。
/// ボーンに付属したコンポーネントのコピーも対応。
/// VRC PhysBone (Collider含む) は別途独立オプションとして用意。
/// </summary>
public class ArmatureTransfer : EditorWindow
{
    // ─────────────────────────────────────────────────────────
    //  定数
    // ─────────────────────────────────────────────────────────

    private const float HEADER_H = 38f;
    private const float STATUS_H = 40f;

    // ─────────────────────────────────────────────────────────
    //  Source / Destination
    // ─────────────────────────────────────────────────────────

    private Transform _srcArmature = null;   // コピー元（素体）の Armature
    private Transform _dstArmature = null;   // コピー先（衣装）の Armature

    // ─────────────────────────────────────────────────────────
    //  Transform オプション
    // ─────────────────────────────────────────────────────────

    private bool _copyPos   = true;
    private bool _copyRot   = true;
    private bool _copyScale = true;

    // ─────────────────────────────────────────────────────────
    //  コンポーネントコピーオプション（汎用）
    // ─────────────────────────────────────────────────────────

    private bool _copyComponents = false;
    // VRCPhysBone / VRCPhysBoneCollider / Transform 以外の
    // 全コンポーネントを自動的にコピーする

    // ─────────────────────────────────────────────────────────
    //  VRC PhysBone オプション
    // ─────────────────────────────────────────────────────────

    private bool _copyPhysBone = false;

    // ─────────────────────────────────────────────────────────
    //  不足オブジェクトのコピーオプション
    // ─────────────────────────────────────────────────────────

    private bool _copyMissingObjects = false;

    // ─────────────────────────────────────────────────────────
    //  UI 状態
    // ─────────────────────────────────────────────────────────

    private Vector2 _scroll;
    private string _statusMessage = "";
    private MessageType _statusType = MessageType.Info;

    // ─────────────────────────────────────────────────────────
    //  結果ログ（開閉式）
    // ─────────────────────────────────────────────────────────

    private bool _foldLog = false;
    private List<string> _logLines = new List<string>();

    // ─────────────────────────────────────────────────────────
    //  メニュー登録
    // ─────────────────────────────────────────────────────────

    [MenuItem("Tools/Cocoa's Toolbox/Armature Transfer")]
    public static void Open()
    {
        var win = GetWindow<ArmatureTransfer>(false, "Armature Transfer", true);
        win.minSize = new Vector2(380f, 420f);
        win.Show();
    }

    // ─────────────────────────────────────────────────────────
    //  OnGUI
    // ─────────────────────────────────────────────────────────

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        DrawHeader();

        EditorGUILayout.Space(4f);

        DrawSourceDestination();

        EditorGUILayout.Space(6f);

        DrawTransformOptions();

        EditorGUILayout.Space(6f);

        DrawComponentOptions();

        EditorGUILayout.Space(6f);

        DrawPhysBoneOptions();

        EditorGUILayout.Space(6f);

        DrawMissingObjectOptions();

        EditorGUILayout.Space(8f);

        DrawApplyButton();

        EditorGUILayout.Space(4f);

        DrawLog();

        EditorGUILayout.EndScrollView();

        // ── ステータスメッセージ ──
        if (!string.IsNullOrEmpty(_statusMessage))
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox(_statusMessage, _statusType);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  ヘッダー
    // ─────────────────────────────────────────────────────────

    private void DrawHeader()
    {
        EditorGUILayout.LabelField("Armature Transfer", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Cocoa's Toolbox", EditorStyles.miniLabel);
        var rect = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, EditorStyles.label.normal.textColor * 0.3f);
    }

    // ─────────────────────────────────────────────────────────
    //  Source / Destination
    // ─────────────────────────────────────────────────────────

    private void DrawSourceDestination()
    {
        EditorGUILayout.LabelField("コピー元 / コピー先", EditorStyles.boldLabel);

        using (new EditorGUI.IndentLevelScope())
        {
            _srcArmature = (Transform)EditorGUILayout.ObjectField(
                "Original Armature（素体）", _srcArmature, typeof(Transform), true);
            _dstArmature = (Transform)EditorGUILayout.ObjectField(
                "衣装 Armature", _dstArmature, typeof(Transform), true);
        }

        if (_srcArmature == null || _dstArmature == null)
        {
            EditorGUILayout.HelpBox(
                "素体と衣装それぞれの Armature（Transform）を設定してください。\n" +
                "Original Armature 配下の全ボーンを走査し、衣装側の同名ボーンへコピーします。",
                MessageType.None);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Transform オプション
    // ─────────────────────────────────────────────────────────

    private void DrawTransformOptions()
    {
        EditorGUILayout.LabelField("Transform コピー", EditorStyles.boldLabel);

        using (new EditorGUI.IndentLevelScope())
        {
            EditorGUILayout.BeginHorizontal();
            _copyPos   = EditorGUILayout.ToggleLeft("Position", _copyPos, GUILayout.Width(100f));
            _copyRot   = EditorGUILayout.ToggleLeft("Rotation", _copyRot, GUILayout.Width(100f));
            _copyScale = EditorGUILayout.ToggleLeft("Scale",    _copyScale, GUILayout.Width(100f));
            EditorGUILayout.EndHorizontal();
        }
    }

    // ─────────────────────────────────────────────────────────
    //  コンポーネントコピーオプション（汎用）
    // ─────────────────────────────────────────────────────────

    private void DrawComponentOptions()
    {
        EditorGUILayout.LabelField("コンポーネントコピー（汎用）", EditorStyles.boldLabel);

        using (new EditorGUI.IndentLevelScope())
        {
            _copyComponents = EditorGUILayout.ToggleLeft(
                "コンポーネントをコピーする（VRC PhysBone 以外）", _copyComponents);

            if (_copyComponents)
            {
                EditorGUILayout.HelpBox(
                    "ボーンに付属する全コンポーネントをコピーします。\n" +
                    "VRCPhysBone / VRCPhysBoneCollider は下の専用オプションで制御されるため除外されます。",
                    MessageType.None);
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  VRC PhysBone オプション
    // ─────────────────────────────────────────────────────────

    private void DrawPhysBoneOptions()
    {
        EditorGUILayout.LabelField("VRC PhysBone コピー", EditorStyles.boldLabel);

        using (new EditorGUI.IndentLevelScope())
        {
            _copyPhysBone = EditorGUILayout.ToggleLeft(
                "VRC PhysBone をコピーする（PhysBone + Collider）", _copyPhysBone);

            if (_copyPhysBone)
            {
                EditorGUILayout.HelpBox(
                    "VRCPhysBone と VRCPhysBoneCollider を同名ボーンにコピーします。\n" +
                    "既存のコンポーネントがある場合は上書きされます。",
                    MessageType.None);
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  不足オブジェクトのコピーオプション
    // ─────────────────────────────────────────────────────────

    private void DrawMissingObjectOptions()
    {
        EditorGUILayout.LabelField("不足オブジェクトの処理", EditorStyles.boldLabel);

        using (new EditorGUI.IndentLevelScope())
        {
            _copyMissingObjects = EditorGUILayout.ToggleLeft(
                "コピー先に存在しないオブジェクトを作成する", _copyMissingObjects);

            if (_copyMissingObjects)
            {
                EditorGUILayout.HelpBox(
                    "コピー元にあるがコピー先に同名のオブジェクトが無い場合、\n" +
                    "親ボーンの下に新しいGameObjectを作成してコピーします。",
                    MessageType.None);
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  適用ボタン
    // ─────────────────────────────────────────────────────────

    private void DrawApplyButton()
    {
        bool canApply = _srcArmature != null && _dstArmature != null
            && (_copyPos || _copyRot || _copyScale || _copyComponents || _copyPhysBone || _copyMissingObjects);

        GUI.enabled = canApply;
        if (GUILayout.Button("✦  Armature Transfer 適用  ✦", GUILayout.Height(30f)))
        {
            ApplyTransfer();
        }
        GUI.enabled = true;

        if (!canApply && _srcArmature != null && _dstArmature != null)
        {
            EditorGUILayout.HelpBox(
                "コピーする項目が何も選択されていません。",
                MessageType.Warning);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  ログ表示（開閉式）
    // ─────────────────────────────────────────────────────────

    private void DrawLog()
    {
        if (_logLines.Count == 0) return;

        _foldLog = EditorGUILayout.Foldout(_foldLog,
            $"結果ログ ({_logLines.Count} 件)", true);

        if (_foldLog)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                foreach (var line in _logLines)
                    EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  ロジック — 適用処理
    // ─────────────────────────────────────────────────────────

    private void ApplyTransfer()
    {
        _logLines.Clear();
        _foldLog = true;

        if (_srcArmature == null)
        {
            SetStatus("コピー元 Armature が未設定です", MessageType.Warning);
            return;
        }
        if (_dstArmature == null)
        {
            SetStatus("コピー先 Armature が未設定です", MessageType.Warning);
            return;
        }
        if (!_copyPos && !_copyRot && !_copyScale && !_copyComponents && !_copyPhysBone && !_copyMissingObjects)
        {
            SetStatus("コピーする項目が何も選択されていません", MessageType.Warning);
            return;
        }

        // VRC PhysBone
        var physBoneTypeNames = new List<string>();
        if (_copyPhysBone)
        {
            physBoneTypeNames.Add("VRCPhysBone");
            physBoneTypeNames.Add("VRCPhysBoneCollider");
        }

        // ── コピー先の全ボーンを辞書化 ──
        var dstBones = new Dictionary<string, Transform>();
        CollectBonesByName(_dstArmature, dstBones);

        int boneCount = 0;
        int generalCompCount = 0;
        int physBoneCompCount = 0;
        int createdObjCount = 0;

        Undo.SetCurrentGroupName("Armature Transfer");
        int undoGroup = Undo.GetCurrentGroup();

        CopyBonesRecursive(
            _srcArmature, _dstArmature, dstBones,
            physBoneTypeNames,
            ref boneCount, ref generalCompCount, ref physBoneCompCount,
            ref createdObjCount);

        Undo.CollapseUndoOperations(undoGroup);

        // ── 結果メッセージ ──
        var parts = new List<string>();
        if (_copyPos || _copyRot || _copyScale)
            parts.Add($"{boneCount} ボーンに Transform をコピー");
        if (generalCompCount > 0)
            parts.Add($"{generalCompCount} 件のコンポーネントをコピー");
        if (physBoneCompCount > 0)
            parts.Add($"{physBoneCompCount} 件の VRC PhysBone 系コンポーネントをコピー");
        if (createdObjCount > 0)
            parts.Add($"{createdObjCount} 件のオブジェクトを新規作成");

        string result = string.Join("、", parts);
        if (string.IsNullOrEmpty(result))
            result = "コピー対象のボーンが見つかりませんでした";

        SetStatus(result, MessageType.Info);
        Debug.Log($"[ArmatureTransfer] {result}");
    }

    /// <summary>
    /// src の local Transform 値を dst にそのままコピーする。
    /// 素体・衣装ともに同名のボーン階層を持つ前提のため、
    /// localPosition / localRotation / localScale を直接コピーするのが正しい。
    /// （ワールド座標変換方式は親階層がズレると誤差が生じるため使用しない）
    /// </summary>
    private void SetWorldTransform(Transform dst, Transform src)
    {
        if (_copyPos)
        {
            dst.localPosition = src.localPosition;
        }

        if (_copyRot)
        {
            dst.localRotation = src.localRotation;
        }

        if (_copyScale)
        {
            dst.localScale = src.localScale;
        }
    }

    // ─────────────────────────────────────────────────────────
    //  ボーン名前正規化ユーティリティ
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// 接頭辞の除去、小文字化、Hip/Hips の揺れなどを吸収した比較用の名前を返す。
    /// </summary>
    private static string NormalizeBoneName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";

        // 接頭辞（mixamorig: や Armature| など）をカット
        int idx = name.LastIndexOf(':');
        if (idx >= 0) name = name.Substring(idx + 1);
        idx = name.LastIndexOf('|');
        if (idx >= 0) name = name.Substring(idx + 1);

        name = name.ToLower().Trim();

        // Hip / Hips の表記揺れを統一
        if (name == "hips") name = "hip";

        return name;
    }

    // ─────────────────────────────────────────────────────────
    //  ボーン収集
    // ─────────────────────────────────────────────────────────

    private void CollectBonesByName(Transform root, Dictionary<string, Transform> dict)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            // Armature ルート自身は辞書に含めない（ルートはdstParentとして別途扱う）
            if (t == root) continue;
            string norm = NormalizeBoneName(t.name);
            if (!dict.ContainsKey(norm))
                dict[norm] = t;
        }
    }

    // ─────────────────────────────────────────────────────────
    //  再帰コピー処理
    // ─────────────────────────────────────────────────────────

    private void CopyBonesRecursive(
        Transform src,
        Transform dstParent,
        Dictionary<string, Transform> dstMap,
        List<string> physBoneTypeNames,
        ref int boneCount,
        ref int generalCompCount,
        ref int physBoneCompCount,
        ref int createdObjCount)
    {
        Transform dst = null;
        bool created = false;

        // ルートオブジェクト（ユーザーが指定したArmature自体）はコピーから除外する
        bool isRoot = (src == _srcArmature);
        string normName = NormalizeBoneName(src.name);

        if (!isRoot)
        {
            if (dstMap.TryGetValue(normName, out dst))
            {
                // 同名ボーンが存在する場合
            }
            else if (_copyMissingObjects && dstParent != null)
            {
                // コピー先にオブジェクトが無い場合、新規作成
                var newObj = new GameObject(src.name);
                Undo.RegisterCreatedObjectUndo(newObj, "Armature Transfer: Create " + src.name);
                newObj.transform.SetParent(dstParent, false);
                // ワールド座標を一致させる
                SetWorldTransform(newObj.transform, src);
                dst = newObj.transform;
                dstMap[normName] = dst;
                createdObjCount++;
                created = true;
                _logLines.Add($"  {src.name}  [新規作成]  parent={dstParent.name}");
            }
            else
            {
                _logLines.Add($"  {src.name}  [スキップ] コピー先に該当ボーンがありません");
            }
        }
        else
        {
            // ルート自身の場合はコピー先のルートをdstParentの引き継ぎ用にする
            dst = dstParent;
        }

        if (dst != null && !isRoot)
        {
            bool transformCopied = false;

            // ── Transform コピー（ワールド座標一致方式）──
            // 親階層が異なっていてもワールド Position/Rotation/Scale が
            // 素体側と一致するように local 値を逆算してセットする
            if (!created && (_copyPos || _copyRot || _copyScale))
            {
                Undo.RecordObject(dst, "Armature Transfer: " + src.name);

                SetWorldTransform(dst, src);

                EditorUtility.SetDirty(dst);
                boneCount++;
                transformCopied = true;
            }

            // ── 汎用コンポーネントコピー ──
            int genCopied = 0;
            if (_copyComponents)
            {
                genCopied = CopyAllComponents(src, dst);
                generalCompCount += genCopied;
            }

            // ── VRC PhysBone コピー ──
            int pbCopied = 0;
            if (_copyPhysBone && physBoneTypeNames.Count > 0)
            {
                pbCopied = CopyComponentsByTypeNames(src, dst, physBoneTypeNames);
                physBoneCompCount += pbCopied;
            }

            // ログ出力
            if (!created && (transformCopied || genCopied > 0 || pbCopied > 0))
            {
                var logParts = new List<string>();
                if (transformCopied)
                {
                    var items = new List<string>();
                    if (_copyPos)   items.Add("Pos");
                    if (_copyRot)   items.Add("Rot");
                    if (_copyScale) items.Add("Scale");
                    logParts.Add($"Transform({string.Join("/", items)})");
                }
                if (genCopied > 0)
                    logParts.Add($"Component×{genCopied}");
                if (pbCopied > 0)
                    logParts.Add($"PhysBone×{pbCopied}");

                _logLines.Add($"  {src.name} -> {dst.name}  [{string.Join(", ", logParts)}]");
            }
        }

        // 子ボーンを再帰処理
        // dst が確定している場合はそれを次の親として渡す。
        // dst が null（スキップされた場合）は dstParent を引き継ぐことで
        // 孫ボーンのマッチングが途切れないようにする。
        Transform nextParent = dst ?? dstParent;
        for (int i = 0; i < src.childCount; i++)
        {
            CopyBonesRecursive(
                src.GetChild(i), nextParent, dstMap,
                physBoneTypeNames,
                ref boneCount, ref generalCompCount, ref physBoneCompCount,
                ref createdObjCount);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  汎用コンポーネントコピー（VRC PhysBone 以外の全コンポーネント）
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// VRCPhysBone / VRCPhysBoneCollider / Transform 以外の
    /// 全コンポーネントを src から dst にコピーする。
    /// 既存コンポーネントがある場合は上書き（既存を削除して追加）。
    /// </summary>
    /// <returns>コピーしたコンポーネント数</returns>
    private int CopyAllComponents(Transform src, Transform dst)
    {
        // VRC PhysBone 系の型を取得（専用オプションで制御するため除外）
        Type physBoneType   = FindTypeByName("VRCPhysBone");
        Type colliderType   = FindTypeByName("VRCPhysBoneCollider");

        int count = 0;
        var srcComps = src.GetComponents<Component>();
        foreach (var srcComp in srcComps)
        {
            if (srcComp == null) continue;
            Type compType = srcComp.GetType();

            // Transform は常にスキップ
            if (compType == typeof(Transform)) continue;

            // VRC PhysBone 系はスキップ（専用オプション側で処理）
            if (physBoneType != null && physBoneType.IsAssignableFrom(compType)) continue;
            if (colliderType != null && colliderType.IsAssignableFrom(compType)) continue;

            // 既存コンポーネントを削除して上書き
            var existing = dst.GetComponent(compType);
            if (existing != null)
            {
                Undo.DestroyObjectImmediate(existing);
            }

            var dstComp = Undo.AddComponent(dst.gameObject, compType);
            if (dstComp != null)
            {
                EditorUtility.CopySerialized(srcComp, dstComp);
                EditorUtility.SetDirty(dst.gameObject);
                count++;
            }
        }
        return count;
    }

    // ─────────────────────────────────────────────────────────
    //  型名指定コンポーネントコピー（VRC PhysBone 専用）
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// 指定した型名リストに一致するコンポーネントを src から dst にコピーする。
    /// 既存コンポーネントがある場合は上書き（既存を削除して追加）。
    /// </summary>
    /// <returns>コピーしたコンポーネント数</returns>
    private int CopyComponentsByTypeNames(
        Transform src, Transform dst, List<string> typeNames)
    {
        int count = 0;
        foreach (var typeName in typeNames)
        {
            Type compType = FindTypeByName(typeName);
            if (compType == null) continue;

            var srcComps = src.GetComponents(compType);
            foreach (var srcComp in srcComps)
            {
                // 既存コンポーネントを削除して上書き
                var existing = dst.GetComponent(compType);
                if (existing != null)
                {
                    Undo.DestroyObjectImmediate(existing);
                }

                var dstComp = Undo.AddComponent(dst.gameObject, compType);
                // SerializedObject 経由で全フィールドコピー
                EditorUtility.CopySerialized(srcComp, dstComp);
                EditorUtility.SetDirty(dst.gameObject);
                count++;
            }
        }
        return count;
    }

    // ─────────────────────────────────────────────────────────
    //  型名検索
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// 型名の短縮形（名前空間なし）でロード済みアセンブリを検索する。
    /// </summary>
    private static Type FindTypeByName(string typeName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == typeName) return t;
                }
            }
            catch (System.Reflection.ReflectionTypeLoadException)
            {
                // 一部のアセンブリで読み込めない型がある場合はスキップ
                continue;
            }
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────
    //  ユーティリティ
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// 指定した Transform のワールドスケール（lossyScale）が targetWorldScale と
    /// 一致するように localScale を逆算してセットする。
    /// </summary>
    private static void SetWorldScale(Transform t, Vector3 targetWorldScale)
    {
        if (t.parent != null)
        {
            Vector3 parentScale = t.parent.lossyScale;
            t.localScale = new Vector3(
                Mathf.Approximately(parentScale.x, 0f) ? 1f : targetWorldScale.x / parentScale.x,
                Mathf.Approximately(parentScale.y, 0f) ? 1f : targetWorldScale.y / parentScale.y,
                Mathf.Approximately(parentScale.z, 0f) ? 1f : targetWorldScale.z / parentScale.z
            );
        }
        else
        {
            t.localScale = targetWorldScale;
        }
    }

    private void SetStatus(string msg, MessageType t)
    {
        _statusMessage = msg;
        _statusType = t;
        Repaint();
    }
}
#endif