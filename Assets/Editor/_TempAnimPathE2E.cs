#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class _TempAnimPathE2E
{
    public static void Run()
    {
        string resultFile = @"C:\Users\yuuya\.gemini\antigravity-cli\brain\99dd2c1f-6ddc-4439-89fb-ab612cd550cd\scratch\anim_e2e_result.txt";
        string tempAssetDir = "Assets/_TempAnimTestAssets";
        GameObject avatarGo = null;
        AnimationPathAdjuster win = null;

        try
        {
            if (AssetDatabase.IsValidFolder(tempAssetDir))
                AssetDatabase.DeleteAsset(tempAssetDir);
            AssetDatabase.CreateFolder("Assets", "_TempAnimTestAssets");

            // 1. Create 2 facial expression .anim clips on path "Body" (BlendShapes + Material PPtr + Hair Active)
            var matSample = new Material(Shader.Find("Standard")) { name = "SampleMat" };
            AssetDatabase.CreateAsset(matSample, tempAssetDir + "/SampleMat.mat");

            AnimationClip CreateExprClip(string clipName, string bs1, string bs2)
            {
                var c = new AnimationClip { name = clipName };
                AnimationUtility.SetEditorCurve(c,
                    EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + bs1),
                    AnimationCurve.Constant(0f, 0.1f, 100f));
                AnimationUtility.SetEditorCurve(c,
                    EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + bs2),
                    AnimationCurve.Constant(0f, 0.1f, 75f));
                AnimationUtility.SetEditorCurve(c,
                    EditorCurveBinding.FloatCurve("Hair", typeof(GameObject), "m_IsActive"),
                    AnimationCurve.Constant(0f, 0.1f, 1f));
                AnimationUtility.SetObjectReferenceCurve(c,
                    EditorCurveBinding.PPtrCurve("Body", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                    new[] { new ObjectReferenceKeyframe { time = 0f, value = matSample } });

                string p = $"{tempAssetDir}/{clipName}.anim";
                AssetDatabase.CreateAsset(c, p);
                return c;
            }

            var clip1 = CreateExprClip("Expr_Smile", "mouth_smile", "blink_left");
            var clip2 = CreateExprClip("Expr_Wink", "blink_right", "v_aa");

            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            win = ScriptableObject.CreateInstance<AnimationPathAdjuster>();

            // Test A: Multi-clip Prepend Parent ("Wrapper" -> "Wrapper/Body"), selecting ONLY "Body" path group!
            typeof(AnimationPathAdjuster).GetMethod("AddSingleClip", flags).Invoke(win, new object[] { clip1 });
            typeof(AnimationPathAdjuster).GetMethod("AddSingleClip", flags).Invoke(win, new object[] { clip2 });
            typeof(AnimationPathAdjuster).GetMethod("ScanAllClips", flags).Invoke(win, new object[] { false });

            var pathGroups = (System.Collections.IList)typeof(AnimationPathAdjuster).GetField("_pathGroups", flags).GetValue(win);
            foreach (var g in pathGroups)
            {
                string gp = (string)g.GetType().GetField("Path").GetValue(g);
                g.GetType().GetField("Selected").SetValue(g, gp == "Body");
            }

            typeof(AnimationPathAdjuster).GetField("_editRuleMode", flags).SetValue(win, 0); // PrependParent
            typeof(AnimationPathAdjuster).GetField("_prependFolderText", flags).SetValue(win, "Wrapper");
            typeof(AnimationPathAdjuster).GetMethod("ExecutePathAdjustment", flags).Invoke(win, null);

            bool testAPassed =
                AnimationUtility.GetEditorCurve(clip1, EditorCurveBinding.FloatCurve("Wrapper/Body", typeof(SkinnedMeshRenderer), "blendShape.mouth_smile")) != null &&
                AnimationUtility.GetEditorCurve(clip2, EditorCurveBinding.FloatCurve("Wrapper/Body", typeof(SkinnedMeshRenderer), "blendShape.blink_right")) != null &&
                AnimationUtility.GetObjectReferenceCurve(clip1, EditorCurveBinding.PPtrCurve("Wrapper/Body", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]")) != null &&
                AnimationUtility.GetEditorCurve(clip1, EditorCurveBinding.FloatCurve("Hair", typeof(GameObject), "m_IsActive")) != null &&
                AnimationUtility.GetEditorCurve(clip1, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.mouth_smile")) == null;

            // Test B: Single clip individual element multi-selection (move ONLY 1 BlendShape element to "Sub2/Body", keep the other on "Wrapper/Body")
            var clip3 = CreateExprClip("Expr_Selective", "brow_up", "brow_down");
            var clipsList = (System.Collections.IList)typeof(AnimationPathAdjuster).GetField("_clips", flags).GetValue(win);
            clipsList.Clear();
            typeof(AnimationPathAdjuster).GetMethod("AddSingleClip", flags).Invoke(win, new object[] { clip3 });
            typeof(AnimationPathAdjuster).GetMethod("ScanAllClips", flags).Invoke(win, new object[] { false });
            typeof(AnimationPathAdjuster).GetField("_selectionMode", flags).SetValue(win, 1); // ByIndividualElement

            var allElements = (System.Collections.IList)typeof(AnimationPathAdjuster).GetField("_allElements", flags).GetValue(win);
            foreach (var el in allElements)
            {
                var b = (EditorCurveBinding)el.GetType().GetField("Binding").GetValue(el);
                el.GetType().GetField("Selected").SetValue(el, b.propertyName == "blendShape.brow_up");
            }
            typeof(AnimationPathAdjuster).GetField("_editRuleMode", flags).SetValue(win, 1); // SetDirectPath
            typeof(AnimationPathAdjuster).GetField("_directTargetPathText", flags).SetValue(win, "Sub2/Body");
            typeof(AnimationPathAdjuster).GetMethod("ExecutePathAdjustment", flags).Invoke(win, null);

            bool testBPassed =
                AnimationUtility.GetEditorCurve(clip3, EditorCurveBinding.FloatCurve("Sub2/Body", typeof(SkinnedMeshRenderer), "blendShape.brow_up")) != null &&
                AnimationUtility.GetEditorCurve(clip3, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.brow_down")) != null;

            // Test C: Avatar Hierarchy Auto-Detection ("Body" moved under "AvatarRoot/FaceContainer/Body" with BlendShape!)
            avatarGo = new GameObject("AvatarRoot");
            avatarGo.AddComponent<Animator>();
            var containerGo = new GameObject("FaceContainer");
            containerGo.transform.SetParent(avatarGo.transform, false);
            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(containerGo.transform, false);
            var smr = bodyGo.AddComponent<SkinnedMeshRenderer>();
            var dummyMesh = new Mesh();
            dummyMesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            dummyMesh.triangles = new[] { 0, 1, 2 };
            dummyMesh.AddBlendShapeFrame("wink_test", 100f, new Vector3[3], new Vector3[3], new Vector3[3]);
            smr.sharedMesh = dummyMesh;

            var clip4 = new AnimationClip { name = "Expr_AutoDetect" };
            AnimationUtility.SetEditorCurve(clip4,
                EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.wink_test"),
                AnimationCurve.Constant(0f, 0.1f, 100f));
            AssetDatabase.CreateAsset(clip4, tempAssetDir + "/Expr_AutoDetect.anim");

            clipsList.Clear();
            typeof(AnimationPathAdjuster).GetMethod("AddSingleClip", flags).Invoke(win, new object[] { clip4 });
            typeof(AnimationPathAdjuster).GetMethod("ScanAllClips", flags).Invoke(win, new object[] { false });
            typeof(AnimationPathAdjuster).GetField("_selectionMode", flags).SetValue(win, 0);
            typeof(AnimationPathAdjuster).GetField("_avatarRoot", flags).SetValue(win, avatarGo.transform);
            typeof(AnimationPathAdjuster).GetMethod("RunAutoDetectPathsFromAvatarHierarchy", flags).Invoke(win, null);
            typeof(AnimationPathAdjuster).GetMethod("ExecutePathAdjustment", flags).Invoke(win, null);

            bool testCPassed =
                AnimationUtility.GetEditorCurve(clip4, EditorCurveBinding.FloatCurve("FaceContainer/Body", typeof(SkinnedMeshRenderer), "blendShape.wink_test")) != null &&
                AnimationUtility.GetEditorCurve(clip4, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.wink_test")) == null;

            UnityEngine.Object.DestroyImmediate(dummyMesh);

            // Test D: Chain move ("Body" -> "Meshes/Body" AND "Meshes/Body" -> "Meshes/Meshes/Body" simultaneously without losing either!)
            var clip5 = new AnimationClip { name = "Expr_Chain" };
            AnimationUtility.SetEditorCurve(clip5, EditorCurveBinding.FloatCurve("Body", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0f, 1f, 1f));
            AnimationUtility.SetEditorCurve(clip5, EditorCurveBinding.FloatCurve("Meshes/Body", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0f, 1f, 0f));
            AssetDatabase.CreateAsset(clip5, tempAssetDir + "/Expr_Chain.anim");

            clipsList.Clear();
            typeof(AnimationPathAdjuster).GetMethod("AddSingleClip", flags).Invoke(win, new object[] { clip5 });
            typeof(AnimationPathAdjuster).GetMethod("ScanAllClips", flags).Invoke(win, new object[] { false });
            typeof(AnimationPathAdjuster).GetField("_editRuleMode", flags).SetValue(win, 0);
            typeof(AnimationPathAdjuster).GetField("_prependFolderText", flags).SetValue(win, "Meshes");
            typeof(AnimationPathAdjuster).GetMethod("ExecutePathAdjustment", flags).Invoke(win, null);

            var c5a = AnimationUtility.GetEditorCurve(clip5, EditorCurveBinding.FloatCurve("Meshes/Body", typeof(GameObject), "m_IsActive"));
            var c5b = AnimationUtility.GetEditorCurve(clip5, EditorCurveBinding.FloatCurve("Meshes/Meshes/Body", typeof(GameObject), "m_IsActive"));
            bool testDPassed = c5a != null && Mathf.Approximately(c5a.Evaluate(0f), 1f) && c5b != null && Mathf.Approximately(c5b.Evaluate(0f), 0f);

            File.WriteAllText(resultFile, $"PASS: testA={testAPassed}, testB={testBPassed}, testC={testCPassed}, testD={testDPassed}");
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultFile, "FAIL: " + ex);
        }
        finally
        {
            if (win != null) UnityEngine.Object.DestroyImmediate(win);
            if (avatarGo != null) UnityEngine.Object.DestroyImmediate(avatarGo);
            if (AssetDatabase.IsValidFolder(tempAssetDir))
                AssetDatabase.DeleteAsset(tempAssetDir);
        }
    }
}
#endif
