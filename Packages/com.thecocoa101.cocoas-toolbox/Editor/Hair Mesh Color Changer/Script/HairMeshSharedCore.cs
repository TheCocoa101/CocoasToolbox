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
/// HairMeshColorChanger / HairMeshDeleter 共通コアエンジン — Cocoa's Toolbox
/// ・Blender の編集モード「Lキー（リンク選択）」相当のメッシュアイランド分割（3D座標連結 / トポロジー連結 / UV連結 / 単一三角面）
/// ・ポーズ・BlendShape変形後のワールド空間レイキャスト（AABB枝刈り＋Moller-Trumbore法）
/// ・リアルタイム・テクスチャ＆UVビュワー（ズーム・パン・UV上でのクリック選択・ホバー連動対応）
/// ・UVマスクラスタライザ（Chamfer距離変換による高速UVパディング＆非選択UVアイランド侵食防止ガード）
/// ・SceneView オーバーレイ描画（Zテスト2パスによる見やすいハイライト描画）
/// </summary>
public enum HairLinkMode
{
    Spatial3D = 0,     // 3D形状アイランド（同一座標の頂点をシーム越えで連結：Blender Lキー標準）
    TopologyOnly = 1,  // トポロジー連結（頂点インデックス共有のみ：シームで区切る）
    UVIsland = 2,      // UVアイランド連結（UV空間上で繋がっている島）
    SingleTriangle = 3 // 1ポリゴン（三角面）単位
}

public enum HairClickBehavior
{
    Toggle = 0,        // クリックで追加・解除をトグル（ドラッグで連続追加/解除）
    SingleOrShift = 1  // 通常クリックで単一選択、Shiftで追加、Ctrlで解除
}

public class HairIsland
{
    public int IslandId;
    public int PrimarySubMesh;
    public int[] GlobalTriangles; // HairMeshContext.Triangles 内のインデックス配列
    public int[] UniqueVertices;  // このアイランドに含まれる頂点インデックス配列
    public Bounds LocalBounds;
    public Bounds WorldBounds;
    public Rect UVBounds;
}

public struct HairTriangle
{
    public int GlobalIndex;
    public int SubMeshIndex;
    public int V0;
    public int V1;
    public int V2;
    public Bounds WorldAABB;
}

public class HairMeshContext
{
    public GameObject TargetGameObject { get; private set; }
    public Component TargetRenderer { get; private set; } // SkinnedMeshRenderer or MeshRenderer
    public SkinnedMeshRenderer SMR { get; private set; }
    public MeshFilter MF { get; private set; }
    public MeshRenderer MR { get; private set; }
    public Mesh SourceMesh { get; private set; }

    public int VertexCount { get; private set; }
    public int SubMeshCount { get; private set; }
    public Vector3[] LocalVertices { get; private set; }
    public Vector3[] LocalNormals { get; private set; }
    public Vector2[] UVs { get; private set; }

    public Vector3[] WorldVertices { get; private set; }
    public Vector3[] WorldNormals { get; private set; }

    public HairTriangle[] Triangles { get; private set; } = Array.Empty<HairTriangle>();
    public List<HairIsland> Islands { get; private set; } = new List<HairIsland>();
    public int[] TriangleToIsland { get; private set; } = Array.Empty<int>();

    public HairLinkMode CurrentLinkMode { get; private set; } = HairLinkMode.Spatial3D;
    public bool SplitBySubMesh { get; private set; } = true;

    private Matrix4x4 _lastTransformMatrix;

    public bool IsValid => SourceMesh != null && TargetRenderer != null && VertexCount > 0 && Triangles.Length > 0;

    public static HairMeshContext Create(UnityEngine.Object rendererOrFilterOrGo, Mesh explicitSourceMesh = null)
    {
        if (rendererOrFilterOrGo == null) return null;

        GameObject go = rendererOrFilterOrGo as GameObject ?? (rendererOrFilterOrGo as Component)?.gameObject;
        if (go == null) return null;

        SkinnedMeshRenderer smr = rendererOrFilterOrGo as SkinnedMeshRenderer;
        MeshRenderer mr = rendererOrFilterOrGo as MeshRenderer;
        MeshFilter mf = rendererOrFilterOrGo as MeshFilter;

        if (smr == null && mr == null && mf == null)
        {
            smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr == null)
            {
                mr = go.GetComponent<MeshRenderer>();
                mf = go.GetComponent<MeshFilter>();
            }
        }
        else if (mr != null && mf == null)
        {
            mf = go.GetComponent<MeshFilter>();
        }
        else if (mf != null && mr == null)
        {
            mr = go.GetComponent<MeshRenderer>();
        }

        Mesh srcMesh = explicitSourceMesh;
        if (srcMesh == null)
        {
            if (smr != null) srcMesh = smr.sharedMesh;
            else if (mf != null) srcMesh = mf.sharedMesh;
        }

        if (srcMesh == null || srcMesh.vertexCount == 0) return null;

        var ctx = new HairMeshContext
        {
            TargetGameObject = go,
            TargetRenderer = (Component)smr ?? mr,
            SMR = smr,
            MF = mf,
            MR = mr,
            SourceMesh = srcMesh
        };

        ctx.LoadMeshData();
        ctx.UpdateWorldGeometry();
        return ctx;
    }

    public void ReplaceSourceMeshReference(Mesh newSourceMesh)
    {
        if (newSourceMesh == null || newSourceMesh == SourceMesh) return;
        SourceMesh = newSourceMesh;
        LoadMeshData();
        UpdateWorldGeometry();
    }

    private void LoadMeshData()
    {
        VertexCount = SourceMesh.vertexCount;
        SubMeshCount = Mathf.Max(1, SourceMesh.subMeshCount);
        LocalVertices = SourceMesh.vertices;
        LocalNormals = SourceMesh.normals;
        if (LocalNormals == null || LocalNormals.Length != VertexCount)
        {
            LocalNormals = new Vector3[VertexCount];
            for (int i = 0; i < VertexCount; i++) LocalNormals[i] = Vector3.up;
        }

        var uvList = new List<Vector2>(VertexCount);
        SourceMesh.GetUVs(0, uvList);
        if (uvList.Count == VertexCount)
        {
            UVs = uvList.ToArray();
        }
        else
        {
            UVs = new Vector2[VertexCount];
        }

        var triList = new List<HairTriangle>(SourceMesh.triangles.Length / 3);
        int globalIdx = 0;
        for (int s = 0; s < SubMeshCount; s++)
        {
            int[] tris = SourceMesh.GetTriangles(s);
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int v0 = tris[i];
                int v1 = tris[i + 1];
                int v2 = tris[i + 2];
                if (v0 < 0 || v0 >= VertexCount || v1 < 0 || v1 >= VertexCount || v2 < 0 || v2 >= VertexCount)
                    continue;

                triList.Add(new HairTriangle
                {
                    GlobalIndex = globalIdx++,
                    SubMeshIndex = s,
                    V0 = v0,
                    V1 = v1,
                    V2 = v2
                });
            }
        }

        Triangles = triList.ToArray();
        TriangleToIsland = new int[Triangles.Length];
    }

    /// <summary>
    /// 現在のボーン変形・BlendShape変形を考慮したワールド空間頂点・法線およびAABBを更新する。
    /// </summary>
    public void UpdateWorldGeometry()
    {
        if (SourceMesh == null || TargetGameObject == null) return;

        if (WorldVertices == null || WorldVertices.Length != VertexCount)
            WorldVertices = new Vector3[VertexCount];
        if (WorldNormals == null || WorldNormals.Length != VertexCount)
            WorldNormals = new Vector3[VertexCount];

        if (SMR != null)
        {
            // SkinnedMeshRenderer の場合、プレビュー用一時Meshが刺さっている場合でも
            // 頂点数が同じならそのまま BakeMesh、頂点数が違う（削除プレビュー中等）なら一時的に SourceMesh を Bake する
            Mesh currentAssigned = SMR.sharedMesh;
            bool needTempSwap = (currentAssigned != SourceMesh && (currentAssigned == null || currentAssigned.vertexCount != VertexCount));

            var baked = new Mesh();
            try
            {
                if (needTempSwap) SMR.sharedMesh = SourceMesh;

                bool hasBones = HasEffectiveSkinning(SMR, SourceMesh);
                if (hasBones)
                {
                    SMR.BakeMesh(baked, false);
                    Matrix4x4 rigidToWorld = Matrix4x4.TRS(SMR.transform.position, SMR.transform.rotation, Vector3.one);
                    Quaternion rot = SMR.transform.rotation;

                    var bVerts = baked.vertices;
                    var bNorms = baked.normals;
                    bool hasBNorms = bNorms != null && bNorms.Length == VertexCount;

                    if (bVerts != null && bVerts.Length == VertexCount)
                    {
                        for (int i = 0; i < VertexCount; i++)
                        {
                            WorldVertices[i] = rigidToWorld.MultiplyPoint3x4(bVerts[i]);
                            if (hasBNorms)
                            {
                                Vector3 wn = rot * bNorms[i];
                                WorldNormals[i] = wn.sqrMagnitude > 1e-20f ? wn.normalized : Vector3.up;
                            }
                            else
                            {
                                WorldNormals[i] = rot * LocalNormals[i];
                            }
                        }
                    }
                    else
                    {
                        TransformWithMatrix(SMR.transform.localToWorldMatrix);
                    }
                }
                else
                {
                    // ボーンなし（BlendShapeのみ、または静的SMR）
                    SMR.BakeMesh(baked, true);
                    Matrix4x4 trs = Matrix4x4.TRS(SMR.transform.position, SMR.transform.rotation, Vector3.one);
                    var bVerts = baked.vertices;
                    if (bVerts != null && bVerts.Length == VertexCount)
                    {
                        for (int i = 0; i < VertexCount; i++)
                        {
                            WorldVertices[i] = trs.MultiplyPoint3x4(bVerts[i]);
                            WorldNormals[i] = SMR.transform.rotation * LocalNormals[i];
                        }
                    }
                    else
                    {
                        TransformWithMatrix(SMR.transform.localToWorldMatrix);
                    }
                }
            }
            finally
            {
                if (needTempSwap) SMR.sharedMesh = currentAssigned;
                UnityEngine.Object.DestroyImmediate(baked);
            }
        }
        else
        {
            TransformWithMatrix(TargetGameObject.transform.localToWorldMatrix);
        }

        _lastTransformMatrix = TargetGameObject.transform.localToWorldMatrix;

        // 各三角形のワールドAABBを更新
        for (int i = 0; i < Triangles.Length; i++)
        {
            var tri = Triangles[i];
            Vector3 p0 = WorldVertices[tri.V0];
            Vector3 p1 = WorldVertices[tri.V1];
            Vector3 p2 = WorldVertices[tri.V2];

            Vector3 min = Vector3.Min(p0, Vector3.Min(p1, p2));
            Vector3 max = Vector3.Max(p0, Vector3.Max(p1, p2));
            tri.WorldAABB = new Bounds((min + max) * 0.5f, (max - min) + new Vector3(1e-4f, 1e-4f, 1e-4f));
            Triangles[i] = tri;
        }

        // 各アイランドのワールドBoundsを更新
        UpdateIslandWorldBounds();
    }

    public void CheckAndUpdateTransformIfChanged()
    {
        if (TargetGameObject == null) return;
        if (TargetGameObject.transform.localToWorldMatrix != _lastTransformMatrix)
        {
            UpdateWorldGeometry();
        }
    }

    private void TransformWithMatrix(Matrix4x4 l2w)
    {
        Matrix4x4 normalMat = l2w.inverse.transpose;
        for (int i = 0; i < VertexCount; i++)
        {
            WorldVertices[i] = l2w.MultiplyPoint3x4(LocalVertices[i]);
            Vector3 wn = normalMat.MultiplyVector(LocalNormals[i]);
            WorldNormals[i] = wn.sqrMagnitude > 1e-20f ? wn.normalized : Vector3.up;
        }
    }

    private static bool HasEffectiveSkinning(SkinnedMeshRenderer smr, Mesh mesh)
    {
        if (smr == null || mesh == null) return false;
        var bindposes = mesh.bindposes;
        if (bindposes == null || bindposes.Length == 0) return false;
        var bones = smr.bones;
        if (bones == null || bones.Length == 0) return false;
        bool anyBone = false;
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] != null) { anyBone = true; break; }
        }
        if (!anyBone) return false;
        return mesh.HasVertexAttribute(VertexAttribute.BlendWeight) ||
               (mesh.boneWeights != null && mesh.boneWeights.Length > 0);
    }

    /// <summary>
    /// 指定された連結モードでメッシュアイランド（髪の房1本1本）を構築する。
    /// </summary>
    public void BuildIslands(HairLinkMode linkMode, bool splitBySubMesh = true)
    {
        CurrentLinkMode = linkMode;
        SplitBySubMesh = splitBySubMesh;
        Islands.Clear();

        int triCount = Triangles.Length;
        if (triCount == 0) return;

        if (linkMode == HairLinkMode.SingleTriangle)
        {
            for (int t = 0; t < triCount; t++)
            {
                var tri = Triangles[t];
                TriangleToIsland[t] = t;

                Vector2 uv0 = UVs[tri.V0];
                Vector2 uv1 = UVs[tri.V1];
                Vector2 uv2 = UVs[tri.V2];
                Vector2 uvMin = Vector2.Min(uv0, Vector2.Min(uv1, uv2));
                Vector2 uvMax = Vector2.Max(uv0, Vector2.Max(uv1, uv2));

                var isl = new HairIsland
                {
                    IslandId = t,
                    PrimarySubMesh = tri.SubMeshIndex,
                    GlobalTriangles = new[] { t },
                    UniqueVertices = new[] { tri.V0, tri.V1, tri.V2 }.Distinct().ToArray(),
                    WorldBounds = tri.WorldAABB,
                    UVBounds = Rect.MinMaxRect(uvMin.x, uvMin.y, uvMax.x, uvMax.y)
                };
                Islands.Add(isl);
            }
            return;
        }

        // Step 1: 頂点の同値クラス（Welded Vertex ID）を求める
        int[] vertexClusterId = new int[VertexCount];

        if (linkMode == HairLinkMode.TopologyOnly)
        {
            for (int v = 0; v < VertexCount; v++)
                vertexClusterId[v] = v;
        }
        else if (linkMode == HairLinkMode.Spatial3D)
        {
            // 3D空間座標がほぼ同一の頂点を同一IDに溶接（UVシームやハードエッジで割れた頂点を繋ぐ）
            Bounds meshBounds = SourceMesh.bounds;
            float maxExtent = Mathf.Max(meshBounds.size.x, Mathf.Max(meshBounds.size.y, meshBounds.size.z));
            float eps = Mathf.Max(1e-5f, maxExtent * 1e-4f);
            double invEps = 1.0 / eps;

            var spatialMap = new Dictionary<(long, long, long), int>(VertexCount);
            for (int v = 0; v < VertexCount; v++)
            {
                Vector3 p = LocalVertices[v];
                long qx = (long)Math.Round(p.x * invEps);
                long qy = (long)Math.Round(p.y * invEps);
                long qz = (long)Math.Round(p.z * invEps);
                var key = (qx, qy, qz);
                if (!spatialMap.TryGetValue(key, out int cid))
                {
                    cid = v;
                    spatialMap[key] = cid;
                }
                vertexClusterId[v] = cid;
            }
        }
        else if (linkMode == HairLinkMode.UVIsland)
        {
            // UV座標がほぼ同一の頂点を同一IDに溶接
            const double invUvEps = 100000.0; // 1e-5
            var uvMap = new Dictionary<(long, long), int>(VertexCount);
            for (int v = 0; v < VertexCount; v++)
            {
                Vector2 uv = UVs[v];
                long qu = (long)Math.Round(uv.x * invUvEps);
                long qv = (long)Math.Round(uv.y * invUvEps);
                var key = (qu, qv);
                if (!uvMap.TryGetValue(key, out int cid))
                {
                    cid = v;
                    uvMap[key] = cid;
                }
                vertexClusterId[v] = cid;
            }
        }

        // Step 2: Union-Find で三角形同士を連結
        int[] parent = new int[triCount];
        int[] rank = new int[triCount];
        for (int t = 0; t < triCount; t++) parent[t] = t;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Unite(int a, int b)
        {
            int ra = Find(a);
            int rb = Find(b);
            if (ra == rb) return;
            if (rank[ra] < rank[rb]) parent[ra] = rb;
            else if (rank[ra] > rank[rb]) parent[rb] = ra;
            else { parent[rb] = ra; rank[ra]++; }
        }

        // クラスタ頂点ID -> 最初に見つかった三角形インデックス（サブメッシュ分離時は (clusterId, subMesh) キー）
        if (splitBySubMesh && SubMeshCount > 1)
        {
            var ownerTri = new Dictionary<long, int>(VertexCount);
            for (int t = 0; t < triCount; t++)
            {
                var tri = Triangles[t];
                long subShift = (long)tri.SubMeshIndex << 32;

                long k0 = subShift | (uint)vertexClusterId[tri.V0];
                if (ownerTri.TryGetValue(k0, out int prev0)) Unite(t, prev0);
                else ownerTri[k0] = t;

                long k1 = subShift | (uint)vertexClusterId[tri.V1];
                if (ownerTri.TryGetValue(k1, out int prev1)) Unite(t, prev1);
                else ownerTri[k1] = t;

                long k2 = subShift | (uint)vertexClusterId[tri.V2];
                if (ownerTri.TryGetValue(k2, out int prev2)) Unite(t, prev2);
                else ownerTri[k2] = t;
            }
        }
        else
        {
            int[] ownerTri = new int[VertexCount];
            for (int i = 0; i < VertexCount; i++) ownerTri[i] = -1;

            for (int t = 0; t < triCount; t++)
            {
                var tri = Triangles[t];

                int c0 = vertexClusterId[tri.V0];
                if (ownerTri[c0] >= 0) Unite(t, ownerTri[c0]);
                else ownerTri[c0] = t;

                int c1 = vertexClusterId[tri.V1];
                if (ownerTri[c1] >= 0) Unite(t, ownerTri[c1]);
                else ownerTri[c1] = t;

                int c2 = vertexClusterId[tri.V2];
                if (ownerTri[c2] >= 0) Unite(t, ownerTri[c2]);
                else ownerTri[c2] = t;
            }
        }

        // Step 3: ルートごとに三角形リストを集約して HairIsland を生成
        var rootToIslandIndex = new Dictionary<int, int>();
        var islandTriLists = new List<List<int>>();

        for (int t = 0; t < triCount; t++)
        {
            int r = Find(t);
            if (!rootToIslandIndex.TryGetValue(r, out int islIdx))
            {
                islIdx = islandTriLists.Count;
                rootToIslandIndex[r] = islIdx;
                islandTriLists.Add(new List<int>());
            }
            islandTriLists[islIdx].Add(t);
            TriangleToIsland[t] = islIdx;
        }

        var vertSet = new HashSet<int>();
        for (int islIdx = 0; islIdx < islandTriLists.Count; islIdx++)
        {
            var tList = islandTriLists[islIdx];
            vertSet.Clear();

            Vector2 uvMin = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 uvMax = new Vector2(float.MinValue, float.MinValue);
            Vector3 locMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 locMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            foreach (int t in tList)
            {
                var tri = Triangles[t];
                vertSet.Add(tri.V0);
                vertSet.Add(tri.V1);
                vertSet.Add(tri.V2);
            }

            foreach (int v in vertSet)
            {
                Vector3 lp = LocalVertices[v];
                locMin = Vector3.Min(locMin, lp);
                locMax = Vector3.Max(locMax, lp);

                Vector2 uv = UVs[v];
                uvMin = Vector2.Min(uvMin, uv);
                uvMax = Vector2.Max(uvMax, uv);
            }

            var isl = new HairIsland
            {
                IslandId = islIdx,
                PrimarySubMesh = Triangles[tList[0]].SubMeshIndex,
                GlobalTriangles = tList.ToArray(),
                UniqueVertices = vertSet.ToArray(),
                LocalBounds = new Bounds((locMin + locMax) * 0.5f, locMax - locMin),
                UVBounds = Rect.MinMaxRect(uvMin.x, uvMin.y, uvMax.x, uvMax.y)
            };
            Islands.Add(isl);
        }

        UpdateIslandWorldBounds();
    }

    private void UpdateIslandWorldBounds()
    {
        if (WorldVertices == null || Islands == null) return;
        foreach (var isl in Islands)
        {
            if (isl.UniqueVertices == null || isl.UniqueVertices.Length == 0) continue;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < isl.UniqueVertices.Length; i++)
            {
                Vector3 wp = WorldVertices[isl.UniqueVertices[i]];
                min = Vector3.Min(min, wp);
                max = Vector3.Max(max, wp);
            }
            isl.WorldBounds = new Bounds((min + max) * 0.5f, (max - min) + new Vector3(1e-3f, 1e-3f, 1e-3f));
        }
    }

    /// <summary>
    /// ワールド空間の Ray で最も手前の三角形およびその所属アイランドをピッキングする。
    /// </summary>
    public bool RaycastIsland(
        Ray worldRay,
        bool bothSides,
        int subMeshFilter,
        HashSet<int> ignoredIslands,
        out int hitIslandId,
        out int hitTriangleIndex,
        out float hitDistance)
    {
        hitIslandId = -1;
        hitTriangleIndex = -1;
        hitDistance = float.MaxValue;

        if (!IsValid || Islands.Count == 0) return false;

        CheckAndUpdateTransformIfChanged();

        for (int i = 0; i < Islands.Count; i++)
        {
            var isl = Islands[i];
            if (ignoredIslands != null && ignoredIslands.Contains(isl.IslandId))
                continue;
            if (subMeshFilter >= 0 && isl.PrimarySubMesh != subMeshFilter)
                continue;

            if (!isl.WorldBounds.IntersectRay(worldRay, out float boundsDist))
                continue;
            if (boundsDist > hitDistance)
                continue;

            int[] tris = isl.GlobalTriangles;
            for (int ti = 0; ti < tris.Length; ti++)
            {
                int tIdx = tris[ti];
                var tri = Triangles[tIdx];
                if (subMeshFilter >= 0 && tri.SubMeshIndex != subMeshFilter)
                    continue;

                Vector3 v0 = WorldVertices[tri.V0];
                Vector3 v1 = WorldVertices[tri.V1];
                Vector3 v2 = WorldVertices[tri.V2];

                if (RayIntersectsTriangle(worldRay, v0, v1, v2, bothSides, out float dist))
                {
                    if (dist >= 0f && dist < hitDistance)
                    {
                        hitDistance = dist;
                        hitTriangleIndex = tIdx;
                        hitIslandId = isl.IslandId;
                    }
                }
            }
        }

        return hitIslandId >= 0;
    }

    /// <summary>
    /// UV空間の座標 (0..1) から該当する三角形とアイランドを逆引きピッキングする。
    /// </summary>
    public bool PickIslandAtUV(Vector2 uv, int subMeshFilter, out int hitIslandId)
    {
        hitIslandId = -1;
        if (!IsValid || Islands.Count == 0) return false;

        // まず UV が直接内部に入る三角形を探す
        float bestEdgeDistSq = float.MaxValue;
        int closestIsland = -1;

        for (int i = 0; i < Islands.Count; i++)
        {
            var isl = Islands[i];
            if (subMeshFilter >= 0 && isl.PrimarySubMesh != subMeshFilter)
                continue;

            // UVラップアラウンドを考慮しつつバウンディングチェック
            Rect expanded = Rect.MinMaxRect(
                isl.UVBounds.xMin - 0.02f, isl.UVBounds.yMin - 0.02f,
                isl.UVBounds.xMax + 0.02f, isl.UVBounds.yMax + 0.02f);

            Vector2 testUv = uv;
            if (!expanded.Contains(testUv))
            {
                // タイルオフセット対応
                Vector2 center = isl.UVBounds.center;
                Vector2 tileOffset = new Vector2(Mathf.Floor(center.x), Mathf.Floor(center.y));
                testUv = uv + tileOffset;
                if (!expanded.Contains(testUv))
                    continue;
            }

            int[] tris = isl.GlobalTriangles;
            for (int ti = 0; ti < tris.Length; ti++)
            {
                var tri = Triangles[tris[ti]];
                if (subMeshFilter >= 0 && tri.SubMeshIndex != subMeshFilter)
                    continue;

                Vector2 a = UVs[tri.V0];
                Vector2 b = UVs[tri.V1];
                Vector2 c = UVs[tri.V2];

                if (PointInTriangle2D(testUv, a, b, c))
                {
                    hitIslandId = isl.IslandId;
                    return true;
                }

                // わずかに外れた場合の最近傍フォールバック（細い毛束のUV用）
                Vector2 centroid = (a + b + c) * (1f / 3f);
                float dSq = (testUv - centroid).sqrMagnitude;
                if (dSq < bestEdgeDistSq && dSq < 0.0004f) // 半径0.02以内
                {
                    bestEdgeDistSq = dSq;
                    closestIsland = isl.IslandId;
                }
            }
        }

        if (closestIsland >= 0)
        {
            hitIslandId = closestIsland;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 選択されたアイランドのUV領域と、非選択アイランドのUV領域が重なっているか（ミラーUV・テクスチャ使い回し）を判定する。
    /// </summary>
    public int CountOverlappingUnselectedIslands(HashSet<int> selectedIslands, int subMeshFilter)
    {
        if (!IsValid || selectedIslands == null || selectedIslands.Count == 0) return 0;

        const int GRID_RES = 256;
        bool[] selectedCells = new bool[GRID_RES * GRID_RES];

        foreach (int islId in selectedIslands)
        {
            if (islId < 0 || islId >= Islands.Count) continue;
            var isl = Islands[islId];
            if (subMeshFilter >= 0 && isl.PrimarySubMesh != subMeshFilter) continue;

            foreach (int tIdx in isl.GlobalTriangles)
            {
                var tri = Triangles[tIdx];
                Vector2 c = (UVs[tri.V0] + UVs[tri.V1] + UVs[tri.V2]) * (1f / 3f);
                int gx = Mathf.Clamp((int)(Mathf.Repeat(c.x, 1f) * GRID_RES), 0, GRID_RES - 1);
                int gy = Mathf.Clamp((int)(Mathf.Repeat(c.y, 1f) * GRID_RES), 0, GRID_RES - 1);
                selectedCells[gy * GRID_RES + gx] = true;
            }
        }

        int overlapIslandCount = 0;
        for (int i = 0; i < Islands.Count; i++)
        {
            if (selectedIslands.Contains(i)) continue;
            var isl = Islands[i];
            if (subMeshFilter >= 0 && isl.PrimarySubMesh != subMeshFilter) continue;

            bool overlaps = false;
            foreach (int tIdx in isl.GlobalTriangles)
            {
                var tri = Triangles[tIdx];
                Vector2 c = (UVs[tri.V0] + UVs[tri.V1] + UVs[tri.V2]) * (1f / 3f);
                int gx = Mathf.Clamp((int)(Mathf.Repeat(c.x, 1f) * GRID_RES), 0, GRID_RES - 1);
                int gy = Mathf.Clamp((int)(Mathf.Repeat(c.y, 1f) * GRID_RES), 0, GRID_RES - 1);
                if (selectedCells[gy * GRID_RES + gx])
                {
                    overlaps = true;
                    break;
                }
            }
            if (overlaps) overlapIslandCount++;
        }

        return overlapIslandCount;
    }

    private static bool RayIntersectsTriangle(
        Ray ray, Vector3 v0, Vector3 v1, Vector3 v2, bool bothSides, out float distance)
    {
        distance = 0f;
        const float EPSILON = 1e-7f;

        Vector3 edge1 = v1 - v0;
        Vector3 edge2 = v2 - v0;
        Vector3 h = Vector3.Cross(ray.direction, edge2);
        float a = Vector3.Dot(edge1, h);

        if (bothSides)
        {
            if (a > -EPSILON && a < EPSILON) return false;
        }
        else
        {
            if (a < EPSILON) return false;
        }

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
            distance = t;
            return true;
        }
        return false;
    }

    public static bool PointInTriangle2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Sign2D(p, a, b);
        float d2 = Sign2D(p, b, c);
        float d3 = Sign2D(p, c, a);

        bool hasNeg = (d1 < -1e-6f) || (d2 < -1e-6f) || (d3 < -1e-6f);
        bool hasPos = (d1 > 1e-6f) || (d2 > 1e-6f) || (d3 > 1e-6f);
        return !(hasNeg && hasPos);
    }

    private static float Sign2D(Vector2 p1, Vector2 p2, Vector2 p3)
    {
        return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
    }
}

/// <summary>
/// SceneView 上での選択アイランド・ホバーアイランドのハイライト描画およびHUD表示ユーティリティ
/// </summary>
public static class HairSceneOverlayRenderer
{
    private static Material _overlayMaterialZTest;
    private static Material _overlayMaterialAlways;

    private static void EnsureMaterials()
    {
        if (_overlayMaterialZTest == null)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            _overlayMaterialZTest = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _overlayMaterialZTest.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _overlayMaterialZTest.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _overlayMaterialZTest.SetInt("_Cull", (int)CullMode.Off);
            _overlayMaterialZTest.SetInt("_ZWrite", 0);
            _overlayMaterialZTest.SetInt("_ZTest", (int)CompareFunction.LessEqual);
        }

        if (_overlayMaterialAlways == null)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            _overlayMaterialAlways = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _overlayMaterialAlways.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _overlayMaterialAlways.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _overlayMaterialAlways.SetInt("_Cull", (int)CullMode.Off);
            _overlayMaterialAlways.SetInt("_ZWrite", 0);
            _overlayMaterialAlways.SetInt("_ZTest", (int)CompareFunction.Always);
        }
    }

    public static Material GetGuiColoredMaterial()
    {
        EnsureMaterials();
        return _overlayMaterialAlways;
    }

    public static void DrawSceneSelectionOverlay(
        HairMeshContext ctx,
        HashSet<int> selectedIslands,
        int hoveredIslandId,
        bool drawSelectedFill,
        bool drawSelectedWire,
        Color selectedFillColor,
        Color selectedWireColor,
        Color hoverFillColor,
        Color hoverWireColor)
    {
        if (ctx == null || !ctx.IsValid) return;
        if (Event.current.type != EventType.Repaint) return;

        EnsureMaterials();
        ctx.CheckAndUpdateTransformIfChanged();

        var wVerts = ctx.WorldVertices;
        var wNorms = ctx.WorldNormals;
        var tris = ctx.Triangles;

        // 1. 選択済みアイランドの描画
        if (selectedIslands != null && selectedIslands.Count > 0 && (drawSelectedFill || drawSelectedWire))
        {
            if (drawSelectedFill)
            {
                // ZTest LessEqual パス（表に見えている部分）
                _overlayMaterialZTest.SetPass(0);
                GL.PushMatrix();
                GL.Begin(GL.TRIANGLES);
                GL.Color(selectedFillColor);
                foreach (int islId in selectedIslands)
                {
                    if (islId < 0 || islId >= ctx.Islands.Count || islId == hoveredIslandId) continue;
                    var isl = ctx.Islands[islId];
                    int[] gTris = isl.GlobalTriangles;
                    for (int i = 0; i < gTris.Length; i++)
                    {
                        var t = tris[gTris[i]];
                        GL.Vertex(wVerts[t.V0] + wNorms[t.V0] * 0.0002f);
                        GL.Vertex(wVerts[t.V1] + wNorms[t.V1] * 0.0002f);
                        GL.Vertex(wVerts[t.V2] + wNorms[t.V2] * 0.0002f);
                    }
                }
                GL.End();
                GL.PopMatrix();
            }

            if (drawSelectedWire)
            {
                _overlayMaterialZTest.SetPass(0);
                GL.PushMatrix();
                GL.Begin(GL.LINES);
                GL.Color(selectedWireColor);
                foreach (int islId in selectedIslands)
                {
                    if (islId < 0 || islId >= ctx.Islands.Count || islId == hoveredIslandId) continue;
                    var isl = ctx.Islands[islId];
                    int[] gTris = isl.GlobalTriangles;
                    for (int i = 0; i < gTris.Length; i++)
                    {
                        var t = tris[gTris[i]];
                        Vector3 p0 = wVerts[t.V0] + wNorms[t.V0] * 0.0003f;
                        Vector3 p1 = wVerts[t.V1] + wNorms[t.V1] * 0.0003f;
                        Vector3 p2 = wVerts[t.V2] + wNorms[t.V2] * 0.0003f;
                        GL.Vertex(p0); GL.Vertex(p1);
                        GL.Vertex(p1); GL.Vertex(p2);
                        GL.Vertex(p2); GL.Vertex(p0);
                    }
                }
                GL.End();
                GL.PopMatrix();
            }
        }

        // 2. ホバー中のアイランドの描画（常にくっきり表示）
        if (hoveredIslandId >= 0 && hoveredIslandId < ctx.Islands.Count)
        {
            var hIsl = ctx.Islands[hoveredIslandId];
            int[] gTris = hIsl.GlobalTriangles;

            // 奥に隠れている部分も薄く表示
            _overlayMaterialAlways.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.TRIANGLES);
            GL.Color(new Color(hoverFillColor.r, hoverFillColor.g, hoverFillColor.b, hoverFillColor.a * 0.35f));
            for (int i = 0; i < gTris.Length; i++)
            {
                var t = tris[gTris[i]];
                GL.Vertex(wVerts[t.V0]);
                GL.Vertex(wVerts[t.V1]);
                GL.Vertex(wVerts[t.V2]);
            }
            GL.End();
            GL.PopMatrix();

            // 手前に見えている部分
            _overlayMaterialZTest.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.TRIANGLES);
            GL.Color(hoverFillColor);
            for (int i = 0; i < gTris.Length; i++)
            {
                var t = tris[gTris[i]];
                GL.Vertex(wVerts[t.V0] + wNorms[t.V0] * 0.0003f);
                GL.Vertex(wVerts[t.V1] + wNorms[t.V1] * 0.0003f);
                GL.Vertex(wVerts[t.V2] + wNorms[t.V2] * 0.0003f);
            }
            GL.End();

            GL.Begin(GL.LINES);
            GL.Color(hoverWireColor);
            for (int i = 0; i < gTris.Length; i++)
            {
                var t = tris[gTris[i]];
                Vector3 p0 = wVerts[t.V0] + wNorms[t.V0] * 0.0004f;
                Vector3 p1 = wVerts[t.V1] + wNorms[t.V1] * 0.0004f;
                Vector3 p2 = wVerts[t.V2] + wNorms[t.V2] * 0.0004f;
                GL.Vertex(p0); GL.Vertex(p1);
                GL.Vertex(p1); GL.Vertex(p2);
                GL.Vertex(p2); GL.Vertex(p0);
            }
            GL.End();
            GL.PopMatrix();
        }
    }

    public static void DrawSceneHUD(
        string toolTitle,
        int selectedIslandCount,
        int totalIslandCount,
        int hoveredIslandId,
        Action onClearSelection,
        Action onExitEditMode)
    {
        Handles.BeginGUI();
        var rect = new Rect(12f, 12f, 320f, 76f);
        EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f, 0.88f));
        EditorGUI.DrawRect(new Rect(rect.x, rect.y, 4f, rect.height), new Color(0.25f, 0.75f, 1f, 1f));

        GUILayout.BeginArea(new Rect(rect.x + 10f, rect.y + 6f, rect.width - 16f, rect.height - 10f));
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label($"🎯 {toolTitle} [選択モード中]", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("終了", EditorStyles.miniButton, GUILayout.Width(42f)))
            {
                onExitEditMode?.Invoke();
            }
        }

        string hoverText = hoveredIslandId >= 0 ? $"ホバー中: 房 #{hoveredIslandId}" : "ホバー: なし";
        GUILayout.Label($"選択中: {selectedIslandCount} / {totalIslandCount} 房   ({hoverText})", EditorStyles.miniLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("左クリック/ドラッグ: 選択・解除   Alt/右ドラッグ: カメラ", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (selectedIslandCount > 0 && GUILayout.Button("全解除", EditorStyles.miniButton, GUILayout.Width(48f)))
            {
                onClearSelection?.Invoke();
            }
        }
        GUILayout.EndArea();
        Handles.EndGUI();
    }
}

/// <summary>
/// リアルタイム・テクスチャ＆UVビュワー
/// SceneView でのクリック・ホバーと同時に、テクスチャ上のどのUV領域が選択されているかをリアルタイム描画する。
/// ビュワー上でのズーム・パンおよびUV直接クリック選択にも対応。
/// </summary>
[Serializable]
public class HairTextureUVViewer
{
    public bool ShowAllUVWire = true;
    public bool ShowSelectedFill = true;
    public bool ShowSelectedWire = true;
    public float SelectedFillOpacity = 0.42f;
    public float Zoom = 1.0f;
    public Vector2 Pan = Vector2.zero;

    private bool _isPanning;
    private bool _isDraggingSelection;
    private bool _dragAddMode;

    public void ResetView()
    {
        Zoom = 1.0f;
        Pan = Vector2.zero;
    }

    public void FitToSelected(HairMeshContext ctx, HashSet<int> selectedIslands)
    {
        if (ctx == null || !ctx.IsValid || selectedIslands == null || selectedIslands.Count == 0)
        {
            ResetView();
            return;
        }

        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);
        bool any = false;

        foreach (int id in selectedIslands)
        {
            if (id < 0 || id >= ctx.Islands.Count) continue;
            Rect r = ctx.Islands[id].UVBounds;
            min = Vector2.Min(min, r.min);
            max = Vector2.Max(max, r.max);
            any = true;
        }

        if (!any)
        {
            ResetView();
            return;
        }

        Vector2 center = (min + max) * 0.5f;
        center.x = Mathf.Repeat(center.x, 1f);
        center.y = Mathf.Repeat(center.y, 1f);

        float extent = Mathf.Max(max.x - min.x, max.y - min.y);
        Zoom = Mathf.Clamp(0.75f / Mathf.Max(0.05f, extent), 1f, 8f);

        // UV (0..1, 下が0・上が1) -> Viewer正規化座標 (上が0・下が1)
        Vector2 viewCenter = new Vector2(center.x, 1f - center.y);
        Pan = (new Vector2(0.5f, 0.5f) - viewCenter) * Zoom;
    }

    /// <summary>
    /// テクスチャビュワーのツールバー＋キャンバスを描画し、ビュワー上でUVアイランドがクリックされた場合はコールバックを呼ぶ。
    /// </summary>
    public void DrawViewer(
        Rect canvasRect,
        Texture displayTexture,
        HairMeshContext ctx,
        int subMeshFilter,
        HashSet<int> selectedIslands,
        int hoveredIslandId,
        Color selectedColor,
        Color hoverColor,
        Action<int, bool> onUvIslandClickedOrDragged,
        Action<int> onUvIslandHovered,
        Action onRepaintRequest)
    {
        Event e = Event.current;

        // 背景枠
        EditorGUI.DrawRect(canvasRect, new Color(0.14f, 0.14f, 0.14f, 1f));

        // キャンバス内の正方形テクスチャ表示領域を算出
        float size = Mathf.Min(canvasRect.width - 4f, canvasRect.height - 4f);
        Rect baseTexRect = new Rect(
            canvasRect.x + (canvasRect.width - size) * 0.5f,
            canvasRect.y + (canvasRect.height - size) * 0.5f,
            size,
            size);

        // ズーム＆パン適用後のテクスチャ矩形（canvasRect ローカル座標系）
        float zoomedSize = size * Zoom;
        Vector2 localBaseCenter = new Vector2(
            (baseTexRect.center.x - canvasRect.x) + Pan.x * size,
            (baseTexRect.center.y - canvasRect.y) + Pan.y * size);

        Rect localTexRect = new Rect(
            localBaseCenter.x - zoomedSize * 0.5f,
            localBaseCenter.y - zoomedSize * 0.5f,
            zoomedSize,
            zoomedSize);

        // マウスインタラクション処理（ズーム / パン / UVクリック選択）
        if (canvasRect.Contains(e.mousePosition))
        {
            Vector2 localMouse = e.mousePosition - canvasRect.position;
            float u = (localMouse.x - localTexRect.x) / Mathf.Max(1f, localTexRect.width);
            float v = 1f - ((localMouse.y - localTexRect.y) / Mathf.Max(1f, localTexRect.height));
            Vector2 mouseUV = new Vector2(u, v);

            if (e.type == EventType.ScrollWheel)
            {
                float oldZoom = Zoom;
                float zoomFactor = e.delta.y < 0f ? 1.18f : (1f / 1.18f);
                Zoom = Mathf.Clamp(Zoom * zoomFactor, 0.8f, 10f);

                // カーソル位置を中心にズーム
                Vector2 rel = (localMouse - new Vector2(canvasRect.width * 0.5f, canvasRect.height * 0.5f)) / Mathf.Max(1f, size);
                Pan = rel - (rel - Pan) * (Zoom / oldZoom);

                e.Use();
                onRepaintRequest?.Invoke();
            }
            else if (e.type == EventType.MouseDown && (e.button == 1 || e.button == 2 || (e.button == 0 && e.alt)))
            {
                _isPanning = true;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _isPanning)
            {
                Pan += e.delta / Mathf.Max(1f, size);
                e.Use();
                onRepaintRequest?.Invoke();
            }
            else if (e.type == EventType.MouseUp && _isPanning)
            {
                _isPanning = false;
                e.Use();
            }
            else if (ctx != null && ctx.IsValid)
            {
                if (e.type == EventType.MouseMove)
                {
                    if (u >= -0.05f && u <= 1.05f && v >= -0.05f && v <= 1.05f &&
                        ctx.PickIslandAtUV(mouseUV, subMeshFilter, out int hitIsl))
                    {
                        onUvIslandHovered?.Invoke(hitIsl);
                    }
                    else
                    {
                        onUvIslandHovered?.Invoke(-1);
                    }
                }
                else if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
                {
                    if (ctx.PickIslandAtUV(mouseUV, subMeshFilter, out int hitIsl))
                    {
                        bool currentlySelected = selectedIslands != null && selectedIslands.Contains(hitIsl);
                        _dragAddMode = e.control ? false : (e.shift ? true : !currentlySelected);
                        _isDraggingSelection = true;
                        onUvIslandClickedOrDragged?.Invoke(hitIsl, _dragAddMode);
                        e.Use();
                    }
                }
                else if (e.type == EventType.MouseDrag && _isDraggingSelection && e.button == 0)
                {
                    if (ctx.PickIslandAtUV(mouseUV, subMeshFilter, out int hitIsl))
                    {
                        onUvIslandClickedOrDragged?.Invoke(hitIsl, _dragAddMode);
                        e.Use();
                    }
                }
                else if (e.type == EventType.MouseUp && _isDraggingSelection)
                {
                    _isDraggingSelection = false;
                    e.Use();
                }
            }
        }

        // クリップ領域内でテクスチャ＋GL UVオーバーレイを描画
        GUI.BeginClip(canvasRect);
        {
            // UV 0..1 領域の背景
            EditorGUI.DrawRect(localTexRect, new Color(0.08f, 0.08f, 0.08f, 1f));

            if (displayTexture != null)
            {
                GUI.DrawTexture(localTexRect, displayTexture, ScaleMode.StretchToFill, true);
            }

            if (Event.current.type == EventType.Repaint && ctx != null && ctx.IsValid)
            {
                Material mat = HairSceneOverlayRenderer.GetGuiColoredMaterial();
                mat.SetPass(0);

                GL.PushMatrix();

                var uvs = ctx.UVs;
                var tris = ctx.Triangles;

                Vector3 UVToGuiPoint(Vector2 uvCoord)
                {
                    // UV が 0..1 の外にある場合は 0..1 タイル内に折り返して表示
                    float ux = uvCoord.x;
                    float uy = uvCoord.y;
                    float px = localTexRect.x + ux * localTexRect.width;
                    float py = localTexRect.y + (1f - uy) * localTexRect.height;
                    return new Vector3(px, py, 0f);
                }

                // 1. 全体のUVワイヤーフレーム（薄く表示）
                if (ShowAllUVWire)
                {
                    GL.Begin(GL.LINES);
                    GL.Color(new Color(0.85f, 0.85f, 0.85f, 0.16f));
                    for (int i = 0; i < tris.Length; i++)
                    {
                        var t = tris[i];
                        if (subMeshFilter >= 0 && t.SubMeshIndex != subMeshFilter) continue;

                        Vector3 p0 = UVToGuiPoint(uvs[t.V0]);
                        Vector3 p1 = UVToGuiPoint(uvs[t.V1]);
                        Vector3 p2 = UVToGuiPoint(uvs[t.V2]);

                        GL.Vertex(p0); GL.Vertex(p1);
                        GL.Vertex(p1); GL.Vertex(p2);
                        GL.Vertex(p2); GL.Vertex(p0);
                    }
                    GL.End();
                }

                // 2. 選択中のアイランドのUV塗りつぶし
                if (ShowSelectedFill && selectedIslands != null && selectedIslands.Count > 0)
                {
                    GL.Begin(GL.TRIANGLES);
                    GL.Color(new Color(selectedColor.r, selectedColor.g, selectedColor.b, SelectedFillOpacity));
                    foreach (int islId in selectedIslands)
                    {
                        if (islId < 0 || islId >= ctx.Islands.Count) continue;
                        var isl = ctx.Islands[islId];
                        if (subMeshFilter >= 0 && isl.PrimarySubMesh != subMeshFilter) continue;

                        Vector2 tileShift = GetUVTileShift(isl.UVBounds);
                        int[] gTris = isl.GlobalTriangles;
                        for (int i = 0; i < gTris.Length; i++)
                        {
                            var t = tris[gTris[i]];
                            GL.Vertex(UVToGuiPoint(uvs[t.V0] - tileShift));
                            GL.Vertex(UVToGuiPoint(uvs[t.V1] - tileShift));
                            GL.Vertex(UVToGuiPoint(uvs[t.V2] - tileShift));
                        }
                    }
                    GL.End();
                }

                // 3. 選択中のアイランドのUVワイヤーフレーム
                if (ShowSelectedWire && selectedIslands != null && selectedIslands.Count > 0)
                {
                    GL.Begin(GL.LINES);
                    GL.Color(new Color(selectedColor.r, selectedColor.g, selectedColor.b, 0.92f));
                    foreach (int islId in selectedIslands)
                    {
                        if (islId < 0 || islId >= ctx.Islands.Count) continue;
                        var isl = ctx.Islands[islId];
                        if (subMeshFilter >= 0 && isl.PrimarySubMesh != subMeshFilter) continue;

                        Vector2 tileShift = GetUVTileShift(isl.UVBounds);
                        int[] gTris = isl.GlobalTriangles;
                        for (int i = 0; i < gTris.Length; i++)
                        {
                            var t = tris[gTris[i]];
                            Vector3 p0 = UVToGuiPoint(uvs[t.V0] - tileShift);
                            Vector3 p1 = UVToGuiPoint(uvs[t.V1] - tileShift);
                            Vector3 p2 = UVToGuiPoint(uvs[t.V2] - tileShift);
                            GL.Vertex(p0); GL.Vertex(p1);
                            GL.Vertex(p1); GL.Vertex(p2);
                            GL.Vertex(p2); GL.Vertex(p0);
                        }
                    }
                    GL.End();
                }

                // 4. ホバー中のアイランドのUVハイライト
                if (hoveredIslandId >= 0 && hoveredIslandId < ctx.Islands.Count)
                {
                    var hIsl = ctx.Islands[hoveredIslandId];
                    Vector2 tileShift = GetUVTileShift(hIsl.UVBounds);
                    int[] gTris = hIsl.GlobalTriangles;

                    GL.Begin(GL.TRIANGLES);
                    GL.Color(new Color(hoverColor.r, hoverColor.g, hoverColor.b, 0.45f));
                    for (int i = 0; i < gTris.Length; i++)
                    {
                        var t = tris[gTris[i]];
                        GL.Vertex(UVToGuiPoint(uvs[t.V0] - tileShift));
                        GL.Vertex(UVToGuiPoint(uvs[t.V1] - tileShift));
                        GL.Vertex(UVToGuiPoint(uvs[t.V2] - tileShift));
                    }
                    GL.End();

                    GL.Begin(GL.LINES);
                    GL.Color(new Color(hoverColor.r, hoverColor.g, hoverColor.b, 1.0f));
                    for (int i = 0; i < gTris.Length; i++)
                    {
                        var t = tris[gTris[i]];
                        Vector3 p0 = UVToGuiPoint(uvs[t.V0] - tileShift);
                        Vector3 p1 = UVToGuiPoint(uvs[t.V1] - tileShift);
                        Vector3 p2 = UVToGuiPoint(uvs[t.V2] - tileShift);
                        GL.Vertex(p0); GL.Vertex(p1);
                        GL.Vertex(p1); GL.Vertex(p2);
                        GL.Vertex(p2); GL.Vertex(p0);
                    }
                    GL.End();
                }

                // 5. UV (0..1) 枠線
                GL.Begin(GL.LINES);
                GL.Color(new Color(0.5f, 0.5f, 0.5f, 0.8f));
                Vector3 c00 = new Vector3(localTexRect.xMin, localTexRect.yMin, 0f);
                Vector3 c10 = new Vector3(localTexRect.xMax, localTexRect.yMin, 0f);
                Vector3 c11 = new Vector3(localTexRect.xMax, localTexRect.yMax, 0f);
                Vector3 c01 = new Vector3(localTexRect.xMin, localTexRect.yMax, 0f);
                GL.Vertex(c00); GL.Vertex(c10);
                GL.Vertex(c10); GL.Vertex(c11);
                GL.Vertex(c11); GL.Vertex(c01);
                GL.Vertex(c01); GL.Vertex(c00);
                GL.End();

                GL.PopMatrix();
            }
        }
        GUI.EndClip();
    }

    public static Vector2 GetUVTileShift(Rect uvBounds)
    {
        Vector2 c = uvBounds.center;
        if (c.x >= 0f && c.x <= 1f && c.y >= 0f && c.y <= 1f)
            return Vector2.zero;
        return new Vector2(Mathf.Floor(c.x), Mathf.Floor(c.y));
    }
}

/// <summary>
/// テクスチャ読み出し・UVマスクラスタライズ・2パス距離変換Dilation・色彩調整アルゴリズム
/// </summary>
public static class HairTextureProcessor
{
    /// <summary>
    /// 非Readableや圧縮テクスチャからも、sRGB色空間を正確に保ったまま Color32[] ピクセル配列を取得する。
    /// </summary>
    public static Color32[] ReadTexturePixelsSRGB(Texture sourceTex, int targetWidth, int targetHeight)
    {
        if (targetWidth <= 0) targetWidth = 512;
        if (targetHeight <= 0) targetHeight = 512;

        if (sourceTex == null)
        {
            var white = new Color32[targetWidth * targetHeight];
            var c = new Color32(255, 255, 255, 255);
            for (int i = 0; i < white.Length; i++) white[i] = c;
            return white;
        }

        RenderTexture prevRt = RenderTexture.active;
        RenderTexture tempRt = RenderTexture.GetTemporary(
            targetWidth,
            targetHeight,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB);

        Texture2D tempTex = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false, false);
        try
        {
            Graphics.Blit(sourceTex, tempRt);
            RenderTexture.active = tempRt;
            tempTex.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0, false);
            tempTex.Apply(false, false);
            return tempTex.GetPixels32();
        }
        finally
        {
            RenderTexture.active = prevRt;
            RenderTexture.ReleaseTemporary(tempRt);
            UnityEngine.Object.DestroyImmediate(tempTex);
        }
    }

    /// <summary>
    /// マテリアルからメインテクスチャを取得する（lilToon / Standard / URP / Unlit 等すべて対応）。
    /// </summary>
    public static Texture GetMainTextureFromMaterial(Material mat, out string propertyName)
    {
        propertyName = "_MainTex";
        if (mat == null) return null;

        string[] candidates = { "_MainTex", "_BaseMap", "_BaseColorMap", "_AlbedoMap" };
        foreach (string prop in candidates)
        {
            if (mat.HasProperty(prop))
            {
                Texture tex = mat.GetTexture(prop);
                if (tex != null)
                {
                    propertyName = prop;
                    return tex;
                }
            }
        }

        if (mat.mainTexture != null)
        {
            return mat.mainTexture;
        }

        return null;
    }

    /// <summary>
    /// 選択されたアイランドのUV領域から [0..1] のマスク配列 (width * height) をラスタライズ生成する。
    /// ・Chamfer 3-4 2パス距離変換により O(W*H) で指定ピクセル数の UVパディング（シーム漏れ防止膨張）と境界ぼかし（Feather）を計算
    /// ・protectUnselectedIslands が true の場合、非選択アイランドのUV内部への色漏れ・はみ出しを防止
    /// </summary>
    public static float[] RasterizeUVMask(
        HairMeshContext ctx,
        HashSet<int> selectedIslands,
        int subMeshFilter,
        int width,
        int height,
        int paddingPixels,
        int featherPixels,
        bool protectUnselectedIslands)
    {
        int totalPixels = width * height;
        float[] mask = new float[totalPixels];

        if (ctx == null || !ctx.IsValid || selectedIslands == null || selectedIslands.Count == 0)
            return mask;

        const int INF_DIST = 1000000;
        int[] distGrid = new int[totalPixels];
        for (int i = 0; i < totalPixels; i++) distGrid[i] = INF_DIST;

        var uvs = ctx.UVs;
        var tris = ctx.Triangles;

        // 1. 選択されたアイランドのUV三角形を distGrid = 0 としてラスタライズ
        foreach (int islId in selectedIslands)
        {
            if (islId < 0 || islId >= ctx.Islands.Count) continue;
            var isl = ctx.Islands[islId];
            if (subMeshFilter >= 0 && isl.PrimarySubMesh != subMeshFilter) continue;

            Vector2 tileShift = HairTextureUVViewer.GetUVTileShift(isl.UVBounds);
            int[] gTris = isl.GlobalTriangles;

            for (int i = 0; i < gTris.Length; i++)
            {
                var t = tris[gTris[i]];
                if (subMeshFilter >= 0 && t.SubMeshIndex != subMeshFilter) continue;

                Vector2 uv0 = uvs[t.V0] - tileShift;
                Vector2 uv1 = uvs[t.V1] - tileShift;
                Vector2 uv2 = uvs[t.V2] - tileShift;

                RasterizeTriangleToGrid(uv0, uv1, uv2, width, height, distGrid, 0);
            }
        }

        // 2. 非選択アイランド保護用のブロックグリッド作成
        byte[] unselectedGuard = null;
        if (protectUnselectedIslands && paddingPixels > 0)
        {
            unselectedGuard = new byte[totalPixels];
            int[] tempGuardDist = new int[totalPixels];
            for (int i = 0; i < totalPixels; i++) tempGuardDist[i] = INF_DIST;

            for (int islId = 0; islId < ctx.Islands.Count; islId++)
            {
                if (selectedIslands.Contains(islId)) continue;
                var isl = ctx.Islands[islId];
                if (subMeshFilter >= 0 && isl.PrimarySubMesh != subMeshFilter) continue;

                Vector2 tileShift = HairTextureUVViewer.GetUVTileShift(isl.UVBounds);
                int[] gTris = isl.GlobalTriangles;
                for (int i = 0; i < gTris.Length; i++)
                {
                    var t = tris[gTris[i]];
                    if (subMeshFilter >= 0 && t.SubMeshIndex != subMeshFilter) continue;

                    Vector2 uv0 = uvs[t.V0] - tileShift;
                    Vector2 uv1 = uvs[t.V1] - tileShift;
                    Vector2 uv2 = uvs[t.V2] - tileShift;

                    RasterizeTriangleToGrid(uv0, uv1, uv2, width, height, tempGuardDist, 0);
                }
            }

            // 選択アイランド自身と重なっていない非選択UV内部をガード対象にする
            for (int i = 0; i < totalPixels; i++)
            {
                if (tempGuardDist[i] == 0 && distGrid[i] != 0)
                    unselectedGuard[i] = 1;
            }
        }

        // 3. Chamfer (3, 4) 2パス距離変換（1px = 3 単位）で高速Dilation
        int maxExpand = Mathf.Max(0, paddingPixels) + Mathf.Max(0, featherPixels);
        if (maxExpand > 0)
        {
            // Forward pass (bottom-left to top-right)
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                int prevRow = (y - 1) * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = row + x;
                    int d = distGrid[idx];
                    if (d == 0) continue;

                    if (x > 0) d = Mathf.Min(d, distGrid[idx - 1] + 3);
                    if (y > 0)
                    {
                        d = Mathf.Min(d, distGrid[prevRow + x] + 3);
                        if (x > 0) d = Mathf.Min(d, distGrid[prevRow + x - 1] + 4);
                        if (x + 1 < width) d = Mathf.Min(d, distGrid[prevRow + x + 1] + 4);
                    }
                    distGrid[idx] = d;
                }
            }

            // Backward pass (top-right to bottom-left)
            for (int y = height - 1; y >= 0; y--)
            {
                int row = y * width;
                int nextRow = (y + 1) * width;
                for (int x = width - 1; x >= 0; x--)
                {
                    int idx = row + x;
                    int d = distGrid[idx];
                    if (d == 0) continue;

                    if (x + 1 < width) d = Mathf.Min(d, distGrid[idx + 1] + 3);
                    if (y + 1 < height)
                    {
                        d = Mathf.Min(d, distGrid[nextRow + x] + 3);
                        if (x + 1 < width) d = Mathf.Min(d, distGrid[nextRow + x + 1] + 4);
                        if (x > 0) d = Mathf.Min(d, distGrid[nextRow + x - 1] + 4);
                    }
                    distGrid[idx] = d;
                }
            }
        }

        // 4. 距離値から [0..1] マスク値へ変換
        float padThreshold = Mathf.Max(0, paddingPixels) * 3f;
        float featherRange = Mathf.Max(0, featherPixels) * 3f;

        for (int i = 0; i < totalPixels; i++)
        {
            int d = distGrid[i];
            if (d == 0)
            {
                mask[i] = 1f;
            }
            else if (unselectedGuard != null && unselectedGuard[i] == 1 && d > 3)
            {
                // 1pxのシーム境界だけは許容し、それより深い非選択UV内部への侵入をブロック
                mask[i] = 0f;
            }
            else if (d <= padThreshold)
            {
                mask[i] = 1f;
            }
            else if (featherRange > 0f && d <= padThreshold + featherRange)
            {
                mask[i] = 1f - ((d - padThreshold) / featherRange);
            }
            else
            {
                mask[i] = 0f;
            }
        }

        return mask;
    }

    private static void RasterizeTriangleToGrid(
        Vector2 uv0, Vector2 uv1, Vector2 uv2,
        int width, int height, int[] distGrid, int targetValue)
    {
        float x0 = uv0.x * width;
        float y0 = uv0.y * height;
        float x1 = uv1.x * width;
        float y1 = uv1.y * height;
        float x2 = uv2.x * width;
        float y2 = uv2.y * height;

        // 0.5px 膨張したバウンディングボックスでラスタライズし、微小な隙間や極細ポリゴンも確実に拾う
        int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(x0, Mathf.Min(x1, x2)) - 0.5f), 0, width - 1);
        int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(x0, Mathf.Max(x1, x2)) + 0.5f), 0, width - 1);
        int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(y0, Mathf.Min(y1, y2)) - 0.5f), 0, height - 1);
        int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(y0, Mathf.Max(y1, y2)) + 0.5f), 0, height - 1);

        // 三角形の頂点およびエッジ自体も確実に塗る（面積が1px未満の極小UVポリゴン対策）
        PlotLineToGrid(Mathf.RoundToInt(x0), Mathf.RoundToInt(y0), Mathf.RoundToInt(x1), Mathf.RoundToInt(y1), width, height, distGrid, targetValue);
        PlotLineToGrid(Mathf.RoundToInt(x1), Mathf.RoundToInt(y1), Mathf.RoundToInt(x2), Mathf.RoundToInt(y2), width, height, distGrid, targetValue);
        PlotLineToGrid(Mathf.RoundToInt(x2), Mathf.RoundToInt(y2), Mathf.RoundToInt(x0), Mathf.RoundToInt(y0), width, height, distGrid, targetValue);

        float denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
        if (Mathf.Abs(denom) < 1e-6f) return;
        float invDenom = 1f / denom;

        for (int py = minY; py <= maxY; py++)
        {
            float cy = py + 0.5f;
            int row = py * width;
            for (int px = minX; px <= maxX; px++)
            {
                if (distGrid[row + px] == targetValue) continue;

                float cx = px + 0.5f;
                float w0 = ((y1 - y2) * (cx - x2) + (x2 - x1) * (cy - y2)) * invDenom;
                float w1 = ((y2 - y0) * (cx - x2) + (x0 - x2) * (cy - y2)) * invDenom;
                float w2 = 1f - w0 - w1;

                if (w0 >= -0.02f && w1 >= -0.02f && w2 >= -0.02f)
                {
                    distGrid[row + px] = targetValue;
                }
            }
        }
    }

    private static void PlotLineToGrid(int x0, int y0, int x1, int y1, int width, int height, int[] distGrid, int targetValue)
    {
        int dx = Mathf.Abs(x1 - x0);
        int dy = -Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        int maxSteps = width + height + 4;
        int step = 0;
        while (step++ < maxSteps)
        {
            if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
            {
                distGrid[y0 * width + x0] = targetValue;
            }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// 4つの基本色彩調整スライダー（色相 Hue / 彩度 Saturation / 明度 Brightness / コントラスト Contrast）
    /// ＋オプションのカラライズ（色乗せ Tint）をマスク領域に適用した Color32[] を生成する。
    /// </summary>
    public static void ApplyColorAdjustments(
        Color32[] srcPixels,
        float[] mask,
        Color32[] dstPixels,
        float hueShiftDeg,      // -180 .. +180
        float saturationDelta,  // -100 .. +100
        float brightnessDelta,  // -100 .. +100
        float contrastDelta,    // -100 .. +100
        bool useTint,
        Color tintColor,
        float tintStrength)     // 0 .. 1
    {
        int len = srcPixels.Length;
        float hueNorm = hueShiftDeg / 360f;
        float satNorm = saturationDelta / 100f;
        float briNorm = brightnessDelta / 100f;

        // コントラスト係数 (-100..+100 -> 0..~4+)
        float cNorm = Mathf.Clamp(contrastDelta / 100f, -0.98f, 0.98f);
        float contrastFactor = (1f + cNorm) / (1f - cNorm);

        Color.RGBToHSV(tintColor, out float tintH, out float tintS, out _);

        for (int i = 0; i < len; i++)
        {
            Color32 src = srcPixels[i];
            float m = mask != null && i < mask.Length ? mask[i] : 1f;
            if (m <= 0.0001f)
            {
                dstPixels[i] = src;
                continue;
            }

            float r = src.r * (1f / 255f);
            float g = src.g * (1f / 255f);
            float b = src.b * (1f / 255f);

            Color.RGBToHSV(new Color(r, g, b, 1f), out float h, out float s, out float v);

            // 1. 色乗せ（Tint / Colorize）オプションが有効な場合
            if (useTint && tintStrength > 0.0001f)
            {
                h = tintH;
                s = Mathf.Lerp(s, tintS, tintStrength);
            }

            // 2. 色相 (Hue)
            if (Mathf.Abs(hueNorm) > 1e-5f)
            {
                h = Mathf.Repeat(h + hueNorm, 1f);
            }

            // 3. 彩度 (Saturation)
            if (satNorm > 0f)
            {
                // 元の彩度が0（黒髪・白髪）でも少し色が付いている場合は自然に持ち上げる
                s = s + (1f - s) * satNorm;
            }
            else if (satNorm < 0f)
            {
                s = s * (1f + satNorm);
            }
            s = Mathf.Clamp01(s);

            Color rgb = Color.HSVToRGB(h, s, v);
            r = rgb.r;
            g = rgb.g;
            b = rgb.b;

            // 4. 明度 (Brightness / Value)
            if (briNorm > 0f)
            {
                r = r + (1f - r) * briNorm * 0.85f + briNorm * 0.15f;
                g = g + (1f - g) * briNorm * 0.85f + briNorm * 0.15f;
                b = b + (1f - b) * briNorm * 0.85f + briNorm * 0.15f;
            }
            else if (briNorm < 0f)
            {
                float scale = 1f + briNorm;
                r *= scale;
                g *= scale;
                b *= scale;
            }

            // 5. コントラスト (Contrast)
            if (Mathf.Abs(cNorm) > 1e-5f)
            {
                r = (r - 0.5f) * contrastFactor + 0.5f;
                g = (g - 0.5f) * contrastFactor + 0.5f;
                b = (b - 0.5f) * contrastFactor + 0.5f;
            }

            r = Mathf.Clamp01(r);
            g = Mathf.Clamp01(g);
            b = Mathf.Clamp01(b);

            if (m < 0.9999f)
            {
                float origR = src.r * (1f / 255f);
                float origG = src.g * (1f / 255f);
                float origB = src.b * (1f / 255f);
                r = Mathf.Lerp(origR, r, m);
                g = Mathf.Lerp(origG, g, m);
                b = Mathf.Lerp(origB, b, m);
            }

            dstPixels[i] = new Color32(
                (byte)Mathf.RoundToInt(r * 255f),
                (byte)Mathf.RoundToInt(g * 255f),
                (byte)Mathf.RoundToInt(b * 255f),
                src.a);
        }
    }

    /// <summary>
    /// マスク配列 (0..1) から白黒マスクテクスチャの Color32[] を生成する。
    /// </summary>
    public static Color32[] BuildMaskPixels(float[] mask, bool invert)
    {
        int len = mask.Length;
        var pixels = new Color32[len];
        for (int i = 0; i < len; i++)
        {
            float v = Mathf.Clamp01(mask[i]);
            if (invert) v = 1f - v;
            byte b = (byte)Mathf.RoundToInt(v * 255f);
            pixels[i] = new Color32(b, b, b, 255);
        }
        return pixels;
    }

    /// <summary>
    /// 重複しない連番アセットパスを生成する（例: Assets/Folder/BaseName_Suffix-000.ext）
    /// </summary>
    public static string GenerateUniqueAssetPath(string folderPath, string baseName, string suffixMarker, string extensionWithDot)
    {
        if (string.IsNullOrEmpty(folderPath) || !folderPath.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
        {
            folderPath = "Assets";
        }
        folderPath = folderPath.Replace('\\', '/').TrimEnd('/');

        EnsureAssetFolderExists(folderPath);

        string safeName = SanitizeFileName(string.IsNullOrWhiteSpace(baseName) ? "HairMesh" : baseName);

        // 既存のサフィックスがあれば除去して二重付与を防ぐ
        int markerIdx = safeName.LastIndexOf(suffixMarker, StringComparison.Ordinal);
        if (markerIdx > 0)
            safeName = safeName.Substring(0, markerIdx);

        for (int i = 0; i < 1000; i++)
        {
            string candidate = $"{folderPath}/{safeName}{suffixMarker}{i:000}{extensionWithDot}";
            if (AssetDatabase.LoadMainAssetAtPath(candidate) == null && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        return AssetDatabase.GenerateUniqueAssetPath($"{folderPath}/{safeName}{suffixMarker}999{extensionWithDot}");
    }

    public static string ResolveDefaultSaveFolder(UnityEngine.Object referenceAsset, string fallbackFolder)
    {
        if (referenceAsset != null)
        {
            string assetPath = AssetDatabase.GetAssetPath(referenceAsset);
            if (!string.IsNullOrEmpty(assetPath))
            {
                assetPath = assetPath.Replace('\\', '/');
                if (assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    string dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(dir))
                        return dir;
                }
            }
        }
        return fallbackFolder;
    }

    public static void EnsureAssetFolderExists(string assetFolderPath)
    {
        if (string.IsNullOrEmpty(assetFolderPath)) return;
        string norm = assetFolderPath.Replace('\\', '/').TrimEnd('/');
        if (AssetDatabase.IsValidFolder(norm)) return;

        string[] parts = norm.Split('/');
        if (parts.Length == 0 || !parts[0].Equals("Assets", StringComparison.OrdinalIgnoreCase))
            return;

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

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = name.Where(c => !invalid.Contains(c)).ToArray();
        string cleaned = new string(chars).Trim();
        return string.IsNullOrEmpty(cleaned) ? "HairMesh" : cleaned;
    }
}
#endif
